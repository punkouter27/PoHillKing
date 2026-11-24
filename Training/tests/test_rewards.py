"""The motion-quality penalties (torque^2, body angular velocity^2, action rate, foot slip, joint speed^2) and the
training config they are read from (Training/config/reward_penalties.json)."""

import json
import os

import pytest
import torch

from koth import env as koth_env

PENALTIES = ("joint_torque", "ang_vel_xy", "action_rate", "feet_slip", "joint_vel")


def _config() -> dict:
    with open(koth_env.PENALTIES_JSON, encoding="utf-8") as f:
        return {k: v for k, v in json.load(f).items() if not k.startswith("_")}


def test_every_penalty_has_a_negative_weight_in_the_config():
    cfg = _config()
    for k in PENALTIES:
        assert k in cfg, f"{k} missing from {os.path.basename(koth_env.PENALTIES_JSON)}"
        assert cfg[k] < 0.0, f"{k} is a penalty: its weight must be negative"


def test_base_reward_reads_the_config():
    cfg = _config()
    reward = koth_env.default_cfg("r0")["reward"]
    for k, v in cfg.items():
        assert reward[k] == pytest.approx(v)


def test_rungs_that_reset_the_reward_keep_their_own_weights():
    # rung getup rebuilds its reward from zero: the torque term stays off there, so the shipped get-up recipe is unchanged
    reward = koth_env.default_cfg("getup")["reward"]
    assert reward["joint_torque"] == 0.0
    assert reward["action_rate"] == pytest.approx(-0.01)


@pytest.mark.gpu
@pytest.mark.slow
@pytest.mark.skipif(not torch.cuda.is_available(), reason="MuJoCo Warp env needs CUDA")
def test_duel_step_without_victory_hold_and_torque_term():
    """Two robots, victory_s == 0: the done branch built its masks on an undefined `device` (NameError) until 2026-11-20."""
    cfg = koth_env.default_cfg("r3")
    assert cfg["victory_s"] == 0.0 and len(cfg["robots"]) == 2
    env = koth_env.KothEnv(cfg, num_envs=2, device="cuda", seed=0)
    env.reset()
    _, rew, _, _ = env.step(torch.zeros(env.num_envs, 29, device=env.device))
    assert torch.isfinite(rew).all()
    assert "joint_torque" in env.reward_terms
    assert (env.reward_terms["joint_torque"] <= 0.0).all()


def _reward_stub(rows: int = 4):
    """A KothEnv with only what _rewards reads (one robot per world, flat floor, rung r0's weights), so the reward
    terms run on the CPU without MuJoCo Warp. Joint/body indices are those of a 29-joint body."""
    env = koth_env.KothEnv.__new__(koth_env.KothEnv)
    cfg = koth_env.default_cfg("r0")
    env.cfg, env.ctrl_dt, env.A, env.N, env.M, env.arena = cfg, 0.02, 1, rows, rows, False
    nq, nv, nbody = 7 + 29, 6 + 29, 32
    g = torch.Generator().manual_seed(0)
    env.qpos = torch.randn(rows, nq, generator=g) * 0.2
    env.qvel = torch.randn(rows, nv, generator=g)
    env.qadr = torch.arange(7, nq)[None]
    env.dadr = torch.arange(6, nv)[None]
    env.aid = torch.arange(29)[None]
    env.actuator_force = torch.randn(rows, 29, generator=g) * 50.0
    env.xpos = torch.randn(rows, nbody, 3, generator=g) * 0.1
    env.feet_body, env.hand_body = torch.tensor([[12, 13]]), torch.tensor([[30, 31]])
    env.foot_rest_z = torch.zeros(rows, 1)
    env.feet_contact = torch.ones(rows, 2, dtype=torch.bool)
    env.feet_air_time = torch.zeros(rows, 2)
    env.prev_feet_xy = env.xpos[:, [12, 13], :2] + 0.01  # each foot moved 1.4 cm since the last step: slip
    env.phase = torch.zeros(rows)
    env.command = torch.tensor([[0.5, 0.0, 0.2]]).repeat(rows, 1)
    env.yaw_target = torch.zeros(rows)
    env.default_pose = torch.zeros(rows, 29)
    env.pose_offset = torch.zeros(rows, 29)
    env.breath_off = torch.zeros(rows, 29)
    env.hip_idx, env.knee_idx = torch.tensor([1, 2, 7, 8]), torch.tensor([3, 9])
    env.upper_idx = env.arm_idx = torch.arange(15, 29)
    env.jnt_lo, env.jnt_hi = -torch.ones(29) * 2.0, torch.ones(29) * 2.0
    env.action = torch.randn(rows, 29, generator=g)
    env.last_action = torch.zeros(rows, 29)
    env.prev_action2 = torch.zeros(rows, 29)
    return env


def test_each_penalty_is_logged_under_its_own_key():
    """The reward the trainer logs (KothEnv.step -> extras["log"] -> TensorBoard): every motion penalty of
    reward_penalties.json is computed as its own term, charged as a negative reward for the motion it is named after,
    and logged under its own rew/<term> key (not lumped into one penalty)."""
    env = _reward_stub()
    pos = torch.tensor([[0.0, 0.0, 0.8]]).repeat(env.M, 1)
    quat = torch.tensor([[1.0, 0.0, 0.0, 0.0]]).repeat(env.M, 1)
    linv = torch.tensor([[0.3, 0.0, 0.1]]).repeat(env.M, 1)
    angv = torch.tensor([[0.5, -0.4, 0.2]]).repeat(env.M, 1)
    grav = torch.tensor([[0.0, 0.0, -1.0]]).repeat(env.M, 1)
    feet = torch.ones(env.M, 2, dtype=torch.bool)
    env._rewards(feet, pos, quat, linv, angv, grav)
    cfg = _config()
    log = koth_env.reward_log(env.reward_terms)
    for k in PENALTIES:
        assert k in env.reward_terms, f"{k} is not a reward term of its own"
        assert (env.reward_terms[k] < 0).all(), f"{k} is not charged as a penalty"
        assert f"rew/{k}" in log, f"{k} has no log key of its own"
        assert float(log[f"rew/{k}"]) == pytest.approx(float(env.reward_terms[k].mean()))
    # each logged value is that term's weight x its own measure x dt, e.g. the angular-velocity term
    assert float(log["rew/ang_vel_xy"]) == pytest.approx(cfg["ang_vel_xy"] * (0.5**2 + 0.4**2) * env.ctrl_dt, rel=1e-5)
    assert len(log) == len(env.reward_terms)


def test_reward_log_uses_the_given_rows_only():
    terms = {"joint_torque": torch.tensor([-1.0, -3.0, -100.0]), "alive": torch.tensor([1.0, 1.0, 1.0])}
    log = koth_env.reward_log(terms, torch.tensor([0, 1]))
    assert set(log) == {"rew/joint_torque", "rew/alive"}
    assert float(log["rew/joint_torque"]) == pytest.approx(-2.0)
