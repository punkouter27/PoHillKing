"""Download the motion-capture clips for the motion prior and build its dataset (koth/mocap.py, koth/amp.py).

  uv run python scripts/fetch_mocap.py                 # assets/mocap/raw/*.bvh -> assets/mocap/cmu_style_<body>.npz (+ .json), every human
  uv run python scripts/fetch_mocap.py --bodies kim --clips 07_01 09_12 --tag walk     # -> assets/mocap/walk_kim.npz
  uv run python scripts/fetch_mocap.py --no-download   # rebuild from the clips already in assets/mocap/raw
Each body gets its own dataset: the clips are fitted to that fighter's joints (koth/mocap.py).

Source: the Carnegie Mellon University Graphics Lab Motion Capture Database (mocap.cs.cmu.edu), in B. Hahne's BVH
conversion as mirrored at github.com/una-dinosauria/cmu-mocap. CMU's terms: the data is free to use, including in
commercial products; it may not be resold as data. Credit: "The data used in this project was obtained from
mocap.cs.cmu.edu. The database was created with funding from NSF EIA-0196217."
Neither the raw clips nor the built datasets are committed (git-ignored, about 4 MB per body); their .json summaries
are. The build is deterministic: run this once on a new machine before train.py --amp.
"""

import argparse
import os
import sys
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import mujoco

from koth.mocap import body_limbs, build_dataset

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
URL = "https://raw.githubusercontent.com/una-dinosauria/cmu-mocap/master/data/{subject:03d}/{clip}.bvh"
# What a fighter on the hill does between shoves: walk at several paces, turn, step back and sideways, stand guard, strike.
CLIPS = {
    "07_01": "walk",
    "07_02": "walk",
    "07_04": "slow walk",
    "07_12": "brisk walk",
    "08_01": "walk",
    "08_04": "slow walk",
    "02_01": "walk",
    "16_15": "walk",
    "16_17": "walk, 90-degree left turn",
    "16_19": "walk, 90-degree right turn",
    "16_33": "slow walk, stop",
    "17_08": "muscular, heavyset person's walk",
    "09_12": "navigate - walk forward, backward, sideways",
    "13_17": "boxing",
    "14_01": "boxing",
    "76_01": "walk backwards then attack with a punch",
    "76_04": "defensive guard pose",
}


# 100STYLE (Mason et al., CC BY 4.0) & CMU personality clip profiles per fighter.
# Place extracted 100STYLE BVHs under assets/mocap/100style/<StyleName>/*.bvh to blend style priors.
STYLE_PROFILES = {
    "kim": {
        "100style": ("MartialArts", "Bouncy", "Strutting", "Rushing"),
        "cmu": ("07_01", "07_12", "13_17", "14_01", "76_01", "76_04"),
    },
    "trump": {"100style": ("Proud", "Wide", "Heavy", "Swagger"), "cmu": ("17_08", "02_01", "07_02", "16_15", "76_04")},
    "grandpa": {"100style": ("Old", "Tired", "Slow", "Shuffle"), "cmu": ("07_04", "08_04", "16_33", "09_12")},
    "brawler": {
        "100style": ("Angry", "Heavy", "Punching", "Wide"),
        "cmu": ("13_17", "14_01", "17_08", "76_01", "76_04"),
    },
}


def resolve_style_bvh(style_dir: str, robot: str) -> list[str]:
    """Discover local 100STYLE CC BY 4.0 BVH clips matching `robot`'s personality styles."""
    if not style_dir or not os.path.isdir(style_dir):
        return []
    styles = tuple(s.lower() for s in STYLE_PROFILES.get(robot, {}).get("100style", ()))
    found = []
    for root_dir, _, files in os.walk(style_dir):
        rel = os.path.relpath(root_dir, style_dir).lower()
        for fn in sorted(files):
            if not fn.lower().endswith(".bvh"):
                continue
            if not styles or any(st in rel or st in fn.lower() for st in styles):
                found.append(os.path.join(root_dir, fn))
    return found


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--clips",
        nargs="*",
        default=None,
        help="CMU clip ids, e.g. 07_01 (defaults to all or per-body personality profile)",
    )
    ap.add_argument("--raw", default=os.path.join(ROOT, "assets", "mocap", "raw"))
    ap.add_argument(
        "--style-dir",
        default=os.path.join(ROOT, "assets", "mocap", "100style"),
        help="Optional directory containing 100STYLE BVH style folders",
    )
    ap.add_argument(
        "--personality",
        action="store_true",
        help="Filter clips per fighter using STYLE_PROFILES (100STYLE + CMU) and write style_<body >.npz",
    )
    ap.add_argument("--bodies", nargs="*", default=["kim", "trump", "grandpa"])
    ap.add_argument("--tag", default="cmu_style")
    ap.add_argument("--no-download", action="store_true")
    ap.add_argument("--no-mirror", action="store_true")
    a = ap.parse_args()
    os.makedirs(a.raw, exist_ok=True)
    clips_to_fetch = list(a.clips) if a.clips else list(CLIPS)
    cmu_paths = {}
    for clip in clips_to_fetch:
        path = os.path.join(a.raw, clip + ".bvh")
        if not os.path.exists(path):
            if a.no_download:
                print("missing", clip)
                continue
            url = URL.format(subject=int(clip.split("_")[0]), clip=clip)
            try:
                with urllib.request.urlopen(
                    urllib.request.Request(url, headers={"User-Agent": "fetch-mocap"}), timeout=60
                ) as r:
                    data = r.read()
            except OSError as e:
                print(f"could not fetch {clip}: {e}")
                continue
            if not data.startswith(b"HIERARCHY"):
                print(f"{clip}: not a BVH file, skipped")
                continue
            open(path, "wb").write(data)
        cmu_paths[clip] = path
    for robot in a.bodies:
        if a.personality and robot in STYLE_PROFILES and not a.clips:
            r_clips = [cmu_paths[c] for c in STYLE_PROFILES[robot]["cmu"] if c in cmu_paths]
        else:
            r_clips = list(cmu_paths.values())
        style_clips = resolve_style_bvh(a.style_dir, robot)
        paths = style_clips + r_clips
        if not paths:
            print(f"skipped {robot}: no clips available")
            continue
        m = mujoco.MjModel.from_xml_path(os.path.join(ROOT, "assets", robot, "scene_flat_1p.xml"))
        tag = f"style_{robot}" if a.personality and a.tag == "cmu_style" else f"{a.tag}_{robot}"
        out = os.path.join(ROOT, "assets", "mocap", f"{tag}.npz")
        meta = build_dataset(paths, out, mirror_too=not a.no_mirror, body=body_limbs(m, "a_"), robot=robot)
        if robot == a.bodies[0]:
            for c in meta["clips"]:
                print(f"  {c['file']:12s} {c['seconds']:6.1f} s  {CLIPS.get(c['file'][:-4], '100STYLE')}")
        print(
            f"{out}: {meta['pairs']} frame pairs from {meta['seconds']} s of motion ({'mirrored too' if meta['mirrored'] else 'not mirrored'})"
        )


if __name__ == "__main__":
    main()
