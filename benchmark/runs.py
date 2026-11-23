"""Training runs grid: Training/runs/runs.html, one row per real training run under Training/runs/, newest first.

    python benchmark/runs.py                                   rebuild Training/runs/runs.html from the run folders
    python benchmark/runs.py --record <run-id> --ended "<how>" [--launcher <script>]
                                                               note this machine and how the run ended, then rebuild

A run is a folder Training/runs/<rung>/<name>/ written by Training/scripts/train.py; its run id is "<rung>/<name>".
The grid is rebuilt from what train.py leaves there (manifest.json: start, end, status, args, robot, commit, the
iterations reached; model_<iteration>.pt checkpoints and their times; the TensorBoard event file's name carries the
host), so a run started by hand, or one whose process was killed, still gets a row. What the folder cannot tell
(which GPU, how the run ended) comes from <run>/run_meta.json, which --record writes; train.py calls it when a run
ends, crash or not. Without it those cells read "-" (or what the manifest's status says).

Steps are env steps: iterations x num_steps_per_env x num_envs (one step of one world). Speed is the run's own env
steps per second, measured between the first and last checkpoint written in this session, so a resumed run is not
credited with the steps of its earlier sessions. Benchmark is the newest benchmark/history.json result on the same
machine from before the run started (ms per step of all worlds, and env steps/s), for comparison.
Stdlib only; core.py next to this file supplies the machine description.
"""

from __future__ import annotations

import argparse
import datetime
import html
import json
import os
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
RESULTS = ROOT / "Training" / "runs"
OUT = RESULTS / "runs.html"
BENCH = HERE / "history.json"
META = "run_meta.json"
TITLE = "PoHillKing"
SKIP = {"_benchmark"}  # the Benchmark's own throwaway run (benchmark/workload.py: runs/kim_r0b/_benchmark)


# ---------------------------------------------------------------- collect


def _json(path: Path) -> dict:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def _stamp(text: str | None) -> float | None:
    try:
        return datetime.datetime.fromisoformat(text).timestamp() if text else None
    except ValueError:
        return None


def _checkpoints(d: Path, iterations: int | None) -> list[tuple[int, float]]:
    """(iteration, mtime) of every model_<iteration>.pt; model_final.pt counts as the last iteration of the run."""
    cps = []
    for p in d.glob("model_*.pt"):
        m = re.fullmatch(r"model_(\d+)\.pt", p.name)
        if m:
            cps.append((int(m.group(1)), p.stat().st_mtime))
        elif p.name == "model_final.pt" and iterations:
            cps.append((iterations - 1, p.stat().st_mtime))
    return cps


def _speed(cps: list[tuple[int, float]], start: float, end: float, per_iter: int | None) -> float | None:
    """Env steps/s between the first and last checkpoint written in [start, end]."""
    session = sorted((c for c in cps if start - 1 <= c[1] <= end + 60), key=lambda c: c[1])
    if not per_iter or len(session) < 2 or session[-1][1] <= session[0][1] or session[-1][0] <= session[0][0]:
        return None
    return (session[-1][0] - session[0][0]) * per_iter / (session[-1][1] - session[0][1])


def _bench_for(host: str | None, before: float) -> dict | None:
    runs = [r for r in _json(BENCH).get("runs", []) if r.get("ok") and r.get("result")]
    if host:
        runs = [r for r in runs if r.get("machine", {}).get("hostname") == host]
    if not runs:
        return None
    stamp = lambda r: datetime.datetime.fromisoformat(r["when"]).timestamp()  # noqa: E731
    earlier = [r for r in runs if stamp(r) <= before]
    return (earlier or runs)[-1]


def _short(path: str | None) -> str | None:
    """A checkpoint path as the run folder it came from: runs/kim_r1b/walker/model_500.pt -> kim_r1b/walker/model_500."""
    if not path:
        return None
    p = path.replace("\\", "/")
    p = p.split("runs/", 1)[1] if "runs/" in p else p
    return p[:-3] if p.endswith(".pt") else p


