# PoHillKing

King of the hill between physics-simulated human bodies (Kim, Trump, Grandpa): every fighter is a neural-network brain
trained in Python on MuJoCo Warp and run in Unity through the MuJoCo plugin and the Unity Inference Engine. No PhysX,
no animation: MuJoCo is the only simulator, in training and in the game.

Play it in a browser: https://white-bush-08dbace0f.4.azurestaticapps.net (status: `/diag`).

## Layout

| Path | What |
|---|---|
| `Assets/_Project` | the Unity game: `Agents` (fighter bodies), `Core` (scripts, editor tools, tests), `Data` (ScriptableObjects, settings), `Environments` (map, FX, prefabs), `RL_Models` (ONNX brains), `Scenes`, `UI` |
| `Assets/WebGLTemplates/Po` | the web page around the WebGL player |
| `Packages/org.mujoco` | the MuJoCo 3.15.0 Unity plugin, vendored |
| `Training/` | Python training (uv project, Python 3.11): `koth/` env and model builders, `scripts/` train / eval / export, `config/` reward weights, `tests/` |
| `scripts/` | publish and deploy the WebGL build, the daily-score Azure Function, asset generators |
| `docs/` | build contract, training log, dated notes and reports |
| `SCREENSHOTS/` | before/after reports of UI changes, one folder each (`ui-<YYMMDD>-<feature>/index.html` + `side-by-side.png`), made by `scripts/screenshots.py` |

## Modes

The Play tab has one card per mode. Every agent is a brain; there is no human player.

| Mode | What happens |
|---|---|
| **Free-for-all** | one round with 8 fighters at once (`MatchDirector.FfaPlayers`). Every available agent is seated once before any repeats, so with three brains it is 3 + 3 + 2. The last fighter left on the platform wins. |
| **Tournament** | 8-player single elimination of one-round 1v1 duels over the menu's seats as shown: 01 v 02, 03 v 04 ..., then semi-finals and the final (7 duels). Tap a seat to change it, or Shuffle. Every duel names a winner on the spot (the last one in, else nearest the centre when time is up, else whoever went over last, else the higher Elo), so 7 duels always end with one champion. |
| Custom | the agents ticked on the Agents tab |
| 4x4 Heats | up to four heats of four, then a final of the heat winners |
| Daily seed | one deterministic 16-fighter match per UTC day |

**Elo** (`MatchLog.cs`, `Ladder`, `persistentDataPath/ladder.json`): a new agent starts at **1000**, K = **32**. Ladders
written before this change (start 1500, K 24) are not rescaled: ratings already in `ladder.json` stay as they are; only
agents the file does not know yet start at 1000. Delete `ladder.json` to restart everyone at 1000.

Every completed hill round is rated, demo mode and CPU-only matches included (every agent is a brain): free-for-all,
heats, daily seed and tournament duels all use one rule. Places come from the knock-out order (winner first, those still in
next, the later out the better); every pair of distinct agents is one Elo game (ahead = win, same place = 0.5) with K
scaled by 1/(N-1), so a 1v1 bout is plain Elo. An agent seated on several bodies counts once, at its best place, and never
plays itself. A round abandoned for the menu (a demo interrupted by input) records nothing, and no round counts twice.
The results sheet shows each fighter's Elo and change; demo mode hides that sheet.

**Voices** (`Presentation/SoundPool.cs`, `VoiceManifest.cs`, `VoiceSynth.cs`): every roster character has a 5 x 5
matrix, emotions `angry`, `happy`, `hurt`, `insult`, `celebrate` at levels 1 to 5 (`SoundPool.Play(agent, emotion, level, ...)`).

- `Assets/_Project/Data/Resources/Voices/voices.json` (schema `po-voice-manifest/1`) lists every character (Kim, Trump,
  Grandpa: one per roster agent) x 5 emotions x 5 levels: the WAV each cell uses, relative to that folder,
  `<id>/<emotion>-<level>.wav` (e.g. `trump/celebrate-5.wav`), whether it `exists`, and the character's procedural
  `voice` profile (pitch, formant scale, breath, rasp, vibrato, tempo, timbre).
- To add a recording: drop the WAV at that path, then run **PoKingHill > Voices > Rebuild voices.json** (dropping or
  deleting a WAV under `Voices/` also refreshes the `exists` flags on import). The generator keeps the profiles already in
  the file, adds new roster agents, and logs "N of M clips recorded; missing: ...". Never edit `exists` by hand.
- What plays: the WAV when `exists` is true; otherwise a procedural imitation by that character (`VoiceSynth`: glottal
  pulses with jitter, shimmer and rasp, four gliding formants, 1 to 4 syllables with consonants: "rraah-hup!",
  "ha-ha-ha", a gasp then "oof", "nyah-nyah", "woo-hoo!"), louder, longer and wider in pitch with the level,
  deterministic per character, emotion, level and take, and cached. A missing or invalid `voices.json` makes every voice
  procedural (one warning).
- Match events drive it: a hit = hurt or angry (level from its strength), a knock-out credit = insult for the pusher and
  happy for the assist, getting back up = angry 3, the round's winner = celebrate 5 (4 when the sea decided it). The fear
  voice (teetering on the rim, going over) keeps its older clips.

## Commands

```bash
# Python checks (repo root)
uvx ruff check . && uvx ruff format --check .
uv run --no-project --python Training/.venv/Scripts/python.exe --with pytest python -m pytest

# Publish the WebGL build (needs Unity 6000.6.9f1 with WebGL; close the editor on this project first)
pwsh -File scripts/publish_webgl.ps1

# Before/after screenshots of a UI change from the local build (SCREENSHOTS/ui-<YYMMDD>-<feature>/)
uv run --no-project --python 3.11 --with playwright==1.49.0 --with pillow python scripts/screenshots.py --feature <name>
```

Everything else (training, gates, Unity CLI): `AGENTS.md`.
