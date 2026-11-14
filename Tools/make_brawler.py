"""Generate the CC0 procedural heavyweight wrestler "Brawler" (1.84 m, 118 kg) rig JSON and GLB scaffold.

  uv --directory training run python ..\tools\make_brawler.py

Writes:
  training/assets/brawler/brawler_rig.json
  Assets/PoKingHill/Fighters/Brawler/brawler.glb
"""
import json, os, struct
import numpy as np

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HEIGHT = 1.84


def _box_mesh(lo, hi):
    x0, y0, z0 = lo; x1, y1, z1 = hi
    v = np.array([
        [x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0],
        [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1],
    ], dtype=np.float32)
    idx = np.array([
        0, 2, 1, 0, 3, 2,
        4, 5, 6, 4, 6, 7,
        0, 1, 5, 0, 5, 4,
        1, 2, 6, 1, 6, 5,
        2, 3, 7, 2, 7, 6,
        3, 0, 4, 3, 4, 7,
    ], dtype=np.uint16)
    return v, idx


def build_rig() -> dict:
    # Model coordinates: +x forward, +y left, +z up. Feet rest at z = 0.
    def bone(head, tail, half_ext, r50):
        h = np.array(head, dtype=float); t = np.array(tail, dtype=float); e = np.array(half_ext, dtype=float)
        lo = np.minimum(h, t) - e
        hi = np.maximum(h, t) + e
        return {
            "head": h.round(4).tolist(),
            "tail": t.round(4).tolist(),
            "r50": round(float(r50), 4),
            "r80": round(float(r50 * 1.15), 4),
            "lo": lo.round(4).tolist(),
            "hi": hi.round(4).tolist(),
            "verts": 64,
        }

    bones = {
        "Hips": bone([0.0, 0.0, 0.96], [0.0, 0.0, 1.07], [0.11, 0.16, 0.06], 0.14),
        "Spine": bone([0.0, 0.0, 1.07], [0.0, 0.0, 1.24], [0.13, 0.18, 0.05], 0.145),
        "Chest": bone([0.01, 0.0, 1.24], [0.0, 0.0, 1.50], [0.14, 0.22, 0.04], 0.155),
        "Neck": bone([0.0, 0.0, 1.50], [0.0, 0.0, 1.60], [0.065, 0.065, 0.02], 0.065),
        "Head": bone([0.0, 0.0, 1.60], [0.0, 0.0, HEIGHT], [0.105, 0.095, 0.0], 0.105),
    }

    for side, s in (("L", 1.0), ("R", -1.0)):
        bones[f"Shoulder.{side}"] = bone([0.0, s * 0.14, 1.46], [0.0, s * 0.24, 1.46], [0.07, 0.05, 0.06], 0.07)
        bones[f"UpperArm.{side}"] = bone([0.0, s * 0.24, 1.46], [0.01, s * 0.27, 1.17], [0.062, 0.062, 0.03], 0.062)
        bones[f"LowerArm.{side}"] = bone([0.01, s * 0.27, 1.17], [0.04, s * 0.28, 0.91], [0.050, 0.050, 0.02], 0.048)
        bones[f"Hand.{side}"] = bone([0.04, s * 0.28, 0.91], [0.04, s * 0.28, 0.78], [0.045, 0.032, 0.01], 0.036)
        bones[f"UpperLeg.{side}"] = bone([0.0, s * 0.125, 0.95], [0.01, s * 0.130, 0.51], [0.092, 0.092, 0.04], 0.092)
        bones[f"LowerLeg.{side}"] = bone([0.01, s * 0.130, 0.51], [-0.01, s * 0.130, 0.085], [0.062, 0.062, 0.03], 0.060)
        bones[f"Foot.{side}"] = bone([-0.01, s * 0.130, 0.085], [0.08, s * 0.130, 0.035], [0.065, 0.052, 0.035], 0.050)
        bones[f"Toe.{side}"] = bone([0.08, s * 0.130, 0.035], [0.185, s * 0.130, 0.0], [0.045, 0.050, 0.02], 0.035)

    all_lo = np.min([b["lo"] for b in bones.values()], axis=0)
    all_hi = np.max([b["hi"] for b in bones.values()], axis=0)
    all_lo[2] = 0.0
    all_hi[2] = HEIGHT
    return {
        "height": HEIGHT,
        "scale_from_scan": 1.0,
        "lo": np.round(all_lo, 4).tolist(),
        "hi": np.round(all_hi, 4).tolist(),
        "bones": bones,
    }


def write_procedural_glb(path: str, rig: dict):
    verts_list, idx_list, v_off = [], [], 0
    for name, b in rig["bones"].items():
        # Convert MuJoCo (+x fwd, +y left, +z up) -> glTF (+x left=-y_mj, +y up=z_mj, +z fwd=x_mj)
        lo_mj, hi_mj = np.array(b["lo"]), np.array(b["hi"])
        v_mj, idx = _box_mesh(lo_mj, hi_mj)
        v_gl = np.stack([-v_mj[:, 1], v_mj[:, 2], v_mj[:, 0]], axis=1).astype(np.float32)
        verts_list.append(v_gl)
        idx_list.append((idx + v_off).astype(np.uint16))
        v_off += len(v_gl)
    verts = np.concatenate(verts_list, axis=0)
    indices = np.concatenate(idx_list, axis=0)
    vb = verts.tobytes()
    ib = indices.tobytes()
    pad = (-len(ib)) % 4
    blob = ib + b"\0" * pad + vb
    blob += b"\0" * ((-len(blob)) % 4)
    gltf = {
        "asset": {"version": "2.0", "generator": "PoHillKing make_brawler.py"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"name": "Brawler", "mesh": 0}],
        "materials": [{"name": "WrestlerMat", "pbrMetallicRoughness": {"baseColorFactor": [0.78, 0.32, 0.22, 1.0], "roughnessFactor": 0.65, "metallicFactor": 0.0}}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 1}, "indices": 0, "material": 0}]}],
        "bufferViews": [
            {"buffer": 0, "byteOffset": 0, "byteLength": len(ib), "target": 34963},
            {"buffer": 0, "byteOffset": len(ib) + pad, "byteLength": len(vb), "target": 34962},
        ],
        "accessors": [
            {"bufferView": 0, "componentType": 5123, "count": int(len(indices)), "type": "SCALAR"},
            {"bufferView": 1, "componentType": 5126, "count": int(len(verts)), "type": "VEC3",
             "min": verts.min(0).round(4).tolist(), "max": verts.max(0).round(4).tolist()},
        ],
        "buffers": [{"byteLength": len(blob)}],
    }
    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * ((-len(js)) % 4)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(blob)))
        f.write(struct.pack("<II", len(js), 0x4E4F534A)); f.write(js)
        f.write(struct.pack("<II", len(blob), 0x004E4942)); f.write(blob)


def main():
    rig = build_rig()
    out_rig = os.path.join(REPO, "training", "assets", "brawler", "brawler_rig.json")
    os.makedirs(os.path.dirname(out_rig), exist_ok=True)
    with open(out_rig, "w", encoding="utf-8") as f:
        json.dump(rig, f, indent=1)
    out_glb = os.path.join(REPO, "Assets", "PoKingHill", "Fighters", "Brawler", "brawler.glb")
    write_procedural_glb(out_glb, rig)
    print(f"brawler: wrote {out_rig} and {out_glb} ({os.path.getsize(out_glb) // 1024} KB)")


if __name__ == "__main__":
    main()