def _config(rung: str, robot: str | None, a: dict) -> tuple[str, str]:
    """The recipe (rung) and the args that shape it, in a few words."""
    bits = [robot] if robot else []
    if a.get("iters") is not None and a["iters"] < 100000:
        bits.append(f"{a['iters']} iters")
    if a.get("max_minutes"):
        bits.append(f"max {a['max_minutes']:.0f} min")
    if a.get("opponent"):
        bits.append(f"{len(a['opponent'])} opponent{'s' if len(a['opponent']) > 1 else ''}")
    if a.get("self_play"):
        bits.append(f"self-play {a['self_play']}")
    if a.get("curriculum"):
        bits.append(f"curriculum {a['curriculum']}")
    if a.get("amp"):
        bits.append(f"amp {a.get('amp_weight')}")
    bits += [str(r) for r in a.get("reward") or []]
    for k in ("action_map", "action_smooth", "obs_delay", "decimation"):
        if a.get(k) is not None:
            bits.append(f"{k.replace('_', '-')} {a[k]}")
    if a.get("body_gait"):
        bits.append("body-gait")
    if a.get("seed"):
        bits.append(f"seed {a['seed']}")
    return rung, " · ".join(bits)


def _host_from_events(d: Path) -> str | None:
    """rsl_rl's TensorBoard file is events.out.tfevents.<time>.<host>.<pid>.<n>."""
    for p in sorted(d.glob("events.out.tfevents.*")):
        parts = p.name.split(".")
        if len(parts) >= 7:
            return ".".join(parts[4:-2])
    return None


def collect() -> list[dict]:
    rows = []
    if not RESULTS.is_dir():
        return rows
    for d in sorted({p.parent for n in ("manifest.json", META) for p in RESULTS.rglob(n)}):
        if d.name in SKIP:
            continue
        man = _json(d / "manifest.json")
        meta = _json(d / META)
        conf = _json(d / "config.json")
        if not man and not (meta and conf):  # shipped/ copies and other folders that are not train.py runs
            continue
        a = man.get("args") or conf.get("args") or {}
        train = man.get("train_cfg") or conf.get("train") or {}
        files = [p.stat().st_mtime for p in d.iterdir() if p.is_file() and p.name != META]
        start = _stamp(man.get("start_time")) or (min(files) if files else d.stat().st_mtime)
        end = _stamp(man.get("end_time")) or (max(files) if files else start)
        cps = _checkpoints(d, man.get("iterations"))
        iterations = man.get("iterations") or (max(c[0] for c in cps) + 1 if cps else None)
        envs = a.get("num_envs")
        horizon = train.get("num_steps_per_env")
        per_iter = horizon * envs if horizon and envs else None
        steps = iterations * per_iter if iterations and per_iter else None
        rate = _speed(cps, start, end, per_iter)
        if rate is None and steps and not a.get("resume") and end > start:
            rate = steps / (end - start)
        status = man.get("status")
        ended = meta.get("ended") or {
            "finished": "finished",
            "failed": "crashed (manifest: failed)",
        }.get(status, "still running or killed")
        if ended == "finished" and man.get("stop_reason"):
            ended += f" ({man['stop_reason'].replace('_', ' ')})"
        host = meta.get("host") or _host_from_events(d)
        git = man.get("git") or conf.get("git") or {}
        rung, cfg = _config(man.get("rung") or a.get("rung") or d.parent.name, man.get("robot") or conf.get("robot"), a)
        warm = [f"resume {_short(a['resume'])}"] if a.get("resume") else []
        warm += [f"warm {_short(a['warm'])}"] if a.get("warm") else []
        rows.append(
            dict(
                run=d.relative_to(RESULTS).as_posix(),
                start=start,
                wall=max(0.0, end - start),
                ended=ended,
                resumed=bool(a.get("resume")),
                iterations=iterations,
                steps=steps,
                rate=rate,
                bench=_bench_for(host, start),
                config=rung,
                config_args=cfg,
                envs=envs,
                warm=warm,
                commit=(git.get("commit") or "")[:7] or meta.get("commit"),  # the commit the run started from
                dirty=git.get("training_dir_dirty"),
                host=host,
                gpu=meta.get("gpu"),
                device=meta.get("device"),
                launcher=meta.get("launcher"),
            )
        )
    rows.sort(key=lambda r: r["start"], reverse=True)
    return rows


