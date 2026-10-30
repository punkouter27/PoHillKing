"""Unattended loop (default 10 h): the hill4 self-play recipe (rung <body>_hill4, train.py --self-play, see queue_hill4.py),
one body at a time, the most urgent first, until the time is up.

Urgency of a body's current best brain (eval.py rows, as queue_hill4 measures them):
  max(0, MIRROR_FLOOR - mirror decided) + max(0, THREE_FLOOR - three-way last one in) + JITTER_W * max(0, jitter / JITTER_REF - 1)
= stalls against its own copy + loses three-ways + moves twitchily. Each round trains the body with the highest
urgency / (1 + 0.5 x failed attempts since its last promotion), so one hopeless body cannot take the whole night.
A failed attempt that decided more mirror duels than the brain it started from is continued by the next attempt of that
body (longer training in 400-iteration segments, each one gated); otherwise the next attempt restarts from the best brain.

Each run: warm from that start brain, against slot1 = the body's best brain or a live copy of the learner (refreshed every
SELF_PLAY iterations), slot2/3 = the other bodies' best brains (co-evolution). Promoted (exported over
Models/<body>/fighter_policy.onnx + value + fighter_action.json, copied to runs/shipped/<body>/best.pt) when, against the
body's best brain: mirror decided >= max(MIRROR_FLOOR, best - 0.10), three-way >= best - 0.03 (vs the shipped other two,
a fixed reference), decided share in the duel vs the best >= 0.45, jitter <= 1.05 x best, and it is better: urgency down
by 0.02 or three-way up by 0.03. Unity afterwards: GameScene.RefreshBrains, RunGetupDuelStats(..., hill: true), RunHumanFfaStats.

Telemetry in runs/overnight/: progress_hill4.txt (events), overnight_hill4_telemetry.jsonl (per run: reward milestones,
loss/reward curve every 10 iterations, checkpoints, eval rows, decision), overnight_hill4_summary.json (best brain per body);
TensorBoard :6006; at the end SCREENSHOTS/training-<YYMMDD>/index.html (run_report.py: reward, decided, value loss).

  .venv/Scripts/python.exe scripts/overnight_hill4.py [--hours 10] [--iters 400] [--num-envs 1024]
"""
import argparse, datetime, json, os, shutil, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import queue_hill as q
import overnight_hill as o
import queue_hill4 as h

q.PROGRESS = os.path.join(q.LOG_DIR, "progress_hill4.txt")
TELEMETRY = os.path.join(q.LOG_DIR, "overnight_hill4_telemetry.jsonl")
SUMMARY = os.path.join(q.LOG_DIR, "overnight_hill4_summary.json")
MIRROR_FLOOR, THREE_FLOOR, JITTER_REF, JITTER_W = 0.6, 0.33, 0.06, 0.2
SELF_PLAY, ESTIMATE_MIN = 25, 110          # one run: ~77 min training at 1024 worlds on the RTX 2060 + ~25 min of evals


def urgency(m): return max(0.0, MIRROR_FLOOR - m["mirror"]) + max(0.0, THREE_FLOOR - m["three"]) + JITTER_W * max(0.0, m["jitter"] / JITTER_REF - 1)


def three_way(me, ckpt, ship):
    """eval rows of `ckpt` in the game's three-way against the shipped other two."""
    others = [b for b in q.HUMANS if b != me]
    return q.evaluate(me, ["--rung", f"{me}_hill", "--ckpt", ckpt, "--opponent"] + [f"{ship[x]}:slot{i + 1}" for i, x in enumerate(others)] + ["--seconds", "20"])


def measure(me, ckpt, ship):
    """mirror decided, three-way vs the shipped other two, jitter; plus the raw rows."""
    three = three_way(me, ckpt, ship)
    mirror = q.evaluate(me, ["--rung", q.DUEL_RUNG[me], "--ckpt", ckpt, "--opponent", ckpt, "--seconds", "25"])
    return dict(mirror=h.decided(mirror), three=q.mean(three, "learner_last_one_in"), jitter=q.mean(three, "jitter_rad")), dict(mirror=mirror, three=three)


