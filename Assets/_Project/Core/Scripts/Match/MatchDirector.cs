using System;
using System.Collections.Generic;
using Mujoco;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Serialization;

namespace PoKingHill
{
    /// <summary>One entry of the roster the menu offers: a brain and how it is driven. At launch an agent is seated on
    /// a body of its own kind (MatchDirector.bodies).</summary>
    [Serializable]
    public class Agent
    {
        public string name = "Agent";
        [Tooltip("What fits on a badge, e.g. All-rounder")] public string shortName = "";
        [Tooltip("The kind of body this brain drives (g1, kim, trump, grandpa)")] public string body = "g1";
        public ModelAsset policy;
        [Tooltip("Optional critic exported by export_policy.py --value: drives the win-odds bar")] public ModelAsset value;
        [Tooltip("103 = locomotion inputs, 112 = with opponent/ring inputs")] public int obsDim = 112;
        public GoalMode goal = GoalMode.Opponent;
        public float stopDist = 0f, vmax = 1f;
        [Tooltip("Trained in a styled action path (rest pose + action smoothing)")] public bool shove;
        [Tooltip("Which styled rest pose with shove: shove (arms forward) or bull (Trump's charge)")] public string stylePose = "shove";
        [Tooltip("Ticked in the menu: this agent takes part in matches")] public bool inGame = true;
        [Tooltip("False = listed in the menu but cannot be ticked yet; note says why")] public bool available = true;
        public string note = "";
        [Tooltip("Optional action path of the policy (<file>_action.json, Training/koth/action_map.py); empty = the original clamp path")] public TextAsset action;
        public string Badge => shortName.Length > 0 ? shortName : name;
    }

    /// <summary>A brain that belongs to a kind of body rather than to an agent (the get-up brain).</summary>
    [Serializable]
    public class BodyBrain { public string body = "g1"; public ModelAsset policy; }

    /// <summary>
    /// Match flow for king of the hill, as an explicit state machine: Menu -> Fight -> Result (-> Replay) -> Fight or
    /// Menu. Every seated agent gets one of the scene's fighter bodies of its own kind (unused bodies are parked in the
    /// sky) and all start on top of the hill. A fighter is out only when it leaves the mountain top (radius &gt;
    /// OutRadiusNow); one that falls on it gets up again with its body's get-up brain (see OutWithGetUp). The round ends
    /// when one is left (it wins), none is left, or the sea reaches the summit (the one nearest the centre wins). Every
    /// agent is a brain: there is no human player. The brains were trained one against one: in a larger fight each one is pointed at one
    /// opponent (nearest, or the easiest to push off). Everything is done by writing mjData (no scene reload, no
    /// Instantiate/Destroy). Presentation listens to <see cref="MatchEvents"/>; the HUD is HudView.
    /// </summary>
    public unsafe partial class MatchDirector : MonoBehaviour
    {
        [Tooltip("Every fighter body in the scene (a_, b_, ...). A match seats agents on the first ones.")]
        [FormerlySerializedAs("fighters")] public PolicyRunner[] bodies = Array.Empty<PolicyRunner>();
        public Sea sea;                                  // optional; without it the round lasts roundSeconds
        public ImpactSynth synth;                        // optional, for the audio counters in the verification file
        public Agent[] roster = Array.Empty<Agent>();
        [Tooltip("The 16 agent seats shown in the menu; shuffled on first entry and by Shuffle players.")]
        public int[] playerSlots = Array.Empty<int>();
        public const int PlayersPerMatch = 16;
        /// <summary>Fighters in a quick match: seats 01-08 of a fresh shuffle, all at once.</summary>
        public const int FfaPlayers = 8;
        public int selectedA, selectedB;
        public string[] maps = { "Summit (baseline)" };
        [Tooltip("Render-only scenery per map, same order as maps. Physics is the same dome for every map.")]
        public GameObject[] mapVisuals = Array.Empty<GameObject>();
        public int selectedMap;
        public Vector2 spawnRadiusA = new(0.5f, 1.2f), spawnRadiusB = new(0.5f, 1.2f);
        const float MinSpawnGap = 1f;
        public float roundSeconds = 25f, outRadius = 2.8f;
        [Tooltip("Out radius used for crowded 12+ fighter matches to give the larger field room to fight.")]
        public float crowdedOutRadius = 3.2f;
        [Tooltip("How long the winner panel stays up before the next round or the menu")] public float pauseBetweenRounds = 5f;
        [Tooltip("The game only (not the statistics gates, which keep training's rule): when one fighter is left, the round waits this long (simulated s) before it is declared; if that fighter leaves the summit too, it is a draw. Without it the last one in was often already going over the edge as it was crowned (5 of 8 rounds, DOCS/20261114). 0 = off.")]
        public float confirmSeconds = 1.5f;
        [Tooltip("Rounds per launch before returning to the menu. 0 = keep fighting. GameSettings overrides it at launch.")] public int roundsPerMatch = 1;
        [Tooltip("Skip the menu and start fighting immediately.")] public bool autoLaunch;
        [Tooltip("Statistical parity gate: play this many rounds fast, write duel_stats_unity.json, then stop. 0 = off.")]
        public int statsRounds = 0;
        [Tooltip("File name the statistics gate writes (empty = duel_stats_unity.json / ffa_stats_unity.json, the parity references); diagnostics runs set their own")]
        public string statsFile = "";
        [Tooltip("Free-for-all statistics gate: the candidate (roster index) on seat A against three opponents drawn from this pool every round (roster indices). Empty = the two-fighter gate.")]
        public int[] statsPool = Array.Empty<int>();
        public int statsCandidate = -1;
        [Tooltip("Seat the pool as listed (one each) instead of drawing three from it: the humans' free-for-all gate")] public bool statsPoolInOrder;
        [Tooltip("Get-up brain per body kind (training rung getup): a fighter that falls gets up again with it")]
        public BodyBrain[] getupBrains = Array.Empty<BodyBrain>();
        [Tooltip("Seconds a fallen fighter has to stand up before it is out; 0 = no count (the game's hill rule)")] public float getupSeconds;
        [Tooltip("Get-ups per fighter per round; 0 = no limit (the game's hill rule)")] public int maxGetups;
        [Tooltip("Only a fall inside the plateau (plateau_r) may be got up from; off = anywhere on the mountain top (the game's hill rule)")] public bool getupOnPlateauOnly;
        [Tooltip("The statistics gate plays with the get-up rule (duel_stats.py --getup, or --hill-rule with the game's values)")] public bool getupInStats;
        [Tooltip("Tale of the tape: crane shot length, then this many seconds per fighter (clamped to fit about 7 s)")] public float introCrane = 1.6f, introPerFighter = 1.6f;