# ---------------------------------------------------------------- render


def _dur(s: float) -> str:
    h, m = divmod(int(s) // 60, 60)
    return f"{h} h {m:02d} m" if h else f"{m} m"


def _num(n: float | None, digits: int = 0) -> str:
    return "-" if n is None else f"{n:,.{digits}f}"


def render(rows: list[dict]) -> str:
    e = html.escape
    body = []
    for r in rows:
        b = r["bench"]
        if b:
            notes = [f"{b['result']['env_steps_per_s']:,} env steps/s", e(b["when"][:10])]
            if datetime.datetime.fromisoformat(b["when"]).timestamp() > r["start"]:
                notes.append("taken after the run")
            if not r["host"]:
                notes.append("machine unknown")
            bench = f"{b['result']['step_time_ms']} ms<small>{' · '.join(notes)}</small>"
        else:
            bench = "-<small>no benchmark on this machine</small>"
        ended = e(r["ended"])
        cls = "bad" if re.search(r"crash|died|killed|HALT|error|exception|fail", r["ended"], re.I) else ""
        iters = f"{_num(r['iterations'])} iterations" if r["iterations"] else ""
        body.append(
            "<tr>"
            f"<td><b>{e(r['run'])}</b>{'<small>resumed</small>' if r['resumed'] else ''}</td>"
            f"<td>{datetime.datetime.fromtimestamp(r['start']).strftime('%Y-%m-%d %H:%M')}</td>"
            f'<td class="n">{_dur(r["wall"])}</td>'
            f'<td class="{cls}">{ended}</td>'
            f'<td class="n">{_num(r["steps"])}<small>{iters}</small></td>'
            f'<td class="n"><b>{_num(r["rate"])}</b><small>env steps/s</small></td>'
            f'<td class="n">{bench}</td>'
            f"<td>{e(r['config'] or '-')}<small>{e(r['config_args'])}</small></td>"
            f'<td class="n">{e(str(r["envs"] or "-"))}</td>'
            f"<td>{e(', '.join(r['warm']) or 'cold')}</td>"
            f'<td class="mono">{e(r["commit"] or "-")}{"<small>dirty</small>" if r["dirty"] else ""}</td>'
            f"<td>{e(r['host'] or '-')}<small>{e(r['gpu'] or '')}{' · ' + e(r['device']) if r['device'] else ''}"
            f"</small></td>"
            "</tr>"
        )
    latest = rows[0] if rows else None
    prev = next((r for r in rows[1:] if r["rate"]), None) if rows else None
    delta = ""
    if latest and latest["rate"] and prev:
        pct = 100 * (latest["rate"] / prev["rate"] - 1)
        delta = f'<span class="{"up" if pct >= 0 else "down"}">{pct:+.0f}% vs {e(prev["run"])}</span>'
    summary = (
        f'<div class="sum"><div><span>Latest run</span><b>{e(latest["run"])}</b></div>'
        f"<div><span>Speed</span><b>{_num(latest['rate'])} env steps/s</b>{delta}</div>"
        f"<div><span>Wall-clock</span><b>{_dur(latest['wall'])}</b></div>"
        f"<div><span>Runs listed</span><b>{len(rows)}</b></div></div>"
        if latest
        else "<p>No training runs under Training/runs/ yet.</p>"
    )
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
    return f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>{e(TITLE)} training runs</title>
<style>
:root{{--bg:#f5f7f6;--card:#fff;--ink:#18211f;--muted:#5d6b67;--line:#d8e0dd;--accent:#0f6b5c;--bad:#b42318;--up:#1f7a43}}
@media (prefers-color-scheme:dark){{:root{{--bg:#0f1514;--card:#161e1c;--ink:#e2eae7;--muted:#91a19c;--line:#27332f;
--accent:#5cc9b0;--bad:#ff8a7a;--up:#5fcf8a;color-scheme:dark}}}}
body{{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 "Segoe UI",system-ui,sans-serif;padding:24px 16px}}
h1{{font-size:20px;margin:0 0 4px}} p{{margin:0;color:var(--muted)}} .wrap{{max-width:1500px;margin:auto;display:grid;gap:16px}}
.sum{{display:flex;flex-wrap:wrap;gap:12px 40px;background:var(--card);border:1px solid var(--line);border-radius:8px;padding:14px 18px}}
.sum span{{display:block;color:var(--muted);font-size:12px}} .sum b{{font-size:20px}} .sum .up,.sum .down{{display:block;font-size:12px}}
.up{{color:var(--up)}} .down{{color:var(--bad)}}
.t{{overflow-x:auto;background:var(--card);border:1px solid var(--line);border-radius:8px}}
table{{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}}
th,td{{padding:8px 12px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top;white-space:nowrap}}
th{{font-size:11px;text-transform:uppercase;letter-spacing:.06em;color:var(--muted);position:sticky;top:0;background:var(--card)}}
tr:last-child td{{border-bottom:0}} td.n{{text-align:right}} td small{{display:block;color:var(--muted);font-size:11px}}
td.bad{{color:var(--bad)}} .mono{{font-family:Consolas,monospace}}
</style></head><body><div class="wrap">
<div><h1>{e(TITLE)} training runs</h1><p>Every real training run under Training/runs/, newest first. Rebuilt {now} by
benchmark/runs.py. Steps = env steps (iterations x steps per env x worlds); Speed = env steps/s this session, between
its first and last checkpoint; Benchmark = the newest benchmark.ps1 result on the same machine before the run (ms per
step of all worlds).</p></div>
{summary}
<div class="t"><table><thead><tr><th>Run</th><th>Started</th><th>Wall-clock</th><th>Ended</th><th>Steps</th>
<th>Speed</th><th>Benchmark</th><th>Config</th><th>Envs</th><th>Warm start</th><th>Commit</th><th>Machine</th></tr></thead>
<tbody>{"".join(body)}</tbody></table></div></div></body></html>
"""


def build() -> Path:
    RESULTS.mkdir(parents=True, exist_ok=True)
    tmp = OUT.with_suffix(".tmp")
    tmp.write_text(render(collect()), encoding="utf-8")
    os.replace(tmp, OUT)
    return OUT


def record(run_id: str, ended: str, launcher: str | None) -> None:
    """Write Training/runs/<rung>/<name>/run_meta.json: this machine, the commit and how the run ended."""
    sys.path.insert(0, str(HERE))
    import core

    m = core.machine()
    d = RESULTS / run_id
    d.mkdir(parents=True, exist_ok=True)
    meta = dict(
        host=m["hostname"],
        gpu=m["gpu"],
        cpu=m["cpu"],
        device="CPU" if os.environ.get("CUDA_VISIBLE_DEVICES") == "-1" else None,
        commit=core.git_commit(),
        ended=ended,
        launcher=launcher,
        recorded=datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
    )
    (d / META).write_text(json.dumps(meta, indent=1), encoding="utf-8")


def main() -> None:
    p = argparse.ArgumentParser(description="Rebuild Training/runs/runs.html (the training runs grid).")
    p.add_argument("--record", metavar="RUN_ID", help="<rung>/<name>: note this machine and how the run ended first")
    p.add_argument("--ended", default="exited", help="how the run ended, in words")
    p.add_argument("--launcher", help="the script that ran it")
    a = p.parse_args()
    if a.record:
        record(a.record, a.ended, a.launcher)
    print(f"Training runs grid: {build()}")


if __name__ == "__main__":
    main()
