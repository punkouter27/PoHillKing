using System.Collections.Generic;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// The tournament standard's modes (README "Modes"):
    ///   FFA 8       one king-of-the-hill match with exactly eight fighters. Seats are filled from the ticked agents
    ///               (every available one if none fit); with fewer than eight agents they are seated more than once
    ///               (mirror bodies), and the status line says so.
    ///   Bracket     the 16-slot single-elimination bracket (EliminationBracket): every distinct available agent,
    ///               seeded by Elo, BYEs on the bottom seeds; each bout is a one-round 1v1 king-of-the-hill match;
    ///               bouts follow each other automatically from the results sheet; saved after every bout
    ///               (persistentDataPath/bracket.json) so it resumes from the menu after a quit.
    /// The older 4x4 Heats-and-Final (MatchDirector.Features.cs) stays as a separate mode: it shares no state with the
    /// bracket.
    /// </summary>
    public partial class MatchDirector
    {
        public const int FfaPlayers = 8;

        EliminationBracket _elim, _savedBracket;
        EliminationBracket.Bout _bout;
        int _boutAgentA = -1, _boutAgentB = -1;
        bool _bracketLaunching;

        public bool InBracket => _elim != null;
        public EliminationBracket Elimination => _elim;
        public EliminationBracket.Bout CurrentBout => _bout;
        /// <summary>An unfinished bracket saved on disk (read when the menu opens).</summary>
        public EliminationBracket SavedBracket => _savedBracket;

        // ------------------------------------------------------------------ FFA 8
        /// <summary>Starts one match with exactly eight fighters; false (with a status) when the bodies cannot seat eight.</summary>
        public bool StartFfa()
        {
            var pool = InGame();
            pool.RemoveAll(i => roster[i].policy == null);
            if (pool.Count == 0) pool = Available();
            if (pool.Count == 0 || bodies.Length < FfaPlayers) { _status = "FFA 8 needs agents and eight fighter bodies"; return false; }
            for (int i = pool.Count - 1; i > 0; i--) { int k = Random.Range(0, i + 1); (pool[i], pool[k]) = (pool[k], pool[i]); }

            var free = new Dictionary<string, int>();
            foreach (var b in bodies) free[b.body] = (free.TryGetValue(b.body, out int n) ? n : 0) + 1;
            var seats = new List<int>(FfaPlayers);
            bool added = true;
            while (seats.Count < FfaPlayers && added)
            {
                added = false;
                foreach (int a in pool)
                {
                    if (seats.Count >= FfaPlayers) break;
                    if (!free.TryGetValue(roster[a].body, out int left) || left <= 0) continue;
                    free[roster[a].body] = left - 1; seats.Add(a); added = true;
                }
            }
            if (seats.Count < FfaPlayers) { _status = $"FFA 8: only {seats.Count} seats fit the bodies of the chosen agents"; return false; }

            int distinct = new HashSet<int>(seats).Count;
            TournamentChampion = "";
            StartMatch(seats);
            _status = distinct < FfaPlayers ? $"FFA 8 · {distinct} agents seated more than once to fill 8" : "FFA 8";
            return true;
        }

        // ------------------------------------------------------------------ 16-slot elimination bracket
        /// <summary>Seeds a new bracket from every distinct available agent by Elo (discarding a saved one) and plays
        /// its first real bout.</summary>
        public void StartTournament()
        {
            var agents = Available();
            agents.Sort((x, y) => { int c = Ladder.Rating(roster[y].name).CompareTo(Ladder.Rating(roster[x].name)); return c != 0 ? c : x.CompareTo(y); });
            var names = new List<string>();
            foreach (int a in agents) if (names.Count < EliminationBracket.Size && !names.Contains(roster[a].name)) names.Add(roster[a].name);
            if (names.Count == 0) { _status = "The bracket needs at least one available agent"; return; }
            _elim = EliminationBracket.Create(names);
            _elim.Save();
            _savedBracket = null;
            TournamentChampion = "";
            PlayBout();
        }

        /// <summary>Continues the saved bracket at its next bout (a bout cut short replays from the start).</summary>
        public void ResumeTournament()
        {
            var saved = _savedBracket ?? EliminationBracket.LoadUnfinished();
            if (saved == null) { StartTournament(); return; }
            _elim = saved; _savedBracket = null;
            TournamentChampion = "";
            PlayBout();
        }

        int AgentByName(string name)
        {
            for (int i = 0; i < roster.Length; i++) if (roster[i].name == name && roster[i].available && roster[i].policy != null) return i;
            return -1;
        }

        void PlayBout()
        {
            while (true)
            {
                _bout = _elim.Next();
                if (_bout == null) { FinishBracket(); return; }
                _boutAgentA = AgentByName(_elim.NameOf(_bout.seedA));
                _boutAgentB = AgentByName(_elim.NameOf(_bout.seedB));
                if (_boutAgentA >= 0 && _boutAgentB >= 0) break;
                // an entrant left the roster since the bracket was saved: the other one goes through unfought
                _elim.Record(_bout, _boutAgentA >= 0 ? _bout.seedA : _boutAgentB >= 0 ? _bout.seedB : 0);
                _elim.Save();
            }
            _bracketLaunching = true;
            try { StartMatch(new List<int> { _boutAgentA, _boutAgentB }); }
            finally { _bracketLaunching = false; }
            _status = $"{EliminationBracket.Label(_bout)}: #{_bout.seedA} {BadgeOf(_boutAgentA)} vs #{_bout.seedB} {BadgeOf(_boutAgentB)}";
        }

        // EndRound: who won the bout (a draw goes to the higher seed inside EliminationBracket.Record).
        void RecordBout(int winnerBody)
        {
            if (_bout == null) return;
            int agent = winnerBody >= 0 && winnerBody < _agentAt.Length ? _agentAt[winnerBody] : -1;
            int seed = agent < 0 ? 0 : agent == _boutAgentA ? _bout.seedA : agent == _boutAgentB ? _bout.seedB : 0;
            _elim.Record(_bout, seed);
            if (_elim.Finished)
            {
                TournamentChampion = _elim.Champion;
                _status = "Bracket champion: " + TournamentChampion;
                EliminationBracket.DeleteSave();
            }
            else _elim.Save();
        }

        void FinishBracket()
        {
            TournamentChampion = _elim.Champion;
            EliminationBracket.DeleteSave();
            _elim = null; _bout = null;
            EnterMenu();
            _status = TournamentChampion.Length > 0 ? "Bracket champion: " + TournamentChampion : "Bracket finished";
        }

        // After the results sheet: the next bout, or the menu once the final is decided. False = not in a bracket.
        bool AdvanceBracket()
        {
            if (!InBracket) return false;
            if (_elim.Finished) { FinishBracket(); return true; }
            PlayBout();
            return true;
        }
    }
}
