"""Balancing pass after overnight_game.py (log 2026-10-30): judge against the CURRENT best brains, not the shipped ones.

overnight_game's yardstick (the shipped fighters) hid co-evolution: its three final brains won 0.16 / 0.22 / 0.56 of the
three-ways against each other. Here a body's game share is its three-way last-one-in share (eval.py rung <body>_hill, 20 s)
against the other two bodies' current best brains, and its duels are against those brains too (rung <body>_vs_<other>, 25 s).
Each round trains the body furthest below a fair third (deficit / (1 + 0.5 x failed attempts)) on rung <body>_hill4 with
overnight_game's recipe (slot1 = its best + a live copy, slot2/3 = the others' current best). A candidate becomes the body's
best when: its share >= the current best's + GAIN and <= CAP (no new tyrant), each duel >= min(DUEL_FLOOR, the current
best's duel against the same brain - DUEL_SLACK), jitter <= 1.05 x the shipped fighter's, no NaN gradient. Before
training it re-judges --seed-runs (default: overnight_game's Kim and Trump runs) the same way. Every eval is cached by the
checkpoints involved, so a promotion only re-measures what it changed. Stops when every body's share >= BALANCED or the
time is up; then exports the bodies whose best changed (over Models/<body>/fighter_*, previous files in
runs/production/<body>/previous/), writes runs/production/production.json and the report
SCREENSHOTS/training-<YYMMDD>-balance/index.html (overnight_game_report.py, round-robin yardstick).

  .venv/Scripts/python.exe scripts/balance_game.py [--hours 6] [--iters 300] [--num-envs 1024] [--start kim=ckpt ...] [--no-export]
Progress: runs/overnight/progress_balance.txt (hourly status grid); telemetry balance_telemetry.jsonl, balance_summary.json.
"""
import argparse, datetime, glob, json, os, shutil, sys, threading, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import queue_hill as q
import queue_hill4 as h
import overnight_game as g

q.PROGRESS = os.path.join(q.LOG_DIR, "progress_balance.txt")
TELEMETRY = os.path.join(q.LOG_DIR, "balance_telemetry.jsonl")
SUMMARY = os.path.join(q.LOG_DIR, "balance_summary.json")
CACHE = os.path.join(q.LOG_DIR, "balance_cache.json")
FAIR, BALANCED, GAIN, CAP = 1 / 3, 0.28, 0.03, 0.43
DUEL_FLOOR, DUEL_SLACK, JITTER_RATIO = 0.45, 0.03, 1.05
cache = json.load(open(CACHE)) if os.path.exists(CACHE) else {}


def cached(key, fn):
    if key not in cache:
        cache[key] = fn(); json.dump(cache, open(CACHE, "w"), indent=1)
    return cache[key]


def three(me, ckpt, best):
    """[last-one-in share, jitter] of `ckpt` on body `me` against the other bodies' current best brains."""
    others = [b for b in q.HUMANS if b != me]
    def run():
        rows = q.evaluate(me, ["--rung", f"{me}_hill", "--ckpt", ckpt, "--opponent"] + [f"{best[x]}:slot{i + 1}" for i, x in enumerate(others)] + ["--seconds", "20"])
        return [q.mean(rows, "learner_last_one_in"), q.mean(rows, "jitter_rad")] if rows else None
    return cached("three|" + "|".join([me, ckpt] + [best[x] for x in others]), run)


def duel(me, ckpt, other, other_ckpt):
    return cached(f"duel|{me}|{ckpt}|{other}|{other_ckpt}", lambda: q.share(q.evaluate(
        me, ["--rung", f"{me}_vs_{other}", "--ckpt", ckpt, "--opponent", other_ckpt, "--seconds", "25"])))


def measure(me, ckpt, best, duels=True):
    t = three(me, ckpt, best) or [0.0, 1.0]
    m = dict(three=t[0], jitter=t[1], duels={})
    if duels: m["duels"] = {o: duel(me, ckpt, o, best[o]) for o in q.HUMANS if o != me}
    return m


