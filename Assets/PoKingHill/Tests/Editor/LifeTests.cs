using NUnit.Framework;
using UnityEngine;

namespace PoKingHill.Tests
{
    /// <summary>The pure parts behind the lifelike layer: the music bed, the soft-part spring, the head's reach, the voice's queue rules.</summary>
    public class LifeTests
    {
        static float Rms(MusicBed m, int samples, out float peak)
        {
            double e = 0; peak = 0f;
            for (int i = 0; i < samples; i++)
            {
                m.Next(out float l, out float r);
                Assert.IsFalse(float.IsNaN(l) || float.IsNaN(r) || float.IsInfinity(l) || float.IsInfinity(r), "sample " + i);
                e += l * l; peak = Mathf.Max(peak, Mathf.Max(Mathf.Abs(l), Mathf.Abs(r)));
            }
            return Mathf.Sqrt((float)(e / samples));
        }

        [Test]
        public void MusicIsFiniteBoundedAndBuilds()
        {
            const int rate = 48000;
            var calm = new MusicBed(rate) { Intensity = 0.1f, Drums = true }; var wild = new MusicBed(rate) { Intensity = 1f, Drums = true };
            Rms(calm, 2 * rate, out _); Rms(wild, 2 * rate, out _);                 // let the intensity settle
            float a = Rms(calm, 4 * rate, out float pa), b = Rms(wild, 4 * rate, out float pb);
            Assert.Greater(a, 0.005f, "the calm bed is audible"); Assert.Greater(b, 1.3f * a, "the music builds with the round");
            Assert.Less(pa, 1.6f); Assert.Less(pb, 1.6f);                           // ImpactSynth scales it by 0.55 x volume before the clamp
            var drone = new MusicBed(rate) { Intensity = 0f, Drums = false };
            Assert.Greater(Rms(drone, 2 * rate, out _), 0.002f, "the menu keeps the drone");
        }

        [Test]
        public void MusicStingRingsAndDies()
        {
            const int rate = 48000;
            var quiet = new MusicBed(rate); float before = Rms(quiet, rate, out _);
            quiet.Sting(true); float during = Rms(quiet, rate, out _);
            Rms(quiet, 8 * rate, out _); float after = Rms(quiet, rate, out _);
            Assert.Greater(during, before + 0.02f); Assert.Less(after, before + 0.01f);
        }

        [Test]
        public void MusicLevelsAndTempo()
        {
            Assert.AreEqual(0, MusicBed.Level(0f)); Assert.AreEqual(1, MusicBed.Level(0.3f)); Assert.AreEqual(3, MusicBed.Level(1f));
            Assert.AreEqual(92f, MusicBed.Bpm(0f), 1e-4f); Assert.AreEqual(132f, MusicBed.Bpm(2f), 1e-4f);
        }

        [Test]
        public void SoftSpringLagsThenSettles()
        {
            var s = new SoftSpring(); Vector3 anchor = Vector3.zero;
            Assert.AreEqual(Vector3.zero, s.Step(anchor, 0.016f, 3f, 0.3f, 0.04f));         // first step snaps
            float worst = 0f;
            for (int i = 0; i < 30; i++) { anchor += Vector3.right * 0.03f; worst = Mathf.Max(worst, s.Step(anchor, 0.016f, 3f, 0.3f, 0.04f).magnitude); }
            Assert.Greater(worst, 0.01f, "it lags behind a moving body"); Assert.LessOrEqual(worst, 0.04f + 1e-5f, "never further than its limit");
            Vector3 lag = Vector3.one; for (int i = 0; i < 400; i++) lag = s.Step(anchor, 0.016f, 3f, 0.3f, 0.04f);
            Assert.Less(lag.magnitude, 1e-3f, "and comes to rest");
            Assert.AreEqual(Vector3.zero, s.Step(anchor + Vector3.up * 5f, 0.016f, 3f, 0.3f, 0.04f));     // a teleport snaps
        }

        [Test]
        public void HeadTurnsWithinANecksReach()
        {
            var limit = new Vector3(65f, 35f, 28f); var frame = Quaternion.identity;        // x forward, y up, z left
            Assert.AreEqual(0f, LifeSigns.LookAngles(frame, Vector3.zero, new Vector3(2f, 0f, 0f), limit).magnitude, 1e-3f);
            var left = LifeSigns.LookAngles(frame, Vector3.zero, new Vector3(1f, 0f, 1f), limit);
            Assert.AreEqual(45f, left.x, 1e-2f); Assert.AreEqual(0f, left.y, 1e-2f);
            Assert.AreEqual(-65f, LifeSigns.LookAngles(frame, Vector3.zero, new Vector3(0.1f, 0f, -1f), limit).x, 1e-2f);      // clamped
            Assert.AreEqual(-35f, LifeSigns.LookAngles(frame, Vector3.zero, new Vector3(0.2f, -1f, 0f), limit).y, 1e-2f);
            Assert.AreEqual(Vector2.zero, LifeSigns.LookAngles(frame, Vector3.zero, new Vector3(-1f, 0f, 0.2f), limit));        // behind: not looked at
            // a turned body: its own forward is still zero yaw
            var turned = Quaternion.Euler(0f, 90f, 0f);
            Assert.AreEqual(0f, LifeSigns.LookAngles(turned, Vector3.zero, turned * Vector3.right, limit).x, 1e-2f);
        }

        [Test]
        public void VoiceKeepsWhatMatters()
        {
            Assert.AreEqual(3, VoiceCaster.Priority("Kim is king of the hill!")); Assert.AreEqual(3, VoiceCaster.Priority("A draw on the summit."));
            Assert.AreEqual(2, VoiceCaster.Priority("Trump shoves Kim off!")); Assert.AreEqual(1, VoiceCaster.Priority("What a collision!"));
            Assert.Greater(VoiceCaster.Duration("one two three four five six", 1f), VoiceCaster.Duration("one two", 1f));
            Assert.Less(VoiceCaster.Duration("one two three four", 2f), VoiceCaster.Duration("one two three four", 1f));
            Assert.IsFalse(VoiceCaster.Available, "no voice in the editor: the menu's switch is locked");
        }
    }
}
