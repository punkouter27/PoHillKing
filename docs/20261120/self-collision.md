# Self-collision between a fighter's own limbs (2026-11-20)

**Question.** The standard asks for self-collision on every non-adjacent body pair. `koth/build_mjcf.py` (`make_robot`)
gives each fighter's geoms `contype = robot bit (+16 feet)` and `conaffinity = other robots | 4 (+16 feet and shins)`,
so a fighter's own arms, torso and thighs pass through each other; only foot vs shin of the other leg collides.
Every shipped brain was trained and gated with that model.

**Measurement.** `scripts/duel_stats.py --policy fighter --getup --hill-rule` (the game's hill rule, CPU MuJoCo
3.15.0, `scene_koth_2p_train.xml`), 40 mirror duels each, with the model as built and with every fighter geom's
`conaffinity` also given its own robot bit (216 more collidable geom pairs per fighter: all non-parent-child pairs):

| Body | Self-collision | Decided | Wins a / b | Median win | Control steps with a same-fighter contact |
|---|---|---|---|---|---|
| Kim (kim_hill2) | as built | 0.15 | 5 / 1 | 17.6 s | 312 of 47 399 (foot/shin) |
| Kim (kim_hill2) | all pairs | 0.15 | 5 / 1 | 17.6 s | 312 of 47 399 |
| Grandpa (g261029_01) | as built | 0.15 | 3 / 3 | 8.1 s | 14 of 46 054 |
| Grandpa (g261029_01) | all pairs | 0.15 | 3 / 3 | 8.1 s | 14 of 46 054 |

The two runs are identical step for step: in these duels a fighter's arms, torso and thighs never come within contact
distance of each other, so turning the extra pairs on changes nothing the brains do here. (Mirror duels mostly stall at
the bell, as logged before for these brains; three-ways and falls were not measured.)

**Decision.** Not enabled in this change. The game's MuJoCo bodies are imported from the MJCF (`ParityBatch.ImportOne`,
`GameScene.RefreshBodies`) and checked against `model_dump_*.json` (`ModelDumpCheck`); turning it on means regenerating
every scene (`python -m koth.build_mjcf ...` for every body and tag), re-importing them into Unity with a rendering
editor, and re-running the parity and statistics gates of every brain, then a WebGL check. The numbers above say the
shipped brains should survive it; the next step is `make_robot`: `ca = other | mask_bit | 4 | (16 if foot or shin)`
plus the full re-import and gates. Training cost: about 4.6x more collidable geom pairs per fighter in MuJoCo Warp.
