using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// The tournament standard's 16-slot single-elimination bracket, pure C# (no scene, EditMode-testable).
    /// Entrants are seeded by Elo (seed 1 = best); empty seeds are BYEs and always take the bottom seeds. First-round
    /// order 1v16, 8v9, 4v13, 5v12, 2v15, 7v10, 3v14, 6v11, so seeds 1 and 2 can only meet in the final. A BYE's
    /// opponent advances unfought, BYE vs BYE sends a BYE on, a draw goes to the higher (lower-numbered) seed.
    /// Saved as JSON (agent names, not roster indices) so an interrupted bracket resumes.
    /// </summary>
    [Serializable]
    public class EliminationBracket
    {
        public const int Size = 16, Rounds = 4;
        public const string ByeLabel = "BYE";
        /// <summary>Seeds of the first round, pairwise: 1v16, 8v9, 4v13, 5v12, 2v15, 7v10, 3v14, 6v11.</summary>
        public static readonly int[] SeedOrder = { 1, 16, 8, 9, 4, 13, 5, 12, 2, 15, 7, 10, 3, 14, 6, 11 };

        [Serializable]
        public class Bout
        {
            public int round, index;          // round 0 = round of 16 .. 3 = final; index within the round
            public int seedA, seedB;          // 1..16, 0 = not known yet (waiting for the previous round)
            public int winner;                // seed that advanced, 0 = undecided
            public bool fought, draw;         // a real bout was played (else a BYE walk-over); it ended level
        }

        /// <summary>Agent name per seed (index = seed - 1); empty = BYE.</summary>
        public List<string> seeds = new();
        public List<Bout> bouts = new();

        public static string PathOverride;
        public static string SavePath => PathOverride ?? System.IO.Path.Combine(Application.persistentDataPath, "bracket.json");

        /// <summary>Builds the bracket from names ranked best first (at most 16; the rest are BYEs) and settles every
        /// BYE walk-over it can.</summary>
        public static EliminationBracket Create(IReadOnlyList<string> rankedNames)
        {
            var b = new EliminationBracket();
            for (int s = 0; s < Size; s++) b.seeds.Add(s < rankedNames.Count && !string.IsNullOrEmpty(rankedNames[s]) ? rankedNames[s] : "");
            for (int r = 0, n = Size / 2; r < Rounds; r++, n /= 2)
                for (int i = 0; i < n; i++)
                    b.bouts.Add(new Bout { round = r, index = i, seedA = r == 0 ? SeedOrder[2 * i] : 0, seedB = r == 0 ? SeedOrder[2 * i + 1] : 0 });
            b.AutoResolve();
            return b;
        }

        public bool IsBye(int seed) => seed < 1 || seed > seeds.Count || string.IsNullOrEmpty(seeds[seed - 1]);
        public string NameOf(int seed) => IsBye(seed) ? ByeLabel : seeds[seed - 1];
        public Bout Final => bouts.Count > 0 ? bouts[^1] : null;
        public bool Finished => Final != null && Final.winner != 0;
        /// <summary>The champion's name, "" while undecided or when every seed was a BYE.</summary>
        public string Champion => Finished && !IsBye(Final.winner) ? NameOf(Final.winner) : "";
        public int RealEntrants { get { int n = 0; for (int s = 1; s <= seeds.Count; s++) if (!IsBye(s)) n++; return n; } }

        public static string RoundName(int round) => round switch { 0 => "Round of 16", 1 => "Quarter-final", 2 => "Semi-final", _ => "Final" };
        public static string Label(Bout b) => b.round == Rounds - 1 ? "Final" : $"{RoundName(b.round)} {b.index + 1}";

        Bout At(int round, int index) { int o = 0; for (int r = 0, n = Size / 2; r < round; r++, n /= 2) o += n; return bouts[o + index]; }

        /// <summary>Settles walk-overs (anything with a BYE in it) and moves winners on; stops at real bouts.</summary>
        public void AutoResolve()
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var b in bouts)
                {
                    if (b.winner != 0 || b.seedA == 0 || b.seedB == 0) continue;
                    bool byeA = IsBye(b.seedA), byeB = IsBye(b.seedB);
                    if (!byeA && !byeB) continue;
                    // BYE's opponent advances unfought; BYE vs BYE sends a BYE on (seed A's)
                    b.winner = byeA && !byeB ? b.seedB : b.seedA;
                    Promote(b); changed = true;
                }
            }
        }

        void Promote(Bout b)
        {
            if (b.round >= Rounds - 1) return;
            var next = At(b.round + 1, b.index / 2);
            if (b.index % 2 == 0) next.seedA = b.winner; else next.seedB = b.winner;
        }

        /// <summary>The next bout two real entrants must fight, in bracket order; null when the bracket is finished.</summary>
        public Bout Next()
        {
            AutoResolve();
            foreach (var b in bouts) if (b.winner == 0 && b.seedA != 0 && b.seedB != 0) return b;
            return null;
        }

        /// <summary>Records a fought bout: the winning seed, or 0 for a draw (the higher seed goes through).</summary>
        public void Record(Bout b, int winnerSeed)
        {
            if (b == null || b.winner != 0) return;
            b.fought = true;
            b.draw = winnerSeed != b.seedA && winnerSeed != b.seedB;
            b.winner = b.draw ? Mathf.Min(b.seedA, b.seedB) : winnerSeed;
            Promote(b);
            AutoResolve();
        }

        public string ToJson() => JsonUtility.ToJson(this, true);
        public static EliminationBracket FromJson(string json)
        {
            var b = JsonUtility.FromJson<EliminationBracket>(json);
            return b != null && b.seeds.Count == Size && b.bouts.Count == Size - 1 ? b : null;
        }

        public void Save()
        {
            try { System.IO.File.WriteAllText(SavePath, ToJson()); }
            catch (Exception ex) { Debug.LogWarning("[Bracket] could not write " + SavePath + ": " + ex.Message); }
        }

        /// <summary>The saved bracket if it is unfinished, else null.</summary>
        public static EliminationBracket LoadUnfinished()
        {
            try
            {
                if (!System.IO.File.Exists(SavePath)) return null;
                var b = FromJson(System.IO.File.ReadAllText(SavePath));
                return b != null && !b.Finished ? b : null;
            }
            catch (Exception ex) { Debug.LogWarning("[Bracket] could not read " + SavePath + ": " + ex.Message); return null; }
        }

        public static void DeleteSave()
        {
            try { if (System.IO.File.Exists(SavePath)) System.IO.File.Delete(SavePath); }
            catch (Exception ex) { Debug.LogWarning("[Bracket] could not delete " + SavePath + ": " + ex.Message); }
        }
    }
}
