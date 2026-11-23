using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PoKingHill.EditorTools;
using UnityEditor.SceneManagement;
using E = PoKingHill.SoundPool.VoiceEmotion;

namespace PoKingHill.Tests
{
    /// <summary>voices.json (po-voice-manifest/1) matches the roster and the files on disk, and the procedural
    /// imitation is audible, scales with the level, differs per character and is deterministic.</summary>
    public class VoiceManifestTests
    {
        static VoiceManifest Committed()
        {
            Assert.IsTrue(File.Exists(VoiceManifestGenerator.ManifestPath), VoiceManifestGenerator.ManifestPath);
            var m = VoiceManifest.Parse(File.ReadAllText(VoiceManifestGenerator.ManifestPath));
            Assert.IsNotNull(m, "voices.json parses as " + VoiceManifest.Schema);
            return m;
        }

        [Test]
        public void ManifestCoversEveryRosterCharacterAndMatchesTheDisk()
        {
            var m = Committed();
            CollectionAssert.AreEqual(VoiceManifest.EmotionNames, m.emotions);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, m.levels);
            var scene = EditorSceneManager.OpenScene(VoiceManifestGenerator.Game, OpenSceneMode.Additive);
            string[] roster;
            try
            {
                var d = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MatchDirector>(true)).First();
                roster = d.roster.Select(a => SoundPool.VoiceKey(a.Badge)).Distinct().ToArray();
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
            Assert.IsNotEmpty(roster);
            CollectionAssert.AreEquivalent(roster, m.characters.Select(c => c.id), "one character per roster agent (BYE is not a character)");
            foreach (var c in m.characters)
            {
                Assert.IsNotNull(c.voice, c.id + " has a voice profile");
                Assert.Greater(c.voice.pitchHz, 50f, c.id);
                foreach (E e in Enum.GetValues(typeof(E)))
                {
                    var cells = c.clips.Of(e);
                    Assert.AreEqual(5, cells.Length, $"{c.id} {e}");
                    for (int l = 1; l <= 5; l++)
                    {
                        var cell = c.Clip(e, l);
                        Assert.IsNotNull(cell, $"{c.id} {e} {l}");
                        Assert.AreEqual($"{c.id}/{VoiceManifest.EmotionNames[(int)e]}-{l}.wav", cell.file);
                        Assert.AreEqual(File.Exists(Path.Combine(VoiceManifest.Root, cell.file)), cell.exists,
                            $"{cell.file}: exists is stale, run PoKingHill/Voices/Rebuild voices.json");
                    }
                }
            }
            // every character sounds different
            Assert.AreEqual(m.characters.Length, m.characters.Select(c => (c.voice.pitchHz, c.voice.formantScale)).Distinct().Count());
        }

        [Test]
        public void BuildListsEveryCellAndKeepsProfiles()
        {
            var old = VoiceManifest.Build(new[] { ("Kim", "Kim") }, null, null, "t0");
            old.characters[0].voice.pitchHz = 222f;
            var m = VoiceManifest.Build(new[] { ("Kim", "Kim"), ("Newcomer (test)", "Newcomer") }, f => f == "kim/hurt-3.wav", old, "t1");
            Assert.AreEqual(2, m.characters.Length);
            Assert.AreEqual(222f, m.Find("kim").voice.pitchHz, "profiles already in the file are kept");
            Assert.AreEqual("newcomer", m.characters[1].id);
            Assert.AreEqual(50, m.Count(out int recorded));
            Assert.AreEqual(1, recorded);
            Assert.IsTrue(m.Find("kim").Clip(E.Hurt, 3).exists);
            Assert.IsNotNull(VoiceManifest.Parse(m.ToJson()), "round trip");
        }

        static float Peak(float[] x) => x.Max(v => Math.Abs(v));
        static float Rms(float[] x) => (float)Math.Sqrt(x.Average(v => (double)v * v));

        [Test]
        public void ProceduralImitationIsAudibleScalesAndIsDeterministic()
        {
            var kim = VoiceManifest.DefaultProfile("kim");
            var trump = VoiceManifest.DefaultProfile("trump");
            foreach (E e in Enum.GetValues(typeof(E)))
            {
                var l1 = SoundPool.SynthVoice(kim, "kim", e, 1);
                var l5 = SoundPool.SynthVoice(kim, "kim", e, 5);
                Assert.IsFalse(l1.Any(float.IsNaN) || l5.Any(float.IsNaN), e.ToString());
                Assert.Greater(Rms(l1), 0.01f, $"{e} level 1 is not silent");
                Assert.Greater(Peak(l5), Peak(l1), $"{e}: level 5 louder");
                Assert.Greater(l5.Length, l1.Length, $"{e}: level 5 longer");
                CollectionAssert.AreEqual(l5, SoundPool.SynthVoice(kim, "kim", e, 5), $"{e}: deterministic");
                var t = SoundPool.SynthVoice(trump, "trump", e, 5);
                Assert.IsFalse(t.Length == l5.Length && t.SequenceEqual(l5), $"{e}: Kim and Trump differ");
                Assert.IsFalse(SoundPool.SynthVoice(kim, "kim", e, 5, 1).SequenceEqual(l5), $"{e}: another take varies");
            }
        }
    }
}
