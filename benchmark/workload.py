"""PoHillKing Workload: the real training/scripts/train.py (rsl_rl PPO on KothEnv), stage 0 of the staged recipe.

Run by serve.py with the training venv:  training/.venv/Scripts/python.exe benchmark/workload.py
rsl_rl's runner hands collect_time and learn_time to Logger.log once per iteration; the hook below feeds them to
the Meter (one iteration = num_steps_per_env Steps), which ends the process after the measured window.
"""

from __future__ import annotations

import os
import runpy
import shutil
import sys
from importlib import metadata
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import core  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
TRAINING = ROOT / "training"
RUNG, NAME = "kim_r0b", "_benchmark"
# Same settings as the main training run (STAGED_TRAINING.md, stage 0). Change these and the Workload ID changes.
ARGS = ["--rung", RUNG, "--name", NAME, "--num-envs", "4096", "--iters", "100000", "--save-interval", "100000"]


def version(pkg: str) -> str | None:
    try:
        return metadata.version(pkg)
    except metadata.PackageNotFoundError:
        return None


def main() -> None:
    from rsl_rl.runners import OnPolicyRunner
    from rsl_rl.utils.logger import Logger

    meter: core.Meter | None = None
    init = OnPolicyRunner.__init__

    def init_(self, env, train_cfg, *a, **k):
        nonlocal meter
        init(self, env, train_cfg, *a, **k)
        policy = train_cfg.get("policy") or train_cfg.get("actor") or {}
        workload = dict(
            repo="PoHillKing",
            trainer=f"rsl_rl {version('rsl-rl-lib')}",
            script="training/scripts/train.py",
            args=" ".join(ARGS),
            num_envs=env.num_envs,
            horizon=train_cfg["num_steps_per_env"],
            net=policy.get("actor_hidden_dims") or policy.get("hidden_dims"),
            config_hash=core.workload_id({"train": train_cfg, "env": getattr(env, "cfg", None)}),
        )
        versions = dict(python=sys.version.split()[0], torch=version("torch"), mujoco_warp=version("mujoco-warp"))
        meter = core.Meter(workload, versions)

    log = Logger.log

    def log_(self, *a, collect_time: float, learn_time: float, **k):
        log(self, *a, collect_time=collect_time, learn_time=learn_time, **k)
        meter.block(collect_time, learn_time, meter.workload["horizon"])

    OnPolicyRunner.__init__, Logger.log = init_, log_
    shutil.rmtree(TRAINING / "runs" / RUNG / NAME, ignore_errors=True)
    os.chdir(TRAINING)
    sys.path.insert(0, str(TRAINING))
    sys.argv = ["scripts/train.py", *ARGS]
    try:
        runpy.run_path(str(TRAINING / "scripts" / "train.py"), run_name="__main__")
    except BaseException as e:  # noqa: BLE001 - every failure becomes a failed row, never a silent miss
        if meter is None:
            meter = core.Meter({"repo": "PoHillKing", "args": " ".join(ARGS)}, {"python": sys.version.split()[0]})
        meter.finish(ok=False, error=core.fail_text(e))


if __name__ == "__main__":
    main()
