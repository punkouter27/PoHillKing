"""Two hours of mirror shaping (2026-10-29): kim_mir2 passed judge_hill but still stalls in its mirror (decided 0.018), and
trump_mir2 was cut at iteration 100. Both continue warm from their mir2 with live self-play:

  kim_mir3    200 iterations, mirror_push_out 2, mirror_stalemate -3, mirror_standoff -0.5 (the standoff term also
              charges the copy that pushed the other away; halved)
  trump_mir3  100 iterations on mir2's terms (1, -1, -1)

then each is judged as judge_hill.py judges (three-way, jitter, duels vs the shipped other two) against the shipped
numbers in runs/overnight/judge_hill.json, once judge_hill.py is done. Nothing is exported.

  .venv/Scripts/python.exe scripts/archive/queue_mir3.py
Progress: runs/overnight/progress_mir3.txt; rows: runs/overnight/mirror_sweep.jsonl; verdicts: runs/overnight/judge_mir3.json.
"""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import queue_hill as q
import overnight_hill4 as w
import queue_hill4 as h
import judge_hill as j

q.PROGRESS = os.path.join(q.LOG_DIR, "progress_mir3.txt")         # judge_hill points it at its own log on import
ROWS = os.path.join(q.LOG_DIR, "mirror_sweep.jsonl")
OUT = os.path.join(q.LOG_DIR, "judge_mir3.json")
ARMS = [("kim", 200, (2.0, -3.0, -0.5)), ("trump", 100, (1.0, -1.0, -1.0))]


def main():
    q.note(f"=== queue_mir3: {ARMS} ===")
    q.ensure_tensorboard()
    ship = {b: h.shipped(b) for b in q.HUMANS}
    done = {}
    for me, iters, (push, stall, standoff) in ARMS:
        label = f"mirror_push_out {push}, mirror_stalemate {stall}, mirror_standoff {standoff}"
        ckpt = w.sweep_arm(me, f"{me}_mir3", ship, iters, 1024,
                           ["--reward", "draw=-20", f"mirror_push_out={push}", f"mirror_stalemate={stall}", f"mirror_standoff={standoff}"],
                           label, ROWS, dict(push=push, stall=stall, standoff=standoff), warm=f"runs/{me}_hill4/{me}_mir2/model_final.pt")
        if ckpt: done[me] = ckpt
    q.wait_done(os.path.join(q.LOG_DIR, "progress_judge.txt"), "judge_hill", hours=1.0)
    shipped = json.load(open(j.OUT)) if os.path.exists(j.OUT) else {}
    res = {}
    for me, ckpt in done.items():
        base = shipped.get(me, {}).get("shipped") or j.judge(me, ship[me], ship)
        r = j.judge(me, ckpt, ship, base); res[me] = dict(shipped=base, candidate=r)
        q.note(f"{me}_mir3 judged: three-way {r['three']:.3f} (shipped {base['three']:.3f}), jitter {r['jitter']:.4f}"
               + "".join(f", vs {o} {s:.3f}" for o, s in r["duels"].items()) + f" -> {'PASS' if r['pass'] else 'fail'}")
        json.dump(res, open(OUT, "w", encoding="utf-8"), indent=1)
    q.note("=== queue_mir3 done ===")


if __name__ == "__main__":
    main()
