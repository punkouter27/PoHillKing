"""Physics rules of every body (AGENTS.md 1.3.3 Physical fidelity / Collision rules), checked on the MJCF scenes the
brains train with (Training/assets/<body>/scene_flat_1p.xml, written by koth/build_mjcf.py; Unity imports the same
model): Earth gravity, every hinge limited and held at its limit, PD position servos with a torque cap, no contact
between adjacent (parent / child) bodies in any pose, and friction on every colliding geom. CPU MuJoCo, seconds.
"""

from __future__ import annotations

import os

import mujoco
import numpy as np
import pytest

ASSETS = os.path.normpath(os.path.join(os.path.dirname(__file__), "..", "assets"))
# The game's bodies (Kim, Trump, Grandpa), the brawler (built, not trained yet) and the G1 the human recipes came from.
BODIES = ("kim", "trump", "grandpa", "brawler", "g1")


@pytest.fixture(scope="module", params=BODIES)
def model(request):
    path = os.path.join(ASSETS, request.param, "scene_flat_1p.xml")
    return request.param, mujoco.MjModel.from_xml_path(path)


def _hinges(m):
    return [j for j in range(m.njnt) if m.jnt_type[j] == mujoco.mjtJoint.mjJNT_HINGE]


def _colliding_body_geoms(m):
    return [
        g for g in range(m.ngeom) if m.geom_bodyid[g] != 0 and (m.geom_contype[g] != 0 or m.geom_conaffinity[g] != 0)
    ]


def test_gravity_is_earth_and_acts(model):
    name, m = model
    np.testing.assert_allclose(m.opt.gravity, [0.0, 0.0, -9.81], err_msg=name)
    d = mujoco.MjData(m)
    mujoco.mj_resetData(m, d)
    d.qpos[2] += 2.0  # well above the floor: free fall for 0.1 s
    z0 = d.qpos[2]
    steps = int(round(0.1 / m.opt.timestep))
    for _ in range(steps):
        mujoco.mj_step(m, d)
    t = steps * m.opt.timestep
    assert z0 - d.qpos[2] == pytest.approx(0.5 * 9.81 * t * t, rel=0.05), name


def test_every_hinge_has_a_limit(model):
    name, m = model
    hinges = _hinges(m)
    assert len(hinges) == 29, f"{name}: {len(hinges)} hinges (the shared 29-joint order, joint_map.json)"
    for j in hinges:
        lo, hi = np.degrees(m.jnt_range[j])
        assert m.jnt_limited[j], f"{name}: {m.joint(j).name} has no range"
        assert -180.0 < lo < hi < 180.0, f"{name}: {m.joint(j).name} range {lo:.0f}..{hi:.0f} deg"


@pytest.mark.parametrize("side", [0, 1], ids=["lower", "upper"])
def test_limits_hold_when_driven_past_them(model, side):
    """Every actuator commanded to the end of its ctrl range (the most a brain can ask), root pinned in the air, 1 s:
    no joint ends more than 3 deg past its range."""
    name, m = model
    d = mujoco.MjData(m)
    mujoco.mj_resetData(m, d)
    d.qpos[2] += 1.0
    root = d.qpos[:7].copy()
    for i in range(m.nu):
        d.ctrl[i] = (
            m.actuator_ctrlrange[i, side] if m.actuator_ctrllimited[i] else m.jnt_range[m.actuator_trnid[i, 0], side]
        )
    for _ in range(int(round(1.0 / m.opt.timestep))):
        d.qpos[:7] = root
        d.qvel[:6] = 0.0
        mujoco.mj_step(m, d)
    worst, joint = 0.0, ""
    for i in range(m.nu):
        j = int(m.actuator_trnid[i, 0])
        q = d.qpos[m.jnt_qposadr[j]]
        past = q - m.jnt_range[j, 1] if side else m.jnt_range[j, 0] - q
        if past > worst:
            worst, joint = past, m.joint(j).name
    assert np.degrees(worst) < 3.0, f"{name}: {joint} ended {np.degrees(worst):.1f} deg past its limit"


