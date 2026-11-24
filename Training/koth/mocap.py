"""Human motion capture as a style reference for the fighters (koth/amp.py).

BVH clips (the CMU Graphics Lab database in B. Hahne's BVH conversion: free to use, scripts/fetch_mocap.py) are
retargeted to the fighters' 29-joint layout and turned into the features a motion-prior discriminator compares:

  per frame (FEAT values, the same for every body: lengths are in pelvis standing heights)
    0      pelvis height above the ground / standing pelvis height
    1:4    gravity direction in the pelvis frame
    4:7    pelvis linear velocity in the pelvis frame / standing pelvis height (1/s)
    7:10   pelvis angular velocity in the pelvis frame (rad/s)
    10:31  the STYLE_JOINTS' angles (rad)
    31:52  their velocities (rad/s)

Retargeting is by direction, not by copying rotations: the BVH rest pose is not the fighters' zero pose (legs splayed,
arms out), but a thigh that points somewhere points there on any skeleton. Per limb, from the fighters' joint chains
(koth/build_human.py: hip and shoulder pitch (y) -> roll (x) -> yaw (z), knee +y, elbow -y, waist yaw -> roll -> pitch):
  thigh / upper arm direction d in the parent frame   roll = asin(d.y), pitch = atan2(-d.x, -d.z)
  shank / forearm direction s in that limb's frame     bend = acos(-s.z), yaw = atan2 of its horizontal part
  torso rotation relative to the pelvis                waist yaw, roll, pitch (ZXY Euler)
  foot direction in the shank frame                    ankle pitch, less the rest pose's toe-down angle
Wrists and ankle roll stay at zero and are left out of the features (STYLE_JOINTS): the clips say nothing reliable
about them.

Those angles are anatomical: zero is a limb hanging straight down, turning about its own length. A fighter's joints
are not quite that: Trump's and Grandpa's arms rest 17 to 20 degrees out from their sides while their shoulder axes
stay square to the torso, so the same angles would put their arms somewhere else. With a body (body_limbs(), read from
its MuJoCo model's zero pose) retarget() therefore solves, per frame and limb, the four joint angles that point that
body's own two segments where the capture's point (a small least-squares problem, started from the anatomical
answer), and keeps them inside the body's joint ranges. Each body gets its own dataset; scripts/test_amp.py checks
them against MuJoCo's forward kinematics.

Model frame: x forward, y left, z up. BVH (CMU): x left, y up, z forward.
"""

from __future__ import annotations

import json
import os

import numpy as np

JOINTS = (
    [
        f"{s}_{j}"
        for s in ("left", "right")
        for j in ("hip_pitch", "hip_roll", "hip_yaw", "knee", "ankle_pitch", "ankle_roll")
    ]
    + ["waist_yaw", "waist_roll", "waist_pitch"]
    + [
        f"{s}_{j}"
        for s in ("left", "right")
        for j in ("shoulder_pitch", "shoulder_roll", "shoulder_yaw", "elbow", "wrist_roll", "wrist_pitch", "wrist_yaw")
    ]
)
STYLE_IDX = [i for i, n in enumerate(JOINTS) if "wrist" not in n and "ankle_roll" not in n]
STYLE_JOINTS = [JOINTS[i] for i in STYLE_IDX]
NJ = len(STYLE_IDX)  # 21
FEAT = 10 + 2 * NJ  # 52
CTRL_DT = 0.02

M_BVH = np.array([[0.0, 0, 1], [1, 0, 0], [0, 1, 0]])  # model = M_BVH @ bvh