        /// <summary>Colour of each body's badge and team trim, in bodies[] order after seating; with
        /// GameSettings.ColorSafe the Okabe-Ito palette, told apart with any colour vision.</summary>
        public static Color[] SeatColors => GameSettings.ColorSafe ? SafeColors : VividColors;
        static readonly Color[] VividColors =
            { new(0.95f, 0.36f, 0.30f), new(0.27f, 0.62f, 0.98f), new(0.98f, 0.78f, 0.25f), new(0.45f, 0.85f, 0.45f), new(0.80f, 0.50f, 0.95f),
              new(0.30f, 0.85f, 0.85f), new(0.98f, 0.52f, 0.72f) };
        static readonly Color[] SafeColors =
            { new(0.84f, 0.37f, 0.00f), new(0.34f, 0.71f, 0.91f), new(0.94f, 0.89f, 0.26f), new(0.00f, 0.62f, 0.45f), new(0.80f, 0.47f, 0.65f),
              new(0.90f, 0.62f, 0.00f), new(0.00f, 0.45f, 0.70f) };

        public static MatchDirector Instance { get; private set; }
        public enum AppMode { Watch, Play, Testbed }
        public AppMode Mode { get; private set; } = AppMode.Watch;
        public int ActiveSeed { get; private set; } = 42;
        public bool Paused { get; private set; }
        public MatchState State { get; private set; } = MatchState.Booting;
        public bool InMenu => State <= MatchState.Menu;
        /// <summary>The round is decided and the winner panel (or its replay) is up.</summary>
        public bool RoundOver => State == MatchState.Result || State == MatchState.Replay;
        /// <summary>Fighters of the current round that are still in (what the camera frames).</summary>
        public IReadOnlyList<PolicyRunner> Alive => _alive;
        /// <summary>Fighters knocked out of the current round, oldest first (the camera cuts to them at the water).</summary>
        public IReadOnlyList<PolicyRunner> Fallen => _fallen;
        public int Playing => _playing;
        public int AgentOf(int body) => body >= 0 && body < _agentAt.Length ? _agentAt[body] : -1;
        public int Wins(int agent) => agent >= 0 && agent < _agentWins.Length ? _agentWins[agent] : 0;
        /// <summary>Wins of one body this session; in a mirror match the badges show these, not the shared agent's.</summary>
        public int BodyWins(int body) => body >= 0 && body < _bodyWins.Length ? _bodyWins[body] : 0;
        public bool AgentSeatedTwice(int agent) { int n = 0; for (int i = 0; i < _playing; i++) if (_agentAt[i] == agent) n++; return n > 1; }
        public int Draws => _draws;
        public bool IsOut(int body) => body >= 0 && body < _out.Length && _out[body];
        /// <summary>Fallen, the get-up brain driving (still in the round).</summary>
        public bool IsDown(int body) => body >= 0 && body < _playing && body < _downAt.Length && _downAt[body] >= 0 && !_out[body];
        /// <summary>Seconds left on a down fighter's count (-1 without a count).</summary>
        public float DownLeft(int body) => !IsDown(body) ? 0f : getupSeconds <= 0 ? -1f : Mathf.Max(0f, (float)(_downAt[body] + getupSeconds - MjScene.Instance.Data->time));
        /// <summary>During the intro: the seat being presented (-1 during the crane shot), and how far into its card (0..1).</summary>
        public int IntroFeatured => State == MatchState.Intro ? (int)Mathf.Floor((Time.unscaledTime - _introStart - introCrane) / Mathf.Max(0.01f, _introPer)) : -1;
        public float IntroPhase => State == MatchState.Intro ? Mathf.Repeat((Time.unscaledTime - _introStart - introCrane) / Mathf.Max(0.01f, _introPer), 1f) : 0f;
        public float IntroElapsed => State == MatchState.Intro ? Time.unscaledTime - _introStart : 0f;
        public float IntroLength => _introLength;
        public double RoundTime => State >= MatchState.Fight && MjScene.InstanceExists && MjScene.Instance.Data != null ? MjScene.Instance.Data->time - _roundStart : 0;
        public float ResultSecondsLeft => Mathf.Max(0f, _resultUntil - Time.unscaledTime);
        public RoundResult LastResult { get; private set; }
        public string Status => _status;
        public double StepMs => _stepMs;
        /// <summary>Plateau radius that counts as "still in" this round.</summary>
        public float OutRadiusNow { get; private set; }
        public bool LastRoundOfMatch => _bracket.Count == 0 && roundsPerMatch > 0 && _roundsThisMatch >= roundsPerMatch;
        public IReadOnlyList<BracketMatch> Bracket => _bracket;
        public bool InTournament => _bracket.Count > 0;
        /// <summary>The bracket match being played or just decided.</summary>
        public int BracketIndex => _bracketAt;
        public string TournamentChampion { get; private set; } = "";

        /// <summary>One duel of the knockout bracket. a, b and winner are entrant numbers (0-15, the menu's seats 01-16;
        /// -1 = not known yet); the winner moves on to match <c>next</c> (-1 = the final).</summary>
        public class BracketMatch { public int a = -1, b = -1, winner = -1, tries, round, next = -1; public string label; }
        readonly List<BracketMatch> _bracket = new(); int _bracketAt;
        int[] _entrants = Array.Empty<int>();     // roster index per entrant, fixed when the tournament starts

        /// <summary>"05 Kim": an entrant's seat number and badge.</summary>
        public string EntrantName(int e) => e >= 0 && e < _entrants.Length ? $"{e + 1:00} {BadgeOf(_entrants[e])}" : "?";

        readonly List<PolicyRunner> _alive = new(), _fallen = new();
        int[] _agentAt = Array.Empty<int>(), _bodyWins = Array.Empty<int>(), _agentWins = Array.Empty<int>();   // roster index per body (-1 = parked); wins per body; wins per agent
        bool[] _out = Array.Empty<bool>(); int _playing;
        double[] _downAt = Array.Empty<double>(), _stoodAt = Array.Empty<double>(); int[] _getups = Array.Empty<int>();
        float _introStart, _introPer, _introLength;
        PolicyRunner[] _target = Array.Empty<PolicyRunner>();
        readonly List<double> _times = new();
        double _lastStandingAt = -1;          // round time when one fighter was left (confirmSeconds), -1 = not yet
        int _draws, _roundsThisMatch; double _roundStart; float _resultUntil; string _status = ""; bool _pending, _knockout;
        readonly System.Diagnostics.Stopwatch _sw = new(); double _stepMs;
        string _shotDir; readonly HashSet<string> _shots = new(); float _menuShotAt = -1, _launchAt = -1;
        ArenaSpec _arena = new();

