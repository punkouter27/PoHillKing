"""Checks of the motion prior that need no GPU and no training run:
  uv run python scripts/test_amp.py            (or pytest scripts/test_amp.py)

  retargeting   a retargeted clip, posed on a fighter's own MuJoCo model, points its limbs where the capture's limbs point
  features      layout, mirror symmetry, a standing fighter reads as standing
  reward        the style reward's range; a pair that crosses a reset is neither rewarded nor learned from
"""
import os, sys, types
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import numpy as np, torch, mujoco
from koth import mocap

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAW = os.path.join(ROOT, "assets", "mocap", "raw")
DATA = os.path.join(ROOT, "assets", "mocap", "cmu_style_kim.npz")


def _angle(a, b):
    a = a / np.linalg.norm(a, axis=-1, keepdims=True); b = b / np.linalg.norm(b, axis=-1, keepdims=True)
    return np.degrees(np.arccos(np.clip((a * b).sum(-1), -1, 1)))


def _quat(R):
    q = np.zeros(4); mujoco.mju_mat2Quat(q, R.reshape(9)); return q


def limb_errors(body: str, clip: str, every: int = 12):
    """Mean angle (degrees) between each limb segment of the posed fighter and of the capture, over a clip."""
    path = os.path.join(RAW, clip + ".bvh")
    names, parents, offsets, chans, data, _ = mocap.parse_bvh(path)
    pos, _ = mocap.bvh_fk(names, parents, offsets, chans, data)
    P = {n: np.einsum("ij,fj->fi", mocap.M_BVH, pos[1:, k]) for k, n in enumerate(names)}
    m = mujoco.MjModel.from_xml_path(os.path.join(ROOT, "assets", body, "scene_flat_1p.xml")); d = mujoco.MjData(m)
    jname = lambda j: mujoco.mj_id2name(m, mujoco.mjtObj.mjOBJ_JOINT, j)
    root = next(j for j in range(m.njnt) if m.jnt_type[j] == mujoco.mjtJoint.mjJNT_FREE); prefix = jname(root).split("floating")[0] if "floating" in jname(root) else ""
    if not prefix:                                  # the free joint's name does not carry the prefix: take it from a hinge
        prefix = next(jname(j) for j in range(m.njnt) if jname(j).endswith("left_knee_joint"))[:-len("left_knee_joint")]
    qadr = [m.jnt_qposadr[mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_JOINT, prefix + n + "_joint")] for n in mocap.JOINTS]
    radr = m.jnt_qposadr[root]; bid = lambda n: mujoco.mj_name2id(m, mujoco.mjtObj.mjOBJ_BODY, prefix + n)
    segs = {"thigh": ("hip_yaw_link", "knee_link", "UpLeg", "Leg"), "shank": ("knee_link", "ankle_roll_link", "Leg", "Foot"),
            "upper arm": ("shoulder_yaw_link", "elbow_link", "Arm", "ForeArm"), "forearm": ("elbow_link", "wrist_yaw_link", "ForeArm", "Hand")}
    err = {k: [] for k in segs}
    c = mocap.retarget(path, mocap.body_limbs(m, prefix))
    for f in range(0, len(c["q"]), every):
        d.qpos[:] = m.qpos0; d.qpos[radr + 3:radr + 7] = _quat(c["R"][f]); d.qpos[qadr] = c["q"][f]
        mujoco.mj_kinematics(m, d)
        for k, (b0, b1, m0, m1) in segs.items():
            for side, cap in (("left_", "Left"), ("right_", "Right")):
                err[k].append(_angle(d.xpos[bid(side + b1)] - d.xpos[bid(side + b0)], P[cap + m1][f] - P[cap + m0][f]))
    return {k: float(np.mean(v)) for k, v in err.items()}


def test_retargeting_matches_the_capture():
    if not os.path.isdir(RAW): print("skipped: no raw clips (scripts/fetch_mocap.py)"); return
    for body in ("kim", "trump", "grandpa"):
        for clip in ("07_01", "14_01"):
            e = limb_errors(body, clip); print(f"  {body:8s} {clip}: " + ", ".join(f"{k} {v:.1f} deg" for k, v in e.items()))
            # what remains is what the body's joint ranges cannot reach; an axis or sign mistake shows as tens of degrees
            assert max(e.values()) < 6.0, (body, clip, e)


