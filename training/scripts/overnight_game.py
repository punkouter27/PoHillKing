"""Unattended co-evolution loop (default 10 h) judged on what the game plays (log 2026-10-29: the mirror bar is dropped).

Agents: Kim, Trump, Grandpa, one fighter brain each. Every round trains the most urgent body on rung <body>_hill4 (the
hill4 self-play recipe of overnight_hill4.py: slot1 = the body's best brain + a live copy of the learner refreshed every
SELF_PLAY iterations, slot2/3 = the OTHER bodies' current best brains, so the three co-evolve), then judges the result
with eval.py against the shipped fighters (a fixed yardstick, the same as judge_hill.py):
  three-way  rung <body>_hill vs the other two shipped fighters, 20 s: last-one-in share, jitter (rad per policy step)
  duels      rung <body>_vs_<other> vs each other shipped fighter, 25 s: decided-duel win share (only when the three-way passes)
Game urgency = max(0, THREE_TARGET - three-way) + 0.5 x sum over the duels of max(0, 0.5 - duel) + JITTER_W x max(0, jitter / JITTER_REF - 1),
plus URGENCY_FLOOR, divided by 1 + 0.5 x failed attempts since the body's last promotion, so one hopeless body cannot take the whole night.
A candidate becomes the body's best when: three-way >= best + THREE_GAIN, jitter <= JITTER_RATIO x shipped, each duel >=
min(DUEL_FLOOR, shipped - DUEL_SLACK), and its run had no NaN gradient (graduation.json). A failed attempt that won more
three-ways than its start brain is continued by the body's next attempt; otherwise the next one restarts from the best.

Starting point: the shipped fighters, then the best judge_hill.py PASS per body (runs/overnight/judge_hill.json, earlier runs).
At the end: a final three-way of the final brains against each other (round robin), the production weights in
runs/production/<body>/best.pt (+ production.json), exported over Assets/PoKingHill/Models/<body>/fighter_policy.onnx
(+ value, + fighter_action.json; the old files in runs/production/<body>/previous/) for every body whose best changed, and
the HTML report SCREENSHOTS/training-<YYMMDD>/index.html (scripts/overnight_game_report.py).
Unity gates still to run afterwards: GameScene.RefreshBrains, RunHumanFfaStats(body), RunGetupDuelStats(..., hill: true).

Telemetry in runs/overnight/: progress_game.txt (events + an hourly status grid), overnight_game_telemetry.jsonl (per run:
reward / value-loss / decided curves every 10 iterations, checkpoints, eval numbers, decision), overnight_game_summary.json.
TensorBoard on :6006.

  .venv/Scripts/python.exe scripts/overnight_game.py [--hours 10] [--iters 300] [--num-envs 1024] [--no-export]
"""
import argparse, datetime, glob, json, os, re, shutil, sys, threading, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import queue_hill as q
import queue_hill4 as h
import overnight_hill4 as w

q.PROGRESS = os.path.join(q.LOG_DIR, "progress_game.txt")
TELEMETRY = os.path.join(q.LOG_DIR, "overnight_game_telemetry.jsonl")
SUMMARY = os.path.join(q.LOG_DIR, "overnight_game_summary.json")
JUDGED = os.path.join(q.LOG_DIR, "judge_hill.json")
PROD = os.path.join(q.ROOT, "runs", "production")
MODELS = h.MODELS
THREE_TARGET, JITTER_REF, JITTER_W = 0.5, 0.06, 0.2
URGENCY_FLOOR = 0.02                    # bodies with no urgency left still take turns
THREE_GAIN, DUEL_FLOOR, DUEL_SLACK, JITTER_RATIO = 0.03, 0.45, 0.03, 1.05
SELF_PLAY = 25
SEC_PER_IT = 12.5                       # rung <body>_hill4 at 1024 worlds on the RTX 2060 with the editor closed (30 s with it open)
JUDGE_MIN, FINAL_MIN = 30, 35           # one judge (three-way + two duels); round robin + exports + report