        void OnEnable()
        {
            Instance = this;
            Application.targetFrameRate = 60;            // the editor otherwise renders uncapped and starves GPU training
            Application.runInBackground = true;
            if (int.TryParse(Cli.Arg("-kothSeed"), out int cliSeed) || int.TryParse(Environment.GetEnvironmentVariable("POHILLKING_SEED"), out cliSeed))
                ActiveSeed = cliSeed;
            UnityEngine.Random.InitState(ActiveSeed);
            _shotDir = Cli.Arg("-kothShots");
            // Player-side statistical gate (-kothRounds N): same rules as ParityBatch.RunDuelStats, no sea and a 25 s bell.
            if (!Application.isEditor && int.TryParse(Cli.Arg("-kothRounds"), out int rounds) && rounds > 0)
            {
                statsRounds = rounds; roundSeconds = 25f;
                if (int.TryParse(Cli.Arg("-kothA"), out int fa)) selectedA = fa;
                if (int.TryParse(Cli.Arg("-kothB"), out int fb)) selectedB = fb;
                if (sea != null) { sea.enabled = false; sea = null; }
            }
            if (statsRounds > 0 || Cli.Flag("-kothAutoLaunch")) autoLaunch = true;
            // The unfocused editor renders about one frame per second, so in stats mode let each frame carry up to 2 s of physics.
            if (statsRounds > 0) { Time.timeScale = 50f; Time.maximumDeltaTime = 2f; pauseBetweenRounds = 0f; roundsPerMatch = 0; }
            OutRadiusNow = outRadius;
            MjScene.Instance.postInitEvent += OnInit;
            MjScene.Instance.preUpdateEvent += Pre; MjScene.Instance.postUpdateEvent += Post;
        }
        void OnDisable()
        {
            if (Paused) { Paused = false; Time.timeScale = 1f; }
            if (Instance == this) Instance = null;
            if (!MjScene.InstanceExists) return;
            MjScene.Instance.postInitEvent -= OnInit; MjScene.Instance.preUpdateEvent -= Pre; MjScene.Instance.postUpdateEvent -= Post;
        }

        public AppMode CycleMode()
        {
            Mode = (AppMode)(((int)Mode + 1) % 3);
            _status = $"Mode: {Mode}";
            return Mode;
        }

        public bool TogglePause()
        {
            Paused = !Paused;
            Time.timeScale = Paused ? 0f : 1f;
            _status = Paused ? "Paused" : "Resumed";
            return Paused;
        }

        void SetState(MatchState s) { if (State == s) return; State = s; MatchEvents.RaiseState(s); }

        void OnInit(object s, MjStepArgs a) => _pending = true;   // act on the first physics step, once every runner has its map
        void Pre(object s, MjStepArgs a)
        {
            if (_pending && Ready())
            {
                _pending = false;
                _agentAt = new int[bodies.Length]; _bodyWins = new int[bodies.Length]; _out = new bool[bodies.Length]; _target = new PolicyRunner[bodies.Length];
                _downAt = new double[bodies.Length]; _stoodAt = new double[bodies.Length]; _getups = new int[bodies.Length];
                _agentWins = new int[Mathf.Max(1, roster.Length)];
                _arena = bodies[0].Spec?.arena ?? new ArenaSpec();
                outRadius = (float)_arena.out_radius; OutRadiusNow = outRadius;   // the out rule is training's (koth/rules.py)
                MjScene.Instance.SyncTransformsOncePerFrame = bodies.Length == PlayersPerMatch;
                if (playerSlots.Length != PlayersPerMatch) RandomizePlayerSlots(true);
                if (autoLaunch) Launch(); else EnterMenu();
            }
            if (!_pending && _agentAt.Length > 0)
            {
                for (int i = _playing; i < bodies.Length; i++) Park(i);
                Retarget();
                TickStrikes();
            }
            _sw.Restart();
        }

        bool Ready()
        {
            if (bodies.Length < 2) return false;
            foreach (var f in bodies) if (f == null || f.Map == null) return false;
            return true;
        }

        public void CyclePlayerSlot(int slot)
        {
            if (slot < 0 || slot >= playerSlots.Length || playerSlots[slot] < 0 || playerSlots[slot] >= roster.Length) return;
            string body = roster[playerSlots[slot]].body;
            var compatible = new List<int>();
            for (int i = 0; i < roster.Length; i++)
                if (roster[i].available && roster[i].policy != null && roster[i].body == body) compatible.Add(i);
            int current = compatible.IndexOf(playerSlots[slot]);
            if (current >= 0 && compatible.Count > 1) playerSlots[slot] = compatible[(current + 1) % compatible.Count];
        }

        // A body nobody is seated on waits far above the sea, re-pinned before every physics step.
        void Park(int i)
        {
            bodies[i].ResetPose(100f + 2f * i, 0f, 0f);
            MjScene.Instance.Data->qpos[bodies[i].Map.RootQposAdr + 2] = 50.0;
        }

        // Each fighter is pointed at one opponent still in. With two fighters: always the other one. With more, either
        // the nearest (as trained) or, with Targeting.Opportunist, the one easiest to push off: near the rim or unsteady.
        // Brains still see one opponent, as trained.
        void Retarget()
        {
            var d = MjScene.Instance.Data;
            bool opportunist = _playing > 2 && statsRounds == 0 && GameSettings.Targeting == Targeting.Opportunist;
            for (int i = 0; i < _playing; i++)
            {
                double* q = d->qpos + bodies[i].Map.RootQposAdr; double best = double.MaxValue, current = double.MaxValue; PolicyRunner pick = null, any = null;
                // nearest and second-nearest still in, as training ranks them (koth/env.py _refresh_nbr): an eliminated
                // fighter only when nobody else is left; with one opponent left the second is the first
                double d1 = double.MaxValue, d2 = double.MaxValue, dAny = double.MaxValue; PolicyRunner n1 = null, n2 = null;
                for (int j = 0; j < _playing; j++)
                {
                    if (j == i) continue;
                    double* o = d->qpos + bodies[j].Map.RootQposAdr;
                    double dist = Math.Sqrt((o[0] - q[0]) * (o[0] - q[0]) + (o[1] - q[1]) * (o[1] - q[1]));
                    if (dist < dAny) { dAny = dist; any = bodies[j]; }
                    if (_out[j]) continue;
                    if (dist < d1) { d2 = d1; n2 = n1; d1 = dist; n1 = bodies[j]; } else if (dist < d2) { d2 = dist; n2 = bodies[j]; }
                    double score = dist;
                    if (opportunist)
                    {
                        double r = Math.Sqrt(o[0] * o[0] + o[1] * o[1]), rim = Math.Clamp((r - 0.8) / Math.Max(0.1, OutRadiusNow - 0.8), 0, 1);
                        score = dist - 1.2 * rim - 0.8 * (1 - ObsBuilder.Up(o + 3));
                    }
                    else score = dist * dist;          // nearest, compared exactly as before (squared distance)
                    if (bodies[j] == _target[i]) current = score;
                    if (score < best) { best = score; pick = bodies[j]; }
                }
                // Keep the current target unless another is clearly better (no flicker between two).
                if (opportunist && pick != null && _target[i] != null && current < double.MaxValue && best > current - 0.25) pick = _target[i];
                _target[i] = pick;
                n1 ??= any; n2 ??= n1;
                // A free-for-all brain (121 inputs) gets what it was trained on: nearest and second-nearest. Two-fighter
                // brains get the chosen target (nearest, or the opportunist pick).
                bool ffaBrain = bodies[i].policyObsDim >= ObsBuilder.ObsDim + 18;
                bodies[i].opponent = ffaBrain ? n1 : pick ?? any;
                bodies[i].opponent2 = n2;
            }
        }