# ------------------------------------------------------------------ BVH
def parse_bvh(path: str):
    """names, parents, offsets (J,3), channel layout per joint, frames (F, channels), frame time."""
    tok = open(path).read().split()
    names, parents, offsets, chans = [], [], [], []
    stack, i, end_site = [], 0, False
    while tok[i] != "MOTION":
        t = tok[i]
        if t in ("ROOT", "JOINT"):
            names.append(tok[i + 1])
            parents.append(stack[-1] if stack else -1)
            offsets.append(None)
            chans.append([])
            i += 2
        elif t == "End":
            end_site = True
            i += 2
        elif t == "{":
            if not end_site:
                stack.append(len(names) - 1)
            i += 1
        elif t == "}":
            if end_site:
                end_site = False
            else:
                stack.pop()
            i += 1
        elif t == "OFFSET":
            if not end_site:
                offsets[stack[-1]] = [float(x) for x in tok[i + 1 : i + 4]]
            i += 4
        elif t == "CHANNELS":
            n = int(tok[i + 1])
            chans[stack[-1]] = tok[i + 2 : i + 2 + n]
            i += 2 + n
        else:
            i += 1
    assert tok[i + 1] == "Frames:" and tok[i + 3] == "Frame" and tok[i + 4] == "Time:", "unexpected MOTION header"
    frames, dt = int(tok[i + 2]), float(tok[i + 5])
    data = np.array(tok[i + 6 :], dtype=np.float64).reshape(frames, -1)
    assert data.shape[1] == sum(len(c) for c in chans), "channel count does not match the hierarchy"
    return names, parents, np.array(offsets), chans, data, dt


def _rot(axis: str, deg: np.ndarray) -> np.ndarray:
    a = np.radians(deg)
    c, s = np.cos(a), np.sin(a)
    z, o = np.zeros_like(a), np.ones_like(a)
    m = {"X": [o, z, z, z, c, -s, z, s, c], "Y": [c, z, s, z, o, z, -s, z, c], "Z": [c, -s, z, s, c, z, z, z, o]}[axis]
    return np.stack(m, -1).reshape(*a.shape, 3, 3)


def bvh_fk(names, parents, offsets, chans, data):
    """World positions (F,J,3) and rotations (F,J,3,3) in BVH coordinates."""
    F, J = data.shape[0], len(names)
    pos = np.zeros((F, J, 3))
    rot = np.zeros((F, J, 3, 3))
    col = 0
    for j in range(J):
        R = np.broadcast_to(np.eye(3), (F, 3, 3)).copy()
        t = np.broadcast_to(offsets[j], (F, 3)).copy()
        for c in chans[j]:
            v = data[:, col]
            col += 1
            if c.endswith("position"):
                t[:, "XYZ".index(c[0])] = v if parents[j] < 0 else t[:, "XYZ".index(c[0])] + v
            else:
                R = R @ _rot(c[0], v)
        if parents[j] < 0:
            pos[:, j], rot[:, j] = t, R
        else:
            p = parents[j]
            pos[:, j] = pos[:, p] + np.einsum("fij,fj->fi", rot[:, p], t)
            rot[:, j] = rot[:, p] @ R
    return pos, rot


# ------------------------------------------------------------------ retargeting
def _unit(v):
    return v / np.maximum(np.linalg.norm(v, axis=-1, keepdims=True), 1e-9)


def _Rx(a):
    c, s = np.cos(a), np.sin(a)
    z, o = np.zeros_like(a), np.ones_like(a)
    return np.stack([o, z, z, z, c, -s, z, s, c], -1).reshape(*a.shape, 3, 3)


def _Ry(a):
    c, s = np.cos(a), np.sin(a)
    z, o = np.zeros_like(a), np.ones_like(a)
    return np.stack([c, z, s, z, o, z, -s, z, c], -1).reshape(*a.shape, 3, 3)


def _Rz(a):
    c, s = np.cos(a), np.sin(a)
    z, o = np.zeros_like(a), np.ones_like(a)
    return np.stack([c, -s, z, s, c, z, z, z, o], -1).reshape(*a.shape, 3, 3)


def _local(R, v):
    return np.einsum("fji,fj->fi", R, v)  # R^T v


def _limb(Rparent, d_upper, d_lower, bend_sign: float):
    """Pitch, roll, yaw of a ball joint whose limb hangs along -z, and the bend of the hinge below it (bend_sign +1: the
    lower segment swings back for a positive angle, as a knee; -1: forward, as an elbow). Returns the lower segment's frame too."""
    v = _local(Rparent, d_upper)
    roll = np.arcsin(np.clip(v[:, 1], -1, 1))
    pitch = np.arctan2(-v[:, 0], -v[:, 2])
    Ru = Rparent @ _Ry(pitch) @ _Rx(roll)
    s = _local(Ru, d_lower)
    bend = np.arccos(np.clip(-s[:, 2], -1, 1))
    h = np.hypot(s[:, 0], s[:, 1])
    w = np.clip((h - 0.1) / 0.3, 0, 1)  # a straight limb has no measurable twist
    yaw = w * np.arctan2(-bend_sign * s[:, 1], -bend_sign * s[:, 0])
    return pitch, roll, yaw, bend, Ru @ _Rz(yaw) @ _Ry(bend_sign * bend)


