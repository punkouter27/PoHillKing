"""The motion-quality penalties (torque^2, body angular velocity^2, action rate, foot slip, joint speed^2) and the
training config they are read from (training/config/reward_penalties.json)."""

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