        void Seat(int count)
        {
            _playing = count;
            for (int i = 0; i < bodies.Length; i++) { bodies[i].paused = i >= count; if (i >= count) _agentAt[i] = -1; _target[i] = null; }
        }

        // ------------------------------------------------------------------ flow
        public void EnterMenu()
        {
            ReplaySystem.StopActive();          // a replay restores its saved state when it ends: end it before placing anyone
            _bracket.Clear();
            _heats.Clear();
            _dailyMatchActive = false;
            _resultUntil = 0; _roundsThisMatch = 0;
            foreach (var f in bodies) f.SetBrain(null, ObsBuilder.ObsDim, GoalMode.None, 0.3f, 0.8f);      // passive stance while the menu is up
            Seat(2);
            for (int i = 0; i < 2 && i < bodies.Length; i++)
            {
                int a = Array.FindIndex(roster, r => r.body == bodies[i].body);
                _agentAt[i] = a >= 0 ? a : 0;
            }
            Place(); ClearOut();
            if (sea != null) sea.Park();
            if (playerSlots.Length != PlayersPerMatch) RandomizePlayerSlots(true);
            _menuShotAt = Time.unscaledTime + 1.5f;
            SetState(MatchState.Menu);
        }

        /// <summary>Menu launch: every ticked agent takes a seat, in random order (one ticked = it fights itself; more
        /// ticked than bodies = that many are drawn). The statistics gate keeps the pair it was started with.</summary>
        public void Launch()
        {
            var agents = new List<int>();
            if (roster.Length > 0)
            {
                if (statsRounds > 0 && FfaStats) agents = FfaDraw();
                else if (statsRounds > 0) { agents.Add(Mathf.Clamp(selectedA, 0, roster.Length - 1)); agents.Add(Mathf.Clamp(selectedB, 0, roster.Length - 1)); }
                else { if (!RandomizePlayerSlots()) return; agents.AddRange(playerSlots); }
            }
            _bracket.Clear();
            StartMatch(agents);
        }

        /// <summary>One tap from the menu: an eight-fighter free-for-all (seats 01-08 of a fresh shuffle).</summary>
        public void QuickMatch() => StartFreeForAll(FfaPlayers);

        /// <summary>Everyone at once: reshuffle all sixteen seats from the available agents and seat the first
        /// <paramref name="count"/> (2-16; 16 = the whole field).</summary>
        public void StartFreeForAll(int count)
        {
            _bracket.Clear(); TournamentChampion = ""; selectedMap = 0;
            if (!RandomizePlayerSlots(true)) return;
            StartMatch(new List<int>(playerSlots).GetRange(0, Mathf.Clamp(count, 2, playerSlots.Length)));
        }

        /// <summary>Randomize all sixteen seats from the selected agents, falling back to every available agent if the
        /// selection cannot cover the body kinds present in this scene.</summary>
        public bool RandomizePlayerSlots(bool allAvailable = false)
        {
            if (bodies.Length != PlayersPerMatch) { _status = "The game scene needs 16 fighter bodies"; return false; }
            var selected = allAvailable ? Available() : InGame();
            if (!CanFillSlots(selected)) selected = Available();
            if (!CanFillSlots(selected)) { _status = "Available agents cannot fill all 16 body slots"; return false; }

            var slots = new List<int>(PlayersPerMatch);
            foreach (var body in bodies)
            {
                var compatible = new List<int>();
                foreach (int i in selected) if (i >= 0 && i < roster.Length && roster[i].available && roster[i].policy != null && roster[i].body == body.body) compatible.Add(i);
                slots.Add(compatible[UnityEngine.Random.Range(0, compatible.Count)]);
            }
            for (int i = slots.Count - 1; i > 0; i--) { int k = UnityEngine.Random.Range(0, i + 1); (slots[i], slots[k]) = (slots[k], slots[i]); }
            playerSlots = slots.ToArray();
            return true;
        }

        List<int> Available()
        {
            var result = new List<int>();
            for (int i = 0; i < roster.Length; i++) if (roster[i].available && roster[i].policy != null) result.Add(i);
            return result;
        }