# Common hierarchy aliases across CMU, 100STYLE (CC BY 4.0), and Mixamo BVH rigs.
_BVH_ALIASES = {
    "Hips": ("Hips", "hips", "Pelvis", "Root"),
    "Spine1": ("Spine1", "Chest", "UpperChest", "Spine2", "Spine", "Torso"),
    "LeftUpLeg": ("LeftUpLeg", "LeftThigh", "L_UpLeg", "L_Thigh"),
    "LeftLeg": ("LeftLeg", "LeftShin", "LeftKnee", "L_Leg", "L_Shin"),
    "LeftFoot": ("LeftFoot", "LeftAnkle", "L_Foot"),
    "LeftToeBase": ("LeftToeBase", "LeftToe", "LeftToes", "L_Toe", "L_ToeBase"),
    "RightUpLeg": ("RightUpLeg", "RightThigh", "R_UpLeg", "R_Thigh"),
    "RightLeg": ("RightLeg", "RightShin", "RightKnee", "R_Leg", "R_Shin"),
    "RightFoot": ("RightFoot", "RightAnkle", "R_Foot"),
    "RightToeBase": ("RightToeBase", "RightToe", "RightToes", "R_Toe", "R_ToeBase"),
    "LeftArm": ("LeftArm", "LeftUpperArm", "L_Arm", "L_UpperArm"),
    "LeftForeArm": ("LeftForeArm", "LeftLowerArm", "LeftElbow", "L_ForeArm"),
    "LeftHand": ("LeftHand", "LeftWrist", "L_Hand"),
    "RightArm": ("RightArm", "RightUpperArm", "R_Arm", "R_UpperArm"),
    "RightForeArm": ("RightForeArm", "RightLowerArm", "RightElbow", "R_ForeArm"),
    "RightHand": ("RightHand", "RightWrist", "R_Hand"),
}


def _canonicalize_bvh(pos: np.ndarray, rot: np.ndarray, names: list[str]):
    """Map CMU / 100STYLE / Mixamo bone names into canonical CMU keys in model coordinates."""
    raw_p = {n.split(":")[-1]: np.einsum("ij,fj->fi", M_BVH, pos[:, k]) for k, n in enumerate(names)}
    raw_r = {n.split(":")[-1]: np.einsum("ij,fjk,lk->fil", M_BVH, rot[:, k], M_BVH) for k, n in enumerate(names)}
    P, R = {}, {}
    for canon, aliases in _BVH_ALIASES.items():
        for a in aliases:
            if a in raw_p:
                P[canon], R[canon] = raw_p[a], raw_r[a]
                break
    # Some 100STYLE / game BVHs end at the foot without a ToeBase node: place a virtual +x toe offset in foot space.
    for side in ("Left", "Right"):
        if f"{side}Foot" in P and f"{side}ToeBase" not in P:
            P[f"{side}ToeBase"] = P[f"{side}Foot"] + np.einsum(
                "fij,j->fi", R[f"{side}Foot"], np.array([0.12, 0.0, 0.0])
            )
            R[f"{side}ToeBase"] = R[f"{side}Foot"]
    return P, R


