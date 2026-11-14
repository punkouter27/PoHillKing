# Lifelike fighters, music, voice, victory, motion prior (2026-11-14)

Requested: features 2, 3, 4, 12, 13, 14, 18 and 20 of the 2026-11-14 feature list.

## Policy impact (stated before any change)

| Feature | Touches | Policies affected |
|---|---|---|
| 2 Head look-at | `SkinFollower`, `LifeSigns` (render only: Head and Neck bones follow `torso_link`, no physics joint) | none |
| 3 Breathing and exertion | `LifeSigns`, `FX/Fighter.shader` (vertex swell, face flush) | none |
| 12 Secondary motion | `LifeSigns` springs, `FX/Fighter.shader` (vertex offset in soft regions) | none |
| 13 Facial expression | `LifeSigns`, `FX/Fighter.shader` (jaw, brow, mouth shading) | none |
| 14 Adaptive music | `MusicBed`, `ImpactSynth`, `GameSettings.MusicVolume` | none |
| 4 Voice commentary | `VoiceCaster`, `Plugins/WebGL/KothSpeech.jslib`, `GameSettings.Voice`, `Commentator` | none |
| 18 Victory and defeat | `PolicyRunner.SetPostRound`, `MatchDirector.EndRound`, `SkinFollower.ArmsUp` | Rules unchanged: it starts after `RoundEnded`, never in a statistics gate. As planned it drove the fight brain with its arms raised; that was measured and dropped (see below). As shipped the brain only gets a centre goal and then a zero command, both inside its training; the raised arms are on the skin. No policy retrained. |
| 20 Motion prior (AMP) | `training/koth/amp.py`, `training/koth/mocap.py`, `scripts/train.py --amp`, `scripts/fetch_mocap.py` | Opt-in: with no `--amp` the trainer is unchanged. A brain trained with it is a new candidate for that body's fighter policy: same 112 inputs, 29 actions, action map and timing; it ships only through the existing graduation and Unity gates. |

Physics model, observation layout, action layout, control rate: unchanged by every item.

## Why the render features are in the shader, not the rig

None of the three meshes has eye, jaw, hair or cloth bones, and none has blendshapes (Kim and Grandpa are 21-bone
scans, Trump a 34-bone GLB with fingers). Head and Neck follow `torso_link`, so look-at is a render-only rotation of
those two bones. Breathing, jiggle and the face are vertex offsets in `FX/Fighter.shader`, driven per renderer through
the property block `BodyLook` already uses. Consequence: no blinking and no eye motion (there are no eyelids to move).

## Regression scenarios