def judge(me, ckpt, best, ship_jitter):
    """(numbers, reasons it is not better than the current best; [] = promote)."""
    cur = measure(me, best[me], best); m = measure(me, ckpt, best, duels=False); why = []
    if m["three"] < cur["three"] + GAIN: why.append(f"share {m['three']:.3f} < {cur['three'] + GAIN:.3f}")
    if m["three"] > CAP: why.append(f"share {m['three']:.3f} > cap {CAP}")
    if m["jitter"] > JITTER_RATIO * ship_jitter: why.append(f"jitter {m['jitter']:.4f} > {JITTER_RATIO * ship_jitter:.4f}")
    if not why:
        m = measure(me, ckpt, best)
        for o, d in m["duels"].items():
            bar = min(DUEL_FLOOR, cur["duels"][o] - DUEL_SLACK)
            if d < bar: why.append(f"duel vs {o} {d:.3f} < {bar:.3f}")
    if not g.nan_free(ckpt): why.append("NaN gradient in training")
    return m, cur, why


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--hours", type=float, default=6.0); ap.add_argument("--iters", type=int, default=300)
    ap.add_argument("--num-envs", type=int, default=1024); ap.add_argument("--no-export", action="store_true")
    ap.add_argument("--start", nargs="*", default=[], help="body=ckpt: start brains (default: the Models/<body> fighters)")
    ap.add_argument("--seed-runs", nargs="*", default=None, help="checkpoints judged before any training (default: overnight_game's kim/trump runs)")
    a = ap.parse_args()
    t0 = time.time(); deadline = t0 + a.hours * 3600; left = lambda: (deadline - time.time()) / 60; hrs = lambda: (time.time() - t0) / 3600
    est = a.iters * g.SEC_PER_IT / 60 + 25
    q.note(f"=== balance_game: {a.hours} h, {a.iters} iterations x {a.num_envs} worlds per run (~{est:.0f} min with its judge); "
           f"stops when every share >= {BALANCED}; ends by ~{datetime.datetime.fromtimestamp(deadline):%m-%d %H:%M} ===")
    q.ensure_tensorboard()
    ship = {b: h.shipped(b) for b in q.HUMANS}                       # rebuilt from the shipped ONNX at the time the cache was made
    best = dict(ship); best.update(dict(s.split("=", 1) for s in a.start))
    ship_jit = {b: (three(b, ship[b], ship) or [0, 0.06])[1] for b in q.HUMANS}
    src = {b: ("start" if b in dict(s.split("=", 1) for s in a.start) else "shipped") for b in q.HUMANS}
    m0 = {b: measure(b, best[b], best) for b in q.HUMANS}
    timeline = [dict(t=0.0, body=b, stage="shipped", run=os.path.basename(os.path.dirname(best[b])), m=m0[b]) for b in q.HUMANS]
    for b in q.HUMANS: q.note(f"start {b}: {best[b]} share {m0[b]['three']:.3f}, duels { {o: round(v, 3) for o, v in m0[b]['duels'].items()} }, jitter {m0[b]['jitter']:.4f}")
    names = {b: (os.path.basename(os.path.dirname(best[b])) if src[b] == "start" else None) for b in q.HUMANS}
    promoted = set(); fails = {b: 0 for b in q.HUMANS}; runs = []; n = {b: 0 for b in q.HUMANS}; day = f"{datetime.date.today():%y%m%d}"
    st = g.Status(t0, deadline); st.iters = a.iters
    def refresh_status():
        st.best = {b: dict(run=names[b], m=measure(b, best[b], best)) for b in q.HUMANS}; st.ship = {b: m0[b] for b in q.HUMANS}
    refresh_status(); threading.Thread(target=st.loop, daemon=True).start()

    def record(name, me, ckpt, warm, t_start, tel):
        before = measure(me, best[me], best)
        m, cur, why = judge(me, ckpt, best, ship_jit[me])
        if not why:
            best[me] = ckpt; names[me] = name; fails[me] = 0; promoted.add(me); result = "PROMOTED"
            st.events.append(f"{name} promoted ({m['three']:.3f})")
        else: fails[me] += 1; result = "kept as a run: " + "; ".join(why)
        shares = {b: measure(b, best[b], best)["three"] for b in q.HUMANS}
        if result == "PROMOTED": timeline.append(dict(t=hrs(), body=me, stage="tonight", run=name, m=measure(me, best[me], best)))
        q.note(f"{name}: share {m['three']:.3f} (best {cur['three']:.3f}), duels { {o: round(v, 3) for o, v in m['duels'].items()} }, jitter {m['jitter']:.4f} -> {result}; "
               f"shares now " + ", ".join(f"{b} {s:.3f}" for b, s in shares.items()))
        r = dict(run=name, body=me, dir=os.path.dirname(ckpt), ckpt=ckpt, warm=warm, t0=t_start, t1=hrs(), m=m, best_m=before, result=result,
                 promoted=result == "PROMOTED", shares=shares, **tel)
        runs.append(r)
        with open(TELEMETRY, "a", encoding="utf-8") as f: f.write(json.dumps(r) + "\n")
        refresh_status(); save(False)

    def save(done):
        g.save(SUMMARY, dict(best={b: dict(ckpt=best[b], m=measure(b, best[b], best), run=names[b], src="tonight" if b in promoted else src[b]) for b in q.HUMANS},
                             shipped=m0, start=datetime.datetime.fromtimestamp(t0).isoformat(), hours=hrs(), budget_hours=a.hours, fails=fails,
                             timeline=timeline, done=done, yardstick="round robin"))
    save(False)
    seeds = a.seed_runs if a.seed_runs is not None else sorted(c for b in ("kim", "trump") for c in glob.glob(os.path.join(q.ROOT, "runs", f"{b}_hill4", f"{b}_g261029_*", "model_final.pt")))
    for c in seeds:
        c = os.path.relpath(c, q.ROOT).replace("\\", "/") if os.path.isabs(c) else c
        me = next(b for b in q.HUMANS if f"/{b}_" in c); name = c.split("/")[-2]
        st.run = f"re-judging {name}"; tel = g.curve(os.path.join(q.LOG_DIR, f"{name}.log"), os.path.dirname(os.path.join(q.ROOT, c))) if os.path.exists(os.path.join(q.LOG_DIR, f"{name}.log")) else {}
        record(name, me, c, "", hrs(), tel); fails[me] = 0          # re-judging an old run is not a failed attempt
    while True:
        shares = {b: measure(b, best[b], best)["three"] for b in q.HUMANS}
        if all(s >= BALANCED for s in shares.values()): q.note(f"balanced: every share >= {BALANCED} ({shares})"); break
        if left() < est + 20: q.note(f"{left():.0f} min left: no time for another run"); break
        me = max(q.HUMANS, key=lambda b: max(0.0, FAIR - shares[b]) / (1 + 0.5 * fails[b]))
        others = [b for b in q.HUMANS if b != me]
        n[me] += 1; name = f"{me}_b{day}_{n[me]:02d}"; rung = f"{me}_hill4"; run_dir = os.path.join(q.ROOT, "runs", rung, name)
        ckpt = f"runs/{rung}/{name}/model_final.pt"; log = os.path.join(q.LOG_DIR, f"{name}.log"); t_start = hrs()
        if os.path.isdir(run_dir) and not os.path.exists(os.path.join(run_dir, "model_final.pt")): shutil.rmtree(run_dir)
        opp = [f"{best[me]}:slot1", f"{best[me]}:slot1:live"] + [f"{best[x]}:slot{i + 2}" for i, x in enumerate(others)]
        q.note(f"{name}: share {shares[me]:.3f} (failed attempts {fails[me]}), warm from {best[me]}, {left():.0f} min left")
        st.run, st.log = name, log
        if not os.path.exists(os.path.join(q.ROOT, ckpt)):
            p = q.run(["scripts/train.py", "--rung", rung, "--name", name, "--num-envs", str(a.num_envs), "--iters", str(a.iters), "--warm", best[me],
                       "--opponent"] + opp + ["--frozen-deterministic", "--critic-warmup", "15", "--reward", "draw=-20", "--self-play", str(g.SELF_PLAY)], me, log)
            if p.returncode != 0 or not os.path.exists(os.path.join(q.ROOT, ckpt)):
                q.note(f"{name}: training FAILED (rc {p.returncode}), see {log}"); fails[me] += 1; continue
        st.run = f"{name} (judging)"
        record(name, me, ckpt, best[me], t_start, g.curve(log, run_dir))
    st.run, st.log = "export", None
    final = {b: measure(b, best[b], best) for b in q.HUMANS}
    prod = {}
    for b in q.HUMANS:
        d = os.path.join(g.PROD, b); os.makedirs(d, exist_ok=True)
        exported = False
        if b in promoted and not a.no_export:
            prev = os.path.join(d, "previous"); shutil.rmtree(prev, ignore_errors=True); os.makedirs(prev)
            for f in glob.glob(os.path.join(g.MODELS, b, "fighter_*")):
                if not f.endswith(".meta"): shutil.copy(f, prev)
            p = q.run(["scripts/export_policy.py", "--ckpt", best[b], "--name", "fighter", "--value"], b, os.path.join(q.LOG_DIR, f"{names[b]}_export.log"))
            exported = p.returncode == 0
            q.note(f"{b}: {names[b]} exported to Models/{b}/fighter_policy.onnx" if exported else f"{b}: export of {names[b]} FAILED, Models unchanged")
        else: q.note(f"{b}: Models/{b} unchanged")
        shutil.copy(os.path.join(q.ROOT, best[b]), os.path.join(d, "best.pt"))
        prod[b] = dict(ckpt=best[b], run=names[b] or "shipped", m=final[b], start=m0[b], exported=exported)
    g.save(os.path.join(g.PROD, "production.json"), dict(date=datetime.date.today().isoformat(), yardstick="round robin", bodies=prod,
                                                        unity_gates_pending=[b for b in q.HUMANS if prod[b]["exported"]]))
    save(True)
    s = json.load(open(SUMMARY)); s["round_robin"] = {b: dict(three=final[b]["three"], jitter=final[b]["jitter"]) for b in q.HUMANS}; s["production"] = prod; g.save(SUMMARY, s)
    p = q.run(["scripts/overnight_game_report.py", "--summary", SUMMARY, "--telemetry", TELEMETRY, "--out",
               os.path.join(os.path.dirname(q.ROOT), "SCREENSHOTS", f"training-{datetime.date.today():%y%m%d}-balance")], "g1", capture=True)
    q.note(f"report: {p.stdout.strip().splitlines()[-1] if p.returncode == 0 and p.stdout.strip() else 'FAILED ' + (p.stderr or '')[-300:]}")
    q.note(f"=== balance_game done after {hrs():.1f} h ===")


if __name__ == "__main__":
    main()
