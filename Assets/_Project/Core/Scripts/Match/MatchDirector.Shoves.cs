using Mujoco;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// Shoves and jabs (2026-11-23, the game only; GameSettings.Strikes). The brains were trained to win by walking into
    /// the others, which does not look like people fighting. Here a fighter that stands within arm's reach of its target
    /// and faces it throws a scripted strike (PolicyRunner.BeginStrike: both arms for a shove, one for a jab) every 0.9 to
    /// 1.8 s. When a fist reaches the target's torso while the arm is going out, the target gets an impulse away from the
    /// striker (StrikeImpulse / its mass, so heavy Trump moves least) and the striker a smaller recoil. Never in a
    /// statistics gate, so the brain gates measure the brains alone.
    /// </summary>
    public unsafe partial class MatchDirector
    {
        [Header("Shoves and jabs (GameSettings.Strikes)")]
        [Tooltip("N s given to the one hit by a two-handed shove (a jab gives JabShare of it)")] public float strikeImpulse = 42f;
        [Tooltip("Pelvis-to-pelvis distance (m) within which a fighter strikes")] public float strikeReach = 0.85f;
        public const float JabShare = 0.6f, RecoilShare = 0.3f, StrikeFacing = 0.8f;
        public static readonly Vector2 StrikeCooldown = new(0.9f, 1.8f);
        /// <summary>Strikes thrown and strikes that landed since the round began.</summary>
        public int StrikesThrown { get; private set; }
        public int StrikesLanded { get; private set; }

        double[] _strikeReady = System.Array.Empty<double>();
        bool[] _strikeHit = System.Array.Empty<bool>();
        int[] _handBody = System.Array.Empty<int>();   // 2 per body: left and right wrist_yaw_link

        bool StrikesOn => statsRounds == 0 && GameSettings.Strikes && State == MatchState.Fight;

        void ResetStrikes()
        {
            StrikesThrown = StrikesLanded = 0;
            if (_strikeReady.Length != bodies.Length) { _strikeReady = new double[bodies.Length]; _strikeHit = new bool[bodies.Length]; }
            double now = MjScene.InstanceExists && MjScene.Instance.Data != null ? MjScene.Instance.Data->time : 0;
            for (int i = 0; i < bodies.Length; i++) { _strikeReady[i] = now + Random.Range(StrikeCooldown.x, StrikeCooldown.y); _strikeHit[i] = false; }
        }

        void TickStrikes()
        {
            if (!StrikesOn || _strikeReady.Length != bodies.Length) return;
            var m = MjScene.Instance.Model; var d = MjScene.Instance.Data;
            if (_handBody.Length != 2 * bodies.Length)
            {
                _handBody = new int[2 * bodies.Length];
                for (int i = 0; i < bodies.Length; i++)
                    for (int s = 0; s < 2; s++)
                        _handBody[2 * i + s] = MujocoLib.mj_name2id(m, (int)MujocoLib.mjtObj.mjOBJ_BODY, bodies[i].robotPrefix + (s == 0 ? "left" : "right") + "_wrist_yaw_link");
            }
            for (int i = 0; i < _playing; i++)
            {
                var me = bodies[i]; var target = me.opponent;
                if (_out[i] || IsDown(i) || target == null || target.Map == null) continue;
                int j = System.Array.IndexOf(bodies, target);
                if (j < 0 || j >= _playing || _out[j]) continue;
                double* q = d->qpos + me.Map.RootQposAdr, o = d->qpos + target.Map.RootQposAdr;
                double dx = o[0] - q[0], dy = o[1] - q[1], dist = System.Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-3) continue;
                double ux = dx / dist, uy = dy / dist;
                if (me.Strike == PolicyRunner.StrikeKind.None)
                {
                    _strikeHit[i] = false;
                    if (d->time < _strikeReady[i] || dist > strikeReach || ObsBuilder.Up(q + 3) < 0.85) continue;
                    // facing: the pelvis's forward axis (yaw of the root quaternion w x y z) toward the target
                    double yaw = System.Math.Atan2(2 * (q[3] * q[6] + q[4] * q[5]), 1 - 2 * (q[5] * q[5] + q[6] * q[6]));
                    if (System.Math.Cos(yaw) * ux + System.Math.Sin(yaw) * uy < StrikeFacing) continue;
                    // a shove half the time, otherwise a jab with the arm on the target's side
                    double side = System.Math.Cos(yaw) * uy - System.Math.Sin(yaw) * ux;   // > 0: target to my left
                    var kind = Random.value < 0.5f ? PolicyRunner.StrikeKind.Shove : side > 0 ? PolicyRunner.StrikeKind.LeftJab : PolicyRunner.StrikeKind.RightJab;
                    if (me.BeginStrike(kind)) { StrikesThrown++; _strikeReady[i] = d->time + PolicyRunner.StrikeLength + Random.Range(StrikeCooldown.x, StrikeCooldown.y); }
                    continue;
                }
                if (_strikeHit[i] || !me.StrikeLive || !FistOnTorso(d, i, j, me.Strike)) continue;
                _strikeHit[i] = true; StrikesLanded++;
                double J = strikeImpulse * (me.Strike == PolicyRunner.StrikeKind.Shove ? 1.0 : JabShare) * (GameSettings.Modifier == ArenaModifier.HeavyKnockback ? 1.6 : 1.0);
                double mt = m->body_subtreemass[target.Map.PelvisBodyId], mm = m->body_subtreemass[me.Map.PelvisBodyId];
                double* vt = d->qvel + target.Map.RootDofAdr, vm = d->qvel + me.Map.RootDofAdr;
                vt[0] += ux * J / mt; vt[1] += uy * J / mt;
                vm[0] -= ux * RecoilShare * J / mm; vm[1] -= uy * RecoilShare * J / mm;
            }
        }

        // A striking fist within 0.32 m (torso radius plus fist) of the target's pelvis axis, between hip and shoulder height.
        bool FistOnTorso(MujocoLib.mjData_* d, int i, int j, PolicyRunner.StrikeKind kind)
        {
            double* p = d->xpos + 3 * bodies[j].Map.PelvisBodyId;
            for (int s = 0; s < 2; s++)
            {
                if (kind == (s == 0 ? PolicyRunner.StrikeKind.RightJab : PolicyRunner.StrikeKind.LeftJab)) continue;
                int h = _handBody[2 * i + s];
                if (h < 0) continue;
                double* f = d->xpos + 3 * h;
                double hx = f[0] - p[0], hy = f[1] - p[1], hz = f[2] - p[2];
                if (hx * hx + hy * hy < 0.32 * 0.32 && hz > -0.2 && hz < 0.75) return true;
            }
            return false;
        }
    }
}
