"""Does the action low-pass make the hill brains stall in their own mirror? (log 2026-10-29: the eight brains trained with
smooth 0.6 + action_rate -0.03 decide 0.004-0.45 of their mirror duels, the unsmoothed fighters 0.89-0.98.)

Rung <body>_hill4 with live self-play (as overnight_hill4.py), warm from the shipped brain, ITERS iterations per arm;
each arm sets the low-pass (train.py --action-smooth) and the action_rate weight. smooth 0.6 / -0.03 is not rerun: the
overnight runs are its arm. Measured like overnight_hill4 (mirror decided, three-way vs the shipped other two, jitter,
decided share in the duel vs the shipped brain). Nothing is exported.

  .venv/Scripts/python.exe scripts/archive/sweep_smooth.py [--iters 200] [--bodies kim grandpa]
Progress: runs/overnight/progress_smooth.txt; rows: runs/overnight/smooth_sweep.jsonl.
"""
import argparse, os, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import queue_hill as q
import overnight_hill4 as w
import queue_hill4 as h

q.PROGRESS = os.path.join(q.LOG_DIR, "progress_smooth.txt")
ROWS = os.path.join(q.LOG_DIR, "smooth_sweep.jsonl")
ARMS = [(0.0, -0.01), (0.3, -0.015)]          # (smooth, action_rate); -0.01 is the rungs' default, -0.03 the hill recipe


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--iters", type=int, default=200); ap.add_argument("--num-envs", type=int, default=1024)
    ap.add_argument("--bodies", nargs="+", default=["kim", "grandpa"]); a = ap.parse_args()
    q.note(f"=== sweep_smooth: {a.bodies} x {ARMS}, {a.iters} iterations, {a.num_envs} worlds ===")
    q.ensure_tensorboard()
    ship = {b: h.shipped(b) for b in q.HUMANS}
    for me in a.bodies:
        for smooth, rate in ARMS:
            w.sweep_arm(me, f"{me}_sm{int(smooth * 10)}", ship, a.iters, a.num_envs,
                        ["--action-smooth", str(smooth), "--reward", "draw=-20", f"action_rate={rate}"],
                        f"smooth {smooth}, action_rate {rate}", ROWS, dict(smooth=smooth, action_rate=rate))
    q.note("=== sweep_smooth done ===")


if __name__ == "__main__":
    main()
