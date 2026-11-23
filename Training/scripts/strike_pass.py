"""Strike pass (2026-11-23): teach each body to win with its hands and feet, not only by walking into the others.

Each body in turn is fine-tuned on rung <body>_hill4 with balance_game.py's recipe (warm from its current best, slot1 = the
best + a live copy, slot2/3 = the other bodies' current best) plus the env's strike_push reward (koth/env.py: the
opponent's outward speed while one of my hands, arms or feet touches them). Trump's arm_contact penalty is switched off.
Pass A trains with the strike reward alone; pass B (if time is left) adds the motion prior (train.py --amp, the body's
CMU clips with boxing and punches) for each body pass A did not promote.

A candidate replaces the body's best when it hits more without losing more:
  strike_touch (eval.py, three-way) >= best's + STRIKE_GAIN, share >= best's - SHARE_SLACK and <= CAP,
  each duel >= min(DUEL_FLOOR, best's - DUEL_SLACK), jitter <= JITTER_RATIO x the shipped fighter's, no NaN gradient.
Or, with --or-share, when it wins more without hitting less: share >= best's + bg.GAIN, the same duel and jitter bars,
strike >= best's - SHARE_SLACK (the platform retrain, where adapting to the ground counts as much as hitting).
Evals are cached in --cache (default balance_game's; a new ground needs its own, every cached number is on the old one).
Run names are <body>_<tag><YYMMDD>_<a|amp>. Nothing is exported unless --export is given (then as balance_game does).

  .venv/Scripts/python.exe scripts/strike_pass.py [--hours 9] [--iters 300] [--strike 4] [--amp-weight 0.5]
      [--start trump=runs/trump_hill4/trump_b261123_04/model_final.pt] [--export]
Log: runs/overnight/strike_<YYMMDD>.out (stdout), runs/overnight/progress_balance.txt; summary strike_summary.json.
"""

import argparse
import datetime
import json
import os
import shutil
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import balance_game as bg
import queue_hill as q
import queue_hill4 as h
import overnight_game as g

STRIKE_GAIN, SHARE_SLACK = 0.05, 0.03
OR_SHARE = False  # --or-share
SUMMARY = os.path.join(q.LOG_DIR, "strike_summary.json")


def strike(me, ckpt, best):
    """Share of the learner's three-way steps with a hand, arm or foot on another fighter."""
    others = [b for b in q.HUMANS if b != me]

    def run():
        rows = q.evaluate(
            me,
            ["--rung", f"{me}_hill", "--ckpt", ckpt, "--opponent"]
            + [f"{best[x]}:slot{i + 1}" for i, x in enumerate(others)]
            + ["--seconds", "20"],
        )
        return q.mean(rows, "strike_touch") if rows else 0.0

    return bg.cached("strike|" + "|".join([me, ckpt] + [best[x] for x in others]), run)


