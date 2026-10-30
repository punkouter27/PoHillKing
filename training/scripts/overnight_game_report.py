"""HTML report of overnight_game.py: what the night's training did for each fighter, in plain words and simple charts.

Reads runs/overnight/overnight_game_summary.json and overnight_game_telemetry.jsonl; writes one self-contained page (inline
SVG, light and dark, hover tooltips, a table under every chart) to SCREENSHOTS/training-<YYMMDD>-game/index.html and prints
its path. Milestone tiers (AGENTS.md): Toddler = wins fewer three-ways than a fair third, Child = at least a fair third,
Adult = no urgency left (three-way >= 0.5, every duel >= 0.5, smooth).

  .venv/Scripts/python.exe scripts/overnight_game_report.py [--summary s.json] [--telemetry t.jsonl] [--out dir]
"""
import argparse, datetime, html, json, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOG = os.path.join(ROOT, "runs", "overnight")
BODIES = ("kim", "trump", "grandpa")
NAME = {"kim": "Kim", "trump": "Trump", "grandpa": "Grandpa"}
WHO = {"kim": "56 kg woman", "trump": "102 kg man", "grandpa": "86 kg man"}
FAIR = 1 / 3
esc = html.escape


def fmt_pct(v): return f"{100 * v:.0f}%"


def tier(m):
    if m["three"] >= 0.5 and all(d >= 0.5 for d in m["duels"].values()) and m["jitter"] <= 0.06: return "Adult"
    return "Child" if m["three"] >= FAIR else "Toddler"


class Axis:
    def __init__(s, lo, hi, a, b): s.lo, s.hi, s.a, s.b = lo, hi, a, b
    def __call__(s, v): return s.a + (v - s.lo) / ((s.hi - s.lo) or 1) * (s.b - s.a)


def ticks(lo, hi, n=5):
    span = (hi - lo) or 1; raw = span / n; mag = 10 ** __import__("math").floor(__import__("math").log10(raw))
    step = min((m * mag for m in (1, 2, 2.5, 5, 10) if m * mag >= raw), default=raw)
    t = __import__("math").ceil(lo / step) * step; out = []
    while t <= hi + 1e-9: out.append(round(t, 10)); t += step
    return out


def tick_label(v, pct):
    if pct: return f"{100 * v:.0f}%"
    a = abs(v)
    return f"{v / 1000:.0f}K" if a >= 1000 else f"{v:.0f}" if a >= 10 or v == int(v) else f"{v:.2g}" if a >= 0.01 else f"{v:.1g}"


def frame(w, h, x, y, xt, yt, xlab, ylab, xpct=False, ypct=False):
    """Recessive chrome: horizontal hairline grid, baseline, tick labels, axis titles."""
    s = []
    for t in yt:
        s.append(f'<line class="grid" x1="{x.a}" x2="{x.b}" y1="{y(t):.1f}" y2="{y(t):.1f}"/>'
                 f'<text class="tick" x="{x.a - 6}" y="{y(t) + 4:.1f}" text-anchor="end">{tick_label(t, ypct)}</text>')
    for t in xt:
        s.append(f'<text class="tick" x="{x(t):.1f}" y="{y.a + 16}" text-anchor="middle">{tick_label(t, xpct)}</text>')
    s.append(f'<line class="axis" x1="{x.a}" x2="{x.b}" y1="{y.a}" y2="{y.a}"/>')
    if xlab: s.append(f'<text class="axt" x="{(x.a + x.b) / 2}" y="{h - 4}" text-anchor="middle">{esc(xlab)}</text>')
    if ylab: s.append(f'<text class="axt" x="{x.a}" y="12">{esc(ylab)}</text>')
    return "".join(s)


def svg(w, h, body, label): return f'<svg viewBox="0 0 {w} {h}" width="100%" role="img" aria-label="{esc(label)}">{body}</svg>'


