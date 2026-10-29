"""Unattended queue: the hill brains retrained with their own body in the ring (rung <body>_hill4, 2026-10-28).

The shipped kim_hill2 holds off a copy of itself: in the Python mirror duel 89 % of the rounds reach the bell, and quick
match seats two bodies of every kind. For each body: learner a_ warm-started from the shipped Models/<body>/fighter_policy.onnx
(rebuilt into a checkpoint by onnx_to_ckpt.py, with the brain's fighter_action.json as its action path), against a
frozen copy of itself (slot1) and the other two humans' shipped fighters (slot2, slot3) on the four-body ring
scene_koth_4p_<me>_<me>_<other>_<other>_train.xml (python -m koth.build_mjcf mix ..., KOTH_ROBOT=g1); half the worlds
are duels against one of the three.
Then eval.py, new brain and shipped brain: the mirror duel (each against a copy of itself, 25 s bell), the three-way
<body>_hill rung against the other two shipped fighters, and the duel of the new brain against the shipped one.
Exported over Models/<body>/fighter_policy.onnx (+ value, + fighter_action.json) only when its mirror duels are decided at
least MIRROR_DECIDED of the time and MIRROR_GAIN more often than the shipped brain's, its three-way stays within
THREE_SLACK of the shipped brain's, it wins at least DUEL_FLOOR of the decided duels against the shipped brain and moves
no more per step than it (jitter). Unity afterwards: GameScene.RefreshBrains, then RunGetupDuelStats(..., hill: true)
against duel_stats.py --getup --hill-rule and RunHumanFfaStats against eval.py.

  .venv/Scripts/python.exe scripts/queue_hill4.py [--iters 400] [--num-envs 1024] [--bodies kim trump grandpa] [--self-play 25 --run hill4b]
--self-play N: slot1 draws between the shipped brain and a live copy of the learner refreshed every N iterations (train.py
--self-play): hill4a, against the shipped copy only, made every brain stall in its own mirror (log 2026-10-28).
Progress: runs/overnight/progress_hill4.txt; eval rows: runs/overnight/<body>_hill4_eval.json; TensorBoard on :6006.
"""
import argparse, json, os, shutil, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import queue_hill as q

q.PROGRESS = os.path.join(q.LOG_DIR, "progress_hill4.txt")
MODELS = os.path.join(os.path.dirname(q.ROOT), "Assets", "PoKingHill", "Models")
MIRROR_DECIDED, MIRROR_GAIN, THREE_SLACK, DUEL_FLOOR = 0.6, 0.2, 0.03, 0.45


def shipped(body):
    """runs/shipped/<body>/model_final.pt: the shipped fighter rebuilt from its ONNX, with its action path in config.json."""
    out = os.path.join(q.ROOT, "runs", "shipped", body, "model_final.pt")
    if not os.path.exists(out):
        p = q.run(["scripts/onnx_to_ckpt.py", "--onnx", os.path.join(MODELS, body, "fighter_policy.onnx"), "--out", out], body, capture=True)
        if p.returncode != 0: raise SystemExit(f"onnx_to_ckpt failed for {body}: {p.stdout[-400:]}{p.stderr[-400:]}")
        side = os.path.join(MODELS, body, "fighter_action.json")
        action = json.load(open(side)) if os.path.exists(side) else None
        json.dump({"env": {"action": action}, "from_onnx": True}, open(os.path.join(os.path.dirname(out), "config.json"), "w"), indent=1)
        q.note(f"{body}: shipped fighter rebuilt as {os.path.relpath(out, q.ROOT)} (action path {action})")
    return os.path.relpath(out, q.ROOT).replace("\\", "/")


