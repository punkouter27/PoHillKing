"""Mirror-only shaping for the hill brains' stalemates (log 2026-10-29: neither live self-play nor dropping the action
low-pass gets the mirror duels decided: kim_sm0 0.103, the overnight runs 0.004-0.45).

Rung <body>_hill4 with live self-play, warm from the shipped brain, on the shipped recipe (smooth 0.6, action_rate -0.03),
plus three terms paid only in the duels against the body's own kind (env reward mirror_push_out, mirror_stalemate,
mirror_standoff, outside the clip): the opponent's outward speed while I am on it, a penalty once both have stood within 0.6 m below 0.5 m/s for
2 s (the rung's stalemate_rule), and one for every second more than 1 m apart after the first 3 s (mirror_standoff: the
hill brains keep away from a copy of themselves, kim_hill4b at a median 2.5 m, scripts/stall_probe.py). Waits for sweep_smooth.py to finish (one training on the GPU at a time). Nothing is exported.

  .venv/Scripts/python.exe scripts/archive/sweep_mirror.py [--iters 200] [--bodies kim grandpa trump] [--push 1.0] [--stall -1.0] [--standoff -1.0] [--run mir2]
Progress: runs/overnight/progress_mirror.txt; rows: runs/overnight/mirror_sweep.jsonl.
"""
import argparse, os, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import queue_hill as q
import overnight_hill4 as w
import queue_hill4 as h

q.PROGRESS = os.path.join(q.LOG_DIR, "progress_mirror.txt")
ROWS = os.path.join(q.LOG_DIR, "mirror_sweep.jsonl")
SMOOTH_LOG = os.path.join(q.LOG_DIR, "progress_smooth.txt")


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--iters", type=int, default=200); ap.add_argument("--num-envs", type=int, default=1024)
    ap.add_argument("--bodies", nargs="+", default=["kim", "grandpa", "trump"])
    ap.add_argument("--push", type=float, default=1.0); ap.add_argument("--stall", type=float, default=-1.0)
    ap.add_argument("--standoff", type=float, default=-1.0); ap.add_argument("--run", default="mir2"); a = ap.parse_args()
    label = f"mirror_push_out {a.push}, mirror_stalemate {a.stall}, mirror_standoff {a.standoff}"
    q.note(f"=== sweep_mirror: {a.bodies}, {label}, {a.iters} iterations: waiting for sweep_smooth ===")
    q.wait_done(SMOOTH_LOG, "sweep_smooth")
    q.ensure_tensorboard()
    ship = {b: h.shipped(b) for b in q.HUMANS}
    for me in a.bodies:
        w.sweep_arm(me, f"{me}_{a.run}", ship, a.iters, a.num_envs,
                    ["--reward", "draw=-20", f"mirror_push_out={a.push}", f"mirror_stalemate={a.stall}", f"mirror_standoff={a.standoff}"],
                    label, ROWS, dict(push=a.push, stall=a.stall, standoff=a.standoff))
    q.note("=== sweep_mirror done ===")


if __name__ == "__main__":
    main()