def dumbbell(rows, label, lo=0.0, hi=1.0, pct=True, ref=None, ref_label="", better="higher", before_label="before (shipped brain)"):
    """One row per body: a hollow grey dot (before) joined to a filled body-coloured dot (after)."""
    w, row_h, top = 640, 46, 26; h = top + row_h * len(rows) + 52
    x = Axis(lo, hi, 110, w - 60); y0 = top + row_h * len(rows)
    s = [frame(w, h, x, Axis(0, 1, y0, top), ticks(lo, hi), [], "", "", xpct=pct)]
    if ref is not None:
        s.append(f'<line class="ref" x1="{x(ref):.1f}" x2="{x(ref):.1f}" y1="{top - 6}" y2="{y0}"/>'
                 f'<text class="tick" x="{x(ref) + 4:.1f}" y="{top - 10}">{esc(ref_label)}</text>')
    f = fmt_pct if pct else (lambda v: f"{v:.3f}")
    for i, (b, before, after, note) in enumerate(rows):
        cy = top + row_h * i + row_h / 2
        s.append(f'<text class="lbl" x="8" y="{cy + 4}">{NAME[b]}</text>')
        x1, x2 = x(before), x(after); gain = (after - before) if better == "higher" else (before - after)
        tip = f"{NAME[b]}: {f(before)} before, {f(after)} now ({'better' if gain > 0 else 'worse' if gain < 0 else 'same'}){note}"
        s.append(f'<g data-tip="{esc(tip)}"><rect x="{min(x1, x2) - 10:.1f}" y="{cy - 14}" width="{abs(x2 - x1) + 20:.1f}" height="28" fill="transparent"/>'
                 f'<line x1="{x1:.1f}" x2="{x2:.1f}" y1="{cy}" y2="{cy}" stroke="var(--{b})" stroke-width="2"/>'
                 f'<circle cx="{x1:.1f}" cy="{cy}" r="5" class="before"/>'
                 f'<circle cx="{x2:.1f}" cy="{cy}" r="6" fill="var(--{b})" class="ring"/></g>')
        right = x2 >= x1
        s.append(f'<text class="val" x="{x2 + (10 if right else -10):.1f}" y="{cy + 4}" text-anchor="{"start" if right else "end"}">{f(after)}</text>'
                 f'<text class="tick" x="{x1 + (-8 if right else 8):.1f}" y="{cy + 4}" text-anchor="{"end" if right else "start"}">{f(before)}</text>')
    s.append(f'<g class="legend" transform="translate(110,{h - 10})"><circle cx="0" cy="-4" r="5" class="before"/><text class="tick" x="10" y="0">{before_label}</text>'
             f'<circle cx="170" cy="-4" r="6" fill="var(--ink2)"/><text class="tick" x="180" y="0">after training (colour = fighter)</text></g>')
    return svg(w, h, "".join(s), label)


def lines(series, label, xlab, ylab, ypct=False, w=640, h=300, ref=None, ref_label="", step=False, xmax=None, ylim=None, extra=None):
    """series: [dict(key, name, pts=[(x, y, tip)], faint=bool, label_end=bool)]."""
    xs = [p[0] for s_ in series for p in s_["pts"]]; ys = [p[1] for s_ in series for p in s_["pts"]]
    if not xs: return '<p class="empty">No data yet.</p>'
    lo, hi = min(ys + ([ref] if ref is not None else [])), max(ys + ([ref] if ref is not None else []))
    pad = (hi - lo) * 0.08 or 0.05; lo, hi = (max(0, lo - pad) if lo >= 0 else lo - pad), hi + pad
    if ypct: lo, hi = max(0, lo), min(1, hi) if hi <= 1 else hi
    if ylim: lo, hi = ylim
    x = Axis(min(xs), xmax if xmax is not None else max(xs), 52, w - 96); y = Axis(lo, hi, h - 40, 22)
    s = [frame(w, h, x, y, ticks(x.lo, x.hi), ticks(lo, hi, 4), xlab, ylab, ypct=ypct)]
    if ref is not None:
        s.append(f'<line class="ref" x1="{x.a}" x2="{x.b}" y1="{y(ref):.1f}" y2="{y(ref):.1f}"/><text class="tick" x="{x.b + 4}" y="{y(ref) + 4:.1f}">{esc(ref_label)}</text>')
    ends = []
    for se in sorted(series, key=lambda z: not z.get("faint")):
        pts = se["pts"]
        if not pts: continue
        d = []
        for i, (px, py, _) in enumerate(pts):
            if i and step: d.append(f"L{x(px):.1f},{y(pts[i - 1][1]):.1f}")
            d.append(f"{'M' if not i else 'L'}{x(px):.1f},{y(py):.1f}")
        op = ' stroke-opacity="0.35"' if se.get("faint") else ""
        s.append(f'<path d="{"".join(d)}" fill="none" stroke="var(--{se["key"]})" stroke-width="{1.5 if se.get("faint") else 2}"{op} stroke-linejoin="round"/>')
        for px, py, tip in pts:
            if se.get("markers"): s.append(f'<circle cx="{x(px):.1f}" cy="{y(py):.1f}" r="4" fill="var(--{se["key"]})" class="ring"/>')
            s.append(f'<circle cx="{x(px):.1f}" cy="{y(py):.1f}" r="9" fill="transparent" data-tip="{esc(tip)}"/>')
        if se.get("label_end"):
            px, py, _ = pts[-1]; ends.append([y(py) + 4, x(px) + 8, se["name"]])
    ends.sort()
    for i in range(1, len(ends)): ends[i][0] = max(ends[i][0], ends[i - 1][0] + 14)     # end labels never overlap
    for ly, lx, nm in ends: s.append(f'<text class="lbl" x="{lx:.1f}" y="{ly:.1f}">{esc(nm)}</text>')
    if extra: s.append(extra(x, y))
    return svg(w, h, "".join(s), label)


