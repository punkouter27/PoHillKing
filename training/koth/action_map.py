"""How a brain's output becomes a joint target. Mirrored by Unity's ActionMap.cs; the spec travels with an exported brain
as <name>_action.json beside its ONNX (scripts/export_policy.py). No file = the original path, which every brain up to
2026-10-20 uses:
  a      = clip(raw, -1, 1)                       (also the "last action" observation)
  target = clip(default + style offset + 0.5 * a, ctrl range)

Options (env cfg "action", all off by default):
  map "tanh"    a = tanh(raw) instead of the clip. The clip has no gradient beyond +-1: the champion's outputs sat on it
                29 % of the time (drive_audit.py, 2026-10-20), so exploration noise there changed nothing and the mean
                could drift outward unopposed. tanh keeps every output inside (-1, 1) and every change of it effective.
  scale [29]    per-joint action scale (rad per unit action) instead of joint_map's single 0.5.
  window True   torque-feasible targets: the target is clamped to what the actuator can act on at the current state,
                kp (target - q) - kv qd within +-forcerange, i.e. target in [q + (kv qd - tau) / kp, q + (kv qd + tau) / kp]
                with the nominal kp, kv, tau. A target beyond the window asks for torque the motor does not have; it
                would only wind up (a full-torque push that stays on after the joint has passed where it should stop).
  smooth k      action low-pass, f = k f_prev + (1 - k) a, before the scale (0 = off; 0.6 at 50 Hz: about 40 ms). Brains
                updating every 20 ms change their targets from step to step; the filter keeps the joints from twitching
                and the brain learns with it, so it is part of the brain (added 2026-10-21 for human-like motion).
  slew s       rate limit after the low-pass: the target action moves at most s per policy step toward f, g = g_prev +
                clip(f - g_prev, -s, s) (0 = off; muscles cannot jump from rest to full force in 20 ms; added 2026-10-29).
  delay n       reaction time: the brain acts on the observation from n policy steps ago (0 = off; 3 at 50 Hz = 60 ms,
                a human's fastest balance reflexes are 50-100 ms). Filled with the first observation after a reset.
  breath a      breathing sway: the waist_pitch rest pose moves by a sin(2 pi breath_hz t) rad, t = policy steps since the
                reset x ctrl_dt (0 = off; 0.03 rad at 0.25 Hz). Open loop: the brain learns to stand with it.
  gait_hz f     the gait clock's frequency for this body (0 = obs.GAIT_FREQ_HZ, 1.5): a heavier, older body steps slower.
                It changes the phase inputs, so it travels with the brain (env.BODY_GAIT_HZ holds the per-body values).
  fatigue {..}  the env cfg "fatigue" the brain was trained with (koth/env.py: actuator force range scaled by
                1 - (1 - min_scale) F, F a lag of normalized joint power; obs True appends F's 29 values to the input).
                Carried so Unity and duel_stats.py run the same model; None = off.
The style low-pass (shove, k 0.5) filters a the same way; with both, the larger k applies."""
from __future__ import annotations
import json, os
import numpy as np

DEFAULT = dict(map="clip", scale=None, window=False, smooth=0.0, slew=0.0, delay=0, breath=0.0, breath_hz=0.25, gait_hz=0.0, fatigue=None)
FATIGUE_KEYS = ("tau_s", "min_scale", "gain", "omega_ref", "obs")


def spec_of(cfg_action: dict | None) -> dict:
    """Complete spec from an env cfg "action" entry (None / missing keys = the original path)."""
    s = dict(DEFAULT); s.update(cfg_action or {})
    assert s["map"] in ("clip", "tanh"), s["map"]
    if s["scale"] is not None: s["scale"] = [float(x) for x in s["scale"]]; assert len(s["scale"]) == 29
    s["smooth"] = float(s["smooth"] or 0.0); assert 0.0 <= s["smooth"] < 1.0, s["smooth"]
    s["slew"] = float(s["slew"] or 0.0); assert s["slew"] >= 0.0, s["slew"]
    s["delay"] = int(s["delay"] or 0); assert 0 <= s["delay"] <= 25, s["delay"]
    for k in ("breath", "breath_hz", "gait_hz"): s[k] = float(s[k] or 0.0); assert s[k] >= 0.0, (k, s[k])
    fz = s["fatigue"]
    s["fatigue"] = {k: (bool(fz[k]) if k == "obs" else float(fz[k])) for k in FATIGUE_KEYS} if fz and fz.get("enabled", True) else None
    return s


def is_default(s: dict) -> bool:
    """True when the action path is the original one (fatigue is not part of the path: env.py runs it on its own)."""
    s = spec_of(s)
    return (s["map"] == "clip" and s["scale"] is None and not s["window"] and s["smooth"] == 0.0 and s["slew"] == 0.0 and s["delay"] == 0
            and s["breath"] == 0.0 and s["gait_hz"] == 0.0)


def sidecar_path(onnx_path: str) -> str:
    return onnx_path[:-len("_policy.onnx")] + "_action.json" if onnx_path.endswith("_policy.onnx") else onnx_path + ".action.json"


