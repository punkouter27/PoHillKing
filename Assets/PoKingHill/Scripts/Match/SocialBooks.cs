using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// Persistent pick-your-winner prediction tracker with upset scoring and win streak (#1).
    /// Atomic temp-file write + FileOverride mirrors Ladder in MatchLog.cs.
    /// </summary>
    public static class PredictionBook
    {
        [Serializable]
        class SaveData
        {
            public int picks, correct, streak, bestStreak, points;
        }

        static string _fileOverride;
        public static string FileOverride
        {
            get => _fileOverride;
            set { _fileOverride = value; _loaded = false; _data = null; }
        }

        static SaveData _data;
        static bool _loaded;

        public static string CurrentPick { get; private set; }
        public static float CurrentOdds { get; private set; }
        public static int LastDelta { get; private set; }
        public static string LastOutcome { get; private set; }

        public static int Picks { get { Load(); return _data.picks; } }
        public static int Correct { get { Load(); return _data.correct; } }
        public static int Streak { get { Load(); return _data.streak; } }
        public static int BestStreak { get { Load(); return _data.bestStreak; } }
        public static int Points { get { Load(); return _data.points; } }

        static string PathOnDisk => string.IsNullOrEmpty(_fileOverride)
            ? Path.Combine(Application.persistentDataPath, "predictions.json")
            : _fileOverride;

        static void Load()
        {
            if (_loaded && _data != null) return;
            _loaded = true;
            _data = new SaveData();
            try
            {
                string p = PathOnDisk;
                if (File.Exists(p))
                {
                    var parsed = JsonUtility.FromJson<SaveData>(File.ReadAllText(p));
                    if (parsed != null) _data = parsed;
                }
            }
            catch (Exception e) { Debug.LogWarning("[PredictionBook] load skipped: " + e.Message); }
        }

        static void Save()
        {
            try
            {
                string p = PathOnDisk;
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(_data, true));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
            catch (Exception e) { Debug.LogWarning("[PredictionBook] save failed: " + e.Message); }
        }

        public static void SetPick(string agentName, float odds01)
        {
            Load();
            CurrentPick = string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim();
            CurrentOdds = Mathf.Clamp(odds01, 0.03f, 1f);
            LastOutcome = null;
            LastDelta = 0;
        }

        public static void ClearPick()
        {
            CurrentPick = null;
            CurrentOdds = 0f;
        }

        /// <summary>Reward accurate picks scaled by upset inverse-odds plus streak multiplier.</summary>
        public static int ScoreForWin(float odds01, int nextStreak)
        {
            float clamped = Mathf.Clamp(odds01, 0.05f, 1f);
            int basePts = Mathf.Max(25, Mathf.RoundToInt(25f / clamped));
            int streakBonus = Math.Max(0, nextStreak - 1) * 15;
            return basePts + streakBonus;
        }

        public static string Settle(string winnerName)
        {
            Load();
            if (string.IsNullOrEmpty(CurrentPick)) return null;
            string pick = CurrentPick;
            float odds = CurrentOdds;
            CurrentPick = null;
            if (string.IsNullOrEmpty(winnerName))
            {
                LastDelta = 0;
                LastOutcome = $"Pick {pick}: Draw (streak kept at {_data.streak})";
                return LastOutcome;
            }
            _data.picks++;
            if (string.Equals(pick, winnerName, StringComparison.OrdinalIgnoreCase))
            {
                _data.correct++;
                _data.streak++;
                if (_data.streak > _data.bestStreak) _data.bestStreak = _data.streak;
                int earned = ScoreForWin(odds, _data.streak);
                _data.points += earned;
                LastDelta = earned;
                LastOutcome = $"Pick {pick} HIT! +{earned} pts (streak {_data.streak})";
            }
            else
            {
                _data.streak = 0;
                LastDelta = 0;
                LastOutcome = $"Pick {pick} missed ({winnerName} won)";
            }
            Save();
            return LastOutcome;
        }

        public static void ResetAll()
        {
            _data = new SaveData();
            _loaded = true;
            CurrentPick = null;
            CurrentOdds = 0f;
            LastDelta = 0;
            LastOutcome = null;
            Save();
        }
    }

    /// <summary>
    /// Persistent career stats and pairwise head-to-head rivalries / nemesis cards (#3).
    /// </summary>
    public static class RivalryBook
    {
        [Serializable]
        public class PairRecord
        {
            public string a, b;
            public int aWins, bWins, aKosB, bKosA;
            public string lastWinner;
        }

        [Serializable]
        public class CareerRecord
        {
            public string name;
            public int wins, matches, kos;
        }

        [Serializable]
        class SaveData
        {
            public List<PairRecord> pairs = new();
            public List<CareerRecord> careers = new();
        }

        static string _fileOverride;
        public static string FileOverride
        {
            get => _fileOverride;
            set { _fileOverride = value; _loaded = false; _data = null; }
        }

        static SaveData _data;
        static bool _loaded;

        static string PathOnDisk => string.IsNullOrEmpty(_fileOverride)
            ? Path.Combine(Application.persistentDataPath, "rivalries.json")
            : _fileOverride;

        static void Load()
        {
            if (_loaded && _data != null) return;
            _loaded = true;
            _data = new SaveData();
            try
            {
                string p = PathOnDisk;
                if (File.Exists(p))
                {
                    var parsed = JsonUtility.FromJson<SaveData>(File.ReadAllText(p));
                    if (parsed != null)
                    {
                        _data = parsed;
                        _data.pairs ??= new List<PairRecord>();
                        _data.careers ??= new List<CareerRecord>();
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[RivalryBook] load skipped: " + e.Message); }
        }

        static void Save()
        {
            try
            {
                string p = PathOnDisk;
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(_data, true));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
            catch (Exception e) { Debug.LogWarning("[RivalryBook] save failed: " + e.Message); }
        }

        static PairRecord Pair(string x, string y, bool create)
        {
            Load();
            if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y) || string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return null;
            bool swap = string.Compare(x, y, StringComparison.OrdinalIgnoreCase) > 0;
            string a = swap ? y : x, b = swap ? x : y;
            for (int i = 0; i < _data.pairs.Count; i++)
            {
                var r = _data.pairs[i];
                if (string.Equals(r.a, a, StringComparison.OrdinalIgnoreCase) && string.Equals(r.b, b, StringComparison.OrdinalIgnoreCase))
                    return r;
            }
            if (!create) return null;
            var added = new PairRecord { a = a, b = b };
            _data.pairs.Add(added);
            return added;
        }

        static CareerRecord Career(string name, bool create)
        {
            Load();
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < _data.careers.Count; i++)
                if (string.Equals(_data.careers[i].name, name, StringComparison.OrdinalIgnoreCase)) return _data.careers[i];
            if (!create) return null;
            var added = new CareerRecord { name = name };
            _data.careers.Add(added);
            return added;
        }

        public static void RecordKnockout(string killer, string victim)
        {
            if (string.IsNullOrEmpty(killer) || string.IsNullOrEmpty(victim) || killer == victim) return;
            var kc = Career(killer, true);
            if (kc != null) kc.kos++;
            var p = Pair(killer, victim, true);
            if (p != null)
            {
                if (string.Equals(p.a, killer, StringComparison.OrdinalIgnoreCase)) p.aKosB++;
                else p.bKosA++;
            }
            Save();
        }

        public static void RecordMatchResult(string winner, IReadOnlyList<string> entrants)
        {
            if (entrants == null || entrants.Count < 2) return;
            Load();
            for (int i = 0; i < entrants.Count; i++)
            {
                var c = Career(entrants[i], true);
                if (c != null) c.matches++;
            }
            if (!string.IsNullOrEmpty(winner))
            {
                var wc = Career(winner, true);
                if (wc != null) wc.wins++;
                for (int i = 0; i < entrants.Count; i++)
                {
                    if (string.Equals(entrants[i], winner, StringComparison.OrdinalIgnoreCase)) continue;
                    var p = Pair(winner, entrants[i], true);
                    if (p == null) continue;
                    if (string.Equals(p.a, winner, StringComparison.OrdinalIgnoreCase)) p.aWins++;
                    else p.bWins++;
                    p.lastWinner = winner;
                }
            }
            Save();
        }

        public static (int aWins, int bWins, int aKosB, int bKosA) HeadToHead(string a, string b)
        {
            var p = Pair(a, b, false);
            if (p == null) return (0, 0, 0, 0);
            bool direct = string.Equals(p.a, a, StringComparison.OrdinalIgnoreCase);
            return direct ? (p.aWins, p.bWins, p.aKosB, p.bKosA) : (p.bWins, p.aWins, p.bKosA, p.aKosB);
        }

        public static (int wins, int matches, int kos) CareerOf(string agent)
        {
            var c = Career(agent, false);
            return c == null ? (0, 0, 0) : (c.wins, c.matches, c.kos);
        }

        public static (string nemesis, int losses, int kos) NemesisOf(string agent)
        {
            Load();
            string best = null;
            int bestScore = 0, bestLosses = 0, bestKos = 0;
            for (int i = 0; i < _data.pairs.Count; i++)
            {
                var p = _data.pairs[i];
                int oppWins, oppKos; string other;
                if (string.Equals(p.a, agent, StringComparison.OrdinalIgnoreCase)) { oppWins = p.bWins; oppKos = p.bKosA; other = p.b; }
                else if (string.Equals(p.b, agent, StringComparison.OrdinalIgnoreCase)) { oppWins = p.aWins; oppKos = p.aKosB; other = p.a; }
                else continue;
                int score = oppWins * 2 + oppKos;
                if (score > bestScore) { bestScore = score; best = other; bestLosses = oppWins; bestKos = oppKos; }
            }
            return (best, bestLosses, bestKos);
        }

        public static string SummaryLine(string a, string b)
        {
            var (aw, bw, ak, bk) = HeadToHead(a, b);
            if (aw + bw + ak + bk == 0) return "First meeting";
            return $"H2H {aw}-{bw} · KOs {ak}-{bk}";
        }

        public static void ResetAll()
        {
            _data = new SaveData();
            _loaded = true;
            Save();
        }
    }
}