def judge(me, ckpt, best, ship_jitter):
    cur = bg.measure(me, best[me], best)
    cur["strike"] = strike(me, best[me], best)
    m = bg.measure(me, ckpt, best)
    m["strike"] = strike(me, ckpt, best)
    why = []
    hits = m["strike"] >= cur["strike"] + STRIKE_GAIN and m["three"] >= cur["three"] - SHARE_SLACK
    wins = OR_SHARE and m["three"] >= cur["three"] + bg.GAIN and m["strike"] >= cur["strike"] - SHARE_SLACK
    if not (hits or wins):
        why.append(
            f"strike {m['strike']:.3f} (needs {cur['strike'] + STRIKE_GAIN:.3f}), share {m['three']:.3f} (needs >= {cur['three'] - SHARE_SLACK:.3f}"
            + (f", or {cur['three'] + bg.GAIN:.3f} without the strike gain)" if OR_SHARE else ")")
        )
    if m["three"] > bg.CAP:
        why.append(f"share {m['three']:.3f} > cap {bg.CAP}")
    if m["jitter"] > bg.JITTER_RATIO * ship_jitter:
        why.append(f"jitter {m['jitter']:.4f} > {bg.JITTER_RATIO * ship_jitter:.4f}")
    for o, d in m["duels"].items():
        bar = min(bg.DUEL_FLOOR, cur["duels"][o] - bg.DUEL_SLACK)
        if d < bar:
            why.append(f"duel vs {o} {d:.3f} < {bar:.3f}")
    if not g.nan_free(ckpt):
        why.append("NaN gradient in training")
    return m, cur, why


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hours", type=float, default=9.0)
    ap.add_argument("--iters", type=int, default=300)
    ap.add_argument("--num-envs", type=int, default=1024)
    ap.add_argument(
        "--strike", type=float, default=4.0, help="strike_push weight (belly_push, which shaped Trump, is 4)"
    )
    ap.add_argument("--amp-weight", type=float, default=0.5)
    ap.add_argument("--bodies", nargs="*", default=list(q.HUMANS))
    ap.add_argument(
        "--start", nargs="*", default=[], help="body=ckpt: start brains (default: the Models/<body> fighters)"
    )
    ap.add_argument("--export", action="store_true")
    ap.add_argument(
        "--or-share", action="store_true", help="also promote a candidate that wins more (see the docstring)"
    )
    ap.add_argument("--cache", default=None, help="eval cache file in runs/overnight (default: balance_game's)")
    ap.add_argument("--tag", default="s", help="run name tag: <body>_<tag><YYMMDD>_a")
    a = ap.parse_args()
    global OR_SHARE
    OR_SHARE = a.or_share
    if a.cache:
        bg.CACHE = os.path.join(q.LOG_DIR, a.cache)
        bg.cache = json.load(open(bg.CACHE)) if os.path.exists(bg.CACHE) else {}
    t0 = time.time()
    deadline = t0 + a.hours * 3600
    left = lambda: (deadline - time.time()) / 60
    est = a.iters * g.SEC_PER_IT / 60 + 30
    q.note(
        f"=== strike_pass: {a.hours} h, strike_push {a.strike}, {a.iters} it x {a.num_envs} worlds (~{est:.0f} min a run) ==="
    )
    q.ensure_tensorboard()
    ship = {b: h.shipped(b) for b in q.HUMANS}
    best = dict(ship)
    best.update(dict(s.split("=", 1) for s in a.start))
    ship_jit = {b: (bg.three(b, ship[b], ship) or [0, 0.06])[1] for b in q.HUMANS}
    m0 = {}
    for b in q.HUMANS:
        m0[b] = dict(bg.measure(b, best[b], best), strike=strike(b, best[b], best))
        q.note(
            f"start {b}: {best[b]} share {m0[b]['three']:.3f}, strike {m0[b]['strike']:.3f}, duels { {o: round(v, 3) for o, v in m0[b]['duels'].items()} }"
        )
    day = f"{datetime.date.today():%y%m%d}"
    runs, promoted = [], {}

    def save(done):
        g.save(
            SUMMARY,
            dict(
                best=best,
                start=m0,
                runs=runs,
                promoted=promoted,
                done=done,
                end={b: dict(bg.measure(b, best[b], best), strike=strike(b, best[b], best)) for b in q.HUMANS},
            ),
        )

    jobs = [(b, False) for b in a.bodies] + [(b, True) for b in a.bodies]
    for me, amp in jobs:
        if amp and me in promoted:
            continue
        if left() < est:
            q.note(f"{left():.0f} min left: no time for {me}{' + amp' if amp else ''}")
            break
        others = [b for b in q.HUMANS if b != me]
        name = f"{me}_{a.tag}{day}_{'amp' if amp else 'a'}"
        rung = f"{me}_hill4"
        run_dir = os.path.join(q.ROOT, "runs", rung, name)
        ckpt = f"runs/{rung}/{name}/model_final.pt"
        log = os.path.join(q.LOG_DIR, f"{name}.log")
        if os.path.isdir(run_dir) and not os.path.exists(os.path.join(run_dir, "model_final.pt")):
            shutil.rmtree(run_dir)
        rew = [f"strike_push={a.strike}", "draw=-20"] + (["arm_contact=0"] if me == "trump" else [])
        opp = [f"{best[me]}:slot1", f"{best[me]}:slot1:live"] + [f"{best[x]}:slot{i + 2}" for i, x in enumerate(others)]
        q.note(f"{name}: warm from {best[me]}, rewards {rew}, {left():.0f} min left")
        if not os.path.exists(os.path.join(q.ROOT, ckpt)):
            p = q.run(
                [
                    "scripts/train.py",
                    "--rung",
                    rung,
                    "--name",
                    name,
                    "--num-envs",
                    str(a.num_envs),
                    "--iters",
                    str(a.iters),
                    "--warm",
                    best[me],
                    "--opponent",
                ]
                + opp
                + ["--frozen-deterministic", "--critic-warmup", "15", "--reward"]
                + rew
                + ["--self-play", str(g.SELF_PLAY)]
                + (["--amp", "--amp-weight", str(a.amp_weight)] if amp else []),
                me,
                log,
            )
            if p.returncode != 0 or not os.path.exists(os.path.join(q.ROOT, ckpt)):
                q.note(f"{name}: training FAILED (rc {p.returncode}), see {log}")
                runs.append(dict(run=name, body=me, amp=amp, result="training failed"))
                save(False)
                continue
        m, cur, why = judge(me, ckpt, best, ship_jit[me])
        result = "PROMOTED" if not why else "kept as a run: " + "; ".join(why)
        if not why:
            best[me] = ckpt
            promoted[me] = name
        q.note(
            f"{name}: strike {m['strike']:.3f} (best {cur['strike']:.3f}), share {m['three']:.3f} (best {cur['three']:.3f}), "
            f"duels { {o: round(v, 3) for o, v in m['duels'].items()} }, jitter {m['jitter']:.4f} -> {result}"
        )
        runs.append(dict(run=name, body=me, amp=amp, ckpt=ckpt, m=m, best_m=cur, result=result))
        save(False)
    if a.export:
        for b, name in promoted.items():
            p = q.run(
                ["scripts/export_policy.py", "--ckpt", best[b], "--name", "fighter", "--value"],
                b,
                os.path.join(q.LOG_DIR, f"{name}_export.log"),
            )
            q.note(f"{b}: {name} exported" if p.returncode == 0 else f"{b}: export of {name} FAILED")
    save(True)
    q.note(f"=== strike_pass done after {(time.time() - t0) / 3600:.1f} h: promoted {promoted or 'none'} ===")


if __name__ == "__main__":
    main()