def retarget(path: str, body: dict | None = None):
    """A clip as model-frame motion at its own frame rate: dict(dt, R (F,3,3) pelvis rotation, pos (F,3) pelvis
    position in standing heights, q (F,29) joint angles). body = None: anatomical angles; body_limbs(...): that
    fighter's own joint angles. The first frame (the T pose) is dropped."""
    names, parents, offsets, chans, data, dt = parse_bvh(path)
    pos, rot = bvh_fk(names, parents, offsets, chans, data)
    P, R = _canonicalize_bvh(pos, rot, names)
    need = [
        "Hips",
        "LeftUpLeg",
        "LeftLeg",
        "LeftFoot",
        "LeftToeBase",
        "RightUpLeg",
        "RightLeg",
        "RightFoot",
        "RightToeBase",
        "Spine1",
        "LeftArm",
        "LeftForeArm",
        "LeftHand",
        "RightArm",
        "RightForeArm",
        "RightHand",
    ]
    missing = [n for n in need if n not in P]
    if missing:
        raise ValueError(f"{os.path.basename(path)}: unsupported BVH skeleton (no {missing})")
    # the T pose: floor under the toes, standing pelvis height, the foot's toe-down angle at rest
    floor = min(P["LeftToeBase"][0, 2], P["RightToeBase"][0, 2], P["LeftFoot"][0, 2], P["RightFoot"][0, 2])
    stand = P["Hips"][0, 2] - floor
    q = np.zeros((len(data), 29))
    Rp, Rt = R["Hips"], R["Spine1"]
    for side, o in (("Left", 0), ("Right", 6)):
        du, dl = _unit(P[side + "Leg"] - P[side + "UpLeg"]), _unit(P[side + "Foot"] - P[side + "Leg"])
        pitch, roll, yaw, knee, Rs = _limb(Rp, du, dl, +1.0)
        if body is not None:
            u0, l0 = body["limbs"][side.lower() + "_leg"]
            sl = slice(o, o + 4)
            x = _fit_limb(
                np.stack([pitch, roll, yaw, knee], 1),
                _local(Rp, du),
                _local(Rp, dl),
                u0,
                l0,
                +1.0,
                body["lo"][sl],
                body["hi"][sl],
            )
            pitch, roll, yaw, knee = x.T
            Rs = Rp @ _Ry(pitch) @ _Rx(roll) @ _Rz(yaw) @ _Ry(knee)
        f = _local(Rs, _unit(P[side + "ToeBase"] - P[side + "Foot"]))
        ankle = np.arctan2(-f[:, 2], f[:, 0])
        q[:, o : o + 5] = np.stack([pitch, roll, yaw, knee, ankle - ankle[0]], 1)
    W = np.einsum("fji,fjk->fik", Rp, Rt)  # waist: Rz(yaw) Rx(roll) Ry(pitch)
    q[:, 12] = np.arctan2(-W[:, 0, 1], W[:, 1, 1])
    q[:, 13] = np.arcsin(np.clip(W[:, 2, 1], -1, 1))
    q[:, 14] = np.arctan2(-W[:, 2, 0], W[:, 2, 2])
    for side, o in (("Left", 15), ("Right", 22)):
        du, dl = _unit(P[side + "ForeArm"] - P[side + "Arm"]), _unit(P[side + "Hand"] - P[side + "ForeArm"])
        pitch, roll, yaw, elbow, _ = _limb(Rt, du, dl, -1.0)
        pitch = np.where(
            pitch > 1.2, pitch - 2 * np.pi, pitch
        )  # an arm overhead is a forward raise, not a backward one
        x = np.stack([pitch, roll, yaw, elbow], 1)
        if body is not None:
            u0, l0 = body["limbs"][side.lower() + "_arm"]
            sl = slice(o, o + 4)
            x = _fit_limb(x, _local(Rt, du), _local(Rt, dl), u0, l0, -1.0, body["lo"][sl], body["hi"][sl])
        q[:, o : o + 4] = x
    if body is not None:
        q = np.clip(q, body["lo"], body["hi"])
    hips = (P["Hips"] - np.array([0, 0, floor])) / stand
    return dict(dt=dt, R=Rp[1:], pos=hips[1:], q=q[1:], stand=stand)


