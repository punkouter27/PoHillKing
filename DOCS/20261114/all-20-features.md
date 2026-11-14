# All 20 Feature Upgrades — Policy Impact & Implementation Plan (2026-11-14)

Covering the 20 net-new / remaining upgrades identified against the `90ca0db` / `116b4af` baseline.

## Policy Impact (stated before any change)

| # | Feature | Files / Systems | Policies affected |
|---|---|---|---|
| 1 | Pick-your-winner predictions | `PredictionBook` (`MatchLog.cs`), `HudView.Menu.cs`, `HudView.cs` | **None** (HUD + local JSON only) |
| 2 | Batched inference by body kind | `PolicyBatcher.cs`, `PolicyRunner.cs` | **None** (identical ONNX weights, inputs and action map; batched `Worker.Schedule` parity-checked against single-runner inference) |
| 3 | Rivalries, nemesis cards & career stats | `RivalryBook` (`MatchLog.cs`), `HudView.cs`, `HudView.Menu.cs` | **None** (telemetry + UI) |
| 4 | Haptics & gamepad rumble | `Haptics.cs`, `GameSettings.cs`, `HudView.Menu.cs` | **None** (input rumble / mobile vibration only) |
| 5 | Time-of-day & storm sky skins | `AtmosphereController.cs`, `FX/SkyGradient.shader`, `GameSettings.cs`, `MatchFx.cs` | **None** (render-only lighting, fog & skybox) |
| 6 | Photo mode on replay pause | `BroadcastCamera.cs`, `HudView.cs` | **None** (camera + HUD visibility + `ScreenCapture`) |
| 7 | 4×4 Heats-and-Final bracket | `MatchDirector.cs`, `HudView.Menu.cs` | **None** (same rules & physics, 4 simultaneous bodies per heat) |
| 8 | KO-of-the-match highlight & clip sharing | `ContactLedger.cs`, `ReplaySystem.cs`, `ShareBridge.cs`, `Plugins/WebGL/KothShare.jslib` | **None** (replay selection + canvas/PNG share) |
| 9 | Fighter POV & Picture-in-Picture KO cam | `BroadcastCamera.cs`, `HudView.cs` | **None** (camera + secondary URP `RenderTexture`) |
| 10 | Footprints, sweat & wet hair | `MatchFx.cs`, `LifeSigns.cs`, `FX/Fighter.shader` | **None** (particles, decals, shader) |
| 11 | Eyes, blinking & eye darting | `LifeSigns.cs`, `FX/Fighter.shader` | **None** (head-frame shader eyelids/saccades; no physics/mesh changes needed) |
| 12 | Cloth/tie/hair spring motion | `SpringBone.cs`, `LifeSigns.cs`, `FX/Fighter.shader` | **None** (render-only Verlet spring chains + multi-region shader lag) |
| 13 | Procedural cheering crowd | `CrowdRing.cs` | **None** (GPU-instanced meshes, zero PhysX colliders) |
| 14 | Live weather sync (Open-Meteo API) | `LiveAtmosphere.cs`, `GameSettings.cs`, `AtmosphereController.cs` | **None** (opt-in visual/wind mood only; deterministic `Sea` unchanged) |
| 15 | Daily seeded match & leaderboard | `DailyChallenge.cs`, `HudView.Menu.cs`, `tools/daily-api/` | **None** (deterministic UTC-date seat/atmosphere seed + optional Azure Function client) |
| 16 | Country flag badges & Wikipedia bios | `AgentBioService.cs`, `HudWidgets.cs`, `HudView.cs` | **None** (UI badges + cached optional REST summary) |
| 17 | Spectator box throw & arena modifiers | `MatchDirector.cs`, `ProjectilePool.cs`, `HudView.cs`, `GameSettings.cs` | **Fun mode only (unranked):** box throw and gravity/friction/knockback modifiers are disabled in `statsRounds > 0` and suppress `Ladder.Record` Elo updates; nominal `mjModel` values are restored on Standard / Menu / gates. No existing policy is retrained. |
| 18 | AMP `100STYLE` personality clips & hill queue | `training/koth/mocap.py`, `training/scripts/fetch_mocap.py`, `training/scripts/queue_hill.py`, `training/scripts/test_amp.py` | **Opt-in training pipeline:** extends `mocap.py` & `queue_hill.py` with `--amp` character style profiles and `100STYLE` BVH support; shipped ONNX policies stay untouched until a full training run graduates and passes Unity gates. |
| 19 | Physical victory R6 rung | `training/koth/env.py`, `training/scripts/train.py`, `training/scripts/test_amp.py` | **New rung only (`victory`):** defines the R6 reward/curriculum in `env.py` with unchanged 103/112 obs and 29 actions; training & graduating its ONNX policy awaits a dedicated GPU run. |
| 20 | 4th fighter scaffold (`brawler`) for `BYE` slot | `tools/make_brawler.py`, `training/koth/build_brawler.py`, `training/koth/build_mjcf.py`, `training/koth/env.py` | **New body scaffold (off-roster until gated):** generates an original CC0-clean 4th fighter mesh + MJCF + `collision_audit.py` check. Per `AGENTS.md` §4, `Game.unity` stays on the 3 gated bodies (`kim`, `trump`, `grandpa`) until `brawler` completes its own R0→R1→getup→hill training ladder and gates. |

## Regression Scenarios
- **Python (`2026-11-14` verified):**
  - `uv --directory training run python scripts/test_amp.py` — **5/5 passed** (`ok`, including `test_100style_alias_retarget_and_features`).
  - `uv --directory training run python scripts/test_train_budget.py` — **2/2 passed** (`OK`).
  - `uv --directory training run python scripts/collision_audit.py --robots g1 kim trump grandpa brawler --no-render` — **exit 0** (`brawler zero: 0, rest: 0, tpose: 0`, output in `SCREENSHOTS/collision-261114/report.html`).
  - `koth.env` smoke check (`default_cfg('victory')`, `human_overrides(..., 'brawler')`) — **passed** (`victory_s=2.5`, `obs_r0=103`, `obs_victory=121`, `brawler` in `HUMANS`, `BODY_GAIT_HZ['brawler']=1.4`).
- **EditMode (`2026-11-14` verified):** `Unity.exe -batchmode -nographics -runTests -testPlatform EditMode -testFilter PoKingHill` — **62 / 62 passed** (0 failed, 4.21 s; includes `FeatureUpgradeTests`, `PhysXAudit`, `GameSceneTests`, `HudStyleTests`, `CollisionTests`, `ActionMapTests`, `FeatureTests`).
- **PlayMode (`2026-11-14` verified DX11):** `Unity.exe -batchmode -runTests -testPlatform PlayMode -testFilter PoKingHill` — **11 / 11 passed** (0 failed, 457.30 s; `FeaturePlayModeTests`, `HudLayoutTests`, `MatchFlowTests`, `StateFlowSoakTests`).
- **UI & Visual Report:** Verified screenshots and layout notes in `SCREENSHOTS/features-261114/report.html`.