def bars(rows, label, ref=None, ref_label=""):
    """Horizontal bars from a zero baseline, one per body, value label at the end."""
    w, bh, gap, top = 640, 22, 18, 24; h = top + (bh + gap) * len(rows) + 28
    hi = max([v for _, v, _ in rows] + [ref or 0, 0.01]) * 1.15; x = Axis(0, min(1, hi) if hi <= 1 else hi, 110, w - 60)
    yb = top + (bh + gap) * len(rows)
    s = [frame(w, h, x, Axis(0, 1, yb, top), ticks(0, x.hi), [], "", "", xpct=True)]
    for i, (b, v, tip) in enumerate(rows):
        y_ = top + (bh + gap) * i + gap / 2; xe = x(v)
        s.append(f'<text class="lbl" x="8" y="{y_ + bh / 2 + 4}">{NAME[b]}</text>'
                 f'<path data-tip="{esc(tip)}" fill="var(--{b})" d="M{x.a},{y_} H{xe - 4:.1f} q4,0 4,4 V{y_ + bh - 4} q0,4 -4,4 H{x.a} Z"/>'
                 f'<text class="val" x="{xe + 8:.1f}" y="{y_ + bh / 2 + 4}">{fmt_pct(v)}</text>')
    if ref is not None:
        s.append(f'<line class="ref" x1="{x(ref):.1f}" x2="{x(ref):.1f}" y1="{top - 6}" y2="{yb}"/><text class="tick" x="{x(ref) + 4:.1f}" y="{top - 10}">{esc(ref_label)}</text>')
    return svg(w, h, "".join(s), label)


def table(head, rows, cls=""):
    return (f'<table class="{cls}"><thead><tr>{"".join(f"<th>{esc(c)}</th>" for c in head)}</tr></thead><tbody>'
            + "".join("<tr>" + "".join(f"<td>{c}</td>" for c in r) + "</tr>" for r in rows) + "</tbody></table>")