def test_actuators_are_pd_servos_with_a_torque_cap(model):
    name, m = model
    assert m.nu == 29, name
    for i in range(m.nu):
        kp, kv = m.actuator_gainprm[i, 0], -m.actuator_biasprm[i, 2]
        assert m.actuator_trntype[i] == mujoco.mjtTrn.mjTRN_JOINT, name
        assert m.actuator_gaintype[i] == mujoco.mjtGain.mjGAIN_FIXED, name
        assert m.actuator_biastype[i] == mujoco.mjtBias.mjBIAS_AFFINE, name
        assert kp > 0 and kv > 0 and m.actuator_biasprm[i, 1] == pytest.approx(-kp), f"{name}: actuator {i} is not PD"
        assert m.actuator_forcelimited[i] and m.actuator_forcerange[i, 1] > 0, f"{name}: actuator {i} has no torque cap"
    # the force MuJoCo applies is kp (target - q) - kv qdot, clipped to the cap
    d = mujoco.MjData(m)
    mujoco.mj_resetData(m, d)
    rng = np.random.default_rng(0)
    d.qpos[7:] += rng.uniform(-0.1, 0.1, m.nq - 7)
    d.qvel[6:] = rng.uniform(-1.0, 1.0, m.nv - 6)
    d.ctrl[:] = np.clip(rng.uniform(-0.3, 0.3, m.nu), m.actuator_ctrlrange[:, 0], m.actuator_ctrlrange[:, 1])
    mujoco.mj_forward(m, d)
    for i in range(m.nu):
        j = int(m.actuator_trnid[i, 0])
        q, qd = d.qpos[m.jnt_qposadr[j]], d.qvel[m.jnt_dofadr[j]]
        want = m.actuator_gainprm[i, 0] * (d.ctrl[i] - q) + m.actuator_biasprm[i, 2] * qd
        want = np.clip(want, *m.actuator_forcerange[i])
        assert d.actuator_force[i] == pytest.approx(want, rel=1e-6, abs=1e-6), f"{name}: actuator {i}"


def test_adjacent_bodies_never_touch(model):
    """Parent / child capsules overlap at every joint; MuJoCo's parent filter must keep them out of the contact list in
    any pose (300 random poses inside the joint ranges, body lifted off the floor)."""
    name, m = model
    assert not (m.opt.disableflags & mujoco.mjtDisableBit.mjDSBL_FILTERPARENT), f"{name}: parent filter disabled"
    d = mujoco.MjData(m)
    rng = np.random.default_rng(1)
    hinges = _hinges(m)
    for _ in range(300):
        mujoco.mj_resetData(m, d)
        for j in hinges:
            d.qpos[m.jnt_qposadr[j]] = rng.uniform(*m.jnt_range[j])
        d.qpos[2] += 2.0
        mujoco.mj_forward(m, d)
        for c in d.contact[: d.ncon]:
            b1, b2 = m.geom_bodyid[c.geom1], m.geom_bodyid[c.geom2]
            if 0 in (b1, b2):
                continue
            assert m.body_parentid[b1] != b2 and m.body_parentid[b2] != b1, (
                f"{name}: adjacent bodies {m.body(b1).name} / {m.body(b2).name} in contact"
            )


def test_every_colliding_geom_has_friction(model):
    name, m = model
    geoms = _colliding_body_geoms(m)
    assert len(geoms) >= 20, name
    for g in geoms:
        assert m.geom_friction[g, 0] >= 0.5, f"{name}: {m.geom(g).name} sliding friction {m.geom_friction[g, 0]}"
        assert m.geom_condim[g] >= 3, f"{name}: {m.geom(g).name} condim {m.geom_condim[g]} (no tangential friction)"
    floor = m.geom("floor").id
    assert m.geom_friction[floor, 0] >= 0.5, f"{name}: floor friction"


def test_friction_holds_a_standing_body(model):
    """Friction does work: the body in its rest pose, servos holding it, does not slide on the floor in 0.5 s."""
    name, m = model
    d = mujoco.MjData(m)
    if m.nkey:
        mujoco.mj_resetDataKeyframe(m, d, 0)
    else:
        mujoco.mj_resetData(m, d)
    for i in range(m.nu):
        d.ctrl[i] = d.qpos[m.jnt_qposadr[m.actuator_trnid[i, 0]]]
    mujoco.mj_forward(m, d)
    feet = [b for b in range(m.nbody) if "ankle_roll" in m.body(b).name]
    assert len(feet) == 2, f"{name}: feet {[m.body(b).name for b in feet]}"
    xy0 = d.xpos[feet, :2].copy()
    for _ in range(int(round(0.5 / m.opt.timestep))):
        mujoco.mj_step(m, d)
    slid = np.linalg.norm(d.xpos[feet, :2] - xy0, axis=1).max()
    assert slid < 0.05, f"{name}: a foot slid {slid * 100:.1f} cm"
