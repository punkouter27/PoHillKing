# Brawler (Heavyweight Wrestler Scaffold)

- **Origin:** 100% procedurally generated in-repo by `tools/make_brawler.py` (CC0 / MIT clean, zero third-party copyrighted meshes or paid textures).
- **Dimensions:** 1.84 m tall, 118.0 kg target fighting weight, wide wrestler stance (`hip_y = +-0.115 m`, `shoulder_y = +-0.24 m`).
- **Outputs:**
  - `Training/assets/brawler/brawler_rig.json`
  - `Training/assets/brawler/brawler_mjx.xml` & `scene_*` (built by `KOTH_ROBOT=brawler python -m koth.build_mjcf`)
  - `Assets/_Project/RL_Models/brawler/brawler_skin.json`
  - `Assets/_Project/Agents/Fighters/Brawler/brawler.glb` (clean procedural humanoid GLB scaffold)
- **Shipping Rule (`AGENTS.md`):** Keep `brawler` isolated from `Game.unity` and `scene_koth_game.xml` until trained through R0 → R1 → getup → hill and verified against Unity parity gates.