- EditMode: `PhysXAudit`, `GameSceneTests` (every reference wired, no missing scripts), `HudStyleTests`, new `LifeTests`
  (music bed is finite, bounded and louder with intensity; spring settles; look clamp; the voice's queue rules).
- PlayMode: `MatchFlowTests`, `HudLayoutTests` (Sound page has two more controls), `StateFlowSoakTests`.
- Victory: winner still upright 3 s after the pose starts, over repeated rounds.
- Training: `scripts/test_amp.py` (discriminator shapes, reward range, no pair across a reset), a short smoke run with
  `--amp` and one without (the second ran: no `amp` block in its manifest, `--amp` None in its config).
- Gates not re-run: no shipped ONNX, sidecar or MJCF changes.

## What was built and what was measured

Editor, Game scene, 16 fighters, 2026-11-14. Captures: `SCREENSHOTS/life-261114/report.html`.

**Life layer (2, 3, 12, 13).** `LifeSigns` and `VoiceCaster` are on `KothRig` in `Game.unity`. In a 16-way match all 16
skins are driven; no console errors. Close-ups of each body with the face held at rest, fear and strain show the jaw,
brow, mouth shading and flush on the right place for Kim; on Trump and Grandpa the mouth sits a little high, on the upper lip. Placement took three passes: a mouth
measured down from the crown landed on Trump's nose (his hair adds height) and one on the body's centre line missed
Grandpa (his scan's head rests turned), so the mouth is now found from the nose tip, wherever it is. The brow offset
was halved after it bent Kim's glasses. Breathing and the springs were checked only by exaggerating the chest swell
(it balloons as expected); at their real amplitudes (4 to 14 mm) they were not judged on screen.

**Music and voice (14, 4).** In a fight the music intensity read 0.87 with two fighters left and the bed's RMS 0.073.
`LifeTests` covers the bed (finite, bounded, louder with intensity, the sting rings and dies). The voice cannot run in
the editor or on Windows (`VoiceCaster.Available` is false; the switch is locked there). The WebGL and Android paths
are compiled out here and were not run on a device.

**Victory (18).** Three designs, measured:

| Design | Trials | Winner still upright |
|---|---|---|
| Fight brain balances with the arms' rest pose raised | 2 real rounds | 0 of 2 |
| Brain walks to the centre and stops, then a plain joint hold raises the arms | 6 controlled | 0 of 6 |
| Brain walks to the centre and stands on a zero command; arms raised on the skin only | 6 controlled | 6 of 6 |

Controlled trial: a fresh 16-way round, one fighter of the body under test told to celebrate from its spawn spot, the
other 15 told to stand (the Defeat mode), read 8 to 12 simulated seconds later; two trials per body. In the third
design 84 of the 90 bystanders were also still standing. Shipped: the third. The winner celebrates only if it is on its
feet when the round ends; in five real rounds it was down, afloat in the arriving sea or already going over in four,
which is the game as it was (the last one in is often barely in).

**Motion prior (20).** `scripts/test_amp.py`: a retargeted clip posed on each fighter's MuJoCo model points its limbs
within 2.3 degrees of the capture (Grandpa's arms in the boxing clip: up to 5.9, at his joint limits). 17 CMU clips,
200 s, 19 764 to 19 768 frame pairs per body after dropping about 111 glitch pairs. Smoke run, `kim_r1b`, 256 worlds, 8
iterations with `--amp`: runs at 5 000 steps/s, style reward 0.33, discriminator output -0.13 on the capture and -0.64
on the learner and separating. That shows the plumbing works, nothing about how a trained fighter moves. Not done:
any training run long enough to change a brain, any evaluation of gait or fight quality with the prior, any export,
any Unity gate.

## Tests

- EditMode 52 of 53. The failure, `HudStyleTests.TypeSizesComeFromTheScale` (an 18px font size in `Hud.uss` line 70),
  is in a file this work did not touch.
- PlayMode 8 of 9 on the final code (MatchFlow, soak and the rest pass). The failure, `HudLayoutTests.EveryScreenFitsEveryPhone`
  ("DOWN" needs 76 units, has 0, in the 16-way fight at 1440x3200), is the same message as in
  `Logs/16-player-hud-tests-final4.xml` from before this work: it depends on a fighter being down at that frame. The
  menu tabs and all four settings sections, the Sound page among them, are laid out before that step and fit all five phones.
- Python: `scripts/test_amp.py` ok, `scripts/test_train_budget.py` ok.

## Left open

- The voice on a real phone and browser.
- Blinking and eye motion need eyelids or eye bones in the meshes (Blender work on all three).
- The victory needs a winner on its feet. Added after the first pass: the get-up rule keeps running after the bell
  (`MatchDirector.AfterBell`, the game only, no events) and the sea stops rising at the bell (`Sea.Hold`; it floated
  winners off the rim during the results screen). Measured over 8 real rounds at the game's 5 s results pause: the
  winner was on its feet at the bell in 3; two of those walked to the centre and celebrated, one fell on the way and
  was getting up when the screen ended. In the other 5 the "winner" was already 4.8 to 5.4 m out and going down the
  slope at the bell: the last one in is often the last one to go over, a moment later. That is the game's rule, not
  something a celebration can fix.
- Retraining with the prior: walker, then fighter, per body, then the gates.