        bool CanFillSlots(List<int> candidates)
        {
            if (candidates.Count == 0) return false;
            foreach (var body in bodies)
            {
                bool found = false;
                foreach (int i in candidates) if (i >= 0 && i < roster.Length && roster[i].available && roster[i].policy != null && roster[i].body == body.body) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>The 16-player knockout: the menu's sixteen seats as shown, paired 01 v 02, 03 v 04 ... in one-on-one
        /// duels of one round each; winners meet in the quarter-finals, semi-finals and the final (15 matches).</summary>
        public void StartTournament()
        {
            if (playerSlots.Length != PlayersPerMatch && !RandomizePlayerSlots(true)) return;
            _entrants = (int[])playerSlots.Clone();
            TournamentChampion = "";
            _bracket.Clear(); _bracket.AddRange(BuildBracket(_entrants.Length)); _bracketAt = 0;
            PlayBracketMatch();
        }

        /// <summary>A single-elimination bracket for a power-of-two field: first-round pairs (0 v 1, 2 v 3 ...), then one
        /// match per pair of winners, each match pointing at the one its winner plays next.</summary>
        public static List<BracketMatch> BuildBracket(int entrants)
        {
            var list = new List<BracketMatch>();
            if (entrants < 2 || (entrants & (entrants - 1)) != 0) return list;
            int matches = entrants / 2, start = 0, round = 0;
            for (int i = 0; i < matches; i++) list.Add(new BracketMatch { a = 2 * i, b = 2 * i + 1 });
            while (true)
            {
                for (int i = 0; i < matches; i++) { list[start + i].round = round; list[start + i].label = RoundName(matches, i); }
                if (matches == 1) break;
                int next = list.Count;
                for (int i = 0; i < matches; i++) list[start + i].next = next + i / 2;
                for (int i = 0; i < matches / 2; i++) list.Add(new BracketMatch());
                start = next; matches /= 2; round++;
            }
            return list;
        }

        static string RoundName(int matchesInRound, int i) => matchesInRound switch
        {
            1 => "Final",
            2 => $"Semi-final {i + 1}",
            4 => $"Quarter-final {i + 1}",
            _ => $"Round of {2 * matchesInRound} · {i + 1}",
        };

        void PlayBracketMatch()
        {
            var m = _bracket[_bracketAt];
            StartMatch(new List<int> { _entrants[m.a], _entrants[m.b] });     // a takes body 0, b body 1
            _status = $"{m.label} ({_bracketAt + 1}/{_bracket.Count}): {EntrantName(m.a)} vs {EntrantName(m.b)}";
        }

        void StartMatch(List<int> agents)
        {
            ReplaySystem.StopActive();
            if (roster.Length > 0)
            {
                if (agents.Count == 1) agents.Add(agents[0]);
                // Each agent takes a free body of its own kind; the bodies in use move to the front of bodies[].
                var seats = new List<int>();
                foreach (int agent in agents)
                {
                    int k = -1;
                    for (int i = seats.Count; i < bodies.Length && k < 0; i++) if (bodies[i].body == roster[agent].body) k = i;
                    if (k < 0) continue;
                    (bodies[seats.Count], bodies[k]) = (bodies[k], bodies[seats.Count]); seats.Add(agent);
                }
                int required = statsRounds > 0 ? 2 : Mathf.Min(agents.Count, PlayersPerMatch);
                if (seats.Count < required) { _status = $"Need {required} compatible fighter bodies to start this match"; return; }
                selectedA = seats[0]; selectedB = seats[1];
                Seat(seats.Count);
                for (int i = 0; i < _playing; i++) _agentAt[i] = seats[i];
            }
            else Seat(2);
            if (statsRounds == 0 && !autoLaunch) roundsPerMatch = _bracket.Count > 0 || _heats.Count > 0 ? 1 : GameSettings.RoundsPerMatch;   // PerfProbe/auto runs keep theirs
            if (!_heatsLaunching) _heats.Clear();
            if (_bracket.Count == 0 && _heats.Count == 0) TournamentChampion = "";       // a plain match: no champion banner
            _dailyMatchActive = _dailyLaunching;
            _roundsThisMatch = 0;
            if (statsRounds == 0 && !autoLaunch && GameSettings.Intro && roster.Length > 0) { BeginIntro(); return; }
            ArmBrains();
            NewRound();
        }

        // Each seated body gets its agent's brain (and its body's get-up brain); the rest stand passively.
        void ArmBrains()
        {
            if (roster.Length == 0) return;
            for (int i = 0; i < bodies.Length; i++)
            {
                if (i >= _playing) { bodies[i].SetBrain(null, ObsBuilder.ObsDim, GoalMode.None, 0.3f, 0.8f); continue; }
                var f = roster[_agentAt[i]];
                bodies[i].SetBrain(f.policy, f.obsDim, f.goal, f.stopDist, f.vmax, f.shove, f.stylePose, f.action);
                bodies[i].getupPolicy = Array.Find(getupBrains, b => b != null && b.body == bodies[i].body)?.policy;
            }
        }

        // ------------------------------------------------------------------ intro (tale of the tape)
        // The seated fighters stand passively on their spawn spots (no brain, hold ctrl, as in the menu) while the camera
        // cranes up to the summit and then visits each one; HudView shows its card. Then the brains are armed and the
        // first round starts from a fresh placement, exactly as without the intro.
        void BeginIntro()
        {
            for (int i = 0; i < bodies.Length; i++) bodies[i].SetBrain(null, ObsBuilder.ObsDim, GoalMode.None, 0.3f, 0.8f);
            Place(); ClearOut();
            if (sea != null) sea.Park();
            _introPer = Mathf.Clamp(6f / Mathf.Max(1, _playing), 0.9f, introPerFighter);
            _introStart = Time.unscaledTime; _introLength = introCrane + _playing * _introPer;
            SetState(MatchState.Intro);
        }

        /// <summary>End the tale of the tape now (HUD button) and start the first round.</summary>
        public void SkipIntro() { if (State == MatchState.Intro) EndIntro(); }
        void EndIntro() { ArmBrains(); NewRound(); }

        List<int> InGame()
        {
            var pool = new List<int>();
            for (int i = 0; i < roster.Length; i++) if (roster[i].available && roster[i].inGame) pool.Add(i);
            return pool;
        }
        public int TickedCount { get { int n = 0; foreach (var f in roster) if (f.available && f.inGame) n++; return n; } }

        void Place()
        {
            float bearing = UnityEngine.Random.Range(0f, 2f * Mathf.PI);
            if (_playing == PlayersPerMatch)
            {
                // Rotate the generated, collision-checked center-ring layout as one and face every fighter inward.
                double* qpos0 = MjScene.Instance.Model->qpos0;
                for (int i = 0; i < _playing; i++)
                {
                    int root = bodies[i].Map.RootQposAdr;
                    float x0 = (float)qpos0[root], y0 = (float)qpos0[root + 1];
                    float radius = Mathf.Sqrt(x0 * x0 + y0 * y0);
                    float angle = Mathf.Atan2(y0, x0) + bearing;
                    bodies[i].ResetPose(radius * Mathf.Cos(angle), radius * Mathf.Sin(angle), angle + Mathf.PI);
                }
                return;
            }

            float rMin = _playing > 2 ? MinSpawnGap / (2f * Mathf.Sin(Mathf.PI / _playing)) : 0f;
            for (int i = 0; i < _playing; i++)
            {
                var band = i == 1 ? spawnRadiusB : spawnRadiusA;
                float lo = _playing >= 12 ? Mathf.Max(band.x, rMin) : Mathf.Min(Mathf.Max(band.x, rMin), band.y);
                float hi = Mathf.Max(band.y, lo);
                float r = UnityEngine.Random.Range(lo, hi), a = bearing + 2f * Mathf.PI * i / _playing;
                bodies[i].ResetPose(r * Mathf.Cos(a), r * Mathf.Sin(a), UnityEngine.Random.Range(-Mathf.PI, Mathf.PI));
            }
        }

        void ClearOut()
        {
            _alive.Clear(); _fallen.Clear(); _lastStandingAt = -1;
            for (int i = 0; i < bodies.Length; i++)
            {
                bool inPlay = i < _playing;
                _out[i] = !inPlay; bodies[i].paused = !inPlay; if (inPlay) _alive.Add(bodies[i]);
                _downAt[i] = -1; _stoodAt[i] = -1; _getups[i] = 0;
            }
        }

        /// <summary>Start the next round of the current match (also the HUD's Reset).</summary>
        public void NewRound()
        {
            ReplaySystem.StopActive();
            OutRadiusNow = _playing >= 12 ? Mathf.Max(crowdedOutRadius, MinSpawnGap / (2f * Mathf.Sin(Mathf.PI / _playing)) + 0.5f) : outRadius;
            Place(); ClearOut(); _knockout = false;
            OnNewRoundFeatures();
            _roundStart = MjScene.Instance.Data->time; _resultUntil = 0;
            if (sea != null) { sea.pace = statsRounds > 0 ? 1f : GameSettings.SeaPace; sea.Restart(); }
            ResetStrikes();
            SetState(MatchState.Fight);
            MatchEvents.RaiseRoundStarted();
        }

        bool Out(MujocoLib.mjData_* d, PolicyRunner r)
        {
            double* q = d->qpos + r.Map.RootQposAdr;
            double rad = Math.Sqrt(q[0] * q[0] + q[1] * q[1]);
            return rad > OutRadiusNow || ObsBuilder.Up(q + 3) < _arena.fall_up || q[2] - _arena.Ground(rad) < _arena.fall_height;
        }

        /// <summary>The get-up rule is on: always in play, in a statistics gate only when started with getupInStats.</summary>
        public bool GetUpRule => statsRounds == 0 || getupInStats;

        // Get-up rule: leaving the mountain top (radius > OutRadiusNow) is out at once. A fall hands the body to its get-up
        // brain; standing again (up > 0.9, pelvis above 85 % of the standing height, for 0.4 s) gives it back to the fight
        // brain. The game's hill rule (the defaults) has no count, no limit and works anywhere on the top; the older gate rule
        // (ParityBatch.RunGetupDuelStats) counts getupSeconds, allows maxGetups and only falls inside plateau_r. A fighter
        // with no get-up brain is out on a fall. Mirrored by Training/scripts/duel_stats.py --getup [--hill-rule].
        bool OutWithGetUp(MujocoLib.mjData_* d, int i, double t)
        {
            var r = bodies[i]; double* q = d->qpos + r.Map.RootQposAdr;
            double rad = Math.Sqrt(q[0] * q[0] + q[1] * q[1]), h = q[2] - _arena.Ground(rad), up = ObsBuilder.Up(q + 3);
            if (rad > OutRadiusNow) return true;
            if (_downAt[i] >= 0)
            {
                if (up > 0.9 && h > 0.85 * (r.Spec?.key_root_z ?? 0.75f))
                {
                    if (_stoodAt[i] < 0) _stoodAt[i] = t;
                    if (t - _stoodAt[i] >= 0.4) { _downAt[i] = -1; _stoodAt[i] = -1; r.SetGetUp(false); MatchEvents.RaiseRecovered(r); }
                }
                else _stoodAt[i] = -1;
                return _downAt[i] >= 0 && getupSeconds > 0 && t - _downAt[i] > getupSeconds;
            }
            bool fallen = up < _arena.fall_up || h < _arena.fall_height;
            if (!fallen) return false;
            if ((!getupOnPlateauOnly || rad < _arena.plateau_r) && (maxGetups <= 0 || _getups[i] < maxGetups) && r.SetGetUp(true))
            {
                _downAt[i] = t; _stoodAt[i] = -1; _getups[i]++; MatchEvents.RaiseDown(r);
                return false;
            }
            return true;
        }

        void Post(object s, MjStepArgs a)
        {
            _stepMs = 0.95 * _stepMs + 0.05 * _sw.Elapsed.TotalMilliseconds;
            if (_pending || !Ready()) return;
            if (State == MatchState.Result) { AfterBell(MjScene.Instance.Data); return; }
            if (State != MatchState.Fight) return;
            var d = MjScene.Instance.Data;
            double t = d->time - _roundStart;
            if (t > 1.2) Shot("combat");
            int alive = 0, last = -1; bool getup = GetUpRule;
            for (int i = 0; i < _playing; i++)
            {
                if (!_out[i] && (getup ? OutWithGetUp(d, i, t) : Out(d, bodies[i])))
                {
                    if (_downAt[i] >= 0) { _downAt[i] = -1; bodies[i].SetGetUp(false); }
                    _out[i] = true; _knockout = true; _alive.Remove(bodies[i]); _fallen.Add(bodies[i]);
                    if (_playing > 2) bodies[i].paused = true;        // in a free-for-all an eliminated fighter goes limp
                    MatchEvents.RaiseKnockout(bodies[i]);
                }
                if (!_out[i]) { alive++; last = i; }
            }
            bool bell = sea != null ? sea.ReachedSummit : t >= roundSeconds;
            if (statsRounds > 0 && FfaStats && (_out[0] || alive == 1 || bell)) { FfaRoundOver(t); return; }    // as eval.py: the candidate's round ends when it is out
            if (alive > 1 && !bell) return;
            // the last one in must stay in for confirmSeconds (the game only): a fighter already on its way over is no king
            if (alive == 1 && !bell && statsRounds == 0 && confirmSeconds > 0f)
            {
                if (_lastStandingAt < 0) _lastStandingAt = t;
                if (t - _lastStandingAt < confirmSeconds) return;
            }
            EndRound(d, alive, last, t);
        }

        // After the bell (the game only): the result stands, nobody can be knocked out any more, but whoever is still on
        // the summit keeps its feet. A fighter that is down, or falls now, gets its get-up brain as during the round, and
        // takes up its post-round part once it stands: the winner walks to the centre and celebrates, the others stand.
        // Without this the winner lay where it fell in most rounds (4 of 5 measured, DOCS/20261114). No events are raised:
        // the feed, the commentary and the ladder are about the round, which is over.
        void AfterBell(MujocoLib.mjData_* d)
        {
            if (statsRounds > 0) return;
            double t = d->time - _roundStart; int winner = LastResult.Winner;
            for (int i = 0; i < _playing; i++)
            {
                if (_out[i]) continue;
                var r = bodies[i]; double* q = d->qpos + r.Map.RootQposAdr;
                double rad = Math.Sqrt(q[0] * q[0] + q[1] * q[1]), h = q[2] - _arena.Ground(rad), up = ObsBuilder.Up(q + 3);
                if (_downAt[i] >= 0)
                {
                    if (up > 0.9 && h > 0.85 * (r.Spec?.key_root_z ?? 0.75f))
                    {
                        if (_stoodAt[i] < 0) _stoodAt[i] = t;
                        if (t - _stoodAt[i] < 0.4) continue;
                        _downAt[i] = -1; _stoodAt[i] = -1; r.SetGetUp(false);
                        r.SetPostRound(i == winner ? PolicyRunner.PostRound.Victory : PolicyRunner.PostRound.Defeat);
                    }
                    else _stoodAt[i] = -1;
                }
                else if ((up < _arena.fall_up || h < _arena.fall_height) && rad <= OutRadiusNow && r.SetGetUp(true))
                {
                    _downAt[i] = t; _stoodAt[i] = -1; r.SetPostRound(PolicyRunner.PostRound.None);
                }
            }
        }

        void EndRound(MujocoLib.mjData_* d, int alive, int last, double t)
        {
            int winner = -1; string how; RoundEnd end;
            if (alive == 1)
            {
                winner = last; end = RoundEnd.Knockout; _times.Add(t);
                how = $"last one on the summit after {t:0.0} s";
                _status = $"{(char)('A' + last)} ({BadgeOf(_agentAt[last])}) wins in {t:0.0} s";
            }
            else if (alive > 1 && sea != null)
            {
                // The sea has reached the top with several still standing: the hill belongs to whoever is nearest its
                // centre. (Without a sea, in the statistics gate, a full-time round stays a tie as in training.)
                double best = double.MaxValue;
                for (int pass = 0; pass < 2 && winner < 0; pass++)          // a fighter that is down only wins if everyone left is down
                    for (int i = 0; i < _playing; i++)
                    {
                        if (_out[i] || (pass == 0 && IsDown(i))) continue;
                        double* q = d->qpos + bodies[i].Map.RootQposAdr; double r2 = q[0] * q[0] + q[1] * q[1];
                        if (r2 < best) { best = r2; winner = i; }
                    }
                end = RoundEnd.SeaArrived;
                how = $"nearest the centre when the sea arrived ({Math.Sqrt(best):0.0} m, {alive} still standing)";
                _status = $"{(char)('A' + winner)} ({BadgeOf(_agentAt[winner])}) holds the summit as the sea arrives";
            }
            else
            {
                end = alive > 1 ? RoundEnd.FullTime : RoundEnd.NobodyLeft;
                how = alive > 1 ? "full time" : _lastStandingAt >= 0 ? "the last one in went over too" : "nobody was left on the summit";
                _status = alive > 1 ? "Tie: full time" : "Tie: nobody left on the summit";
            }
            if (winner >= 0) Award(winner); else _draws++;
            _roundsThisMatch++;
            var agents = new int[_playing]; for (int i = 0; i < _playing; i++) agents[i] = _agentAt[i];
            LastResult = new RoundResult(winner, winner >= 0 ? BadgeOf(_agentAt[winner]) : "No winner", how, end, (float)t, agents);
            if (statsRounds > 0)
            {
                if (_bodyWins[0] + _bodyWins[1] + _draws >= statsRounds) WriteStats(); else NewRound();
                return;
            }
            if (_bracket.Count > 0) RecordBracket(winner);
            // after the round (the game only; the gates returned above): the sea stops where it is (it would float the
            // winner off a rim it barely held), the winner celebrates, whoever else still stands gives up
            if (sea != null) sea.Hold();
            for (int i = 0; i < _playing; i++)
                if (!_out[i] && !IsDown(i)) bodies[i].SetPostRound(i == winner ? PolicyRunner.PostRound.Victory : PolicyRunner.PostRound.Defeat);
            _resultUntil = Time.unscaledTime + pauseBetweenRounds;
            MatchEvents.RaiseRoundEnded(LastResult);       // first: the ladder and telemetry settle before the results sheet reads them
            SetState(MatchState.Result);
        }

        void RecordBracket(int winnerBody)
        {
            var m = _bracket[_bracketAt];
            // both went over (the last one in fell inside confirmSeconds, most tied duels): whoever went over last goes through
            if (winnerBody < 0 && _fallen.Count == 2 && _alive.Count == 0)
            {
                winnerBody = Array.IndexOf(bodies, _fallen[1]);
                _status = $"{m.label}: both went over, {EntrantName(winnerBody == 0 ? m.a : m.b)} lasted longer";
            }
            if (winnerBody < 0 && m.tries++ == 0) { _status = m.label + ": tie, replayed once"; return; }
            int w;
            if (winnerBody >= 0) w = winnerBody == 0 ? m.a : m.b;          // PlayBracketMatch seats a on body 0, b on body 1
            else
            {
                // a second tie: the higher Elo goes through (a coin toss between two copies of one agent)
                float ra = Ladder.Rating(NameOf(_entrants[m.a], "?")), rb = Ladder.Rating(NameOf(_entrants[m.b], "?"));
                w = ra > rb ? m.a : rb > ra ? m.b : UnityEngine.Random.value < 0.5f ? m.a : m.b;
            }
            m.winner = w;
            if (m.next >= 0)
            {
                var next = _bracket[m.next];
                if (next.a < 0) next.a = w; else next.b = w;
            }
            else TournamentChampion = EntrantName(w);
        }

        /// <summary>Leave the winner panel now (its button).</summary>
        public void Continue() { if (State == MatchState.Result) _resultUntil = Time.unscaledTime; }

        /// <summary>ReplaySystem holds the result while it plays the knockout back.</summary>
        public void BeginReplay() { if (State == MatchState.Result) SetState(MatchState.Replay); }
        public void EndReplay() { if (State != MatchState.Replay) return; _resultUntil = Mathf.Max(_resultUntil, Time.unscaledTime + 2f); SetState(MatchState.Result); }

        void Advance()
        {
            if (ShotsDone()) return;
            if (_bracket.Count > 0)
            {
                var m = _bracket[_bracketAt];
                if (m.winner < 0) { PlayBracketMatch(); return; }                  // replay a tied match
                if (++_bracketAt < _bracket.Count) { PlayBracketMatch(); return; }
                _status = "Tournament champion: " + TournamentChampion;
                _bracket.Clear(); EnterMenu(); return;
            }
            if (LastRoundOfMatch && !(_shotDir != null && Cli.Exit)) EnterMenu(); else NewRound();
        }

        // ------------------------------------------------------------------ free-for-all statistics gate
        bool FfaStats => statsPool != null && statsPool.Length > 0 && statsCandidate >= 0;
        int _ffaWin, _ffaLoss, _ffaTie; readonly List<double> _ffaTimes = new();

        // Candidate first (it takes body A), then three opponents drawn from the pool, as eval.py draws per world.
        List<int> FfaDraw()
        {
            var agents = new List<int> { statsCandidate };
            if (statsPoolInOrder) { agents.AddRange(statsPool); return agents; }
            for (int k = 0; k < Mathf.Min(3, bodies.Length - 1); k++) agents.Add(statsPool[UnityEngine.Random.Range(0, statsPool.Length)]);
            return agents;
        }

        void FfaRoundOver(double t)
        {
            int alive = 0; for (int i = 0; i < _playing; i++) if (!_out[i]) alive++;
            if (_out[0]) _ffaLoss++; else if (alive == 1) { _ffaWin++; _ffaTimes.Add(t); } else _ffaTie++;
            int n = _ffaWin + _ffaLoss + _ffaTie;
            if (n < statsRounds) { StartMatch(FfaDraw()); return; }
            _ffaTimes.Sort(); double median = _ffaTimes.Count > 0 ? _ffaTimes[_ffaTimes.Count / 2] : -1;
            string F(double v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            string json = $"{{\n \"candidate\": \"{NameOf(statsCandidate, "?")}\", \"rounds\": {n}, \"last_one_in\": {F((double)_ffaWin / n)}, \"out\": {F((double)_ffaLoss / n)}, " +
                          $"\"undecided\": {F((double)_ffaTie / n)}, \"median_win_time_s\": {F(median)}\n}}\n";
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "Training", "assets", bodies[0].body, statsFile.Length > 0 ? statsFile : "ffa_stats_unity.json"));
            System.IO.File.WriteAllText(path, json); Debug.Log("[MatchDirector] wrote " + path + "\n" + json);
            foreach (var f in bodies) f.paused = true;
            StatsDone();
        }

        // End of a statistics gate: back to real time (50x with 2 s frames keeps the editor's main thread busy, so the
        // CLI cannot reach it), then leave play mode, the editor, or the player.
        void StatsDone()
        {
            RestoreModifier();
            statsRounds = 0; Time.timeScale = 1f; Time.maximumDeltaTime = 1f / 3f;
#if UNITY_EDITOR
            if (Cli.Exit) UnityEditor.EditorApplication.Exit(0); else UnityEditor.EditorApplication.ExitPlaymode();
#else
            Application.Quit();
#endif
        }

        void Award(int body) { _bodyWins[body]++; if (_agentAt[body] >= 0 && _agentAt[body] < _agentWins.Length) _agentWins[_agentAt[body]]++; }

        public string NameOf(int agent, string fallback) => roster.Length > 0 && agent >= 0 ? roster[Mathf.Clamp(agent, 0, roster.Length - 1)].name : fallback;
        public string BadgeOf(int agent) => roster.Length > 0 && agent >= 0 ? roster[Mathf.Clamp(agent, 0, roster.Length - 1)].Badge : "Robot";
        public string AgentNameAtBody(int body) => body >= 0 && body < _agentAt.Length && _agentAt[body] >= 0 ? NameOf(_agentAt[body], bodies[body].body) : (body >= 0 && body < bodies.Length ? bodies[body].body : "?");

        // ------------------------------------------------------------------ verification helpers
        public void Shot(string name)
        {
            if (_shotDir == null || !_shots.Add(name)) return;
            System.IO.Directory.CreateDirectory(_shotDir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(_shotDir, name + ".png"));
            Debug.Log("[MatchDirector] screenshot " + name);
        }
        // Automated capture run (-kothShots dir -kothExit): leave once menu, combat and ejection frames exist.
        bool ShotsDone()
        {
            if (_shotDir == null || !Cli.Exit) return false;
            if (!_shots.Contains("menu") || !_shots.Contains("combat") || !_shots.Contains("ejection")) return false;
            if (Cli.Flag("-kothWaitSplash") && !_shots.Contains("splash")) return false;      // keep playing until the camera has cut to a faller at the water
            if (synth != null)
                System.IO.File.WriteAllText(System.IO.Path.Combine(_shotDir, "audio.json"),
                    $"{{\"thuds\": {synth.Thuds}, \"footfalls\": {synth.Clicks}, \"splashes\": {synth.Splashes}, \"voices\": {synth.Voices}, \"peak_sample\": {synth.Peak.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, \"audio_blocks\": {synth.Blocks}}}\n");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(0);
#endif
            return true;
        }

        void WriteStats()
        {
            _times.Sort(); int winsA = _bodyWins[0], winsB = _bodyWins[1], n = winsA + winsB + _draws;
            double Q(double f) => _times.Count == 0 ? -1 : _times[Math.Min(_times.Count - 1, (int)(f * _times.Count))];
            double mean = 0; foreach (var x in _times) mean += x; mean = _times.Count > 0 ? mean / _times.Count : -1;
            string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            string json = $"{{\n \"rounds\": {n}, \"wins_a\": {winsA}, \"wins_b\": {winsB}, \"ties\": {_draws}, \"decided\": {F((double)(winsA + winsB) / n)},\n" +
                          $" \"median_time_s\": {F(Q(0.5))}, \"mean_time_s\": {F(mean)}, \"p25_time_s\": {F(Q(0.25))}, \"p75_time_s\": {F(Q(0.75))}\n}}\n";
            // The player has no training folder beside it: it writes next to the executable, like perf_unity.json.
            string path = System.IO.Path.GetFullPath(Application.isEditor ? System.IO.Path.Combine(Application.dataPath, "..", "Training", "assets", bodies[0].body, statsFile.Length > 0 ? statsFile : "duel_stats_unity.json")
                                                                          : System.IO.Path.Combine(Application.dataPath, "..", "duel_stats_unity.json"));
            System.IO.File.WriteAllText(path, json); Debug.Log("[MatchDirector] wrote " + path + "\n" + json);
            bodies[0].paused = bodies[1].paused = true;      // seats A and B: the pair the two-fighter gate measures
            StatsDone();
        }

        void Update()
        {
            for (int i = 0; i < mapVisuals.Length; i++) if (mapVisuals[i] != null && mapVisuals[i].activeSelf != (i == selectedMap)) mapVisuals[i].SetActive(i == selectedMap);
            if (State == MatchState.Menu && _menuShotAt > 0 && Time.unscaledTime >= _menuShotAt)
            {
                _menuShotAt = -1; Shot("menu");
                if (_shotDir != null && Cli.Exit) _launchAt = Time.unscaledTime + 1.0f;     // automated capture run: continue into a round
            }
            if (_launchAt > 0 && Time.unscaledTime >= _launchAt) { _launchAt = -1; Launch(); }
            if (State == MatchState.Intro && Time.unscaledTime >= _introStart + _introLength) EndIntro();
            TickFeatures();
            if (State == MatchState.Result)
            {
                float shownFor = pauseBetweenRounds - (_resultUntil - Time.unscaledTime);
                if (shownFor >= 1f) { if (_knockout) Shot("ejection"); Shot("winner"); }
                if (Time.unscaledTime >= _resultUntil)
                {
                    if (!AdvanceHeats()) Advance();
                }
            }
        }
    }
}
