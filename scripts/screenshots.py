"""Before/after screenshots of a UI change in one command (AGENTS.md 1.3.1 "UI documentation").

Serves a LOCAL WebGL build (never the live site), captures the key screens at 1366x768 and 390x844 with Playwright's
Chromium, sets each beside its "before" and writes SCREENSHOTS/ui-<YYMMDD>-<feature>/:
    index.html          before | after per screen and width, with a note on what changed
    side-by-side.png    every pair in one annotated image (the changed area boxed in red)
    before-<screen>-<width>.png, after-<screen>-<width>.png   (palette PNGs, at most ~300 KB each)

    uv run --no-project --python 3.11 --with playwright==1.49.0 --with pillow python scripts/screenshots.py --feature diag-cache
        before = the newest webgl-* release built at or before HEAD~1, after = Builds/WebGL (publish_webgl.ps1 -NoPublish)
    ... --before <git ref>              before = the newest webgl-* release at or before that commit
    ... --before-dir <folder>           before = PNGs you already have, named <screen>-<width>.png (e.g. game-1366.png)
    ... --after-build <folder>          after = another local build folder
    ... --screens game=/ diag=/diag     the screens (default: game and diag)
    ... --note diag="Results kept 60 s"  a note per screen (repeatable)
    ... --wait 25                       seconds the game page gets to load before its capture

Rebuilding an old commit's WebGL player takes 20+ minutes with the pinned editor, so "before" comes from the GitHub
releases publish_webgl.ps1 makes (it keeps them all unless -Keep) or from a folder of PNGs. Chromium for Playwright 1.49
is installed once with `uv run --no-project --python 3.11 --with playwright==1.49.0 python -m playwright install chromium`.
"""

from __future__ import annotations

import argparse
import datetime as dt
import functools
import html
import http.server
import json
import os
import subprocess
import tempfile
import threading
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SIZES = ((1366, 768), (390, 844))
SCREENS = {"game": "/", "diag": "/diag"}
MAX_PNG = 300 * 1024


class _BuildHandler(http.server.SimpleHTTPRequestHandler):
    """The build served as staticwebapp.config.json serves it: /diag -> diag.html, gzip/br encodings by extension."""

    def translate_path(self, path):
        if path.split("?")[0].rstrip("/") == "/diag":
            path = "/diag.html"
        return super().translate_path(path)

    def end_headers(self):
        p = self.path.split("?")[0]
        if p.endswith(".gz") or p.endswith(".unityweb"):  # this game's builds: gzip with the .unityweb extension
            self.send_header("Content-Encoding", "gzip")
        elif p.endswith(".br"):
            self.send_header("Content-Encoding", "br")
        super().end_headers()

    def guess_type(self, path):
        p = str(path)
        for ext in (".gz", ".br", ".unityweb"):
            if p.endswith(ext):
                p = p[: -len(ext)]
        if p.endswith(".wasm"):
            return "application/wasm"
        if p.endswith(".js"):
            return "application/javascript"
        return super().guess_type(p) if p != str(path) else super().guess_type(path)

    def log_message(self, *args):
        pass


def serve(folder: str):
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(_BuildHandler, directory=folder))
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, f"http://127.0.0.1:{srv.server_address[1]}"


def git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()


def release_build(ref: str, work: str) -> tuple[str, str]:
    """Unzips the newest webgl-* release whose commit is `ref` or an ancestor of it; returns (folder, tag)."""
    want = git("rev-parse", ref)
    out = subprocess.run(
        ["gh", "release", "list", "--limit", "100", "--json", "tagName,createdAt"],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=True,
    ).stdout
    tags = sorted((r for r in json.loads(out) if r["tagName"].startswith("webgl-")), key=lambda r: r["createdAt"])
    subprocess.run(["git", "fetch", "--quiet", "--tags", "origin"], cwd=ROOT, check=False)
    for r in reversed(tags):
        tag = r["tagName"]
        ok = subprocess.run(["git", "merge-base", "--is-ancestor", tag, want], cwd=ROOT, capture_output=True)
        if ok.returncode == 0:
            subprocess.run(
                ["gh", "release", "download", tag, "-p", "webgl.zip", "-D", work, "--clobber"], cwd=ROOT, check=True
            )
            folder = os.path.join(work, "before")
            with zipfile.ZipFile(os.path.join(work, "webgl.zip")) as z:
                z.extractall(folder)
            return folder, tag
    raise SystemExit(f"No webgl-* release at or before {ref}; pass --before-dir with PNGs instead.")


