# Tournament standard (2026-11-22)

Cross-project standard: BYE label, 16-slot elimination bracket seeded by Elo, Elo 1000 / K 32, FFA with 8 fighters,
5 x 5 voice matrix per agent, 4x4 menu grid. User-facing description: README "Modes (tournament standard)".

## Policy impact

None retrained. Everything is match flow, rating, HUD and audio, outside the policies (AGENTS.md 1.3.3):

- Bracket bouts are 1v1 king of the hill: the brains' training setting (duels), under the game's hill rule as before.
- FFA 8 seats eight fighters at once: inside what quick match (16) and the three-way gates already cover; targeting is
  unchanged (nearest or opportunist, one opponent per brain).
- Elo, BYE labels, menu, voices: presentation and bookkeeping only.

Regression scenarios: EditMode `TournamentStandardTests` (seed order, BYE walk-overs, draws to the higher seed, resume,
Elo, voice names and level scaling); PlayMode `FeaturePlayModeTests` (FFA 8 seats 8, a bracket bout seats two different
agents) and `MatchFlowTests.CharacterVoiceMatrixHasProceduralFallbacks`.

## Decisions

- The old "16-player tournament" button was one 16-fighter round crowned as a tournament (the same match as Quick
  match). It is replaced by the elimination bracket; its unused `BracketMatch` list is gone.
- 4x4 Heats-and-Final stays as its own mode; it shares no state with the bracket.
- The bracket seeds distinct agents (3 today), so most seeds are BYEs: two real bouts (2v3 semi-final, then the final).
- FFA 8 repeats agents to fill 8 seats while fewer than 8 exist.
- Existing `ladder.json` ratings (1500 / K 24 era) are not rescaled.
- WAVs load through `Resources` (there was no audio-file loader before; all audio was synthesized).