def write_sidecar(onnx_path: str, s: dict) -> str | None:
    """<name>_action.json beside <name>_policy.onnx, or remove a stale one when the brain uses the original path."""
    p = sidecar_path(onnx_path); s = spec_of(s)
    if is_default(s) and s["fatigue"] is None:
        if os.path.exists(p): os.remove(p)
        return None
    out = dict(map=s["map"], scale=s["scale"], window=bool(s["window"]), smooth=s["smooth"])
    if s["slew"]: out["slew"] = s["slew"]
    if s["delay"]: out["delay"] = s["delay"]
    if s["breath"]: out["breath"] = s["breath"]; out["breath_hz"] = s["breath_hz"]
    if s["gait_hz"]: out["gait_hz"] = s["gait_hz"]
    if s["fatigue"]: out["fatigue"] = dict(enabled=True, **s["fatigue"])
    json.dump(out, open(p, "w"), indent=1)
    return p


def read_sidecar(onnx_path: str) -> dict:
    p = sidecar_path(onnx_path)
    return spec_of(json.load(open(p)) if os.path.exists(p) else None)


def ckpt_spec(ckpt: str) -> dict:
    """The spec a checkpoint was trained with, from config.json beside it (runs/<rung>/<name>/); original path if none.
    The env cfg "fatigue" rides along when it was on."""
    cfg = os.path.join(os.path.dirname(os.path.abspath(ckpt)), "config.json")
    if not os.path.exists(cfg): return spec_of(None)
    env = json.load(open(cfg))["env"]; s = dict(env.get("action") or {})
    if (env.get("fatigue") or {}).get("enabled"): s["fatigue"] = env["fatigue"]
    return spec_of(s)


class NumpyMap:
    """CPU mirror (duel_stats.py, cpu_sim users): to_action(raw) -> a; target(...) -> ctrl. kp/kv/tau per joint of
    one robot, from the compiled model (nominal values)."""
    def __init__(self, s: dict, base_scale: float, kp=None, kv=None, tau=None):
        self.s = spec_of(s); self.scale = np.asarray(self.s["scale"], dtype=np.float64) if self.s["scale"] is not None else float(base_scale)
        self.kp, self.kv, self.tau = kp, kv, tau; self.smooth = self.s["smooth"]; self.slew = self.s["slew"]; self.delay = self.s["delay"]
        self.fatigue = self.s["fatigue"]; self.breath, self.breath_hz, self.gait_hz = self.s["breath"], self.s["breath_hz"], self.s["gait_hz"]
        assert not self.s["window"] or kp is not None

    @staticmethod
    def gains(m, aid):
        """(kp, kv, tau) of actuators aid of a mujoco.MjModel (position actuators: biasprm = [0, -kp, -kv])."""
        return m.actuator_gainprm[aid, 0].copy(), -m.actuator_biasprm[aid, 2].copy(), m.actuator_forcerange[aid, 1].copy()

    def to_action(self, raw: np.ndarray) -> np.ndarray:
        return (np.tanh(raw) if self.s["map"] == "tanh" else np.clip(raw, -1, 1)).astype(np.float32)

    def slew_limit(self, g_prev: np.ndarray, f: np.ndarray) -> np.ndarray:
        """The rate-limited action (slew; f unchanged when off). Callers keep g_prev, zero at a reset."""
        return f if self.slew <= 0 else g_prev + np.clip(f - g_prev, -self.slew, self.slew)

    def target(self, base: np.ndarray, f: np.ndarray, q: np.ndarray, qd: np.ndarray, lo: np.ndarray, hi: np.ndarray) -> np.ndarray:
        """base = default (+ style offset), f = the (filtered) action a."""
        t = base + self.scale * f
        if self.s["window"]: t = np.clip(t, q + (self.kv * qd - self.tau) / self.kp, q + (self.kv * qd + self.tau) / self.kp)
        return np.clip(t, lo, hi)


class NumpyFatigue:
    """CPU mirror of env.py's fatigue (_apply_fatigue_limits / _update_fatigue) for one robot: forcerange of its 29
    actuators scaled by 1 - (1 - min_scale) F before the physics steps, F updated once per policy step from the last
    physics step's actuator force and joint velocity. Unity: PolicyRunner (ApplyFatigue / UpdateFatigue)."""
    def __init__(self, fz: dict, m, aid, dadr, ctrl_dt: float):
        self.fz, self.m, self.aid, self.dadr, self.dt = fz, m, np.asarray(aid), np.asarray(dadr), ctrl_dt
        self.frc0 = m.actuator_forcerange[self.aid].copy(); self.F = np.zeros(len(self.aid))

    def reset(self):
        self.F[:] = 0; self.m.actuator_forcerange[self.aid] = self.frc0

    def apply(self):
        self.m.actuator_forcerange[self.aid] = self.frc0 * (1 - (1 - self.fz["min_scale"]) * self.F)[:, None]

    def update(self, d):
        p = np.abs(d.actuator_force[self.aid] * d.qvel[self.dadr]) / (self.frc0[:, 1] * self.fz["omega_ref"])
        self.F = np.clip(self.F + self.dt * (self.fz["gain"] * p - self.F) / self.fz["tau_s"], 0.0, 1.0)