def test_feature_layout_and_mirror():
    assert mocap.FEAT == 52 and mocap.NJ == 21 and len(mocap.JOINTS) == 29
    assert not any("wrist" in n or "ankle_roll" in n for n in mocap.STYLE_JOINTS)
    rng = np.random.default_rng(0); x = rng.normal(size=(5, 2 * mocap.FEAT)).astype(np.float32)
    assert np.allclose(mocap.mirror(mocap.mirror(x)), x), "mirroring twice is the identity"
    assert not np.allclose(mocap.mirror(x), x)
    if not os.path.exists(DATA): print("skipped: no dataset"); return
    z = np.load(DATA); p = z["pairs"]
    assert p.shape[1] == 2 * mocap.FEAT and np.isfinite(p).all() and list(z["joints"]) == mocap.STYLE_JOINTS
    h = p[:, 0]; g = p[:, 1:4]
    assert 0.8 < np.median(h) < 1.1, f"standing pelvis height in standing heights: {np.median(h)}"
    assert np.allclose(np.linalg.norm(g, axis=1), 1, atol=1e-3) and np.median(g[:, 2]) < -0.95, "gravity points down in the pelvis frame"
    knee = p[:, 10 + mocap.STYLE_JOINTS.index("left_knee")]; elbow = p[:, 10 + mocap.STYLE_JOINTS.index("left_elbow")]
    assert knee.min() > -0.2 and elbow.min() > -0.2, "knees and elbows bend one way only"
    assert str(z["robot"]) == "kim"
    # consecutive frames: the second half of a pair is 20 ms after the first
    assert np.abs(p[:, mocap.FEAT] - p[:, 0]).max() < 0.1


def test_standing_fighter_reads_as_standing():
    from koth import amp
    N, A = 3, 1; q = torch.zeros(N, 7 + 29); q[:, 2] = 0.75; q[:, 3] = 1.0; v = torch.zeros(N, 6 + 29)
    env = types.SimpleNamespace(N=N, A=A, M=N * A, arena=False, qpos=q, qvel=v, rq7=torch.arange(7)[None], rd6=torch.arange(6)[None],
                                key_root_z=torch.tensor([0.75]), _flat=lambda x: x.reshape(N * A, *x.shape[2:]),
                                _joints=lambda: (q[:, 7:], v[:, 6:]))
    f = amp.env_features(env)
    assert f.shape == (N, mocap.FEAT) and torch.allclose(f[:, 0], torch.ones(N)) and torch.allclose(f[:, 1:4], torch.tensor([0.0, 0, -1]).expand(N, 3), atol=1e-6)
    assert float(f[:, 4:].abs().max()) == 0.0


def test_reward_range_and_reset_pairs():
    from koth import amp
    d = torch.tensor([-5.0, -1.0, 0.0, 1.0, 3.0, 9.0])
    r = amp.style_reward(d); assert float(r.min()) >= 0 and float(r.max()) <= 1 and float(r[3]) == 1.0 and float(r[1]) == 0.0
    mp = amp.MotionPrior.__new__(amp.MotionPrior)
    mp.buf = torch.zeros(8, 2 * mocap.FEAT); mp.head = 0; mp.filled = 0
    mp.prev = torch.ones(4, mocap.FEAT); cur = torch.full((4, mocap.FEAT), 2.0); cur[3, 0] = float("nan")
    x, valid = mp.pairs(cur, torch.tensor([False, True, False, False]))
    assert valid.tolist() == [True, False, True, False], "a reset row and a non-finite row make no pair"
    mp._store(x[valid]); assert mp.filled == 2 and mp.head == 2
    for _ in range(5): mp._store(x[valid])
    assert mp.filled == 8 and mp.head == 4, "the buffer is a ring"
    disc = amp.Discriminator(torch.zeros(2 * mocap.FEAT), torch.ones(2 * mocap.FEAT)); out = disc(torch.randn(7, 2 * mocap.FEAT))
    assert out.shape == (7,) and torch.isfinite(out).all()


if __name__ == "__main__":
    for name, fn in list(globals().items()):
        if name.startswith("test_"): print(name); fn()
    print("ok")
