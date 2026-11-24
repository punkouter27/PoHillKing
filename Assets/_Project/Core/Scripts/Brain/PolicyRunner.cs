using System.Collections.Generic;
using System.Linq;
using Mujoco;
using Unity.InferenceEngine;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// One policy driving one robot. Runs inference once every <c>decimation</c> physics steps from
    /// MjScene.preUpdateEvent (never from Update), writes ctrl = default + scale * action into mjData.ctrl and
    /// holds it between policy ticks. With no model assigned it writes the gravity-compensated hold ctrl
    /// (HoldCtrl) so the robot stands passively: that is the zero-brain parity test.
    /// </summary>
    [DefaultExecutionOrder(-100)]   // subscribes to MjScene.postInitEvent first: sets ls_iterations and the keyframe before other listeners read the model
    public unsafe class PolicyRunner : MonoBehaviour
    {
        public TextAsset jointMapJson;
        [Tooltip("a_ or b_")] public string robotPrefix = "a_";
        [Tooltip("Which kind of body this is (g1, kim): a brain only fits the body it was trained on")] public string body = "g1";
        [Tooltip("ONNX policy. Leave empty for passive hold.")] public ModelAsset policy;
        [Tooltip("Optional <name>_action.json beside the policy (Training/koth/action_map.py): tanh map, per-joint scale, torque window. Empty = the original clamp path.")]
        public TextAsset actionMap;
        public Vector3 command;   // vx, vy, yaw rate
        [Tooltip("None: use the command field. Center/Opponent: command is computed every tick by GoalCommand (same law as training).")]
        public GoalMode goal = GoalMode.None;
        public float goalStopDist = 0.3f, goalVmax = 0.8f;
        public PolicyRunner opponent;
        [Tooltip("Second-nearest fighter still in, for 121-input free-for-all brains (null = the opponent again)")] public PolicyRunner opponent2;
        [Tooltip("Network input size: 103 = locomotion state, 112 = state + 9 opponent/ring values (combat policies).")]
        public int policyObsDim = ObsBuilder.ObsDim;
        public bool paused;
        [Tooltip("Optional get-up brain (103 inputs, zero command; training rung getup) that takes over while the fighter is down")]
        public ModelAsset getupPolicy;
        [Tooltip("Shove style (mirror of SHOVE_* in Training/koth/env.py): the arm rest pose moves to hands-forward as the opponent closes from 1.6 m to 1.0 m, and the action is low-passed before it becomes a joint target.")]
        public bool shoveStyle;
        [Tooltip("The styled rest pose (mirror of STYLE_POSES in Training/koth/obs.py): shove = arms forward (G1), bull = Trump's charge.")]
        public string stylePose = "shove";
        public const float ShoveFar = 1.6f, ShoveNear = 1.0f, ShoveSmooth = 0.5f;
        // joint-name substring -> target angle; the first key a joint's name contains wins, as in Python's dict order
        static readonly Dictionary<string, (string key, float target)[]> StylePoses = new()
        {
            ["shove"] = new[] { ("shoulder_pitch", -1.0f), ("elbow", 0.5f) },
            ["bull"] = new[] { ("waist_pitch", 0.30f), ("shoulder_pitch", 0.35f), ("elbow", 0.60f), ("knee", 0.40f), ("hip_pitch", -0.30f), ("ankle_pitch", -0.10f) },
        };

        /// <summary>What a fighter does once the round is over (MatchDirector.EndRound; never in a statistics gate, never
        /// in training). Victory: the fight brain walks the winner to the middle of the summit and stands there on a zero
        /// command, which it was trained to do. Defeat: it stands where it is. The joint targets are the brain's own
        /// throughout: the raised arms and the hung head are drawn on the skin (SkinFollower.ArmsUp, LifeSigns), because
        /// neither the brain nor a plain joint hold kept a fighter on its feet with its arms really up (8 falls in 8
        /// trials, DOCS/20261114/lifelike-features.md).</summary>
        public enum PostRound { None, Victory, Defeat }
        // the walk ends VictoryRadius from the centre or after VictoryWait s; the celebration then comes in over PostRoundEase s
        public const float PostRoundEase = 1.2f, VictoryRadius = 0.7f, VictoryVmax = 0.6f, VictoryWait = 2.5f;
        public PostRound After => _after;
        /// <summary>How far the winner's celebration is in, 0..1 (it starts when the walk to the centre is over).</summary>
        public float PostRoundBlend => _afterBlend;
        PostRound _after; float _afterBlend; int _afterTicks;

        public void SetPostRound(PostRound mode) { if (mode != _after) { _after = mode; _afterBlend = 0f; _afterTicks = 0; } }

        /// <summary>A scripted arm strike laid over the brain's joint targets (the game only: MatchDirector.Shoves starts
        /// them, never in a statistics gate or in training). Shove = both arms, Jab = one. The arm is cocked (elbow bent,
        /// shoulder back), thrust straight forward, held for a moment and returned to the brain's own targets. Only the
        /// shoulder pitch and elbow of the striking arm(s) are touched, and only in what is written to mjData.ctrl: the
        /// brain's held targets (Ctrl) are unchanged, so its next tick carries on from its own plan.</summary>
        public enum StrikeKind { None, Shove, LeftJab, RightJab }
        public const float StrikeWindup = 0.10f, StrikeThrust = 0.12f, StrikeHold = 0.10f, StrikeRecover = 0.22f;
        public const float StrikeLength = StrikeWindup + StrikeThrust + StrikeHold + StrikeRecover;
        // joint targets (rad) for the cocked and the extended arm: shoulder pitch < 0 lifts the arm forward, elbow > 0 bends it
        public const float CockPitch = 0.25f, CockElbow = 1.6f, ReachPitch = -1.45f, ReachElbow = 0.1f;
        public StrikeKind Strike => _strike;
        /// <summary>Seconds into the current strike (0 with none).</summary>
        public float StrikeTime => _strikeT;
        /// <summary>The fist is going out or held out: the window in which a strike can land (MatchDirector.Shoves).</summary>
        public bool StrikeLive => _strike != StrikeKind.None && _strikeT >= StrikeWindup && _strikeT < StrikeWindup + StrikeThrust + StrikeHold;
        StrikeKind _strike; float _strikeT; int[] _armJ;   // _armJ: left pitch, left elbow, right pitch, right elbow

        /// <summary>Start a strike; false while one is still running, while the get-up brain drives, after the round.</summary>
        public bool BeginStrike(StrikeKind kind)
        {
            if (kind == StrikeKind.None || _strike != StrikeKind.None || _getup || _after != PostRound.None || Map == null || _armJ == null) return false;
            _strike = kind; _strikeT = 0f;
            return true;
        }

        /// <summary>How far the striking arm is laid over the brain (0..1) and the pitch/elbow target at time t of a strike.</summary>
        public static float StrikePose(float t, out float pitch, out float elbow)
        {
            static float S(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
            float t1 = StrikeWindup, t2 = t1 + StrikeThrust, t3 = t2 + StrikeHold;
            if (t < t1) { pitch = CockPitch; elbow = CockElbow; return S(t / t1); }
            float k = S((t - t1) / StrikeThrust);
            pitch = Mathf.Lerp(CockPitch, ReachPitch, k); elbow = Mathf.Lerp(CockElbow, ReachElbow, k);
            if (t < t3) return 1f;
            return 1f - S((t - t3) / StrikeRecover);
        }

        public JointMap Map { get; private set; }
        public JointMapSpec Spec => _spec;
        /// <summary>The pelvis body's GameObject transform (moved by the MuJoCo plugin): what cameras and effects follow.</summary>
        public Transform PelvisTransform { get { if (_pelvisT == null) { var go = GameObject.Find(robotPrefix + "pelvis"); if (go != null) _pelvisT = go.transform; } return _pelvisT; } }
        Transform _pelvisT;
        public float[] Obs { get; private set; } = new float[ObsBuilder.ObsDim];
        public float[] LastAction { get; private set; }
        public float[] RawAction { get; private set; }   // network output before clamping
        public double[] Ctrl { get; private set; }        // held position targets, canonical order
        public event System.Action<PolicyRunner> OnPolicyStep;
        public double LastInferenceMs { get; private set; }
        public int NanInfCount { get; private set; }
        public void ResetTelemetry() => NanInfCount = 0;

        /// <summary>Append tail features to a pre-sized zero-initialized observation vector without reordering existing prefix inputs.</summary>
        public static void AppendZeroInitInputs(float[] src, int baseCount, float[] dst)
        {
            if (dst == null || dst.Length <= baseCount || src == null) return;
            System.Array.Clear(dst, baseCount, dst.Length - baseCount);
            int copy = System.Math.Min(src.Length, dst.Length - baseCount);
            if (copy > 0) System.Array.Copy(src, 0, dst, baseCount, copy);
        }
        /// <summary>The get-up brain is driving (SetGetUp).</summary>
        public bool GettingUp => _getup;
        /// <summary>Per-joint shove/style rest-pose offset (target - default), for rollouts that mirror PolicyTick.</summary>
        public float[] StyleOffsets => _shoveOffset;
        public float GaitPhase => _phase;
        /// <summary>The shove style's low-passed action (rollouts copy it to continue the filter).</summary>
        public float[] FilteredAction => _actF;
        /// <summary>The fight brain's action path (null = the original clamp path), for rollouts that mirror PolicyTick.</summary>
        public ActionMap Action => _amap;
        ActionMap _amap;

        JointMapSpec _spec;
        Worker _worker, _getupWorker;
        Tensor<float> _input, _getupInput; float[] _getupObs; bool _getup;
        static readonly float[] ZeroCommand = new float[3];
        int _substep;
        float _phase;
        readonly float[] _cmd = new float[3];
        float[] _actF, _shoveOffset;
        // action_map.py slew / delay / breath / fatigue (all off without a sidecar that asks for them)
        float[] _actS, _fatigue, _obsRing, _obsDelayed;
        double[] _frc0;                    // each actuator's nominal force range (lo, hi), for fatigue
        int _ringHead, _ticks, _waistPitch = -1;
        bool _ringFill = true, _fatigueFresh = true, _fatigueApplied;
        readonly System.Diagnostics.Stopwatch _sw = new();

        void OnEnable()
        {
            _spec = JointMap.LoadSpec(jointMapJson);
            LastAction = new float[_spec.joints.Length];
            RawAction = new float[_spec.joints.Length]; Ctrl = new double[_spec.joints.Length];
            _actF = new float[_spec.joints.Length]; _shoveOffset = new float[_spec.joints.Length];
            _actS = new float[_spec.joints.Length]; _fatigue = new float[_spec.joints.Length];
            _waistPitch = System.Array.FindIndex(_spec.joints, j => j.Contains("waist_pitch"));
            _armJ = new[] { "left_shoulder_pitch", "left_elbow", "right_shoulder_pitch", "right_elbow" }
                .Select(n => System.Array.FindIndex(_spec.joints, j => j.Contains(n))).ToArray();
            if (System.Array.IndexOf(_armJ, -1) >= 0) _armJ = null;   // a body without these joints cannot strike
            SetStyleOffsets();
            LoadBrain();
            MjScene.Instance.postInitEvent += OnSceneInit;
            MjScene.Instance.preUpdateEvent += OnPreStep;
            if (MjScene.Instance.Model != null) OnSceneInit(null, null);
        }

        void OnDisable()
        {
            if (MjScene.InstanceExists)
            {
                MjScene.Instance.postInitEvent -= OnSceneInit;
                MjScene.Instance.preUpdateEvent -= OnPreStep;
            }
            _worker?.Dispose(); _input?.Dispose(); _getupWorker?.Dispose(); _getupInput?.Dispose(); _getupWorker = null; _getupInput = null;
        }

        void OnSceneInit(object sender, MjStepArgs args)
        {
            Map = new JointMap(MjScene.Instance.Model, _spec, robotPrefix);
            var m = MjScene.Instance.Model; _frc0 = new double[2 * Map.N]; _fatigueApplied = false;
            for (int i = 0; i < Map.N; i++) { _frc0[2 * i] = m->actuator_forcerange[2 * Map.ActId[i]]; _frc0[2 * i + 1] = m->actuator_forcerange[2 * Map.ActId[i] + 1]; }
            // The plugin's MjGlobalSettings has no ls_iterations field; mirror the training value directly.
            if (_spec.ls_iterations > 0) MjScene.Instance.Model->opt.ls_iterations = _spec.ls_iterations;
            if (Mathf.Abs(Time.fixedDeltaTime - _spec.sim_dt) > 1e-6f)
                Debug.LogError($"Fixed Timestep {Time.fixedDeltaTime} != training sim_dt {_spec.sim_dt}");
            ResetPose();   // the plugin cannot import keyframes: start from the training keyframe explicitly
        }

        void OnPreStep(object sender, MjStepArgs args)
        {
            if (Map == null || paused) return;
            if (PolicyBatcher.Active) return;
            var d = MjScene.Instance.Data;
            if (_substep++ % _spec.decimation == 0) PolicyTick(d);
            WriteCtrl(d);
        }

        internal void WriteCtrl(MujocoLib.mjData_* d)
        {
            // MjActuator.OnSyncState writes its own Control field (0) into mjData.ctrl after every mj_step, so the
            // held targets must be rewritten before every physics step, not only on policy ticks.
            for (int i = 0; i < Map.N; i++) d->ctrl[Map.ActId[i]] = Ctrl[i];
            if (_strike == StrikeKind.None) return;
            if (_getup || _after != PostRound.None || _strikeT >= StrikeLength) { _strike = StrikeKind.None; _strikeT = 0f; return; }
            float w = StrikePose(_strikeT, out float pitch, out float elbow);
            for (int side = 0; side < 2; side++)
            {
                if (_strike == (side == 0 ? StrikeKind.RightJab : StrikeKind.LeftJab)) continue;
                int p = _armJ[2 * side], e = _armJ[2 * side + 1];
                d->ctrl[Map.ActId[p]] = System.Math.Clamp(Ctrl[p] + w * (pitch - Ctrl[p]), Map.CtrlMin[p], Map.CtrlMax[p]);
                d->ctrl[Map.ActId[e]] = System.Math.Clamp(Ctrl[e] + w * (elbow - Ctrl[e]), Map.CtrlMin[e], Map.CtrlMax[e]);
            }
            _strikeT += _spec.sim_dt;
        }

        internal bool PrepareBatchedTick(MujocoLib.mjData_* d, out ModelAsset asset, out float[] obsVec, out int obsDim, out bool isGetup)
        {
            asset = null; obsVec = null; obsDim = 0; isGetup = false;
            if (Map == null || paused || (_substep++ % _spec.decimation != 0)) return false;
            if (_getup && getupPolicy != null)
            {
                command = Vector3.zero;
                _getupObs ??= new float[ObsBuilder.ObsDim];
                ObsBuilder.Fill(_getupObs, d, Map, ZeroCommand, LastAction, _phase);
                asset = getupPolicy; obsVec = _getupObs; obsDim = ObsBuilder.ObsDim; isGetup = true;
                return true;
            }
            if (policy == null)
            {
                for (int i = 0; i < Map.N; i++) Ctrl[i] = _spec.hold_ctrl[i];
                return false;
            }
            BuildFightObs(d);
            asset = policy;
            obsVec = _obsRing != null ? DelayedObs() : Obs;
            obsDim = policyObsDim;
            return true;
        }

        internal void ApplyBatchedAction(MujocoLib.mjData_* d, System.ReadOnlySpan<float> act, bool isGetup, double elapsedMs)
        {
            LastInferenceMs = elapsedMs;
            InferenceLog.Note(isGetup ? getupPolicy : policy, body, "CPU worker, batched");
            if (isGetup) ApplyGetUpAction(act);
            else ApplyFightAction(d, act);
        }

        void PolicyTick(MujocoLib.mjData_* d)
        {
            if (_getup && _getupWorker != null) { GetUpTick(d); return; }
            if (_worker == null)
            {
                for (int i = 0; i < Map.N; i++) Ctrl[i] = _spec.hold_ctrl[i];
                return;
            }
            BuildFightObs(d);
            _sw.Restart();
            _input.Upload(_obsRing != null ? DelayedObs() : Obs);
            _worker.Schedule(_input);
            // CPU backend: wait for the jobs, then read the worker's own output in place (no clone, no managed array).
            var output = _worker.PeekOutput() as Tensor<float>;
            output.CompleteAllPendingOperations();
            var act = output.AsReadOnlySpan();
            LastInferenceMs = _sw.Elapsed.TotalMilliseconds;
            InferenceLog.Note(policy, body, "CPU worker");
            ApplyFightAction(d, act);
        }

        void BuildFightObs(MujocoLib.mjData_* d)
        {
            _cmd[0] = command.x; _cmd[1] = command.y; _cmd[2] = command.z;
            if (_after == PostRound.Victory)
            {
                double* q = d->qpos + Map.RootQposAdr; double r = System.Math.Sqrt(q[0] * q[0] + q[1] * q[1]);
                GoalCommand.Compute(q + 3, -q[0], -q[1], 0.35f, Mathf.Min(goalVmax, VictoryVmax), _cmd);
                if (r < VictoryRadius || _afterBlend > 0f || ++_afterTicks * _spec.ctrl_dt > VictoryWait)
                { _cmd[0] = _cmd[1] = _cmd[2] = 0f; _afterBlend = Mathf.Min(1f, _afterBlend + _spec.ctrl_dt / PostRoundEase); }
                command = new Vector3(_cmd[0], _cmd[1], _cmd[2]);
            }
            else if (_after == PostRound.Defeat) { _cmd[0] = _cmd[1] = _cmd[2] = 0f; command = Vector3.zero; }
            else if (goal != GoalMode.None)
            {
                double* q = d->qpos + Map.RootQposAdr; double gx = -q[0], gy = -q[1]; float stop = goalStopDist;
                if (goal == GoalMode.Opponent && opponent != null && opponent.Map != null)
                {
                    double* o = d->qpos + opponent.Map.RootQposAdr; gx = o[0] - q[0]; gy = o[1] - q[1];
                }
                GoalCommand.Compute(q + 3, gx, gy, stop, goalVmax, _cmd);
                command = new Vector3(_cmd[0], _cmd[1], _cmd[2]);
            }
            var fat = _amap != null && _frc0 != null ? _amap.Fatigue : null;
            if (fat != null) { if (!_fatigueFresh) UpdateFatigue(d, fat); _fatigueFresh = false; }
            int core = policyObsDim - (fat != null && fat.Obs ? Map.N : 0);      // fatigue inputs come last (env.py _observe)
            ObsBuilder.Fill(Obs, d, Map, _cmd, LastAction, _phase);
            if (core > ObsBuilder.ObsDim && opponent != null && opponent.Map != null)
            {
                ObsBuilder.FillCombat(Obs, ObsBuilder.ObsDim, d, Map, opponent.Map);
                // free-for-all brains (121 inputs): the same 9 values again for the second-nearest fighter still in,
                // or for the opponent again when it is the only one left (Training/koth/env.py _refresh_nbr)
                if (core >= ObsBuilder.ObsDim + 18)
                    ObsBuilder.FillCombat(Obs, ObsBuilder.ObsDim + 9, d, Map, opponent2 != null && opponent2.Map != null ? opponent2.Map : opponent.Map);
            }
            if (core < policyObsDim) System.Array.Copy(_fatigue, 0, Obs, core, Map.N);
        }

        void ApplyFightAction(MujocoLib.mjData_* d, System.ReadOnlySpan<float> act)
        {
            var fat = _amap != null && _frc0 != null ? _amap.Fatigue : null;
            float blend = 0f;
            if (shoveStyle && _after == PostRound.None && opponent != null && opponent.Map != null)
            {
                double* me = d->qpos + Map.RootQposAdr; double* op = d->qpos + opponent.Map.RootQposAdr;
                double dist = System.Math.Sqrt((op[0] - me[0]) * (op[0] - me[0]) + (op[1] - me[1]) * (op[1] - me[1]));
                blend = Mathf.Clamp01((ShoveFar - (float)dist) / (ShoveFar - ShoveNear));
            }
            float slew = _amap != null ? _amap.Slew : 0f;
            double breath = _amap != null && _amap.Breath > 0f ? _amap.Breath * System.Math.Sin(2.0 * System.Math.PI * _amap.BreathHz * _ticks * _spec.ctrl_dt) : 0.0;
            for (int i = 0; i < Map.N; i++)
            {
                float raw = act[i];
                if (!float.IsFinite(raw)) { NanInfCount++; raw = 0f; }
                RawAction[i] = raw;
                float a = _amap != null ? _amap.ToAction(raw) : Mathf.Clamp(raw, -1f, 1f);
                LastAction[i] = a;
                float k = Mathf.Max(shoveStyle ? ShoveSmooth : 0f, _amap != null ? _amap.Smooth : 0f);     // action low-pass (action_map.py)
                float f = k > 0f ? k * _actF[i] + (1f - k) * a : a; _actF[i] = f;
                if (slew > 0f) f = _actS[i] + Mathf.Clamp(f - _actS[i], -slew, slew);      // rate limit (action_map.py slew)
                _actS[i] = f;
                double rest = Map.DefaultPose[i] + blend * _shoveOffset[i] + (i == _waistPitch ? breath : 0.0);
                Ctrl[i] = _amap == null ? System.Math.Clamp(rest + _spec.action_scale * f, Map.CtrlMin[i], Map.CtrlMax[i])
                    : _amap.Target(i, rest, f, _spec.action_scale, d->qpos[Map.QposAdr[i]], d->qvel[Map.DofAdr[i]], Map.Kp[i], Map.Kv[i], Map.Tau[i], Map.CtrlMin[i], Map.CtrlMax[i]);
            }
            if (fat != null) ApplyFatigue(fat);
            _phase += 2f * Mathf.PI * (_amap != null && _amap.GaitHz > 0f ? _amap.GaitHz : ObsBuilder.GaitFreqHz) * _spec.ctrl_dt;
            if (_phase > 2f * Mathf.PI) _phase -= 2f * Mathf.PI;
            _ticks++;
            OnPolicyStep?.Invoke(this);
        }

        // action_map.py delay: Obs goes into a ring of delay + 1 inputs and the brain gets the oldest; after a reset (or a
        // get-up) the ring is filled with the first input, as env.py _delay_obs does.
        float[] DelayedObs()
        {
            int D = policyObsDim, L = _amap.Delay + 1;
            if (_ringFill) { for (int k = 0; k < L; k++) System.Array.Copy(Obs, 0, _obsRing, k * D, D); _ringHead = 0; _ringFill = false; }
            else { _ringHead = (_ringHead + 1) % L; System.Array.Copy(Obs, 0, _obsRing, _ringHead * D, D); }
            System.Array.Copy(_obsRing, ((_ringHead - _amap.Delay) % L + L) % L * D, _obsDelayed, 0, D);
            return _obsDelayed;
        }

        // env.py _update_fatigue: F follows the normalized power |tau qd| / (tau_max omega_ref) of the last physics step.
        void UpdateFatigue(MujocoLib.mjData_* d, ActionMap.FatigueSpec fz)
        {
            for (int i = 0; i < Map.N; i++)
            {
                double p = System.Math.Abs(d->actuator_force[Map.ActId[i]] * d->qvel[Map.DofAdr[i]]) / (_frc0[2 * i + 1] * fz.OmegaRef);
                _fatigue[i] = Mathf.Clamp01(_fatigue[i] + _spec.ctrl_dt * (fz.Gain * (float)p - _fatigue[i]) / fz.TauS);
            }
        }

        // env.py _apply_fatigue_limits: each actuator's force range scaled by 1 - (1 - min_scale) F until the next policy step.
        void ApplyFatigue(ActionMap.FatigueSpec fz)
        {
            var m = MjScene.Instance.Model;
            for (int i = 0; i < Map.N; i++)
            {
                double s = 1.0 - (1.0 - fz.MinScale) * _fatigue[i]; int a = Map.ActId[i];
                m->actuator_forcerange[2 * a] = _frc0[2 * i] * s; m->actuator_forcerange[2 * a + 1] = _frc0[2 * i + 1] * s;
            }
            _fatigueApplied = true;
        }

        void RestoreForceRange()
        {
            if (!_fatigueApplied || _frc0 == null || !MjScene.InstanceExists || MjScene.Instance.Model == null) return;
            var m = MjScene.Instance.Model;
            for (int i = 0; i < Map.N; i++) { int a = Map.ActId[i]; m->actuator_forcerange[2 * a] = _frc0[2 * i]; m->actuator_forcerange[2 * a + 1] = _frc0[2 * i + 1]; }
            _fatigueApplied = false;
        }

        // The get-up brain: the 103 locomotion inputs with a zero command, ctrl = default + scale * action (no style
        // path), as the getup rung trains it (koth/env.py). Shares the last action and the gait clock with the fight
        // brain, so the hand-over in either direction is continuous.
        void GetUpTick(MujocoLib.mjData_* d)
        {
            command = Vector3.zero;
            ObsBuilder.Fill(_getupObs, d, Map, ZeroCommand, LastAction, _phase);
            _sw.Restart();
            _getupInput.Upload(_getupObs); _getupWorker.Schedule(_getupInput);
            var output = _getupWorker.PeekOutput() as Tensor<float>; output.CompleteAllPendingOperations();
            var act = output.AsReadOnlySpan(); LastInferenceMs = _sw.Elapsed.TotalMilliseconds;
            InferenceLog.Note(getupPolicy, body, "CPU worker");
            ApplyGetUpAction(act);
        }

        void ApplyGetUpAction(System.ReadOnlySpan<float> act)
        {
            for (int i = 0; i < Map.N; i++)
            {
                float raw = act[i];
                if (!float.IsFinite(raw)) { NanInfCount++; raw = 0f; }
                RawAction[i] = raw; float a = Mathf.Clamp(raw, -1f, 1f); LastAction[i] = a; _actF[i] = a; _actS[i] = a;
                Ctrl[i] = System.Math.Clamp(Map.DefaultPose[i] + _spec.action_scale * a, Map.CtrlMin[i], Map.CtrlMax[i]);
            }
            _phase += 2f * Mathf.PI * ObsBuilder.GaitFreqHz * _spec.ctrl_dt;
            if (_phase > 2f * Mathf.PI) _phase -= 2f * Mathf.PI;
            _ringFill = true;          // the fight brain's delayed inputs restart from its first observation after the get-up
            OnPolicyStep?.Invoke(this);
        }

        /// <summary>Hand the body to the get-up brain (on) or back to the fight brain (off). Returns false when there is no
        /// get-up brain for this body. The worker is made once, on first use.</summary>
        public bool SetGetUp(bool on)
        {
            if (on && getupPolicy == null) return false;
            if (on && _getupWorker == null)
            {
                _getupWorker = new Worker(ModelLoader.Load(getupPolicy), BackendType.CPU);
                _getupObs = new float[ObsBuilder.ObsDim]; _getupInput = new Tensor<float>(new TensorShape(1, ObsBuilder.ObsDim));
            }
            _getup = on;
            return true;
        }

        /// <summary>Training keyframe for this robot: default joint pose, pelvis at its spawn x/y/yaw (qpos0) and the
        /// keyframe height, zero velocities, hold ctrl. Pure mjData writes; no scene recreation.</summary>
        public void ResetPose()
        {
            _getup = false;
            var m = MjScene.Instance.Model; var d = MjScene.Instance.Data;
            for (int k = 0; k < 7; k++) d->qpos[Map.RootQposAdr + k] = m->qpos0[Map.RootQposAdr + k];
            d->qpos[Map.RootQposAdr + 2] = _spec.key_root_z;
            for (int k = 0; k < 6; k++) d->qvel[Map.RootDofAdr + k] = 0;
            for (int i = 0; i < Map.N; i++)
            {
                d->qpos[Map.QposAdr[i]] = Map.DefaultPose[i]; d->qvel[Map.DofAdr[i]] = 0;
                Ctrl[i] = _spec.hold_ctrl[i]; d->ctrl[Map.ActId[i]] = Ctrl[i];
            }
            ResetClock();
        }

        // Policy clock, last action and shove filter back to the start of an episode (no allocation: ResetPose runs every step for parked bodies).
        void ResetClock()
        {
            _substep = 0; _phase = 0; _ticks = 0; _ringFill = true; _fatigueFresh = true; _after = PostRound.None; _afterBlend = 0f;
            _strike = StrikeKind.None; _strikeT = 0f;
            System.Array.Clear(LastAction, 0, LastAction.Length); System.Array.Clear(_actF, 0, _actF.Length);
            System.Array.Clear(_actS, 0, _actS.Length); System.Array.Clear(_fatigue, 0, _fatigue.Length);
            RestoreForceRange();
        }

        void LoadBrain()
        {
            _amap = policy != null ? ActionMap.Parse(actionMap) : null;
            if (policy == null) return;
            _worker = new Worker(ModelLoader.Load(policy), BackendType.CPU);
            Obs = new float[policyObsDim]; _input = new Tensor<float>(new TensorShape(1, policyObsDim));
            _obsRing = _amap != null && _amap.Delay > 0 ? new float[(_amap.Delay + 1) * policyObsDim] : null;
            _obsDelayed = _obsRing != null ? new float[policyObsDim] : null;
        }

        /// <summary>Swap the brain at runtime (menu fighter selection). policy = null gives the passive stance.</summary>
        public void SetBrain(ModelAsset newPolicy, int obsDim, GoalMode newGoal, float stopDist, float vmax, bool shove = false, string pose = "shove", TextAsset action = null)
        {
            _worker?.Dispose(); _input?.Dispose(); _worker = null; _input = null;
            policy = newPolicy; policyObsDim = obsDim; actionMap = action; goal = newGoal; goalStopDist = stopDist; goalVmax = vmax; command = Vector3.zero; shoveStyle = shove;
            _getup = false;
            if (pose != stylePose) { stylePose = pose; SetStyleOffsets(); }
            LoadBrain(); ResetClock();
        }

        /// <summary>Offset of each joint's styled rest pose from its default (target - default, 0 for joints the pose leaves alone).</summary>
        void SetStyleOffsets()
        {
            var pose = StylePoses.TryGetValue(stylePose ?? "", out var p) ? p : StylePoses["shove"];
            for (int i = 0; i < _spec.joints.Length; i++)
            {
                _shoveOffset[i] = 0f;
                foreach (var (key, target) in pose)
                    if (_spec.joints[i].Contains(key)) { _shoveOffset[i] = target - _spec.default_pose[i]; break; }
            }
        }

        /// <summary>Keyframe pose at a chosen spot on the summit (MuJoCo frame x, y in metres, yaw in radians).</summary>
        public void ResetPose(float x, float y, float yaw)
        {
            ResetPose();
            var d = MjScene.Instance.Data; double* q = d->qpos + Map.RootQposAdr;
            q[0] = x; q[1] = y;
            q[2] += (_spec.arena ?? new ArenaSpec()).Ground(System.Math.Sqrt(x * x + y * y));
            q[3] = System.Math.Cos(yaw / 2); q[4] = 0; q[5] = 0; q[6] = System.Math.Sin(yaw / 2);
        }
    }
}
