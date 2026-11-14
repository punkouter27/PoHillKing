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
import argparse, os, sys, urllib.request
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import mujoco
from koth.mocap import build_dataset, body_limbs

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
URL = "https://raw.githubusercontent.com/una-dinosauria/cmu-mocap/master/data/{subject:03d}/{clip}.bvh"
# What a fighter on the hill does between shoves: walk at several paces, turn, step back and sideways, stand guard, strike.
CLIPS = {
    "07_01": "walk", "07_02": "walk", "07_04": "slow walk", "07_12": "brisk walk", "08_01": "walk", "08_04": "slow walk",
    "02_01": "walk", "16_15": "walk", "16_17": "walk, 90-degree left turn", "16_19": "walk, 90-degree right turn", "16_33": "slow walk, stop",
    "17_08": "muscular, heavyset person's walk", "09_12": "navigate - walk forward, backward, sideways",
    "13_17": "boxing", "14_01": "boxing", "76_01": "walk backwards then attack with a punch", "76_04": "defensive guard pose",
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--clips", nargs="*", default=list(CLIPS), help="CMU clip ids, e.g. 07_01")
    ap.add_argument("--raw", default=os.path.join(ROOT, "assets", "mocap", "raw"))
    ap.add_argument("--bodies", nargs="*", default=["kim", "trump", "grandpa"]); ap.add_argument("--tag", default="cmu_style")
    ap.add_argument("--no-download", action="store_true"); ap.add_argument("--no-mirror", action="store_true")
    a = ap.parse_args()
    os.makedirs(a.raw, exist_ok=True); paths = []
    for clip in a.clips:
        path = os.path.join(a.raw, clip + ".bvh")
        if not os.path.exists(path):
            if a.no_download: print("missing", clip); continue
            url = URL.format(subject=int(clip.split("_")[0]), clip=clip)
            try:
                with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "fetch-mocap"}), timeout=60) as r: data = r.read()
            except OSError as e: print(f"could not fetch {clip}: {e}"); continue
            if not data.startswith(b"HIERARCHY"): print(f"{clip}: not a BVH file, skipped"); continue
            open(path, "wb").write(data)
        paths.append(path)
    if not paths: sys.exit("no clips")
    for robot in a.bodies:
        m = mujoco.MjModel.from_xml_path(os.path.join(ROOT, "assets", robot, "scene_flat_1p.xml"))
        out = os.path.join(ROOT, "assets", "mocap", f"{a.tag}_{robot}.npz")
        meta = build_dataset(paths, out, mirror_too=not a.no_mirror, body=body_limbs(m, "a_"), robot=robot)
        if robot == a.bodies[0]:
            for c in meta["clips"]: print(f"  {c['file']:12s} {c['seconds']:6.1f} s  {CLIPS.get(c['file'][:-4], '')}")
        print(f"{out}: {meta['pairs']} frame pairs from {meta['seconds']} s of motion ({'mirrored too' if meta['mirrored'] else 'not mirrored'})")

if __name__ == "__main__":
    main()