def sweep_arm(me, name, ship, iters, num_envs, train_args, label, rows_path, row, warm=None):
    """One sweep arm on rung <me>_hill4 (live self-play, warm from `warm`, default the shipped brain): train unless done,
    measure, duel against the shipped brain, note and append a row to `rows_path`. `train_args` are the arm's extra
    train.py flags. Returns the checkpoint, or None when training failed."""
    others = [b for b in q.HUMANS if b != me]; rung = f"{me}_hill4"; warm = warm or ship[me]
    ckpt = f"runs/{rung}/{name}/model_final.pt"; log = os.path.join(q.LOG_DIR, f"{name}.log")
    opp = [f"{ship[me]}:slot1", f"{warm}:slot1:live"] + [f"{ship[x]}:slot{i + 2}" for i, x in enumerate(others)]
    if not os.path.exists(os.path.join(q.ROOT, ckpt)):
        q.note(f"{name}: training ({label}, self-play every {SELF_PLAY})")
        p = q.run(["scripts/train.py", "--rung", rung, "--name", name, "--num-envs", str(num_envs), "--iters", str(iters),
                   "--warm", warm, "--opponent"] + opp + ["--frozen-deterministic", "--critic-warmup", "15", "--self-play", str(SELF_PLAY)]
                  + train_args, me, log)
        if p.returncode != 0: q.note(f"{name}: training FAILED (rc {p.returncode}), see {log}"); return None
    m, rows = measure(me, ckpt, ship)
    duel_rows = q.evaluate(me, ["--rung", q.DUEL_RUNG[me], "--ckpt", ckpt, "--opponent", ship[me], "--seconds", "25"]); duel = q.share(duel_rows)
    q.note(f"{name}: {label}: mirror {m['mirror']:.3f}, three-way {m['three']:.3f}, duel vs shipped {duel:.3f}, "
           f"jitter {m['jitter']:.4f}, urgency {urgency(m):.3f}")
    with open(rows_path, "a", encoding="utf-8") as f:
        f.write(json.dumps(dict(row, run=name, body=me, iters=iters, m=m, duel=duel, eval=dict(rows, duel_vs_shipped=duel_rows))) + "\n")
    return ckpt