def delta(new, old, pct=True, better="higher"):
    d = new - old; good = d > 0 if better == "higher" else d < 0
    if (abs(round(100 * d)) == 0 if pct else abs(d) < 5e-5): return '<span class="flat">same</span>'
    txt = f"{100 * d:+.0f} pts" if pct else f"{d:+.4f}"
    return f'<span class="{"up" if good else "down"}">{"▲" if d > 0 else "▼"} {txt}</span>'


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--summary", default=os.path.join(LOG, "overnight_game_summary.json"))
    ap.add_argument("--telemetry", default=os.path.join(LOG, "overnight_game_telemetry.jsonl")); ap.add_argument("--out", default=None); a = ap.parse_args()
    S = json.load(open(a.summary, encoding="utf-8"))
    rr_mode = S.get("yardstick") == "round robin"          # balance_game.py: every number is against the other bodies' CURRENT best brains
    before_word = "at the start" if rr_mode else "shipped"
    runs = [json.loads(l) for l in open(a.telemetry, encoding="utf-8")] if os.path.exists(a.telemetry) else []
    best, ship, tl = S["best"], S["shipped"], S.get("timeline", [])
    rr = S.get("round_robin"); prod = S.get("production", {})
    start = datetime.datetime.fromisoformat(S["start"]); hours = S.get("hours", 0); budget = S.get("budget_hours", hours)
    day = (start + datetime.timedelta(hours=hours)).strftime("%y%m%d")
    out = a.out or os.path.join(os.path.dirname(ROOT), "SCREENSHOTS", f"training-{day}-game"); os.makedirs(out, exist_ok=True)
    promos = [r for r in runs if r.get("promoted")]
    start_best = {b: next((e for e in tl if e["body"] == b and e["stage"] == "earlier runs"), next(e for e in tl if e["body"] == b and e["stage"] == "shipped")) for b in BODIES}
    improved = [b for b in BODIES if best[b]["m"]["three"] > ship[b]["three"] + 1e-9]

    # 1. headline dumbbell: three-way share, shipped -> now
    c1 = dumbbell([(b, ship[b]["three"], best[b]["m"]["three"], f" · brain {best[b]['run'] or 'shipped'}") for b in BODIES],
                  "Three-way win share before and after", 0, max(0.8, max(best[b]["m"]["three"] for b in BODIES) + 0.1), ref=FAIR, ref_label="fair share 33%",
                  before_label=f"before ({before_word})")
    t1 = table(["Fighter", "Brain " + before_word, "Best brain now", "Change", "Brain", "Where it came from", "Tier"],
               [[NAME[b], fmt_pct(ship[b]["three"]), fmt_pct(best[b]["m"]["three"]), delta(best[b]["m"]["three"], ship[b]["three"]),
                 esc(best[b]["run"] or "shipped"), {"shipped": "unchanged", "start": "unchanged (start brain)", "judge_hill": "earlier runs (judged today)", "tonight": "trained tonight"}.get(best[b]["src"], best[b]["src"]),
                 f'<span class="tier {tier(best[b]["m"]).lower()}">{tier(best[b]["m"])}</span>'] for b in BODIES])

    # 2. progress through the night: best three-way (step) + every attempt (dots)
    ser = []
    for b in BODIES:
        pts = [(0.0, start_best[b]["m"]["three"], f"{NAME[b]} at the start: {fmt_pct(start_best[b]['m']['three'])} ({start_best[b]['run'] or 'shipped'})")]
        pts += [(e["t"], e["m"]["three"], f"{NAME[b]}: {e['run']} promoted at {e['t']:.1f} h, {fmt_pct(e['m']['three'])}") for e in tl if e["body"] == b and e["stage"] == "tonight"]
        pts.append((max(hours, pts[-1][0]), best[b]["m"]["three"], f"{NAME[b]} at the end: {fmt_pct(best[b]['m']['three'])}"))
        ser.append(dict(key=b, name=NAME[b], pts=pts, label_end=True, markers=True))
    xa = [r for r in runs if r.get("m")]; xmx = max(budget, hours, 0.1)
    ys = [p[1] for s_ in ser for p in s_["pts"]] + [r["m"]["three"] for r in xa] + [FAIR]
    pad = (max(ys) - min(ys)) * 0.08 or 0.05
    def cross(x, y):
        out = []
        for r in xa:
            cx, cy, verdict = x(r["t1"]), y(r["m"]["three"]), "PROMOTED" if r.get("promoted") else r["result"]
            tip = f"{r['run']} ({NAME[r['body']]}) finished at {r['t1']:.1f} h: {fmt_pct(r['m']['three'])} three-way · {verdict}"
            out.append(f'<g data-tip="{esc(tip)}"><path d="M{cx:.1f},{cy:.1f} m-5,-5 l10,10 m0,-10 l-10,10" stroke="var(--{r["body"]})" '
                       f'stroke-width="2" stroke-opacity="{1 if r.get("promoted") else 0.45}"/><circle cx="{cx:.1f}" cy="{cy:.1f}" r="9" fill="transparent"/></g>')
        return "".join(out)
    c2 = lines(ser, "Best three-way share through the night", "hours of training", "three-way win share", ypct=True, step=True, ref=FAIR,
               ref_label="fair 33%", xmax=xmx, ylim=(max(0, min(ys) - pad), max(ys) + pad), extra=cross)
    t2 = table(["Run", "Fighter", "Started from", "Hours", "Three-way", "vs best before", "Duels (decided, won)", "Smoothness (jitter)", "Result"],
               [[esc(r["run"]), NAME[r["body"]], esc(os.path.basename(os.path.dirname(r.get("warm", "")))) or "-", f"{r['t0']:.1f}-{r['t1']:.1f}",
                 fmt_pct(r["m"]["three"]) if r.get("m") else "-", delta(r["m"]["three"], r["best_m"]["three"]) if r.get("m") else "-",
                 ", ".join(f"vs {NAME[o]} {fmt_pct(v)}" for o, v in r["m"]["duels"].items()) if r.get("m") and r["m"]["duels"] else "not measured (three-way failed)",
                 f"{r['m']['jitter']:.4f}" if r.get("m") else "-",
                 '<b class="up">promoted</b>' if r.get("promoted") else esc(r["result"])] for r in runs])

    # 3. training curves per fighter: mean reward, rounds decided, value loss (solid = promoted, faint = not)
    curves = []
    for key, ylab, pct in (("reward", "mean reward per episode", False), ("decided", "rounds decided (not timed out)", True), ("value_loss", "value loss (critic error)", False)):
        row = []
        for b in BODIES:
            ser3 = [dict(key=b, name=r["run"], faint=not r.get("promoted"),
                         pts=[(c["it"], c[key], f"{r['run']} iteration {c['it']}: {key.replace('_', ' ')} {c[key]:.3g}") for c in r.get("curve", []) if key in c])
                    for r in runs if r["body"] == b]
            ser3 = [z for z in ser3 if z["pts"]]
            row.append(f'<figure class="mini"><figcaption>{NAME[b]}</figcaption>'
                       + (lines(ser3, f"{NAME[b]} {ylab}", "iteration", ylab, ypct=pct, w=360, h=220) if ser3 else '<p class="empty">No run for this fighter.</p>') + "</figure>")
        curves.append(f'<h3>{esc(ylab[0].upper() + ylab[1:])}</h3><div class="grid3">{"".join(row)}</div>')
    tc = table(["Run", "Iterations", "Reward first → last", "Decided first → last", "Value loss first → last", "Checkpoints"],
               [[esc(r["run"]), str(r["curve"][-1]["it"] + 1) if r.get("curve") else "-",
                 f"{r['curve'][0].get('reward', 0):.2f} → {r['curve'][-1].get('reward', 0):.2f}" if r.get("curve") else "-",
                 f"{fmt_pct(r['curve'][0].get('decided', 0))} → {fmt_pct(r['curve'][-1].get('decided', 0))}" if r.get("curve") else "-",
                 f"{r['curve'][0].get('value_loss', 0):.3g} → {r['curve'][-1].get('value_loss', 0):.3g}" if r.get("curve") else "-",
                 esc(", ".join(r.get("checkpoints", [])) or "model_final.pt")] for r in runs])

    # 4. duels, smoothness, round robin
    td = table(["Fighter", "Opponent", f"Brain {before_word} wins", "Best brain now wins", "Change"],
               [[NAME[b], NAME[o], fmt_pct(ship[b]["duels"][o]), fmt_pct(best[b]["m"]["duels"][o]), delta(best[b]["m"]["duels"][o], ship[b]["duels"][o])]
                for b in BODIES for o in BODIES if o != b and o in best[b]["m"]["duels"]])
    jmax = max([ship[b]["jitter"] for b in BODIES] + [best[b]["m"]["jitter"] for b in BODIES]) * 1.2
    c4 = dumbbell([(b, ship[b]["jitter"], best[b]["m"]["jitter"], "") for b in BODIES], "Jitter before and after (lower is smoother)", 0, jmax, pct=False, better="lower")
    c5 = bars([(b, rr[b]["three"], f"{NAME[b]} ({best[b]['run'] or 'shipped'}) is the last one standing in {fmt_pct(rr[b]['three'])} of three-ways against the other final brains")
               for b in BODIES], "Final brains against each other", ref=FAIR, ref_label="fair share 33%") if rr else '<p class="empty">The round robin runs at the end of the loop.</p>'
    t5 = table(["Fighter", "Brain", "Wins vs the other final brains", "Exported to Unity"],
               [[NAME[b], esc(best[b]["run"] or "shipped"), fmt_pct(rr[b]["three"]) if rr else "-",
                 ("yes: Models/" + b + "/fighter_policy.onnx" if prod.get(b, {}).get("exported") else "no (unchanged)") if prod else "pending"] for b in BODIES])

    kpis = [(f"{hours:.1f} h", "of unattended training"), (str(len(runs)), "training runs"), (str(len(promos)), "new best brains tonight"),
            (f"{len(improved)} of 3", "fighters beat their shipped brain")]
    headline = (f"Over {hours:.1f} hours the loop trained the fighter who needed it most, judged every new brain on the game's own matches "
                f"and kept only the ones that won more. " + (" ".join(f"{NAME[b]} now wins {fmt_pct(best[b]['m']['three'])} of three-ways "
                f"({before_word}: {fmt_pct(ship[b]['three'])})." for b in BODIES)))
    page = f"""<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>PoKingHill overnight training {day}</title><style>
.viz-root{{color-scheme:light;--surface:#fcfcfb;--page:#f9f9f7;--ink:#0b0b0b;--ink2:#52514e;--muted:#898781;--gridc:#e1e0d9;--axisc:#c3c2b7;--ring:#fcfcfb;
--kim:#2a78d6;--trump:#eb6834;--grandpa:#1baf7a;--good:#006300;--bad:#d03b3b;--border:rgba(11,11,11,.10)}}
@media (prefers-color-scheme:dark){{:root:where(:not([data-theme="light"])) .viz-root{{color-scheme:dark;--surface:#1a1a19;--page:#0d0d0d;--ink:#f0efec;--ink2:#c3c2b7;
--gridc:#2c2c2a;--axisc:#383835;--ring:#1a1a19;--kim:#3987e5;--trump:#d95926;--grandpa:#199e70;--good:#0ca30c;--bad:#e66767;--border:rgba(255,255,255,.10)}}}}
:root[data-theme="dark"] .viz-root{{color-scheme:dark;--surface:#1a1a19;--page:#0d0d0d;--ink:#f0efec;--ink2:#c3c2b7;--gridc:#2c2c2a;--axisc:#383835;--ring:#1a1a19;
--kim:#3987e5;--trump:#d95926;--grandpa:#199e70;--good:#0ca30c;--bad:#e66767;--border:rgba(255,255,255,.10)}}
body{{margin:0}} .viz-root{{background:var(--page);color:var(--ink);font:15px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif;padding:24px;min-height:100vh}}
main{{max-width:1120px;margin:0 auto}} h1{{font-size:26px;margin:0 0 4px}} h2{{font-size:19px;margin:36px 0 4px}} h3{{font-size:15px;margin:18px 0 6px;color:var(--ink2)}}
.sub,.lead{{color:var(--ink2)}} .lead{{max-width:820px}} section{{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:18px 20px;margin:14px 0}}
.kpis{{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin:18px 0}} .kpi{{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:14px 16px}}
.kpi b{{display:block;font-size:28px}} .kpi span{{color:var(--ink2);font-size:13px}}
.grid3{{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}} figure.mini{{margin:0}} figcaption{{font-weight:600;font-size:13px}}
svg text{{font:12px system-ui,-apple-system,"Segoe UI",sans-serif}} .grid{{stroke:var(--gridc);stroke-width:1}} .axis{{stroke:var(--axisc);stroke-width:1}}
.ref{{stroke:var(--muted);stroke-width:1;stroke-dasharray:4 3}} .tick{{fill:var(--muted)}} .axt{{fill:var(--ink2)}} .lbl{{fill:var(--ink);font-weight:600}} .val{{fill:var(--ink);font-weight:600}}
.before{{fill:var(--surface);stroke:var(--muted);stroke-width:2}} .ring{{stroke:var(--ring);stroke-width:2}}
table{{border-collapse:collapse;width:100%;font-size:13px;margin-top:10px;font-variant-numeric:tabular-nums}} th,td{{text-align:left;padding:5px 8px;border-bottom:1px solid var(--border)}}
th{{color:var(--ink2);font-weight:600}} details{{margin-top:8px}} summary{{cursor:pointer;color:var(--ink2);font-size:13px}}
.up{{color:var(--good)}} .down{{color:var(--bad)}} .flat{{color:var(--muted)}} .empty{{color:var(--muted)}}
.tier{{border-radius:99px;padding:1px 9px;font-size:12px;font-weight:600;border:1px solid var(--border)}} .tier.adult::before{{content:"● "}} .tier.child::before{{content:"◐ "}} .tier.toddler::before{{content:"○ "}}
#tip{{position:fixed;pointer-events:none;background:var(--ink);color:var(--surface);padding:6px 9px;border-radius:6px;font-size:12px;max-width:340px;display:none;z-index:9}}
dl{{display:grid;grid-template-columns:max-content 1fr;gap:4px 14px;margin:0}} dt{{font-weight:600}} dd{{margin:0;color:var(--ink2)}}
@media (max-width:820px){{.grid3,.kpis{{grid-template-columns:1fr 1fr}}}}
</style></head><body><div class="viz-root"><main>
<h1>Overnight training: King of the Hill fighters</h1>
<div class="sub">Started {start:%Y-%m-%d %H:%M} · {hours:.1f} h of {budget:.0f} h · three fighters trained against each other: Kim ({WHO['kim']}), Trump ({WHO['trump']}), Grandpa ({WHO['grandpa']})</div>
<div class="kpis">{"".join(f'<div class="kpi"><b>{v}</b><span>{esc(t)}</span></div>' for v, t in kpis)}</div>
<p class="lead">{esc(headline)}</p>

<h2>1 · Who wins the free-for-all?</h2>
<p class="lead">The game's quick match puts all three fighters on the hill; the last one still on top wins. {"The share is measured against the other two fighters' current best brains (a round robin), so a balanced game has every fighter near the dashed line, a fair third." if rr_mode else "The share is measured against the two other shipped brains (a fixed yardstick, so before and after compare fairly). The dashed line is a fair third."}</p>
<section>{c1}<details><summary>Table</summary>{t1}</details></section>

<h2>2 · Progress through the night</h2>
<p class="lead">Lines: each fighter's best brain so far (steps up when a new brain is promoted). Crosses: every training run when it
finished (solid = promoted, faint = kept on disk but not good enough). The loop always trains whoever needs it most.</p>
<section>{c2}<details open><summary>Every run</summary>{t2}</details></section>

<h2>3 · Learning curves</h2>
<p class="lead">What each training run saw while it learned (every 10th iteration). <b>Mean reward</b> rising means the brain is doing more of
what it is paid for (pushing others off, staying on top). <b>Rounds decided</b> is the share of training rounds that ended with a winner rather
than a stalemate. <b>Value loss</b> is how wrong the brain's own score predictions are; lower and steady is healthy. Solid = the run was promoted.</p>
<section>{"".join(curves)}<details><summary>Table</summary>{tc}</details></section>

<h2>4 · One-on-one duels</h2>
<p class="lead">Share of decided duels won against each other fighter's shipped brain. A new brain must not get worse at duels to be promoted
(at least 45%, or within 3 points of the shipped brain).</p>
<section>{td}</section>

<h2>5 · Smoothness</h2>
<p class="lead">Jitter = how much the joint targets change per control step (radians). Lower looks calmer and more human. A new brain may be at most 5% twitchier than the shipped one.</p>
<section>{c4}</section>

<h2>6 · Final brains against each other</h2>
<p class="lead">The three final brains in the same free-for-all. Close to the dashed line means a balanced game; far above means that fighter dominates.</p>
<section>{c5}{t5}</section>

<h2>Milestones and next steps</h2>
<section><dl>
<dt>○ Toddler</dt><dd>wins fewer free-for-alls than a fair third</dd><dt>◐ Child</dt><dd>wins at least a fair third</dd>
<dt>● Adult</dt><dd>wins half the free-for-alls, half of every duel, and moves smoothly (jitter ≤ 0.06)</dd>
<dt>Graduation</dt><dd>runs with a NaN gradient are never promoted (graduation.json); exported brains still need their Unity gates
(GameScene.RefreshBrains, RunHumanFfaStats, RunGetupDuelStats with the hill rule) before they are trusted in the game.</dd>
<dt>Production weights</dt><dd>training/runs/production/&lt;fighter&gt;/best.pt and production.json; the replaced ONNX files are kept in runs/production/&lt;fighter&gt;/previous/.</dd>
</dl></section>
<p class="sub">Generated {datetime.datetime.now():%Y-%m-%d %H:%M} by training/scripts/overnight_game_report.py · TensorBoard: training/runs (port 6006)</p>
</main><div id="tip"></div></div>
<script>const t=document.getElementById('tip');document.addEventListener('mousemove',e=>{{const g=e.target.closest('[data-tip]');
if(!g||!g.dataset.tip){{t.style.display='none';return}}t.textContent=g.dataset.tip;t.style.display='block';
t.style.left=Math.min(e.clientX+14,innerWidth-360)+'px';t.style.top=(e.clientY+14)+'px'}});</script></body></html>"""
    path = os.path.join(out, "index.html")
    open(path, "w", encoding="utf-8").write(page)
    print(path)


if __name__ == "__main__":
    main()
