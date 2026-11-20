# PoHillKing

King of the hill between physics-simulated human bodies (Kim, Trump, Grandpa): every fighter is a neural-network brain
trained in Python on MuJoCo Warp and run in Unity through the MuJoCo plugin and the Unity Inference Engine. No PhysX,
no animation: MuJoCo is the only simulator, in training and in the game.

Play it in a browser: https://white-bush-08dbace0f.4.azurestaticapps.net (status: `/diag`).

## Layout

| Path | What |
|---|---|
| `Assets/PoKingHill` | the Unity game (scripts, scenes, models, UI, tests) |
| `Assets/WebGLTemplates/Po` | the web page around the WebGL player |
| `Packages/org.mujoco` | the MuJoCo 3.15.0 Unity plugin, vendored |
| `training/` | Python training (uv project, Python 3.11): `koth/` env and model builders, `scripts/` train / eval / export, `config/` reward weights, `tests/` |
| `scripts/` | publish and deploy the WebGL build, the daily-score Azure Function, asset generators |
| `docs/` | build contract, training log, dated notes and reports |

## Commands

```bash
# Python checks (repo root)
uvx ruff check . && uvx ruff format --check .
uv run --no-project --python training/.venv/Scripts/python.exe --with pytest python -m pytest

# Publish the WebGL build (needs Unity 6000.6.9f1 with WebGL; close the editor on this project first)
pwsh -File scripts/publish_webgl.ps1
```

Everything else (training, gates, Unity CLI): `AGENTS.md`.
