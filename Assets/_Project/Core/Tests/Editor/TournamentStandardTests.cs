using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace PoKingHill.Tests
{
    /// <summary>The tournament standard: Elo 1000 / K 32
    /// and the 5 x 5 voice matrix's names and procedural fallback.</summary>
    public class TournamentStandardTests
    {
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
