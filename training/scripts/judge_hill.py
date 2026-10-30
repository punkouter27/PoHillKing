"""Re-judge every hill brain candidate on what the game plays (log 2026-10-29): quick match seats one agent per body kind
(Kim, Trump, Grandpa), tournaments distinct agents, so a mirror duel happens only when a player ticks one agent. The
mirror bar of queue_hill4 / overnight_hill4 is dropped.

Per body, the shipped fighter and every runs/<body>_hill4/*/model_final.pt (plus --extra checkpoints), with eval.py:
  three-way: rung <body>_hill against the other two shipped fighters, 20 s (last one in, jitter)
  duels:     rung <body>_vs_<other> against each other shipped fighter, 25 s (decided share), only when the three-way passes
Bars: three-way >= shipped + THREE_GAIN, each duel's decided share >= min(DUEL_FLOOR, shipped's - DUEL_SLACK), jitter <=
JITTER_RATIO x shipped. Nothing is exported: a brain that passes still needs its Unity gates (RunHumanFfaStats,
RunGetupDuelStats) and the user's go. Waits for sweep_mirror.py to finish (one GPU job at a time).

  .venv/Scripts/python.exe scripts/judge_hill.py [--bodies kim grandpa trump] [--extra runs/x/y/model_final.pt ...]
Progress: runs/overnight/progress_judge.txt; rows: runs/overnight/judge_hill.json.
"""
import argparse, glob, json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import queue_hill as q
import queue_hill4 as h
import overnight_hill4 as w

q.PROGRESS = os.path.join(q.LOG_DIR, "progress_judge.txt")
OUT = os.path.join(q.LOG_DIR, "judge_hill.json")
MIRROR_LOG = os.path.join(q.LOG_DIR, "progress_mirror.txt")
THREE_GAIN, DUEL_FLOOR, DUEL_SLACK, JITTER_RATIO = 0.03, 0.45, 0.03, 1.05


def judge(me, ckpt, ship, base=None):
    others = [b for b in q.HUMANS if b != me]
    three = w.three_way(me, ckpt, ship)
    r = dict(ckpt=ckpt, three=q.mean(three, "learner_last_one_in"), jitter=q.mean(three, "jitter_rad"), duels={})
    if base is not None and (r["three"] < base["three"] + THREE_GAIN or r["jitter"] > JITTER_RATIO * base["jitter"]):
        r["pass"] = False; return r
    for o in others:
        r["duels"][o] = q.share(q.evaluate(me, ["--rung", f"{me}_vs_{o}", "--ckpt", ckpt, "--opponent", ship[o], "--seconds", "25"]))
    if base is not None:
        r["pass"] = all(r["duels"][o] >= min(DUEL_FLOOR, base["duels"][o] - DUEL_SLACK) for o in others)
    return r


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--bodies", nargs="+", default=list(q.HUMANS))
    ap.add_argument("--extra", nargs="*", default=[]); a = ap.parse_args()
    q.note(f"=== judge_hill: {a.bodies}; three-way >= shipped + {THREE_GAIN}, duels >= min({DUEL_FLOOR}, shipped - {DUEL_SLACK}), "
           f"jitter <= {JITTER_RATIO} x shipped: waiting for sweep_mirror ===")
    q.wait_done(MIRROR_LOG, "sweep_mirror")
    ship = {b: h.shipped(b) for b in q.HUMANS}
    res = json.load(open(OUT)) if os.path.exists(OUT) else {}
    fmt = lambda r: (f"three-way {r['three']:.3f}, jitter {r['jitter']:.4f}"
                     + "".join(f", vs {o} {s:.3f}" for o, s in r["duels"].items()))
    for me in a.bodies:
        base = judge(me, ship[me], ship); q.note(f"{me} shipped: {fmt(base)}")
        res[me] = dict(shipped=base, candidates={})
        cands = sorted(glob.glob(os.path.join(q.ROOT, "runs", f"{me}_hill4", "*", "model_final.pt")))
        cands = [os.path.relpath(c, q.ROOT).replace("\\", "/") for c in cands] + [c for c in a.extra if f"/{me}_" in c]
        for c in cands:
            r = judge(me, c, ship, base); name = c.split("/")[-2]
            res[me]["candidates"][name] = r
            q.note(f"{name}: {fmt(r)} -> {'PASS' if r['pass'] else 'fail'}")
            json.dump(res, open(OUT, "w", encoding="utf-8"), indent=1)
    q.note("=== judge_hill done ===")


if __name__ == "__main__":
    main()
