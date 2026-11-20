# Tournament and character audio update

## Policy impact

The existing fighter policy interface is unchanged, but every shipped fighter policy is affected by the new 16-way match dynamics and must be checked in the expanded match. No observation or action dimensions are being changed. Regression focus: seating all 16 fighters, match completion and rating persistence, plus procedural voice fallback during match events.

## Scope

Expand the game to 16 simultaneous fighters, add 16 main-menu slots that auto-populate randomly from available roster agents, and retain a reserved BYE slot for a future roster addition. Add body-specific emotional voice cues with procedural fallbacks. Update the MuJoCo game model and runtime capacity to match; keep policy observation/action interfaces unchanged.

## Implementation notes

- The game model now reserves six Kim, five Trump and five Grandpa bodies. The model's initial poses and runtime seat ring use collision-free spacing, with keyframe/root heights following the hill surface; 12+ fighters use a wider elimination radius.
- The HUD shows 16 randomizable player slots and a separate BYE placeholder. The live scoreboard uses two compact rows, and odds, telemetry and results buffers cover all seats.
- Emotional voices are synthesized per body profile and emotion (strain, pain, fear and triumph); the no-pool fallback remains procedural. Existing material-based impact sounds are retained.
- A PlayMode regression checks the 48 synthesized body/emotion variants and verifies both character selection and the generic fallback path.
- Tournament mode currently runs a single 16-player FFA championship so all fighters remain simultaneous. This is not a traditional advancing elimination bracket; that conflicts with the latest 16-at-once requirement and needs a product-level interpretation before replacing the FFA.

## Centered 16-player spawn follow-up

**Ten-game observation:** Diagnostic-only PlayMode run; no policy, physics, or gameplay changes. Measure opening posture using upright orientation and pelvis height, excluding eliminated fighters, at simulated (not wall-clock) times. Record winners, ending conditions, falls and recoveries in `Logs/ten-game-observation.csv`.

The explicit ten-game PlayMode run completed successfully (10/10 matches reached Result). Trump won 5, Kim 3, Grandpa 2. Nine ended by knockout; one ended when the sea arrived. Mean duration was 16.293 simulated seconds (range 11.420-23.760). At 0.5 simulated seconds every match had 16 upright fighters; at 2 seconds the mean was 14.5 (range 12-16); at 5 seconds it was 12.2 (range 9-16). There were 101 down events and 10 recoveries. These physical posture counts are stricter than `IsDown`, which does not include eliminated or merely leaning fighters. The earlier two-wall-second check does not prove two simulated seconds of stability on a slow editor.

During observation, the focused editor took about 1.54 real seconds per rendered frame at the test's requested 4x speed. Restoring 1x reduced a sampled frame to 0.51 seconds, still about 2 FPS. Background ticking was already enabled. This remains a CPU physics/inference performance issue; the observation does not establish smooth real-time gameplay.

## 16-player performance follow-up

**Policy impact before change:** None intended: keep the MuJoCo model, 2 ms integration step, 20 ms policy tick, collision pairs and solver settings unchanged. Defer only native-to-Unity render transform copies; keep actuator/control and joint state synchronization on every physics step. The game reads policy observations, targeting and elimination state from native MuJoCo data. Regression: compare identical seeded starts with immediate/deferred transforms, validate rendered body poses, and rerun match startup/flow plus the baseline performance probe.

**Baseline:** A 20-real-second editor probe at 1918x953 measured 1.68 FPS, 3.1189 ms per full physics tick, 2.6447 ms for native stepping plus state sync, and 0.2814 ms per individual inference. A separate repeated native-to-Unity synchronization benchmark measured 0.547 ms per call. These measurements include editor overhead and are not release-build performance.

**Change:** `MjScene.SyncTransformsOncePerFrame` is opt-in and enabled by the 16-body match director. Body, geom and site poses are copied before ordinary presentation `Update`/`LateUpdate`; joints, sensors, mocap inputs and actuator synchronization remain per-step. Explicit full synchronization (reset/replay) still updates immediately. Winner confetti reads the native pelvis position because end-of-round events can precede the frame's visual sync.

**Measurements:** The first after-change 20-second capture averaged 12.57 FPS, 1.841 ms/full tick and 1.4769 ms/native step plus per-step state sync (`Logs/perf-16-after-first.json`). A subsequent same-editor alternating comparison restarted the 16-fighter match with the same random seed, ran at 1x with the profiler disabled, warmed up for two wall-clock seconds and measured ten wall-clock seconds per capture:

| Capture | Visual sync | Mean FPS | Full tick ms | Native step + per-step sync ms |
|---|---|---:|---:|---:|
| 0 | Every physics step | 2.75 | 2.65 | 2.21 |
| 1 | Once per frame | 11.02 | 1.84 | 1.45 |
| 2 | Every physics step | 2.79 | 2.57 | 2.12 |
| 3 | Once per frame | 10.06 | 1.78 | 1.42 |

Raw captures: `Logs/perf-paired-0-immediate.json` through `Logs/perf-paired-3-deferred.json`. This is about 3.8x higher mean FPS across the paired captures, not a 60 FPS claim. The windows are wall-clock based, so faster runs cover more simulated time; their contact histories and phases are not controlled identically. The independent restored-state native-trajectory regression addresses correctness, not these noisy performance differences. Deferred transform-copy cost moves outside the per-step timer but remains included in end-to-end frame time. At 420-446 physics steps per wall-clock second in these captures, the 500 Hz simulation was still not consistently real-time.

**Validation:** `Logs/render-sync-edit-tests.xml`: 29/29 passed (scene integrity and MuJoCo scene tests). The new native-state test restores the same model/data state and checks identical joint positions to 1e-12 after 20 immediate/deferred steps, held actuator control, joint readback and the frame pose update. `Logs/render-sync-play-tests.xml`: 4/4 passed (physically upright centered start, intro, Menu/Fight/Result/Menu and replay/menu placement). The stability regression now samples simulation time and height/upright posture rather than wall time or only the director's down flag: 16/16 upright at 0.562, 1.060, 1.674 and 2.008 simulated seconds. No policy retraining, model/physics/interface version change or solver/timestep change.

**Remaining performance issue:** Native 16-body stepping and 16 policy runners still consume most of a CPU core's available frame budget. Next recommended investigation is a standalone release-build profile to separate editor overhead from native solver and inference costs, without reducing simulation fidelity.

**Policy impact:** This changes the match-start rule for every fighter policy in the 16-player game. It does not change body physics, observation/action interfaces, or policy assets. Regression scenarios: verify all seats start on the flat summit without inter-fighter penetration, then run the 16-player match-flow test.

**Implementation and verification:** The 16 seats now use a 4/8/4 concentric layout at radii 0.60 m, 1.05 m and 1.45 m, all on the flat 1.5 m summit. Fighters face inward, and Unity rotates the generated model's layout as a unit so runtime resets match the MuJoCo keyframe. The generated model has zero inter-fighter penetrations and level foot contact; a 2-second held-pose simulation kept all fighters upright (minimum up-dot 1.000, maximum radius 1.462 m). `CenteredSixteenPlayerStartKeepsMostFightersStandingForTwoSeconds` passed in the live Unity scene: 0 down / 16 standing at 0.5, 1.0, 1.5 and 2.0 seconds. `GameModelStartsAllSixteenFightersInTheCenteredRingsFacingInward` and `IntroLeadsIntoTheFirstRound` also passed. The full PlayMode flow reached Result/Menu, but `ConsoleGuard` marked it failed because Unity logged an unrelated Burst warning for `InferenceEngine.CPUBackend.CopyJob`.
