using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace PoKingHill.Tests
{
    /// <summary>The tournament standard: the 16-slot elimination bracket (seeding, BYEs, draws, saving), Elo 1000 / K 32
    /// and the 5 x 5 voice matrix's names and procedural fallback.</summary>
    public class TournamentStandardTests
    {
        static List<string> Names(int n) => Enumerable.Range(1, n).Select(i => "A" + i).ToList();

        [Test]
        public void FirstRoundFollowsTheStandardSeedOrder()
        {
            CollectionAssert.AreEqual(new[] { 1, 16, 8, 9, 4, 13, 5, 12, 2, 15, 7, 10, 3, 14, 6, 11 }, EliminationBracket.SeedOrder);
            var b = EliminationBracket.Create(Names(16));
            Assert.AreEqual(15, b.bouts.Count);
            var first = b.bouts.Where(x => x.round == 0).Select(x => (x.seedA, x.seedB)).ToArray();
            CollectionAssert.AreEqual(new[] { (1, 16), (8, 9), (4, 13), (5, 12), (2, 15), (7, 10), (3, 14), (6, 11) }, first);
            var next = b.Next();
            Assert.AreEqual((1, 16), (next.seedA, next.seedB));
        }

        [Test]
        public void FullBracketPlaysFifteenBoutsAndTopSeedsWin()
        {
            var b = EliminationBracket.Create(Names(16));
            int fought = 0;
            for (var m = b.Next(); m != null; m = b.Next()) { b.Record(m, System.Math.Min(m.seedA, m.seedB)); fought++; }
            Assert.AreEqual(15, fought);
            Assert.IsTrue(b.Finished);
            Assert.AreEqual("A1", b.Champion);
            var sf = b.bouts.Where(x => x.round == 2).Select(x => (x.seedA, x.seedB)).ToArray();
            CollectionAssert.AreEqual(new[] { (1, 4), (2, 3) }, sf, "seeds 1 and 2 only meet in the final");
        }

        [Test]
        public void ByesTakeTheBottomSeedsAndWalkOver()
        {
            var b = EliminationBracket.Create(Names(5));
            for (int s = 1; s <= 5; s++) Assert.IsFalse(b.IsBye(s));
            for (int s = 6; s <= 16; s++) { Assert.IsTrue(b.IsBye(s)); Assert.AreEqual("BYE", b.NameOf(s)); }
            // round of 16: only walk-overs (1, 4, 5, 2, 3 go through unfought; 8v9 sends a BYE on)
            Assert.IsTrue(b.bouts.Where(x => x.round == 0).All(x => x.winner != 0 && !x.fought));
            Assert.IsTrue(b.IsBye(b.bouts[1].winner), "BYE vs BYE sends a BYE on");
            var next = b.Next();
            Assert.AreEqual(1, next.round);
            Assert.AreEqual((4, 5), (next.seedA, next.seedB));
        }

        [Test]
        public void ThreeEntrantsNeedTwoBoutsAndADrawGoesToTheHigherSeed()
        {
            var b = EliminationBracket.Create(Names(3));
            var sf = b.Next();
            Assert.AreEqual(2, sf.round); Assert.AreEqual((2, 3), (sf.seedA, sf.seedB));
            b.Record(sf, 0);                                    // draw
            Assert.IsTrue(sf.draw); Assert.AreEqual(2, sf.winner);
            var final = b.Next();
            Assert.AreEqual(3, final.round); Assert.AreEqual((1, 2), (final.seedA, final.seedB));
            b.Record(final, 2);
            Assert.AreEqual("A2", b.Champion);
            Assert.IsNull(b.Next());
        }

        [Test]
        public void OneOrNoEntrantsFinishWithoutABout()
        {
            var one = EliminationBracket.Create(Names(1));
            Assert.IsTrue(one.Finished); Assert.AreEqual("A1", one.Champion);
            var none = EliminationBracket.Create(new List<string>());
            Assert.IsTrue(none.Finished); Assert.AreEqual("", none.Champion);
        }

        [Test]
        public void BracketResumesFromItsSaveAndForgetsAFinishedOne()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "koth_bracket_test.json"); File.Delete(tmp);
            EliminationBracket.PathOverride = tmp;
            try
            {
                var b = EliminationBracket.Create(Names(6));
                var m = b.Next(); b.Record(m, m.seedB); b.Save();
                var r = EliminationBracket.LoadUnfinished();
                Assert.IsNotNull(r);
                CollectionAssert.AreEqual(b.seeds, r.seeds);
                Assert.AreEqual(b.Next().index, r.Next().index);
                Assert.AreEqual(b.Next().round, r.Next().round);
                for (var x = r.Next(); x != null; x = r.Next()) r.Record(x, x.seedA);
                r.Save();
                Assert.IsNull(EliminationBracket.LoadUnfinished(), "a finished bracket is not offered for resuming");
            }
            finally { EliminationBracket.PathOverride = null; File.Delete(tmp); }
        }

        [Test]
        public void EloStartsAt1000WithK32AndKeepsSavedRatings()
        {
            Assert.AreEqual(1000f, Ladder.Start);
            Assert.AreEqual(32f, Ladder.K);
            string tmp = Path.Combine(Path.GetTempPath(), "koth_ladder_migrate_test.json");
            File.WriteAllText(tmp, "{\"agents\":[{\"name\":\"Old\",\"rating\":1500,\"rounds\":10,\"wins\":5,\"losses\":5,\"kos\":0,\"assists\":0}]}");
            Ladder.FileOverride = tmp;
            try
            {
                Assert.AreEqual(1500f, Ladder.Rating("Old"), "existing ladder.json ratings stay as they are");
                Assert.AreEqual(1000f, Ladder.Rating("New"));
                var roster = new[] { new Agent { name = "New" }, new Agent { name = "Other" } };
                Ladder.Record(new RoundResult(0, "New", "", RoundEnd.Knockout, 3f, new[] { 0, 1 }), roster);
                Assert.AreEqual(1016f, Ladder.Rating("New"), 1e-3, "equal ratings: the winner gains K/2");
            }
            finally { Ladder.FileOverride = null; File.Delete(tmp); }
        }

        [Test]
        public void VoiceMatrixNamesAndMapping()
        {
            Assert.AreEqual("Voices/kim/hurt-3", SoundPool.VoicePath("Kim", SoundPool.VoiceEmotion.Hurt, 3));
            Assert.AreEqual("Voices/trump/celebrate-5", SoundPool.VoicePath("Trump (Bulldozer: 102 kg)", SoundPool.VoiceEmotion.Celebrate, 9));
            Assert.AreEqual("Voices/grandpa/insult-1", SoundPool.VoicePath("Grandpa", SoundPool.VoiceEmotion.Insult, 0));
            Assert.AreEqual(SoundPool.VoiceEmotion.Hurt, SoundPool.StandardOf(SoundPool.Emotion.Pain));
            Assert.AreEqual(SoundPool.VoiceEmotion.Celebrate, SoundPool.StandardOf(SoundPool.Emotion.Triumph));
            Assert.AreEqual(SoundPool.VoiceEmotion.Angry, SoundPool.StandardOf(SoundPool.Emotion.Strain));
            Assert.AreEqual(1, SoundPool.LevelOf(0f)); Assert.AreEqual(3, SoundPool.LevelOf(0.5f)); Assert.AreEqual(5, SoundPool.LevelOf(1f));
        }

        [Test]
        public void ProceduralVoiceCellsScaleWithLevel()
        {
            foreach (SoundPool.VoiceEmotion e in System.Enum.GetValues(typeof(SoundPool.VoiceEmotion)))
            {
                float prevPeak = 0f; int prevLength = 0;
                for (int level = 1; level <= SoundPool.VoiceLevels; level++)
                {
                    var x = SoundPool.SynthVoice(0, e, level);
                    float peak = x.Max(v => System.Math.Abs(v));
                    Assert.IsFalse(x.Any(float.IsNaN), $"{e} {level}");
                    Assert.Greater(peak, prevPeak, $"{e}: level {level} is louder");
                    Assert.Greater(x.Length, prevLength, $"{e}: level {level} is longer");
                    prevPeak = peak; prevLength = x.Length;
                }
            }
        }
    }
}