def small_png(img, path: str) -> None:
    """Saves a palette PNG, scaled down until it is at most MAX_PNG bytes."""
    from PIL import Image

    scale = 1.0
    while True:
        im = img if scale == 1.0 else img.resize((int(img.width * scale), int(img.height * scale)), Image.LANCZOS)
        im.convert("RGB").quantize(colors=128, method=Image.Quantize.MEDIANCUT).save(path, optimize=True)
        if os.path.getsize(path) <= MAX_PNG or scale < 0.3:
            return
        scale *= 0.8


def capture(base: str, screens: dict, out_dir: str, prefix: str, wait_s: float) -> dict:
    """Screens of the build served at `base`: {(screen, width): png path}; console lines in <prefix>-console.json."""
    from PIL import Image
    from playwright.sync_api import sync_playwright

    shots, console = {}, {}
    with sync_playwright() as pw:
        browser = pw.chromium.launch(args=["--use-angle=d3d11", "--enable-gpu", "--ignore-gpu-blocklist"])
        try:
            for w, h in SIZES:
                for name, route in screens.items():
                    page = browser.new_page(viewport={"width": w, "height": h}, device_scale_factor=1)
                    lines = console.setdefault(f"{name}-{w}", [])
                    page.on("console", lambda m, lines=lines: lines.append(m.text))
                    page.goto(base + route, wait_until="load")
                    if route == "/":
                        try:
                            page.wait_for_function(
                                "() => { const l = document.getElementById('loading'); "
                                "return !l || getComputedStyle(l).display === 'none'; }",
                                timeout=wait_s * 1000,
                            )
                        except Exception:
                            lines.append(f"(screenshots.py: still loading after {wait_s:.0f} s)")
                        page.wait_for_timeout(4000)  # the menu fades in
                    else:
                        page.wait_for_timeout(2500)  # live probes answer
                    raw = os.path.join(out_dir, f"{prefix}-{name}-{w}.raw.png")
                    page.screenshot(path=raw)
                    page.close()
                    final = os.path.join(out_dir, f"{prefix}-{name}-{w}.png")
                    with Image.open(raw) as im:
                        small_png(im.copy(), final)
                    os.remove(raw)
                    shots[(name, w)] = final
        finally:
            browser.close()
    with open(os.path.join(out_dir, f"{prefix}-console.json"), "w", encoding="utf-8") as f:
        json.dump(console, f, indent=1)
    return shots


def from_dir(folder: str, screens: dict, out_dir: str) -> dict:
    """Before PNGs taken earlier: <screen>-<width>.png (any prefix), copied in as before-<screen>-<width>.png."""
    from PIL import Image

    shots, files = {}, os.listdir(folder)
    for name in screens:
        for w, _ in SIZES:
            hit = [f for f in files if f.lower().endswith(f"{name}-{w}.png")]
            if hit:
                final = os.path.join(out_dir, f"before-{name}-{w}.png")
                with Image.open(os.path.join(folder, sorted(hit)[0])) as im:
                    small_png(im.copy(), final)
                shots[(name, w)] = final
    return shots


def changed_box(a, b):
    """The bounding box of what differs between two screens (same size), or None."""
    from PIL import ImageChops

    if a.size != b.size:
        return (0, 0, b.width, b.height)
    diff = ImageChops.difference(a.convert("L"), b.convert("L")).point(lambda v: 255 if v > 24 else 0)
    return diff.getbbox()


def side_by_side(pairs: list, path: str) -> None:
    """One image: a row per screen/width, before | after, the changed area of "after" boxed in red."""
    from PIL import Image, ImageDraw

    rows, pad, label = [], 12, 22
    for title, before, after in pairs:
        a = Image.open(before).convert("RGB") if before else None
        b = Image.open(after).convert("RGB")
        scale = 560 / b.width if b.width > 560 else 1.0
        size = (int(b.width * scale), int(b.height * scale))
        box = changed_box(a, b) if a is not None else None
        a = a.resize(size) if a is not None else Image.new("RGB", size, (40, 40, 48))
        b = b.resize(size)
        row = Image.new("RGB", (2 * size[0] + 3 * pad, size[1] + label + pad), (11, 15, 23))
        d = ImageDraw.Draw(row)
        d.text((pad, 4), f"{title}  BEFORE", fill=(154, 163, 181))
        d.text((2 * pad + size[0], 4), f"{title}  AFTER", fill=(230, 233, 242))
        row.paste(a, (pad, label))
        row.paste(b, (2 * pad + size[0], label))
        if box:
            x0, y0, x1, y1 = (int(v * scale) for v in box)
            ox = 2 * pad + size[0]
            d.rectangle((ox + x0, label + y0, ox + x1, label + y1), outline=(240, 64, 64), width=3)
        rows.append(row)
    sheet = Image.new("RGB", (max(r.width for r in rows), sum(r.height for r in rows)), (11, 15, 23))
    y = 0
    for r in rows:
        sheet.paste(r, (0, y))
        y += r.height
    small_png(sheet, path)