def report(runs, best, base, t0):
    day = datetime.date.today().strftime("%y%m%d"); out = os.path.join(os.path.dirname(q.ROOT), "SCREENSHOTS", f"training-{day}")
    os.makedirs(out, exist_ok=True)
    fmt = lambda m: f"mirror {m['mirror']:.3f}, 3-way {m['three']:.3f}, jitter {m['jitter']:.4f}"
    tiers = {b: ("adult" if urgency(best[b]["m"]) < 0.02 else "child" if best[b]["m"]["mirror"] >= MIRROR_FLOOR else "toddler") for b in q.HUMANS}
    sec = dict(title="hill4 self-play, the most urgent body first",
               summary={t: ", ".join(b for b in q.HUMANS if tiers[b] == t) or "none" for t in ("toddler", "child", "adult")},
               runs=[{"label": r["run"], "dir": os.path.relpath(r["dir"], os.path.join(q.ROOT, "runs")).replace("\\", "/")} for r in runs if r.get("dir")],
               baseline=None,
               metrics=[{"tag": "Train/mean_reward", "label": "mean reward"}, {"tag": "ep/decided", "label": "rounds decided"}, {"tag": "Loss/value", "label": "value loss"}],
               evals=[{"label": r["run"], "values": {"body": r["body"], "start": os.path.basename(os.path.dirname(r["warm"])), "new": fmt(r["m"]),
                                                     "best before": fmt(r["best_m"]), "duel vs best": f"{r['duel']:.3f}", "result": r["result"]}} for r in runs if r.get("m")],
               verdict="; ".join(f"{b}: {os.path.relpath(best[b]['ckpt'], q.ROOT) if os.path.isabs(best[b]['ckpt']) else best[b]['ckpt']} ({fmt(best[b]['m'])}; shipped {fmt(base[b])})" for b in q.HUMANS))
    if sec["runs"]: sec["baseline"] = sec["runs"][0]["label"]
    spec = dict(title=f"Overnight hill4 self-play {day}", subtitle=f"{(time.time() - t0) / 3600:.1f} h, {len(runs)} runs; Toddler = stalls in its mirror, Child = decides its mirror, Adult = no urgency left", sections=[sec])
    json.dump(spec, open(os.path.join(out, "spec.json"), "w"), indent=1)
    p = q.run(["scripts/run_report.py", os.path.join(out, "spec.json")], "g1", capture=True)
    q.note(f"report: {os.path.join(out, 'index.html')}" if p.returncode == 0 else f"report FAILED: {(p.stderr or '')[-300:]}")


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--hours", type=float, default=10.0); ap.add_argument("--iters", type=int, default=400)
    ap.add_argument("--num-envs", type=int, default=1024); a = ap.parse_args()
    t0 = time.time(); deadline = t0 + a.hours * 3600; left = lambda: (deadline - time.time()) / 60
    q.note(f"=== overnight_hill4: {a.hours} h, {a.iters} iterations x {a.num_envs} worlds per run, self-play every {SELF_PLAY} ===")
    q.ensure_tensorboard()
    ship = {b: h.shipped(b) for b in q.HUMANS}
    base = {}
    for b in q.HUMANS:                        # the shipped brains' numbers: from queue_hill4's evals when they exist (same method)
        prev = os.path.join(q.LOG_DIR, f"{b}_hill4a_eval.json")
        if os.path.exists(prev):
            ev = json.load(open(prev))
            base[b] = dict(mirror=h.decided(ev["mirror_shipped"]), three=q.mean(ev["three_shipped"], "learner_last_one_in"), jitter=q.mean(ev["three_shipped"], "jitter_rad"))
        else: base[b] = measure(b, ship[b], ship)[0]
    best = {b: dict(ckpt=ship[b], m=base[b], run=None) for b in q.HUMANS}
    fails = {b: 0 for b in q.HUMANS}; cont = {b: None for b in q.HUMANS}; cont_m = {}; letter = {b: "b" for b in q.HUMANS}; runs = []
    for b in q.HUMANS: q.note(f"start {b}: shipped {best[b]['m']}, urgency {urgency(best[b]['m']):.3f}")
    while left() >= ESTIMATE_MIN:
        me = max(q.HUMANS, key=lambda b: urgency(best[b]["m"]) / (1 + 0.5 * fails[b]))
        others = [b for b in q.HUMANS if b != me]
        warm = cont[me] or best[me]["ckpt"]; warm_m = cont_m[me] if cont[me] else best[me]["m"]
        name = f"{me}_hill4{letter[me]}"; letter[me] = chr(ord(letter[me]) + 1)
        rung = f"{me}_hill4"; run_dir = os.path.join(q.ROOT, "runs", rung, name); ckpt = f"runs/{rung}/{name}/model_final.pt"
        if os.path.isdir(run_dir) and not os.path.exists(os.path.join(run_dir, "model_final.pt")): shutil.rmtree(run_dir)   # a run cut short
        opp = [f"{best[me]['ckpt']}:slot1", f"{warm}:slot1:live"] + [f"{best[x]['ckpt']}:slot{i + 2}" for i, x in enumerate(others)]
        log = os.path.join(q.LOG_DIR, f"{name}.log")
        q.note(f"{name}: urgency {urgency(best[me]['m']):.3f} (failed attempts {fails[me]}), warm from {warm}, {left():.0f} min left")
        if not os.path.exists(os.path.join(q.ROOT, ckpt)):
            p = q.run(["scripts/train.py", "--rung", rung, "--name", name, "--num-envs", str(a.num_envs), "--iters", str(a.iters), "--warm", warm,
                       "--opponent"] + opp + ["--frozen-deterministic", "--critic-warmup", "15", "--reward", "draw=-20", "--self-play", str(SELF_PLAY)], me, log)
            if p.returncode != 0 or not os.path.exists(os.path.join(q.ROOT, ckpt)):
                q.note(f"{name}: training FAILED (rc {p.returncode}), see {log}"); fails[me] += 1; cont[me] = None; continue
        tel = o.telemetry(log, run_dir) if os.path.exists(log) else {}
        m, rows = measure(me, ckpt, ship)
        duel_rows = q.evaluate(me, ["--rung", q.DUEL_RUNG[me], "--ckpt", ckpt, "--opponent", best[me]["ckpt"], "--seconds", "25"]); duel = q.share(duel_rows)
        bm = best[me]["m"]
        better = urgency(m) <= urgency(bm) - 0.02 or m["three"] >= bm["three"] + 0.03
        ok = (better and m["mirror"] >= max(MIRROR_FLOOR, bm["mirror"] - 0.10) and m["three"] >= bm["three"] - 0.03 and duel >= 0.45 and m["jitter"] <= 1.05 * bm["jitter"])
        result = "kept as a run"
        if ok:
            p = q.run(["scripts/export_policy.py", "--ckpt", ckpt, "--name", "fighter", "--value"], me, os.path.join(q.LOG_DIR, f"{name}_export.log"))
            if p.returncode == 0:
                shutil.copy(os.path.join(q.ROOT, ckpt), os.path.join(q.ROOT, "runs", "shipped", me, "best.pt"))
                best[me] = dict(ckpt=ckpt, m=m, run=name); fails[me] = 0; cont[me] = None; result = "PROMOTED"
            else: result = "passed, export refused (see export log)"
        if result != "PROMOTED":
            fails[me] += 1
            cont[me] = ckpt if m["mirror"] > warm_m["mirror"] else None; cont_m[me] = m
        q.note(f"{name}: mirror {m['mirror']:.3f} (best {bm['mirror']:.3f}), three-way {m['three']:.3f} ({bm['three']:.3f}), duel vs best {duel:.3f}, "
               f"jitter {m['jitter']:.4f} ({bm['jitter']:.4f}), urgency {urgency(m):.3f} ({urgency(bm):.3f}) -> {result}"
               + (f"; next {me} attempt continues it" if cont[me] else ""))
        r = dict(run=name, body=me, dir=run_dir, ckpt=ckpt, warm=warm, m=m, best_m=bm, duel=duel, result=result, telemetry=tel, eval=dict(rows, duel_vs_best=duel_rows))
        runs.append(r)
        with open(TELEMETRY, "a", encoding="utf-8") as f: f.write(json.dumps(r) + "\n")
        json.dump(dict(best=best, shipped=base, fails=fails, hours=(time.time() - t0) / 3600), open(SUMMARY, "w"), indent=1)
    q.note(f"{left():.0f} min left: no time for another run")
    for b in q.HUMANS:
        q.note(f"final {b}: " + (f"{best[b]['ckpt']} ({best[b]['run']}) is in Models/{b}/fighter_policy.onnx" if best[b]["run"] else "shipped brain unchanged"))
    json.dump(dict(best=best, shipped=base, fails=fails, hours=(time.time() - t0) / 3600, done=True), open(SUMMARY, "w"), indent=1)
    report(runs, best, base, t0)
    q.note(f"=== overnight_hill4 done after {(time.time() - t0) / 3600:.1f} h ===")


if __name__ == "__main__":
    main()
