using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>A damped spring that follows an anchor (LifeSigns' soft regions): what lags behind is the jiggle.</summary>
    public struct SoftSpring
    {
        public Vector3 pos, vel; bool _set;

        /// <summary>Advance by dt towards the anchor; returns the lag (pos - anchor), at most maxLag long. A jump of the
        /// anchor (a reset, a replay seek) snaps instead of whipping.</summary>
        public Vector3 Step(Vector3 anchor, float dt, float hz, float damping, float maxLag)
        {
            if (!_set || (anchor - pos).sqrMagnitude > 0.25f) { pos = anchor; vel = Vector3.zero; _set = true; return Vector3.zero; }
            float w = 2f * Mathf.PI * hz; int n = Mathf.Clamp(Mathf.CeilToInt(dt / 0.008f), 1, 8); float h = Mathf.Min(dt, 0.064f) / n;
            for (int i = 0; i < n; i++) { vel += h * (-w * w * (pos - anchor) - 2f * damping * w * vel); pos += h * vel; }
            Vector3 lag = pos - anchor; float m = lag.magnitude;
            if (m > maxLag) { lag *= maxLag / m; pos = anchor + lag; }
            return lag;
        }
    }

    /// <summary>
    /// What makes the humans look alive, render side only (the physics, the brains and the gates never see it):
    ///   look       the head and neck turn to the opponent the brain is pointed at, glance at the drop when the capture
    ///              point nears the rim, to the camera in the tale of the tape and for the winner, down for the beaten
    ///              (the physics model has no neck joint: SkinFollower.Look turns the two bones on screen)
    ///   breath     the rib cage swells, faster and deeper with exertion (a slow follower of Biometrics.strain), and the
    ///              face flushes with it
    ///   soft parts belly, hair and chest lag behind the body on springs (SoftSpring)
    ///   victory    the winner's arms rise in a V once it stands at the centre (SkinFollower.ArmsUp): on the skin only,
    ///              the physics arms stay where the fight brain balances them
    ///   face       the jaw drops and the brow moves with what happens: a hit (pain), the rim or a knock-out (fear), heavy
    ///              strain (a grimace), panting, the win (a shout)
    /// The meshes have no face, hair or cloth bones and no blendshapes, so breath, soft parts and face are vertex offsets in
    /// FX/Fighter.shader, written into each skin's property block next to BodyLook's layers. No eyes move and nothing blinks.
    /// </summary>
    [DefaultExecutionOrder(200)]          // after SkinFollower has placed the bones
    public class LifeSigns : MonoBehaviour
    {
        public MatchDirector director;
        public Biometrics bio;            // optional: strain and the rim
        [Tooltip("How far the head turns from the chest (degrees): side to side, down, up")] public Vector3 lookLimit = new(65f, 35f, 28f);
        [Tooltip("Fastest head turn (degrees per second)")] public float lookSpeed = 260f;
        [Tooltip("Breaths per second at rest and when spent")] public Vector2 breathHz = new(0.24f, 0.95f);
        [Tooltip("Chest swell at rest and when spent (m, for a 1.75 m body)")] public Vector2 breathSwell = new(0.004f, 0.014f);

        /// <summary>How soft each body is: belly, hair, chest (0 = stiff), plus cloth tie/hem springs (#12).</summary>
        static readonly Dictionary<string, Vector3> Softness = new()
        {
            ["kim"] = new Vector3(0.35f, 1f, 0.5f), ["trump"] = new Vector3(1f, 0.8f, 0.5f), ["grandpa"] = new Vector3(0.9f, 0.25f, 0.4f),
        };
        static readonly Dictionary<string, Vector2> ClothSoftness = new()
        {
            ["kim"] = new Vector2(0.45f, 0.85f), ["trump"] = new Vector2(1.0f, 0.90f), ["grandpa"] = new Vector2(0.75f, 0.80f),
        };

        class Fighter
        {
            public SkinFollower skin; public PolicyRunner runner; public Renderer[] renderers; public float k, head;      // k: size against 1.75 m
            public Vector3 mouth; public Vector3 soft; public Vector2 clothSoft;
            public float yaw, pitch, weight, exertion, phase, jaw, brow, noise;
            public float painUntil, pain, fearUntil, triumphUntil, glanceAt;
            public float blink, nextBlink, blinkDur, flinchUntil;
            public float eyeX, eyeY, eyeTargetX, eyeTargetY, nextDart;
            public SoftSpring belly, hair, chest, tie, hem;
        }

        readonly List<Fighter> _all = new(); readonly Dictionary<PolicyRunner, Fighter> _of = new();
        static readonly Dictionary<string, Vector3> MouthOf = new();      // per body kind, in the head frame
        MaterialPropertyBlock _mpb; bool _ready; int _winner = -1; Transform _cam;
        readonly Vector4[] _jigC = new Vector4[5], _jigD = new Vector4[5];
        static readonly int Head = Shader.PropertyToID("_LifeHead"), HeadUp = Shader.PropertyToID("_LifeHeadUp"), Mouth = Shader.PropertyToID("_LifeMouth"),
                            Face = Shader.PropertyToID("_LifeFace"), Eyes = Shader.PropertyToID("_LifeEyes"), Chest = Shader.PropertyToID("_LifeChest"), Breath = Shader.PropertyToID("_LifeBreath"),
                            JigC = Shader.PropertyToID("_LifeJigC"), JigD = Shader.PropertyToID("_LifeJigD");

        // verification (LifeTests, captures)
        public int Fighters => _all.Count;
        public float JawOf(PolicyRunner r) => _of.TryGetValue(r, out var f) ? f.jaw : 0f;
        public float ExertionOf(PolicyRunner r) => _of.TryGetValue(r, out var f) ? f.exertion : 0f;
        public float BlinkOf(PolicyRunner r) => _of.TryGetValue(r, out var f) ? f.blink : 0f;
        public Vector2 EyeDartOf(PolicyRunner r) => _of.TryGetValue(r, out var f) ? new Vector2(f.eyeX, f.eyeY) : Vector2.zero;
        /// <summary>Captures and tuning: hold every face at this jaw, brow and flush (null = live).</summary>
        [NonSerialized] public Vector3? FaceOverride;
        public Vector3 MouthAt(PolicyRunner r) => _of.TryGetValue(r, out var f) ? f.mouth / f.head : Vector3.zero;
        public Vector2 LookOf(PolicyRunner r) => _of.TryGetValue(r, out var f) ? new Vector2(f.yaw, f.pitch) * f.weight : Vector2.zero;

        void OnEnable()
        {
            MatchEvents.Impact += OnImpact; MatchEvents.Down += OnDown; MatchEvents.Knockout += OnKo; MatchEvents.RoundEnded += OnEnd; MatchEvents.RoundStarted += OnRound;
        }
        void OnDisable()
        {
            MatchEvents.Impact -= OnImpact; MatchEvents.Down -= OnDown; MatchEvents.Knockout -= OnKo; MatchEvents.RoundEnded -= OnEnd; MatchEvents.RoundStarted -= OnRound;
        }

        void OnImpact(Vector3 p, float strength, PolicyRunner r)
        {
            if (strength < 0.3f || !_of.TryGetValue(r, out var f)) return;
            f.pain = Mathf.Max(f.pain, strength); f.painUntil = Time.time + 0.45f;
            f.flinchUntil = Time.time + 0.22f;
        }
        void OnDown(PolicyRunner r) { if (_of.TryGetValue(r, out var f)) { f.pain = 0.8f; f.painUntil = Time.time + 0.8f; } }
        void OnKo(PolicyRunner r) { if (_of.TryGetValue(r, out var f)) f.fearUntil = Time.time + 1.6f; }
        void OnRound() { _winner = -1; foreach (var f in _all) { f.painUntil = f.fearUntil = f.triumphUntil = 0f; } }
        void OnEnd(RoundResult res)
        {
            _winner = res.Winner;
            if (res.Winner >= 0 && res.Winner < director.bodies.Length && _of.TryGetValue(director.bodies[res.Winner], out var f)) f.triumphUntil = Time.time + 3.5f;
        }

        bool Init()
        {
            var skins = FindObjectsByType<SkinFollower>(FindObjectsInactive.Exclude);
            foreach (var s in skins) if (!s.Ready) return false;           // their Start has not run yet
            _mpb = new MaterialPropertyBlock();
            foreach (var s in skins)
            {
                var runner = Array.Find(director.bodies, b => b.robotPrefix == s.robotPrefix);
                if (runner == null || s.HeadBone == null || s.skinRoot == null) continue;
                var f = new Fighter { skin = s, runner = runner, renderers = s.skinRoot.GetComponentsInChildren<Renderer>(), k = s.Stature / 1.75f, head = 0.135f * s.Stature,
                                      soft = Softness.TryGetValue(runner.body ?? "", out var so) ? so : new Vector3(0.3f, 0.3f, 0.3f),
                                      clothSoft = ClothSoftness.TryGetValue(runner.body ?? "", out var cs) ? cs : new Vector2(0.5f, 0.6f),
                                      noise = _all.Count * 7.31f, phase = _all.Count * 1.7f,
                                      nextBlink = 1.5f + (_all.Count % 5) * 0.65f, blinkDur = 0.14f, nextDart = 0.6f + (_all.Count % 4) * 0.35f };
                if (!MouthOf.TryGetValue(runner.body ?? "", out f.mouth)) { f.mouth = FindMouth(f); MouthOf[runner.body ?? ""] = f.mouth; }
                _all.Add(f); _of[runner] = f;
            }
            return true;
        }

        static Quaternion Frame(Fighter f) => f.skin.TorsoBody.rotation;      // x forward, y up, z left (MuJoCo x, z, y)

        // The mouth in the head frame, from the mesh in its current pose (the head is rigid, the look is still zero).
        // Hair makes the crown a poor yardstick (Trump's adds centimetres), so the face is read from its profile: the
        // nose tip is the point furthest forward between a third and 85 % of a head below the crown,
        // the mouth 10.5 % of a head below it, on the face's front (checked on close-ups, SCREENSHOTS/life-261114).
        // Falls back to proportions when the mesh cannot be read (a player build may have dropped its CPU copy).
        static Vector3 FindMouth(Fighter f)
        {
            float s = f.head; var fallback = new Vector3(0.5f * s, 0.16f * s, 0f);
            try
            {
                var toHead = Matrix4x4.TRS(f.skin.HeadBone.position, Frame(f), Vector3.one).inverse;
                var mesh = new Mesh(); var pts = new List<Vector3>(); var verts = new List<Vector3>();
                foreach (var r in f.renderers)
                {
                    if (r is not SkinnedMeshRenderer smr || smr.sharedMesh == null) continue;
                    smr.BakeMesh(mesh, true); mesh.GetVertices(verts); var m = toHead * smr.transform.localToWorldMatrix;
                    foreach (var v in verts) { var h = m.MultiplyPoint3x4(v); if (h.y > -0.6f * s && h.y < 1.6f * s && h.x * h.x + h.z * h.z < s * s) pts.Add(h); }
                }
                Destroy(mesh);
                if (pts.Count < 50) return fallback;
                float crown = float.MinValue; foreach (var h in pts) if (h.x * h.x + h.z * h.z < 0.36f * s * s) crown = Mathf.Max(crown, h.y);
                float noseX = float.MinValue, noseY = 0f, noseZ = 0f;       // a scan's head may rest turned (Grandpa's): the nose need not be on the body's centre line
                foreach (var h in pts)
                    if (Mathf.Abs(h.z) < 0.25f * s && h.y < crown - 0.35f * s && h.y > crown - 0.85f * s && h.x > noseX) { noseX = h.x; noseY = h.y; noseZ = h.z; }
                float y = noseY - 0.105f * s, z = 0.85f * noseZ, x = float.MinValue;
                foreach (var h in pts) if (Mathf.Abs(h.y - y) < 0.03f * s && Mathf.Abs(h.z - z) < 0.06f * s) x = Mathf.Max(x, h.x);
                if (noseX == float.MinValue || x < 0.15f * s || x > 1.2f * s || crown < 0.4f * s || crown > 1.6f * s) return fallback;
                return new Vector3(x, y, z);
            }
            catch (Exception e) { Debug.LogWarning("[LifeSigns] the face of " + f.runner.body + " is placed by proportion: " + e.Message); return fallback; }
        }

        /// <summary>Yaw (to the left) and pitch (up) in degrees from a body frame to a point, limited to what a neck does.
        /// Something behind the shoulders is not looked at (zero).</summary>
        public static Vector2 LookAngles(Quaternion frame, Vector3 from, Vector3 to, Vector3 limit)
        {
            Vector3 v = Quaternion.Inverse(frame) * (to - from);
            if (v.sqrMagnitude < 1e-6f) return Vector2.zero;
            float yaw = Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg, pitch = Mathf.Atan2(v.y, Mathf.Sqrt(v.x * v.x + v.z * v.z)) * Mathf.Rad2Deg;
            if (Mathf.Abs(yaw) > 115f) return Vector2.zero;
            return new Vector2(Mathf.Clamp(yaw, -limit.x, limit.x), Mathf.Clamp(pitch, -limit.y, limit.z));
        }

        void LateUpdate()
        {
            if (director == null || director.State < MatchState.Menu) return;
            if (!_ready) { _ready = Init(); return; }
            bool gate = director.statsRounds > 0; float dt = Time.deltaTime, now = Time.time;
            if (_cam == null && Camera.main != null) _cam = Camera.main.transform;
            var state = director.State;
            foreach (var f in _all)
            {
                var torso = f.skin.TorsoBody; int seat = Array.IndexOf(director.bodies, f.runner);
                bool seated = seat >= 0 && seat < director.Playing, parked = torso.position.y > 20f;
                if (gate || parked) { f.skin.Look = Quaternion.identity; f.skin.ArmsUp = 0f; f.weight = 0f; continue; }
                // the winner's arms go up once it stands at the centre; they drop when the next round places it afresh
                f.skin.ArmsUp = f.runner.After == PolicyRunner.PostRound.Victory && director.RoundOver ? f.runner.PostRoundBlend : 0f;
                bool isOut = seated && director.IsOut(seat), down = seated && director.IsDown(seat), fight = state == MatchState.Fight && seated && !isOut;
                var vit = bio != null && seated ? bio.Of(f.runner) : null;
                Quaternion frame = Frame(f); Vector3 headPos = f.skin.HeadBone.position;

                // ---- look
                Vector2 want = Vector2.zero; float weight = 0f;
                if (fight && !down)
                {
                    weight = 1f;
                    bool edge = vit != null && vit.rim < 0.3f;
                    if (edge && now > f.glanceAt + 1.8f) f.glanceAt = now;
                    if (edge && now < f.glanceAt + 0.6f)
                    {
                        // the drop: the ground a body length further out
                        Vector3 p = f.skin.PelvisBody.position, flat = new(p.x, 0f, p.z);
                        want = LookAngles(frame, headPos, p + flat.normalized * (1.2f * f.k) - Vector3.up * (0.9f * f.k), lookLimit);
                    }
                    else if (f.runner.opponent != null && _of.TryGetValue(f.runner.opponent, out var o)) want = LookAngles(frame, headPos, o.skin.HeadBone.position, lookLimit);
                }
                else if (!isOut && !down && seated && (state == MatchState.Intro || (director.RoundOver && seat == _winner)))
                { weight = 1f; if (_cam != null) want = LookAngles(frame, headPos, _cam.position, lookLimit); }
                else if (!isOut && !down && seated && director.RoundOver) { weight = 1f; want = new Vector2(0f, -22f); }       // beaten: eyes on the ground
                else if (!isOut && !down) { weight = 0.6f; want = new Vector2(18f * (Mathf.PerlinNoise(now * 0.11f, f.noise) - 0.5f) * 2f, 0f); }   // menu: looking around
                // small movements a held head still makes
                want += new Vector2(Mathf.PerlinNoise(now * 0.7f, f.noise) - 0.5f, Mathf.PerlinNoise(f.noise, now * 0.6f) - 0.5f) * 5f;
                float ease = 1f - Mathf.Exp(-dt / 0.1f);
                f.yaw = Mathf.MoveTowards(f.yaw, Mathf.Lerp(f.yaw, want.x, ease), lookSpeed * dt);
                f.pitch = Mathf.MoveTowards(f.pitch, Mathf.Lerp(f.pitch, want.y, ease), lookSpeed * dt);
                f.weight = Mathf.MoveTowards(f.weight, weight, dt / 0.35f);

                // ---- breath
                float strain = fight && vit != null ? Mathf.Clamp01((vit.strain - 0.15f) / 0.45f) : 0f;
                f.exertion += (strain - f.exertion) * Mathf.Clamp01(dt / (strain > f.exertion ? 6f : 14f));
                f.phase += 2f * Mathf.PI * Mathf.Lerp(breathHz.x, breathHz.y, f.exertion) * dt; if (f.phase > 2f * Mathf.PI) f.phase -= 2f * Mathf.PI;
                float inhale = 0.5f - 0.5f * Mathf.Cos(f.phase), swell = isOut ? 0f : Mathf.Lerp(breathSwell.x, breathSwell.y, f.exertion) * f.k * inhale;
                Quaternion look = frame * Quaternion.AngleAxis(-f.yaw * f.weight, Vector3.up) * Quaternion.AngleAxis((f.pitch * f.weight) + 1.5f * inhale * f.exertion, Vector3.forward) * Quaternion.Inverse(frame);
                f.skin.Look = look;

                // ---- face
                float jaw = 0.22f * f.exertion * inhale, brow = 0f;
                float grim = vit != null && fight ? Mathf.Clamp01((vit.strain - 0.35f) / 0.4f) : 0f;
                if (grim > 0f) { jaw = Mathf.Max(jaw, 0.18f * grim); brow = -0.8f * grim; }
                if (fight && vit != null && vit.doomed) { jaw = Mathf.Max(jaw, 0.55f); brow = 0.8f; }
                if (now < f.painUntil) { jaw = Mathf.Max(jaw, 0.45f + 0.4f * f.pain); brow = -1f; } else f.pain = 0f;
                if (now < f.fearUntil) { jaw = 1f; brow = 1f; }
                if (now < f.triumphUntil) { jaw = Mathf.Max(jaw, 0.55f + 0.3f * Mathf.Sin(now * 9f)); brow = 0.6f; }
                f.jaw += (jaw - f.jaw) * Mathf.Clamp01(dt * 14f); f.brow += (brow - f.brow) * Mathf.Clamp01(dt * 8f);

                // ---- eyes: spontaneous blinking, hit flinch and micro-saccades (#11)
                Vector3 fwd = frame * Vector3.right, up = frame * Vector3.up, chest = f.skin.ChestBone.position, pelvis = f.skin.PelvisBody.position;
                if (now >= f.nextBlink)
                {
                    f.blinkDur = 0.12f + 0.06f * Mathf.PerlinNoise(f.noise, now);
                    f.nextBlink = now + 2.2f + 3.1f * Mathf.PerlinNoise(now * 0.37f, f.noise);
                }
                float autoBlink = (f.nextBlink - now > -f.blinkDur && f.nextBlink - now <= 0f)
                    ? Mathf.Sin(Mathf.Clamp01(-(f.nextBlink - now) / Mathf.Max(0.05f, f.blinkDur)) * Mathf.PI) : 0f;
                float wantBlink = now < f.flinchUntil ? 0.92f : (now < f.painUntil ? 0.55f : (now < f.fearUntil ? 0f : autoBlink));
                f.blink = Mathf.MoveTowards(f.blink, wantBlink, dt * 16f);

                if (now >= f.nextDart)
                {
                    float s1 = Mathf.PerlinNoise(now * 1.7f, f.noise) * 2f - 1f;
                    float s2 = Mathf.PerlinNoise(f.noise, now * 1.9f) * 2f - 1f;
                    f.eyeTargetX = Mathf.Clamp(s1 * 0.9f, -1f, 1f);
                    f.eyeTargetY = Mathf.Clamp(s2 * 0.55f, -0.7f, 0.7f);
                    f.nextDart = now + (fight ? 0.35f : 0.95f) + 0.9f * Mathf.PerlinNoise(now, f.noise * 2f);
                }
                f.eyeX = Mathf.Lerp(f.eyeX, f.eyeTargetX, Mathf.Clamp01(dt * 24f));
                f.eyeY = Mathf.Lerp(f.eyeY, f.eyeTargetY, Mathf.Clamp01(dt * 24f));
                float hairWet = Mathf.Clamp01(f.exertion * 0.75f + (director.sea != null && pelvis.y < director.sea.Level ? 1f : 0f));

                // ---- soft parts: anchors on the body + cloth/tie/hem springs (#12)
                Vector3 bellyAt = Vector3.Lerp(pelvis, chest, 0.45f) + fwd * (0.10f * f.k), hairAt = headPos + (look * up) * (0.62f * f.head) - (look * fwd) * (0.03f * f.k),
                        chestAt = chest + fwd * (0.08f * f.k);
                Vector3 tieAt = Vector3.Lerp(chest, pelvis, 0.38f) + fwd * (0.14f * f.k),
                        hemAt = pelvis - up * (0.14f * f.k);
                _jigC[0] = Region(bellyAt, 0.17f * f.k, f.soft.x); _jigD[0] = f.belly.Step(bellyAt, dt, 3.2f, 0.28f, 0.035f * f.k) * f.soft.x;
                _jigC[1] = Region(hairAt, 0.15f * f.k, f.soft.y); _jigD[1] = f.hair.Step(hairAt, dt, 4.5f, 0.32f, 0.025f * f.k) * f.soft.y;
                _jigC[2] = Region(chestAt, 0.13f * f.k, f.soft.z); _jigD[2] = f.chest.Step(chestAt, dt, 4f, 0.35f, 0.02f * f.k) * f.soft.z;
                _jigC[3] = Region(tieAt, 0.14f * f.k, f.clothSoft.x); _jigD[3] = f.tie.Step(tieAt, dt, 3.4f, 0.22f, 0.055f * f.k) * f.clothSoft.x;
                _jigC[4] = Region(hemAt, 0.22f * f.k, f.clothSoft.y); _jigD[4] = f.hem.Step(hemAt, dt, 3.8f, 0.25f, 0.045f * f.k) * f.clothSoft.y;

                var toHead = Matrix4x4.TRS(headPos, look * frame, Vector3.one).inverse; Vector3 headUp = look * up;
                foreach (var r in f.renderers)
                {
                    r.GetPropertyBlock(_mpb);
                    _mpb.SetMatrix(Head, toHead); _mpb.SetVector(HeadUp, headUp); _mpb.SetVector(Mouth, new Vector4(f.mouth.x, f.mouth.y, f.mouth.z, f.head));
                    _mpb.SetVector(Face, FaceOverride is Vector3 fo ? new Vector4(fo.x, fo.y, fo.z, 0f) : new Vector4(f.jaw, f.brow, f.exertion, 0f));
                    _mpb.SetVector(Eyes, new Vector4(f.blink, f.eyeX, f.eyeY, hairWet));
                    _mpb.SetVector(Chest, new Vector4(chestAt.x, chestAt.y, chestAt.z, 0.21f * f.k)); _mpb.SetVector(Breath, new Vector4(fwd.x, fwd.y, fwd.z, swell));
                    _mpb.SetVectorArray(JigC, _jigC); _mpb.SetVectorArray(JigD, _jigD);
                    r.SetPropertyBlock(_mpb);
                }
            }
        }

        static Vector4 Region(Vector3 centre, float radius, float softness) => new(centre.x, centre.y, centre.z, softness > 0f ? radius : 0f);
    }
}