def write_page(path: str, feature: str, before_what: str, after_what: str, pairs: list, notes: dict) -> None:
    def rel(p):
        return os.path.basename(p) if p else ""

    cells = []
    for (name, w), before, after in pairs:
        note = html.escape(notes.get(name, ""))
        b = f'<img src="{rel(before)}" alt="before: {name} at {w}px">' if before else "<p>(no before capture)</p>"
        cells.append(
            f"<section><h2>{html.escape(name)} · {w}px</h2>{f'<p class=note>{note}</p>' if note else ''}"
            f'<div class="pair"><figure><figcaption>Before</figcaption>{b}</figure>'
            f'<figure><figcaption>After</figcaption><img src="{rel(after)}" alt="after: {name} at {w}px"></figure></div>'
            "</section>"
        )
    page = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{html.escape(feature)} · before / after</title>
<style>
  body {{ margin: 0; background: #0b0f17; color: #e6e9f2; font: 14px/1.5 Inter, system-ui, sans-serif; }}
  main {{ max-width: 1200px; margin: 0 auto; padding: 16px; }}
  h1 {{ font-size: 20px; }} h2 {{ font-size: 15px; margin: 24px 0 6px; }} .note, .meta {{ color: #9aa3b5; }}
  .pair {{ display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }} figure {{ margin: 0; }}
  figcaption {{ color: #9aa3b5; font-size: 12px; }} img {{ max-width: 100%; border: 1px solid #232b3d; }}
  @media (max-width: 700px) {{ .pair {{ grid-template-columns: 1fr; }} }}
</style></head><body><main>
<h1>{html.escape(feature)}</h1>
<p class="meta">Before: {html.escape(before_what)}. After: {html.escape(after_what)}. Captured locally
{dt.datetime.now(dt.timezone.utc):%Y-%m-%d %H:%M} UTC at 1366x768 and 390x844 by scripts/screenshots.py.
All pairs in one image: <a style="color:#e6e9f2" href="side-by-side.png">side-by-side.png</a>.</p>
{"".join(cells)}
</main></body></html>
"""
    with open(path, "w", encoding="utf-8") as f:
        f.write(page)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--feature", required=True, help="short name, e.g. diag-cache")
    ap.add_argument("--before", default="HEAD~1", help="git ref: its newest webgl-* release is 'before'")
    ap.add_argument("--before-dir", help="folder of before PNGs named <screen>-<width>.png")
    ap.add_argument("--after-build", default=os.path.join(ROOT, "Builds", "WebGL"))
    ap.add_argument("--screens", nargs="*", help="name=route pairs (default: game=/ diag=/diag)")
    ap.add_argument("--note", action="append", default=[], help="screen=text (repeatable)")
    ap.add_argument("--wait", type=float, default=40.0, help="seconds the game page gets to load")
    a = ap.parse_args()

    screens = dict(s.split("=", 1) for s in a.screens) if a.screens else dict(SCREENS)
    notes = dict(n.split("=", 1) for n in a.note)
    stamp = dt.datetime.now(dt.timezone.utc).strftime("%y%m%d")
    out = os.path.join(ROOT, "SCREENSHOTS", f"ui-{stamp}-{a.feature}")
    os.makedirs(out, exist_ok=True)
    if not os.path.isfile(os.path.join(a.after_build, "index.html")):
        raise SystemExit(f"No WebGL build at {a.after_build}: run scripts/publish_webgl.ps1 -NoPublish first.")

    with tempfile.TemporaryDirectory() as work:
        if a.before_dir:
            before, before_what = from_dir(a.before_dir, screens, out), f"PNGs from {a.before_dir}"
        else:
            folder, tag = release_build(a.before, work)
            srv, base = serve(folder)
            try:
                before, before_what = capture(base, screens, out, "before", a.wait), f"release {tag}"
            finally:
                srv.shutdown()
        srv, base = serve(a.after_build)
        try:
            after = capture(base, screens, out, "after", a.wait)
        finally:
            srv.shutdown()
    after_what = f"local build {os.path.relpath(a.after_build, ROOT)} at {git('rev-parse', '--short', 'HEAD')}"
    pairs = [((n, w), before.get((n, w)), after[(n, w)]) for w, _ in SIZES for n in screens]
    side_by_side([(f"{n} {w}px", b, f) for (n, w), b, f in pairs], os.path.join(out, "side-by-side.png"))
    write_page(os.path.join(out, "index.html"), a.feature, before_what, after_what, pairs, notes)
    print(os.path.relpath(os.path.join(out, "index.html"), ROOT))


if __name__ == "__main__":
    main()