def urgency(m):
    return (max(0.0, THREE_TARGET - m["three"]) + 0.5 * sum(max(0.0, 0.5 - d) for d in m["duels"].values())
            + JITTER_W * max(0.0, m["jitter"] / JITTER_REF - 1))


def curve(log_path, run_dir):
    """Every 10th iteration of a train.py log: reward, losses, decided / win shares; plus the checkpoints written."""
    txt = re.sub(r"\x1b\[[0-9;]*m", "", open(log_path, encoding="utf-8", errors="ignore").read())
    rows = []
    for block in re.split(r"Learning iteration ", txt)[1:]:
        it = int(block.split("/", 1)[0])
        if it % 10 and "ETA: 0:00:00" not in block: continue
        r = {"it": it}
        for key, name in (("Mean reward", "reward"), ("Mean value loss", "value_loss"), ("Mean surrogate loss", "surrogate_loss"),
                          ("Mean episode length", "ep_len"), ("ep/decided", "decided"), ("rew/win", "win"), ("ep/fallen", "fallen")):
            m = re.search(re.escape(key) + r":\s*(-?[\d.]+(?:e-?\d+)?)", block)
            if m: r[name] = float(m.group(1))
        rows.append(r)
    return dict(curve=rows, checkpoints=sorted(os.path.basename(p) for p in glob.glob(os.path.join(run_dir, "model_*.pt"))))


def nan_free(ckpt):
    g = os.path.join(q.ROOT, os.path.dirname(ckpt), "graduation.json")
    return not os.path.exists(g) or not json.load(open(g)).get("updates_with_nan_gradients")


def judge(me, ckpt, ship, best, base):
    """The candidate's numbers and whether it beats `best` (bars vs the body's shipped `base` for duels and jitter)."""
    others = [b for b in q.HUMANS if b != me]
    three = w.three_way(me, ckpt, ship)
    m = dict(three=q.mean(three, "learner_last_one_in"), jitter=q.mean(three, "jitter_rad"), duels={})
    why = []
    if m["three"] < best["three"] + THREE_GAIN: why.append(f"three-way {m['three']:.3f} < {best['three'] + THREE_GAIN:.3f}")
    if m["jitter"] > JITTER_RATIO * base["jitter"]: why.append(f"jitter {m['jitter']:.4f} > {JITTER_RATIO * base['jitter']:.4f}")
    if not why:
        for o in others:
            m["duels"][o] = q.share(q.evaluate(me, ["--rung", f"{me}_vs_{o}", "--ckpt", ckpt, "--opponent", ship[o], "--seconds", "25"]))
            bar = min(DUEL_FLOOR, base["duels"][o] - DUEL_SLACK)
            if m["duels"][o] < bar: why.append(f"duel vs {o} {m['duels'][o]:.3f} < {bar:.3f}")
    if not nan_free(ckpt): why.append("NaN gradient in training")
    return m, why


class Status:
    """Hourly status grid in the progress file (AGENTS.md: progress updates)."""
    def __init__(s, t0, deadline): s.t0, s.deadline, s.run, s.log, s.iters, s.best, s.ship, s.events = t0, deadline, None, None, 0, {}, {}, []

    def grid(s):
        it, rew = None, None
        if s.log and os.path.exists(s.log):
            tail = open(s.log, encoding="utf-8", errors="ignore").read()[-60000:]
            m = re.findall(r"Learning iteration (\d+)/", tail); it = int(m[-1]) if m else None
            r = re.findall(r"Mean reward:\s*(-?[\d.]+)", tail); rew = float(r[-1]) if r else None
        el, left = (time.time() - s.t0) / 3600, (s.deadline - time.time()) / 3600
        lines = [f"--- status {el:.1f} h in, {left:.1f} h left; run {s.run or '-'}"
                 + (f" iteration {it}/{s.iters}, mean reward {rew}" if it is not None else "") + " ---",
                 f"{'body':8} {'best brain':16} {'3-way':>6} {'(ship)':>7} {'duels':>13} {'jitter':>7} {'urgency':>7}"]
        for b, x in s.best.items():
            m = x["m"]; d = "/".join(f"{v:.2f}" for v in m["duels"].values())
            lines.append(f"{b:8} {(x['run'] or 'shipped'):16} {m['three']:6.3f} {s.ship[b]['three']:7.3f} {d:>13} {m['jitter']:7.4f} {urgency(m):7.3f}")
        if s.events: lines.append("since start: " + "; ".join(s.events[-6:]))
        return lines

    def loop(s):
        while True:
            time.sleep(3600)
            try:
                for l in s.grid(): q.note(l)
            except Exception as e: q.note(f"status grid failed: {e}")


