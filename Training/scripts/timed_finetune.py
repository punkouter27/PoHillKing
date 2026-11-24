"""Bounded, candidate-only combat fine-tuning with local telemetry and no shipped-asset writes."""

import argparse
import datetime as dt
import hashlib
import json
import os
import socket
import subprocess
import sys
import time
from pathlib import Path

import run_report

ROOT = Path(__file__).resolve().parents[1]
BODIES = ("kim", "trump", "grandpa")
MODELS = ROOT.parent / "Assets" / "_Project" / "RL_Models"


def stop_owned_process(proc):
    # Windows venv launchers can have a real interpreter child; terminating only the launcher leaks the GPU job.
    if os.name == "nt":
        script = """
        $ErrorActionPreference = 'Stop'
        function Stop-OwnedTree([int]$ProcessId) {
            $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId")
            foreach ($child in $children) { Stop-OwnedTree $child.ProcessId }
            try { Stop-Process -Id $ProcessId -Force -ErrorAction Stop }
            catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
                if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { throw }
                Write-Output "Owned process $ProcessId already exited after its children"
            }
        }
        Stop-OwnedTree """ + str(proc.pid)
        subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script], check=True)
    else:
        proc.terminate()
    proc.wait(timeout=30)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--deadline", required=True, help="absolute ISO timestamp including timezone")
    ap.add_argument("--num-envs", type=int, default=512)
    a = ap.parse_args()
    deadline = dt.datetime.fromisoformat(a.deadline)
    if deadline.tzinfo is None:
        ap.error("--deadline must include a timezone")
    left = lambda: (deadline - dt.datetime.now(dt.timezone.utc)).total_seconds()
    if left() < 15 * 60:
        ap.error("at least 15 minutes must remain")
    out = ROOT.parent / "docs" / dt.datetime.now().strftime("%Y%m%d") / "training-90min"
    out.mkdir(parents=True, exist_ok=True)
    models = sorted(p for b in BODIES for p in (MODELS / b).glob("*") if p.is_file())
    hashes = lambda: {str(p.relative_to(MODELS)): hashlib.sha256(p.read_bytes()).hexdigest() for p in models}
    original = hashes()
    state = dict(
        start=dt.datetime.now().astimezone().isoformat(),
        deadline=a.deadline,
        phase="starting",
        pid=os.getpid(),
        baseline={},
        candidate={},
        exported=False,
        shipped_sha256=original,
        pending_gates=["100 consecutive target episodes", "duel regression", "16-player regression", "Unity parity"],
    )
    run_dir = None
    tensorboard = None
    active = None
    last_report = 0
    last_hour = time.monotonic()
    metrics = [
        dict(tag="Train/mean_reward", label="Mean episode reward"),
        dict(tag="Train/mean_episode_length", label="Mean episode length"),
        dict(tag="Policy/mean_std", label="Exploration standard deviation"),
    ]

    def note(message):
        line = f"{dt.datetime.now().astimezone().isoformat(timespec='seconds')} {message}"
        print(line, flush=True)
        with (out / "progress.txt").open("a", encoding="utf-8") as f:
            f.write(line + "\n")

    def report():
        nonlocal last_report, last_hour
        last_report = time.monotonic()
        state["updated"] = dt.datetime.now().astimezone().isoformat()
        state["remaining_minutes"] = round(max(0, left()) / 60, 2)
        state["metrics"] = {}
        runs = [] if run_dir is None else [dict(label=state["body"] + " candidate", dir=str(run_dir))]
        for metric in metrics:
            points = [] if run_dir is None else run_report.series(str(run_dir), metric["tag"])
            if points:
                state["metrics"][metric["tag"]] = dict(iteration=points[-1][0], value=points[-1][1])
        rows = [
            dict(label="shipped " + body, values={k: f"{v:.5f}" for k, v in values.items()})
            for body, values in state["baseline"].items()
        ]
        if state["candidate"]:
            row = dict(state["candidate"])
            row["share_delta"] = row["share"] - state["baseline"][state["body"]]["share"]
            rows.append(dict(label="candidate " + state["body"], values={k: f"{v:.5f}" for k, v in row.items()}))
        spec = dict(
            title="PoHillKing - bounded candidate training",
            subtitle=f"Phase: {state['phase']}; updated {state['updated']}; deadline {a.deadline}",
            sections=[
                dict(
                    title="Combat policy fine-tune",
                    runs=runs,
                    metrics=metrics,
                    evals=rows,
                    summary=dict(
                        toddler="25/25 solo get-up drops passed for each body; keep those policies frozen.",
                        child="Warm-start the weakest measured combat body from its own shipped actor.",
                        adult="Candidate only: consecutive-episode graduation and Unity gates remain mandatory.",
                    ),
                    verdict=state.get("error")
                    or "No shipped policy is replaced. Three/four-body curriculum is not a 16-player gate.",
                )
            ],
        )
        (out / "report.json").write_text(json.dumps(spec, indent=2), encoding="utf-8")
        run_report.main(str(out / "report.json"))
        (out / "status.json").write_text(json.dumps(state, indent=2), encoding="utf-8")
        if time.monotonic() - last_hour >= 3600:
            note(
                "HOURLY STATUS "
                + json.dumps({k: state.get(k) for k in ("body", "phase", "remaining_minutes", "metrics")})
            )
            last_hour = time.monotonic()

    def job(args, label, body, allowed=(0,), seconds=None):
        nonlocal active
        log = out / (label + ".log")
        end = time.monotonic() + min(left(), seconds if seconds is not None else left())
        state.update(phase=label, log=str(log))
        with log.open("w", encoding="utf-8") as f:
            active = subprocess.Popen(
                [sys.executable] + args,
                cwd=ROOT,
                env=dict(os.environ, KOTH_ROBOT=body, PYTHONUNBUFFERED="1"),
                stdout=f,
                stderr=subprocess.STDOUT,
            )
            state["child_pid"] = active.pid
            report()
            while active.poll() is None:
                if time.monotonic() >= end:
                    stop_owned_process(active)
                    raise TimeoutError(f"{label} exceeded its deadline; periodic checkpoints retained")
                if time.monotonic() - last_report >= 60:
                    report()
                time.sleep(2)
        code = active.returncode
        active = None
        if code not in allowed:
            raise RuntimeError(f"{label} failed ({code}); see {log}")
        return [
            json.loads(line)
            for line in log.read_text(encoding="utf-8").splitlines()
            if line.startswith("{") and line.endswith("}")
        ]

    def evaluate(body, ckpt, label):
        others = [b for b in BODIES if b != body]
        rows = job(
            ["scripts/eval.py", "--rung", body + "_hill", "--ckpt", ckpt, "--opponent"]
            + [f"runs/shipped/{b}/model_final.pt:slot{i + 1}" for i, b in enumerate(others)]
            + ["--seeds", "3", "--num-envs", "128", "--seconds", "20"],
            label,
            body,
            (0, 1),
            8 * 60,
        )
        if len(rows) != 3:
            raise RuntimeError(f"{label}: expected three evaluation seeds, got {len(rows)}")
        return dict(
            share=sum(r["learner_last_one_in"] for r in rows) / len(rows),
            jitter=sum(r["jitter_rad"] for r in rows) / len(rows),
        )

    try:
        note(
            f"Budget ends {a.deadline}; one {a.num_envs}-world run; reserve 12 minutes for final evaluation. No auto-export."
        )
        for port in range(6007, 6020):
            with socket.socket() as s:
                if s.connect_ex(("127.0.0.1", port)) != 0:
                    break
        else:
            raise RuntimeError("No free TensorBoard port in 6007-6019")
        with (out / "tensorboard.log").open("w") as f:
            tensorboard = subprocess.Popen(
                [
                    sys.executable,
                    "-m",
                    "tensorboard.main",
                    "--logdir",
                    str(ROOT / "runs"),
                    "--host",
                    "127.0.0.1",
                    "--port",
                    str(port),
                ],
                stdout=f,
                stderr=subprocess.STDOUT,
            )
        state["tensorboard"] = f"http://127.0.0.1:{port}"
        for body in BODIES:
            state["baseline"][body] = evaluate(body, f"runs/shipped/{body}/model_final.pt", body + "-baseline")
            report()
        body = min(BODIES, key=lambda b: state["baseline"][b]["share"])
        state["body"] = body
        note(f"Baseline {state['baseline']}; selected {body} for body-specific fine-tuning.")
        budget = left() / 60 - 12
        if budget < 5:
            raise RuntimeError("Insufficient time remains for meaningful fine-tuning after baselines")
        name = f"{body}_bounded_{dt.datetime.now():%y%m%d_%H%M%S}"
        run_dir = ROOT / "runs" / (body + "_hill4") / name
        state["run_dir"] = str(run_dir)
        others = [b for b in BODIES if b != body]
        warm = f"runs/shipped/{body}/model_final.pt"
        sidecar = MODELS / body / "fighter_action.json"
        action = json.loads(sidecar.read_text()) if sidecar.exists() else {}
        args = (
            [
                "scripts/train.py",
                "--rung",
                body + "_hill4",
                "--name",
                name,
                "--num-envs",
                str(a.num_envs),
                "--iters",
                "100000",
                "--warm",
                warm,
                "--opponent",
                warm + ":slot1",
                warm + ":slot1:live",
            ]
            + [f"runs/shipped/{b}/model_final.pt:slot{i + 2}" for i, b in enumerate(others)]
            + [
                "--frozen-deterministic",
                "--critic-warmup",
                "15",
                "--self-play",
                "25",
                "--reward",
                "draw=-20",
                "--action-smooth",
                str(action.get("smooth", 0)),
                "--checkpoint-only",
                "--save-interval",
                "25",
                "--max-minutes",
                str(budget),
            ]
        )
        note(f"Training ETA: up to {budget:.1f} minutes including initialization; {name}.")
        job(args, "training", body, seconds=(budget + 2) * 60)
        checkpoint = run_dir / "model_final.pt"
        if not checkpoint.is_file():
            raise RuntimeError("Training returned without its final checkpoint")
        manifest = json.loads((run_dir / "manifest.json").read_text())
        state["training"] = dict(checkpoint=str(checkpoint), iterations=manifest["iterations"], bars=manifest["bars"])
        state["candidate"] = evaluate(body, str(checkpoint), "candidate-evaluation")
        state["phase"] = "completed-candidate-not-graduated"
        note("Finished. " + json.dumps(state["candidate"]) + "; no policy exported or promoted.")
    except Exception as error:
        state.update(phase="failed", error=f"{type(error).__name__}: {error}")
        note(state["error"])
        raise
    finally:
        if active is not None and active.poll() is None:
            stop_owned_process(active)
        if tensorboard is not None and tensorboard.poll() is None:
            stop_owned_process(tensorboard)
        state["shipped_assets_unchanged"] = hashes() == original
        report()
        if not state["shipped_assets_unchanged"]:
            raise RuntimeError("Shipped assets changed during candidate-only training")


if __name__ == "__main__":
    main()