def body_limbs(mjm, prefix: str) -> dict:
    """What retarget() needs to fit a clip to one fighter: per limb, the zero-pose direction of its upper and lower
    segment in the parent's frame (every link frame is world-aligned in the zero pose), and the 29 joint ranges."""
    import mujoco

    d = mujoco.MjData(mjm)
    d.qpos[:] = mjm.qpos0
    mujoco.mj_kinematics(mjm, d)
    bid = lambda n: mujoco.mj_name2id(mjm, mujoco.mjtObj.mjOBJ_BODY, prefix + n)
    seg = lambda parent, a, b: d.xmat[bid(parent)].reshape(3, 3).T @ _unit(d.xpos[bid(b)] - d.xpos[bid(a)])
    limbs = {}
    for side in ("left_", "right_"):
        limbs[side + "leg"] = (
            seg("pelvis", side + "hip_yaw_link", side + "knee_link"),
            seg("pelvis", side + "knee_link", side + "ankle_roll_link"),
        )
        limbs[side + "arm"] = (
            seg("torso_link", side + "shoulder_yaw_link", side + "elbow_link"),
            seg("torso_link", side + "elbow_link", side + "wrist_yaw_link"),
        )
    jid = [mujoco.mj_name2id(mjm, mujoco.mjtObj.mjOBJ_JOINT, prefix + n + "_joint") for n in JOINTS]
    assert min(jid) >= 0, f"{prefix}: not a 29-joint fighter"
    return dict(limbs=limbs, lo=mjm.jnt_range[jid, 0].copy(), hi=mjm.jnt_range[jid, 1].copy())


def _chain(x, u0, l0, bend_sign):
    """Upper and lower segment directions in the parent frame for joint angles x (F,4) = pitch, roll, yaw, bend."""
    R = _Ry(x[:, 0]) @ _Rx(x[:, 1]) @ _Rz(x[:, 2])
    return np.einsum("fij,j->fi", R, u0), np.einsum("fij,j->fi", R @ _Ry(bend_sign * x[:, 3]), l0)


def _fit_limb(x0, t_upper, t_lower, u0, l0, bend_sign, lo, hi, iters: int = 25):
    """The joint angles (F,4) whose segments point along t_upper, t_lower (parent frame): damped Gauss-Newton on all
    frames at once with a numeric Jacobian, inside the joint ranges."""

    def res(x):
        u, l = _chain(x, u0, l0, bend_sign)
        return np.concatenate([u - t_upper, l - t_lower], 1)

    x = np.clip(x0, lo, hi)
    r = res(x)
    lam = np.full(len(x), 1e-2)
    for _ in range(iters):
        J = np.stack([(res(x + 1e-5 * np.eye(4)[k]) - r) / 1e-5 for k in range(4)], 2)  # (F,6,4)
        g = np.einsum("fki,fk->fi", J, r)
        H = np.einsum("fki,fkj->fij", J, J) + lam[:, None, None] * np.eye(4)
        xn = np.clip(x - np.linalg.solve(H, g[..., None])[..., 0], lo, hi)
        rn = res(xn)
        better = (rn**2).sum(1) < (r**2).sum(1)
        x = np.where(better[:, None], xn, x)
        r = np.where(better[:, None], rn, r)
        lam = np.where(better, lam * 0.3, lam * 5.0).clip(1e-6, 1e3)
    return x


# ------------------------------------------------------------------ features
def _log_so3(R):
    """Rotation vector of each (..., 3, 3) rotation."""
    c = np.clip((np.trace(R, axis1=-2, axis2=-1) - 1) / 2, -1, 1)
    a = np.arccos(c)
    v = np.stack([R[..., 2, 1] - R[..., 1, 2], R[..., 0, 2] - R[..., 2, 0], R[..., 1, 0] - R[..., 0, 1]], -1)
    k = np.where(a < 1e-6, 0.5, a / np.maximum(2 * np.sin(a), 1e-9))
    return v * k[..., None]


