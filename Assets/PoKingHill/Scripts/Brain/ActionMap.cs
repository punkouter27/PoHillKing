using System;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// Mirror of training/koth/action_map.py (keep both in step): how a brain's output becomes a joint target. A brain exported with
    /// &lt;name&gt;_action.json beside its ONNX uses this; without one (every brain up to 2026-10-20) PolicyRunner keeps the
    /// original path, a = clamp(raw, -1, 1), target = clamp(default + offset + action_scale * a, ctrl range).
    ///   map "tanh"   a = tanh(raw)
    ///   scale[29]    per-joint action scale instead of joint_map's single one
    ///   window       target clamped to the torque-feasible window q + (kv qd -+ tau) / kp (nominal gains)
    ///   smooth k     action low-pass f = k f_prev + (1 - k) a before the scale (PolicyRunner; with the shove style the larger k)
    ///   slew s       rate limit after the low-pass, g = g_prev + clamp(f - g_prev, -s, s) (PolicyRunner)
    ///   delay n      the brain acts on the observation from n policy steps ago (PolicyRunner's ring)
    ///   breath a     waist_pitch rest pose + a sin(2 pi breath_hz t), t = policy steps since the reset x ctrl_dt
    ///   gait_hz f    the gait clock's frequency (0 = ObsBuilder.GaitFreqHz)
    ///   fatigue      actuator force range x (1 - (1 - min_scale) F), F a lag of normalized joint power; obs: F's 29 values
    ///                end the input (PolicyRunner.UpdateFatigue / ApplyFatigue = koth/env.py _update_fatigue / _apply_fatigue_limits)
    /// </summary>
    public sealed class ActionMap
    {
        [Serializable] class FatigueJson { public bool enabled; public float tau_s = 10f, min_scale = 0.6f, gain = 2f, omega_ref = 10f; public bool obs; }
        [Serializable] class Json
        {
            public string map = "clip"; public float[] scale; public bool window; public float smooth;
            public float slew; public int delay; public float breath, breath_hz = 0.25f, gait_hz; public FatigueJson fatigue;
        }

        /// <summary>The fatigue model a brain was trained with (koth/env.py cfg "fatigue").</summary>
        public sealed class FatigueSpec
        {
            public readonly float TauS, MinScale, Gain, OmegaRef; public readonly bool Obs;
            public FatigueSpec(float tauS, float minScale, float gain, float omegaRef, bool obs) { TauS = tauS; MinScale = minScale; Gain = gain; OmegaRef = omegaRef; Obs = obs; }
        }

        public readonly bool Tanh, Window;
        public readonly float[] Scale;     // null = joint_map's action_scale for every joint
        public readonly float Smooth;      // action low-pass k, 0 = off
        public readonly float Slew;        // action rate limit per policy step, 0 = off
        public readonly int Delay;         // observation delay in policy steps, 0 = off
        public readonly float Breath, BreathHz;   // waist_pitch sway amplitude (rad, 0 = off) and frequency
        public readonly float GaitHz;      // gait clock, 0 = ObsBuilder.GaitFreqHz
        public readonly FatigueSpec Fatigue;      // null = off

        ActionMap(Json j)
        {
            Tanh = j.map == "tanh"; Scale = j.scale; Window = j.window; Smooth = j.smooth; Slew = j.slew; Delay = j.delay;
            Breath = j.breath; BreathHz = j.breath_hz; GaitHz = j.gait_hz;
            var f = j.fatigue; Fatigue = f != null && f.enabled ? new FatigueSpec(f.tau_s, f.min_scale, f.gain, f.omega_ref, f.obs) : null;
        }

        /// <summary>Parse a sidecar; null (the original path) for no file.</summary>
        public static ActionMap Parse(TextAsset json)
        {
            if (json == null || string.IsNullOrWhiteSpace(json.text)) return null;
            var j = JsonUtility.FromJson<Json>(json.text);
            if (j.scale != null && j.scale.Length == 0) j.scale = null;
            if (j.scale != null && j.scale.Length != 29) throw new ArgumentException($"{json.name}: scale has {j.scale.Length} values, not 29");
            if (j.smooth < 0f || j.smooth >= 1f) throw new ArgumentException($"{json.name}: smooth {j.smooth} outside [0, 1)");
            if (j.slew < 0f || j.breath < 0f || j.breath_hz < 0f || j.gait_hz < 0f) throw new ArgumentException($"{json.name}: negative slew, breath or gait_hz");
            if (j.delay < 0 || j.delay > 25) throw new ArgumentException($"{json.name}: delay {j.delay} outside [0, 25]");
            return new ActionMap(j);
        }

        public float ToAction(float raw) => Tanh ? (float)Math.Tanh(raw) : Mathf.Clamp(raw, -1f, 1f);

        /// <summary>Joint target for joint i: rest = default (+ style offset), f = the (filtered) action, q / qd the joint's
        /// current state, kp / kv / tau its nominal gains and force limit, lo / hi its control range.</summary>
        public double Target(int i, double rest, float f, float baseScale, double q, double qd, double kp, double kv, double tau, double lo, double hi)
        {
            double t = rest + (Scale != null ? Scale[i] : baseScale) * f;
            if (Window && kp > 0) t = Math.Clamp(t, q + (kv * qd - tau) / kp, q + (kv * qd + tau) / kp);
            return Math.Clamp(t, lo, hi);
        }
    }
}