def decided(rows): return 1.0 - q.mean(rows, "undecided")


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--iters", type=int, default=400); ap.add_argument("--num-envs", type=int, default=1024)
    ap.add_argument("--bodies", nargs="+", default=list(q.HUMANS)); ap.add_argument("--self-play", type=int, default=0)
    ap.add_argument("--run", default="hill4a", help="run name suffix: runs/<body>_hill4/<body>_<run>"); a = ap.parse_args()
    q.note(f"=== queue_hill4 {a.run}: {a.bodies}, {a.iters} iterations, {a.num_envs} worlds, self-play every {a.self_play or '-'} ===")
    q.ensure_tensorboard()
    ship = {b: shipped(b) for b in q.HUMANS}
    for me in a.bodies:
        others = [h for h in q.HUMANS if h != me]
        bodies = [me, me] + others
        scene = os.path.join(q.ROOT, "assets", "g1", f"scene_koth_4p_{'_'.join(bodies)}_train.xml")
        if not os.path.exists(scene):
            q.run(["-m", "koth.build_mjcf", "mix"] + bodies, "g1", os.path.join(q.LOG_DIR, f"build_mix_4p_{me}.log"))
        rung, name = f"{me}_hill4", f"{me}_{a.run}"
        ckpt = f"runs/{rung}/{name}/model_final.pt"
        opp = [f"{ship[me]}:slot1"] + ([f"{ship[me]}:slot1:live"] if a.self_play else []) + [f"{ship[o]}:slot{i + 2}" for i, o in enumerate(others)]
        if not os.path.exists(os.path.join(q.ROOT, ckpt)):
            q.note(f"{name}: training ({rung}, warm from {ship[me]}, opponents itself + {others}, half the worlds duels)")
            p = q.run(["scripts/train.py", "--rung", rung, "--name", name, "--num-envs", str(a.num_envs), "--iters", str(a.iters),
                       "--warm", ship[me], "--opponent"] + opp + ["--frozen-deterministic", "--critic-warmup", "15", "--reward", "draw=-20"]
                      + (["--self-play", str(a.self_play)] if a.self_play else []), me, os.path.join(q.LOG_DIR, f"{name}.log"))
            if p.returncode != 0: q.note(f"{name}: training FAILED (rc {p.returncode}), see runs/overnight/{name}.log"); continue
        three = ["--rung", f"{me}_hill", "--opponent"] + [f"{ship[o]}:slot{i + 1}" for i, o in enumerate(others)] + ["--seconds", "20"]
        duel = lambda ck, op: q.evaluate(me, ["--rung", q.DUEL_RUNG[me], "--ckpt", ck, "--opponent", op, "--seconds", "25"])
        ev = {"mirror_new": duel(ckpt, ckpt), "mirror_shipped": duel(ship[me], ship[me]), "duel_vs_shipped": duel(ckpt, ship[me]),
              "three_new": q.evaluate(me, three[:2] + ["--ckpt", ckpt] + three[2:]),
              "three_shipped": q.evaluate(me, three[:2] + ["--ckpt", ship[me]] + three[2:])}
        with open(os.path.join(q.LOG_DIR, f"{name}_eval.json"), "w", encoding="utf-8") as f: json.dump(ev, f, indent=1)
        m_new, m_old = decided(ev["mirror_new"]), decided(ev["mirror_shipped"])
        t_new, t_old = q.mean(ev["three_new"], "learner_last_one_in"), q.mean(ev["three_shipped"], "learner_last_one_in")
        d = q.share(ev["duel_vs_shipped"]); j_new, j_old = q.mean(ev["three_new"], "jitter_rad"), q.mean(ev["three_shipped"], "jitter_rad")
        q.note(f"{name}: mirror duel decided {m_new:.3f} (shipped {m_old:.3f}); three-way last one in {t_new:.3f} (shipped {t_old:.3f}); "
               f"duel vs shipped decided share {d:.3f}; jitter {j_new:.4f} rad/step (shipped {j_old:.4f})")
        if m_new >= max(MIRROR_DECIDED, m_old + MIRROR_GAIN) and t_new >= t_old - THREE_SLACK and d >= DUEL_FLOOR and j_new <= j_old:
            p = q.run(["scripts/export_policy.py", "--ckpt", ckpt, "--name", "fighter", "--value"], me, os.path.join(q.LOG_DIR, f"{name}_export.log"))
            if p.returncode == 0:
                shutil.copy(os.path.join(q.ROOT, ckpt), os.path.join(q.ROOT, "runs", "shipped", me, f"{name}.pt"))
                q.note(f"{name}: PROMOTED -> Assets/PoKingHill/Models/{me}/fighter_policy.onnx")
            else: q.note(f"{name}: passed the bars but export_policy refused or failed, see runs/overnight/{name}_export.log")
        else:
            q.note(f"{name}: kept as a run (needs mirror decided >= max({MIRROR_DECIDED}, shipped + {MIRROR_GAIN}), three-way within "
                   f"{THREE_SLACK} of shipped, duel vs shipped >= {DUEL_FLOOR}, jitter <= shipped)")
    q.note(f"=== queue_hill4 {a.run} done ===")


if __name__ == "__main__":
    main()
