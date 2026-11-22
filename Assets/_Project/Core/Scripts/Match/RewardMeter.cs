using System;
using System.Collections.Generic;
using Mujoco;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// The training reward's motion-quality penalties, measured live for every fighter the way Training/koth/env.py
    /// _rewards charges them: on every policy step (50 Hz, PolicyRunner.OnPolicyStep), each term's value times its
    /// RewardWeights weight times the control dt. Terms: joint torque², pelvis roll/pitch angular velocity², action
    /// rate (jitter) ‖a_t − a_t−1‖², foot slip (foot planar speed while it touches the ground) and joint speed².
    /// The positive task terms (push-out, survival) are not mirrored, so the episode value is the penalty part of the
    /// reward. Reset at every round start. Read by the Tab overlay; inert in the statistics gates. Allocation-free after
    /// the first step.
    /// </summary>
    public unsafe class RewardMeter : MonoBehaviour
    {
        public enum Term { JointTorque, AngVel, ActionRate, FootSlip, JointVel }
        public const int TermCount = 5;
        public static readonly string[] TermNames = { "torque²", "ang vel²", "action rate", "foot slip", "joint vel²" };

        /// <summary>Foot speed (m/s) above which a step is a teleport (round reset), not a slip.</summary>
        public const double TeleportSpeed = 25.0;

        public MatchDirector director;
        public RewardWeights weights;

        /// <summary>One fighter's episode so far: total and per-term sums (weighted, × dt), and policy steps counted.</summary>
        public sealed class Episode
        {
            public readonly double[] Sum = new double[TermCount];
            public readonly double[] Raw = new double[TermCount];      // unweighted sums, for tests and the overlay
            public double Total; public int Steps;
            /// <summary>Largest single-step foot slip (m/s) and the step it happened on, for diagnosing teleports.</summary>
            public double MaxSlip; public int MaxSlipStep, SlipSteps;
            internal float[] Prev; internal bool HasPrev; internal int Left = -1, Right = -1; internal double Lx, Ly, Rx, Ry;
            public void Clear() { Array.Clear(Sum, 0, TermCount); Array.Clear(Raw, 0, TermCount); Total = 0; Steps = 0; HasPrev = false; MaxSlip = 0; MaxSlipStep = 0; SlipSteps = 0; }
        }

        readonly Dictionary<PolicyRunner, Episode> _eps = new();
        PolicyRunner[] _runners = Array.Empty<PolicyRunner>();
        bool _ready;

        public static RewardMeter Instance { get; private set; }
        public bool Ready => _ready;
        public Episode Of(PolicyRunner r) => r != null && _eps.TryGetValue(r, out var e) ? e : null;

        void OnEnable() { Instance = this; MatchEvents.RoundStarted += ResetAll; }
        void OnDisable()
        {
            if (Instance == this) Instance = null;
            MatchEvents.RoundStarted -= ResetAll;
            foreach (var r in _runners) if (r != null) r.OnPolicyStep -= OnStep;
            _ready = false;
        }

        public void ResetAll() { foreach (var e in _eps.Values) e.Clear(); }

        void Update()
        {
            if (_ready || director == null || director.statsRounds > 0 || !MjScene.InstanceExists || MjScene.Instance.Model == null) return;
            if (director.bodies.Length == 0 || director.bodies[0] == null || director.bodies[0].Map == null) return;
            if (weights == null) weights = RewardWeights.Current;
            var m = MjScene.Instance.Model;
            _runners = (PolicyRunner[])director.bodies.Clone();
            foreach (var r in _runners)
            {
                if (r == null) continue;
                var e = new Episode { Prev = new float[r.Map.N] };
                e.Left = MujocoLib.mj_name2id(m, (int)MujocoLib.mjtObj.mjOBJ_BODY, r.robotPrefix + "left_ankle_roll_link");
                e.Right = MujocoLib.mj_name2id(m, (int)MujocoLib.mjtObj.mjOBJ_BODY, r.robotPrefix + "right_ankle_roll_link");
                _eps[r] = e;
                r.OnPolicyStep += OnStep;
            }
            _ready = true;
        }

        void OnStep(PolicyRunner r)
        {
            if (!_eps.TryGetValue(r, out var e) || r.Map == null || !MjScene.InstanceExists) return;
            var m = MjScene.Instance.Model; var d = MjScene.Instance.Data; if (m == null || d == null) return;
            var map = r.Map; float dt = r.Spec != null && r.Spec.ctrl_dt > 0 ? r.Spec.ctrl_dt : 0.02f;
            double torque = 0, jvel = 0, rate = 0, slip = 0;
            for (int i = 0; i < map.N; i++)
            {
                double f = d->actuator_force[map.ActId[i]]; torque += f * f;
                double v = d->qvel[map.DofAdr[i]]; jvel += v * v;
            }
            double* w = d->qvel + map.RootDofAdr + 3;      // free joint: angular velocity in the pelvis frame
            double ang = w[0] * w[0] + w[1] * w[1];
            var a = r.LastAction;
            if (e.HasPrev) for (int i = 0; i < a.Length && i < e.Prev.Length; i++) { double k = a[i] - e.Prev[i]; rate += k * k; }
            Array.Copy(a, e.Prev, Math.Min(a.Length, e.Prev.Length));
            if (e.Left >= 0 && e.Right >= 0)
            {
                double* L = d->xpos + 3 * e.Left; double* R = d->xpos + 3 * e.Right;
                if (e.HasPrev)
                {
                    bool lDown = false, rDown = false;
                    for (int c = 0; c < d->ncon; c++)
                    {
                        var con = d->contact + c; int b1 = m->geom_bodyid[con->geom1], b2 = m->geom_bodyid[con->geom2];
                        int foot = b1 == 0 ? b2 : b2 == 0 ? b1 : -1;          // a foot touching the world body (hill, floor)
                        if (foot == e.Left) lDown = true; else if (foot == e.Right) rDown = true;
                    }
                    double ls = Math.Sqrt((L[0] - e.Lx) * (L[0] - e.Lx) + (L[1] - e.Ly) * (L[1] - e.Ly)) / dt;
                    double rs = Math.Sqrt((R[0] - e.Rx) * (R[0] - e.Rx) + (R[1] - e.Ry) * (R[1] - e.Ry)) / dt;
                    // A foot that moved faster than TeleportSpeed was placed, not stepped (a new round writes qpos a policy
                    // step after RoundStarted): training never sees that step, so neither does the meter.
                    if (ls < TeleportSpeed && rs < TeleportSpeed)
                    {
                        if (lDown) slip += ls;
                        if (rDown) slip += rs;
                    }
                }
                e.Lx = L[0]; e.Ly = L[1]; e.Rx = R[0]; e.Ry = R[1];
            }
            if (slip > e.MaxSlip) { e.MaxSlip = slip; e.MaxSlipStep = e.Steps; }
            if (slip > 0) e.SlipSteps++;
            e.HasPrev = true;
            Add(e, Term.JointTorque, torque, weights.jointTorque, dt);
            Add(e, Term.AngVel, ang, weights.angVelXy, dt);
            Add(e, Term.ActionRate, rate, weights.actionRate, dt);
            Add(e, Term.FootSlip, slip, weights.feetSlip, dt);
            Add(e, Term.JointVel, jvel, weights.jointVel, dt);
            e.Steps++;
        }

        static void Add(Episode e, Term t, double value, float weight, float dt)
        {
            double r = weight * value * dt;
            e.Raw[(int)t] += value; e.Sum[(int)t] += r; e.Total += r;
        }
    }
}
