using NUnit.Framework;
using UnityEngine;

namespace PoKingHill.Tests
{
    /// <summary>
    /// The demo (attract) mode's 30 s idle wait and the camera dwells skip frames longer than 0.25 s
    /// (IdleClock), so a hitch never counts as idle time or as screen time of a shot; the inference log line comes once
    /// per brain; the benchmark names the hardware and what it measured.
    /// </summary>
    public class ShowcaseClockTests
    {
        GameObject _go; DemoMode _demo; DemoSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<DemoSettings>();
            _settings.idleSeconds = 30f; _settings.dwellSeconds = 7f;
            _go = new GameObject("demo under test");
            _demo = _go.AddComponent<DemoMode>();
            _demo.settings = _settings;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
            Object.DestroyImmediate(_settings);
            InferenceLog.Clear();
        }

        static int FramesUntil(System.Func<float, bool> tick, float frame, int cap)
        {
            for (int i = 1; i <= cap; i++) if (tick(frame)) return i;
            return -1;
        }

        [Test]
        public void ClockCountsNormalFramesAndSkipsLongOnes()
        {
            var c = new IdleClock();
            c.Advance(0.016f); c.Advance(0.25f);
            Assert.AreEqual(0.266f, c.Seconds, 1e-5);
            c.Advance(0.2501f); c.Advance(5f); c.Advance(-1f); c.Advance(float.NaN); c.Advance(float.PositiveInfinity);
            Assert.AreEqual(0.266f, c.Seconds, 1e-5, "a long, negative or NaN frame adds nothing");
            c.Reset();
            Assert.AreEqual(0f, c.Seconds);
        }

        [Test]
        public void DemoStartsAfterThirtySecondsOfNormalFrames()
        {
            int frames = FramesUntil(_demo.AdvanceIdle, 1f / 60f, 100000);
            Assert.AreEqual(30f, frames / 60f, 1f / 60f + 1e-3, "30 s of 60 fps frames");
        }

        [Test]
        public void ALongFrameIsNotIdleTime()
        {
            for (int i = 0; i < 29 * 60; i++) Assert.IsFalse(_demo.AdvanceIdle(1f / 60f));
            Assert.IsFalse(_demo.AdvanceIdle(10f), "a 10 s hitch at 29 s must not start the demo");
            Assert.IsFalse(_demo.AdvanceIdle(0.26f), "nor a 0.26 s one");
            Assert.AreEqual(29f, _demo.IdleSeconds, 0.01f);
            int more = FramesUntil(_demo.AdvanceIdle, 1f / 60f, 1000);
            Assert.AreEqual(60, more, 1, "the last second still has to pass in normal frames");
        }

        [Test]
        public void InputRestartsTheIdleWait()
        {
            for (int i = 0; i < 20 * 60; i++) _demo.AdvanceIdle(1f / 60f);
            _demo.ResetIdle();
            Assert.AreEqual(0f, _demo.IdleSeconds);
            Assert.AreEqual(30 * 60, FramesUntil(_demo.AdvanceIdle, 1f / 60f, 100000), 1);
        }

        [Test]
        public void ShotDwellSkipsLongFrames()
        {
            _demo.ResetDwell();
            for (int i = 0; i < 6 * 30; i++) Assert.IsFalse(_demo.AdvanceDwell(1f / 30f));
            Assert.IsFalse(_demo.AdvanceDwell(3f), "a 3 s hitch must not end the shot early");
            int more = FramesUntil(_demo.AdvanceDwell, 1f / 30f, 1000);
            Assert.AreEqual(30, more, 1, "the shot still gets its last second on screen");
        }

        [Test]
        public void InferenceLineComesOncePerBrain()
        {
            InferenceLog.Clear();
            var a = new TextAsset("a") { name = "kim_fighter" };
            var b = new TextAsset("b") { name = "kim_getup" };
            try
            {
                Assert.IsTrue(InferenceLog.Note(a, "kim", "CPU"));
                Assert.IsFalse(InferenceLog.Note(a, "kim", "CPU"), "the same brain logs once");
                Assert.IsTrue(InferenceLog.Note(b, "kim", "CPU"));
                Assert.IsFalse(InferenceLog.Note(null, "kim", "CPU"));
                CollectionAssert.AreEqual(new[] { "kim_fighter", "kim_getup" }, InferenceLog.Brains);
            }
            finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
        }

        [Test]
        public void BenchmarkLabelNamesTheHardwareAndWhatWasMeasured()
        {
            string hw = SimBenchmark.Hardware();
            StringAssert.Contains("CPU " + SystemInfo.processorType.Trim(), hw);
            StringAssert.Contains("GPU ", hw);
            string label = SimBenchmark.Label(3.0, 12000, 1200, 8, hw);
            StringAssert.Contains(hw, label);
            StringAssert.Contains("Measured:", label);
            StringAssert.Contains("MuJoCo", label);
            StringAssert.Contains("physics steps/s", label);
            StringAssert.Contains("not a training run", label);
        }
    }
}
