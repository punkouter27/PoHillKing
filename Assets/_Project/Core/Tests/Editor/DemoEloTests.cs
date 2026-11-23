using System.IO;
using NUnit.Framework;

namespace PoKingHill.Tests
{
    /// <summary>Demo / CPU-only hill matches update the ladder by finishing order: pairwise Elo with K/(N-1), an agent on
    /// several bodies counted once at its best place and never against itself, only completed rounds, once.</summary>
    public class DemoEloTests
    {
        static readonly Agent[] Roster = { new Agent { name = "Kim" }, new Agent { name = "Trump" }, new Agent { name = "Grandpa" } };
        string _tmp;

        [SetUp] public void Scratch() { _tmp = Path.Combine(Path.GetTempPath(), "koth_demo_elo.json"); File.Delete(_tmp); Ladder.FileOverride = _tmp; }
        [TearDown] public void Clean() { Ladder.FileOverride = null; File.Delete(_tmp); }

        // a quick match as the demo plays it: 16 bodies, the three agents seated several times each
        static readonly int[] Seats = { 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0 };

        static double[] KoTimes(int winner)
        {
            var ko = new double[Seats.Length];
            for (int i = 0; i < ko.Length; i++) ko[i] = i == winner ? -1 : 1 + i;       // later index = out later = better
            return ko;
        }

        [Test]
        public void PlacementsFollowTheKnockOutOrder()
        {
            CollectionAssert.AreEqual(new[] { 0, 2, 1, 3 }, Ladder.Placements(0, 4, new double[] { -1, 5, 9, 2 }));
            CollectionAssert.AreEqual(new[] { 1, 0, 1 }, Ladder.Placements(1, 3, null), "without knock-out times the rest tie");
            CollectionAssert.AreEqual(new[] { 0, 0, 2 }, Ladder.Placements(-1, 3, new double[] { -1, -1, 4 }), "no winner: those still in share first");
        }

        [Test]
        public void ADemoQuickMatchUpdatesRatingsByPlacement()
        {
            // Kim (body 0) wins; Grandpa's best copy (body 14) goes out after Trump's best (body 13): Kim, Grandpa, Trump
            Ladder.Record(new RoundResult(0, "Kim", "", RoundEnd.Knockout, 20f, Seats), Roster, KoTimes(0));
            // N = 3 distinct agents, K' = 32 / 2 = 16, all start at 1000 (expected score 0.5 each pair)
            Assert.AreEqual(1016f, Ladder.Rating("Kim"), 1e-3);
            Assert.AreEqual(1000f, Ladder.Rating("Grandpa"), 1e-3);
            Assert.AreEqual(984f, Ladder.Rating("Trump"), 1e-3);
            Assert.AreEqual(1, Ladder.Rounds("Kim"), "an agent on six bodies plays one round, not six");
            Assert.AreEqual(1, Ladder.Wins("Kim")); Assert.AreEqual(1, Ladder.Losses("Trump"));
            Assert.AreEqual(3000f, Ladder.Rating("Kim") + Ladder.Rating("Trump") + Ladder.Rating("Grandpa"), 1e-3, "zero sum");
        }

        [Test]
        public void SameAgentPairsAreSkipped()
        {
            Ladder.Record(new RoundResult(1, "Kim", "", RoundEnd.Knockout, 9f, new[] { 0, 0, 0 }), Roster, new double[] { 3, -1, 5 });
            Assert.AreEqual(Ladder.Start, Ladder.Rating("Kim"));
            Assert.AreEqual(0, Ladder.Rounds("Kim"), "a mirror-only round is not rated");
        }

        [Test]
        public void OneVersusOneIsPlainEloAndADrawIsHalf()
        {
            Ladder.Record(new RoundResult(0, "Kim", "", RoundEnd.Knockout, 9f, new[] { 0, 1 }), Roster, new double[] { -1, 4 });
            Assert.AreEqual(1016f, Ladder.Rating("Kim"), 1e-3);
            float kim = Ladder.Rating("Kim"), trump = Ladder.Rating("Trump");
            Ladder.Record(new RoundResult(-1, "No winner", "", RoundEnd.FullTime, 25f, new[] { 0, 1 }), Roster, new double[] { -1, -1 });
            Assert.Less(Ladder.Rating("Kim"), kim, "a draw moves the higher rating down");
            Assert.Greater(Ladder.Rating("Trump"), trump);
        }

        [Test]
        public void OnlyCompletedRoundsCountAndOnlyOnce()
        {
            var gate = new RoundGate();
            Assert.IsFalse(gate.TryConsume(), "an end without a start");
            gate.Arm(); Assert.IsTrue(gate.TryConsume()); Assert.IsFalse(gate.TryConsume(), "never twice");
            gate.Arm(); gate.Abort(); Assert.IsFalse(gate.TryConsume(), "left for the menu mid-round (demo interrupted)");

            // the aborted demo: MatchLog asks the gate before recording, so the ladder file is never touched
            if (gate.TryConsume()) Ladder.Record(new RoundResult(0, "Kim", "", RoundEnd.Knockout, 5f, Seats), Roster, KoTimes(0));
            Assert.AreEqual(Ladder.Start, Ladder.Rating("Kim"));
            Assert.IsFalse(File.Exists(_tmp));
        }
    }
}