def seed_best(ship, base):
    """Each body's best start: the shipped fighter, or the best judge_hill.py PASS (three-way + 0.1 x mean duel)."""
    best = {b: dict(ckpt=ship[b], m=base[b], run=None, src="shipped") for b in q.HUMANS}
    if not os.path.exists(JUDGED): return best
    res = json.load(open(JUDGED))
    for b in q.HUMANS:
        cands = [(n, c) for n, c in res.get(b, {}).get("candidates", {}).items() if c.get("pass") and len(c["duels"]) == 2
                 and os.path.exists(os.path.join(q.ROOT, c["ckpt"])) and nan_free(c["ckpt"])]
        if cands:
            n, c = max(cands, key=lambda nc: nc[1]["three"] + 0.1 * sum(nc[1]["duels"].values()) / 2)
            best[b] = dict(ckpt=c["ckpt"], m=dict(three=c["three"], jitter=c["jitter"], duels=c["duels"]), run=n, src="judge_hill")
    return best


def save(path, obj): json.dump(obj, open(path, "w", encoding="utf-8"), indent=1)


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--hours", type=float, default=10.0); ap.add_argument("--iters", type=int, default=300)
    ap.add_argument("--num-envs", type=int, default=1024); ap.add_argument("--no-export", action="store_true"); a = ap.parse_args()
    t0 = time.time(); deadline = t0 + a.hours * 3600; left = lambda: (deadline - time.time()) / 60; hrs = lambda: (time.time() - t0) / 3600
    est = a.iters * SEC_PER_IT / 60 + JUDGE_MIN
    q.note(f"=== overnight_game: {a.hours} h, {a.iters} iterations x {a.num_envs} worlds per run (~{est:.0f} min each with its judge, "
           f"so ~{int((a.hours * 60 - FINAL_MIN) // est)} runs), self-play every {SELF_PLAY}; ends ~{datetime.datetime.fromtimestamp(deadline):%m-%d %H:%M} ===")
    q.ensure_tensorboard()
    ship = {b: h.shipped(b) for b in q.HUMANS}
    judged = json.load(open(JUDGED)) if os.path.exists(JUDGED) else {}
    base = {}
    for b in q.HUMANS:
        s = judged.get(b, {}).get("shipped")
        if s and len(s.get("duels", {})) == 2: base[b] = dict(three=s["three"], jitter=s["jitter"], duels=s["duels"])
        else:
            q.note(f"{b}: measuring the shipped fighter"); m = dict(three=0, jitter=1, duels={})
            r = w.three_way(b, ship[b], ship); m.update(three=q.mean(r, "learner_last_one_in"), jitter=q.mean(r, "jitter_rad"))
            for o in [x for x in q.HUMANS if x != b]:
                m["duels"][o] = q.share(q.evaluate(b, ["--rung", f"{b}_vs_{o}", "--ckpt", ship[b], "--opponent", ship[o], "--seconds", "25"]))
            base[b] = m
    best = seed_best(ship, base)
    timeline = [dict(t=0.0, body=b, stage="shipped", run=None, m=base[b]) for b in q.HUMANS]
    timeline += [dict(t=0.0, body=b, stage="earlier runs", run=best[b]["run"], m=best[b]["m"]) for b in q.HUMANS if best[b]["run"]]
    st = Status(t0, deadline); st.best, st.ship, st.iters = best, base, a.iters
    threading.Thread(target=st.loop, daemon=True).start()
    for b in q.HUMANS:
        q.note(f"start {b}: shipped three-way {base[b]['three']:.3f}; best {best[b]['run'] or 'shipped'} three-way {best[b]['m']['three']:.3f}, "
               f"duels {best[b]['m']['duels']}, jitter {best[b]['m']['jitter']:.4f}, urgency {urgency(best[b]['m']):.3f}")
    t0_day = f"{datetime.date.today():%y%m%d}"
    fails = {b: 0 for b in q.HUMANS}; cont = {b: None for b in q.HUMANS}; n = {b: 0 for b in q.HUMANS}; runs = []
    summary = lambda done=False: save(SUMMARY, dict(best=best, shipped=base, ship_ckpt=ship, start=datetime.datetime.fromtimestamp(t0).isoformat(),
                                                    hours=hrs(), budget_hours=a.hours, fails=fails, timeline=timeline, done=done))
    summary()
    while left() >= est + FINAL_MIN:
        me = max(q.HUMANS, key=lambda b: (urgency(best[b]["m"]) + URGENCY_FLOOR) / (1 + 0.5 * fails[b]))
        others = [b for b in q.HUMANS if b != me]
        warm, warm_m = (cont[me]["ckpt"], cont[me]["m"]) if cont[me] else (best[me]["ckpt"], best[me]["m"])
        n[me] += 1; name = f"{me}_g{t0_day}_{n[me]:02d}"
        rung = f"{me}_hill4"; run_dir = os.path.join(q.ROOT, "runs", rung, name); ckpt = f"runs/{rung}/{name}/model_final.pt"
        if os.path.isdir(run_dir) and not os.path.exists(os.path.join(run_dir, "model_final.pt")): shutil.rmtree(run_dir)
        opp = [f"{best[me]['ckpt']}:slot1", f"{warm}:slot1:live"] + [f"{best[x]['ckpt']}:slot{i + 2}" for i, x in enumerate(others)]
        log = os.path.join(q.LOG_DIR, f"{name}.log"); t_start = hrs()
        q.note(f"{name}: urgency {urgency(best[me]['m']):.3f} (failed attempts {fails[me]}), warm from {warm}, opponents "
               f"{[best[x]['run'] or 'shipped' for x in others]}, {left():.0f} min left")
        st.run, st.log = name, log
        if not os.path.exists(os.path.join(q.ROOT, ckpt)):
            p = q.run(["scripts/train.py", "--rung", rung, "--name", name, "--num-envs", str(a.num_envs), "--iters", str(a.iters), "--warm", warm,
                       "--opponent"] + opp + ["--frozen-deterministic", "--critic-warmup", "15", "--reward", "draw=-20", "--self-play", str(SELF_PLAY)], me, log)
            if p.returncode != 0 or not os.path.exists(os.path.join(q.ROOT, ckpt)):
                q.note(f"{name}: training FAILED (rc {p.returncode}), see {log}"); fails[me] += 1; cont[me] = None
                runs.append(dict(run=name, body=me, result="training failed", t0=t_start, t1=hrs())); continue
        tel = curve(log, run_dir)
        st.run = f"{name} (judging)"
        bm = best[me]["m"]; m, why = judge(me, ckpt, ship, bm, base[me])
        if not why:
            best[me] = dict(ckpt=ckpt, m=m, run=name, src="tonight"); fails[me] = 0; cont[me] = None; result = "PROMOTED"
            timeline.append(dict(t=hrs(), body=me, stage="tonight", run=name, m=m)); st.events.append(f"{name} promoted ({m['three']:.3f})")
        else:
            fails[me] += 1; result = "kept as a run: " + "; ".join(why)
            cont[me] = dict(ckpt=ckpt, m=m) if m["three"] > warm_m["three"] else None
        q.note(f"{name}: three-way {m['three']:.3f} (best {bm['three']:.3f}, shipped {base[me]['three']:.3f}), duels {({k: round(v, 3) for k, v in m['duels'].items()})}, "
               f"jitter {m['jitter']:.4f}, urgency {urgency(m) if len(m['duels']) == 2 else float('nan'):.3f} -> {result}"
               + (f"; the next {me} attempt continues it" if cont[me] else ""))
        r = dict(run=name, body=me, dir=os.path.relpath(run_dir, q.ROOT).replace("\\", "/"), ckpt=ckpt, warm=warm, t0=t_start, t1=hrs(), m=m, best_m=bm,
                 result=result, promoted=result == "PROMOTED", **tel)
        runs.append(r)
        with open(TELEMETRY, "a", encoding="utf-8") as f: f.write(json.dumps(r) + "\n")
        summary()
    st.run, st.log = "final round robin", None
    q.note(f"{left():.0f} min left: no time for another run; final three-way of the final brains against each other")
    rr = {}
    for me in q.HUMANS:
        others = [b for b in q.HUMANS if b != me]
        rows = q.evaluate(me, ["--rung", f"{me}_hill", "--ckpt", best[me]["ckpt"], "--opponent"]
                          + [f"{best[x]['ckpt']}:slot{i + 1}" for i, x in enumerate(others)] + ["--seconds", "20"])
        rr[me] = dict(three=q.mean(rows, "learner_last_one_in"), jitter=q.mean(rows, "jitter_rad"))
        q.note(f"round robin {me} ({best[me]['run'] or 'shipped'}) vs the other final brains: last one in {rr[me]['three']:.3f}")
    os.makedirs(PROD, exist_ok=True); prod = {}
    for b in q.HUMANS:
        d = os.path.join(PROD, b); os.makedirs(d, exist_ok=True)
        shutil.copy(os.path.join(q.ROOT, best[b]["ckpt"]), os.path.join(d, "best.pt"))
        exported = False
        if best[b]["run"] and not a.no_export:
            prev = os.path.join(d, "previous"); os.makedirs(prev, exist_ok=True)
            for f in glob.glob(os.path.join(MODELS, b, "fighter_*")):
                if not f.endswith(".meta"): shutil.copy(f, prev)
            p = q.run(["scripts/export_policy.py", "--ckpt", best[b]["ckpt"], "--name", "fighter", "--value"], b, os.path.join(q.LOG_DIR, f"{best[b]['run']}_export.log"))
            exported = p.returncode == 0
            q.note(f"{b}: {best[b]['run']} exported to Models/{b}/fighter_policy.onnx" if exported else f"{b}: export of {best[b]['run']} FAILED, Models unchanged")
        else: q.note(f"{b}: shipped fighter unchanged")
        prod[b] = dict(ckpt=best[b]["ckpt"], run=best[b]["run"] or "shipped", src=best[b]["src"], m=best[b]["m"], shipped=base[b], round_robin=rr[b], exported=exported)
    save(os.path.join(PROD, "production.json"), dict(date=datetime.date.today().isoformat(), bodies=prod,
                                                    unity_gates_pending=[b for b in q.HUMANS if prod[b]["exported"]]))
    summary(True)
    s = json.load(open(SUMMARY)); s["round_robin"] = rr; s["production"] = prod; save(SUMMARY, s)
    p = q.run(["scripts/overnight_game_report.py"], "g1", capture=True)
    q.note(f"report: {p.stdout.strip().splitlines()[-1] if p.returncode == 0 and p.stdout.strip() else 'FAILED ' + (p.stderr or '')[-300:]}")
    q.note(f"=== overnight_game done after {hrs():.1f} h ===")


if __name__ == "__main__":
    main()