def features(clip: dict, dt: float = CTRL_DT) -> np.ndarray:
    """(T, FEAT) frames at the control rate. Positions and angles are interpolated; the pelvis rotation takes the
    nearest source frame (at 120 Hz that is within 4 ms)."""
    src = np.arange(len(clip["q"])) * clip["dt"]
    t = np.arange(0.0, src[-1], dt)
    if len(t) < 3:
        return np.zeros((0, FEAT))
    near = np.clip(np.round(t / clip["dt"]).astype(int), 0, len(src) - 1)
    pos = np.stack([np.interp(t, src, clip["pos"][:, k]) for k in range(3)], 1)
    q = np.stack([np.interp(t, src, np.unwrap(clip["q"][:, k])) for k in range(29)], 1)[:, STYLE_IDX]
    R = clip["R"][near]
    vel = np.gradient(pos, dt, axis=0)
    qd = np.gradient(q, dt, axis=0)
    w = np.zeros((len(t), 3))
    w[:-1] = _log_so3(np.einsum("fji,fjk->fik", R[:-1], R[1:])) / dt
    w[-1] = w[-2]
    grav = _local(R, np.broadcast_to(np.array([0.0, 0, -1]), (len(t), 3)))
    return np.concatenate([pos[:, 2:3], grav, _local(R, vel), w, q, qd], 1).astype(np.float32)


def mirror(f: np.ndarray) -> np.ndarray:
    """The same motion with left and right swapped (frames or pairs of frames)."""
    if f.shape[-1] == 2 * FEAT:
        return np.concatenate([mirror(f[..., :FEAT]), mirror(f[..., FEAT:])], -1)
    out = f.copy()
    out[..., 2] *= -1
    out[..., 5] *= -1
    out[..., 7] *= -1
    out[..., 9] *= -1  # gravity y, velocity y, roll and yaw rates
    names = STYLE_JOINTS
    swap = np.arange(NJ)
    sign = np.ones(NJ, dtype=f.dtype)
    for i, n in enumerate(names):
        other = n.replace("left_", "RIGHT_").replace("right_", "left_").replace("RIGHT_", "right_")
        swap[i] = names.index(other)
        if "roll" in n or "yaw" in n:
            sign[i] = -1
    for base in (10, 10 + NJ):
        out[..., base : base + NJ] = f[..., base : base + NJ][..., swap] * sign
    return out


def plausible(pairs: np.ndarray) -> np.ndarray:
    """Which frame pairs a person could have made: capture glitches (a marker swap, a dropped frame) show as a pelvis
    that jumps or joints that spin faster than any limb does."""
    ok = np.ones(len(pairs), dtype=bool)
    for f in (pairs[:, :FEAT], pairs[:, FEAT:]):
        ok &= (
            (f[:, 0] > 0.2)
            & (f[:, 0] < 1.4)
            & (np.linalg.norm(f[:, 4:7], axis=1) < 4.0)
            & (np.abs(f[:, 7:10]).max(1) < 15.0)
            & (np.abs(f[:, 10 + NJ :]).max(1) < 40.0)
        )
    return ok & (np.abs(pairs[:, FEAT] - pairs[:, 0]) < 0.05)


def build_dataset(
    paths: list[str], out: str, mirror_too: bool = True, body: dict | None = None, robot: str = ""
) -> dict:
    """Retarget every clip (to one fighter when body is given) and save (N, 2 FEAT) consecutive-frame pairs with
    their statistics. Returns a summary."""
    pairs, clips = [], []
    for p in paths:
        f = features(retarget(p, body))
        if len(f) < 2:
            continue
        x = np.concatenate([f[:-1], f[1:]], 1)
        ok = plausible(x)
        x = x[ok]
        pairs.append(x)
        clips.append(
            dict(
                file=os.path.basename(p),
                frames=int(len(f)),
                seconds=round(len(f) * CTRL_DT, 2),
                dropped=int((~ok).sum()),
            )
        )
        if mirror_too:
            pairs.append(mirror(x))
    x = np.concatenate(pairs, 0).astype(np.float32)
    assert np.isfinite(x).all(), "non-finite features"
    np.savez_compressed(
        out, pairs=x, mean=x.mean(0), std=x.std(0), joints=np.array(STYLE_JOINTS), dt=CTRL_DT, robot=np.array(robot)
    )
    meta = dict(
        robot=robot,
        pairs=int(len(x)),
        dropped_glitch_pairs=sum(c["dropped"] for c in clips),
        seconds=round(sum(c["seconds"] for c in clips), 1),
        mirrored=mirror_too,
        feature_width=FEAT,
        clips=clips,
    )
    json.dump(meta, open(os.path.splitext(out)[0] + ".json", "w"), indent=1)
    return meta
