using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoKingHill.Tests
{
    /// <summary>The demo (attract) mode stays off for the rest of the suite, so a test that idles on the menu is not
    /// taken over; ShowcasePlayTests turns it on for its own test.</summary>
    [SetUpFixture]
    public class ShowcaseSuite
    {
        [OneTimeSetUp] public void Up() => DemoMode.Suppressed = true;
        [OneTimeTearDown] public void Down() => DemoMode.Suppressed = false;
    }

    /// <summary>
    /// The showcase features in the real game scene, run, not read: every reward penalty term is charged in a live
    /// match (RewardMeter), the Tab overlay shows FPS, episode reward, inference latency and the curriculum stage and
    /// its benchmark button measures the local simulation speed (SimBenchmark), and the demo mode starts after 30 s
    /// idle on the menu, cycles showcase shots and ends on a key press.
    /// </summary>
    public class ShowcasePlayTests
    {
        static IEnumerator Until(System.Func<bool> ok, float seconds, string what)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!ok()) { if (Time.realtimeSinceStartup > end) Assert.Fail("timed out waiting for " + what); yield return null; }
        }

        static IEnumerator Menu()
        {
            SceneManager.LoadScene("Game");
            yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60, "the menu");
        }

        [UnityTest, Timeout(180000)]
        public IEnumerator EveryRewardPenaltyTermIsCharged()
        {
            using var guard = new ConsoleGuard();
            yield return Menu();
            var d = MatchDirector.Instance;
            var replay = Object.FindAnyObjectByType<ReplaySystem>(); if (replay != null) replay.autoDelay = 999f;
            d.QuickMatch(); d.SkipIntro();
            Assert.AreEqual(MatchState.Fight, d.State);
            RewardMeter meter = null;
            yield return Until(() => (meter = Object.FindAnyObjectByType<RewardMeter>()) != null && meter.Ready, 20, "the reward meter");
            var first = d.bodies[0];
            // every brain in the match printed its one inference line (InferenceLog)
            for (int i = 0; i < d.Playing; i++)
                if (d.bodies[i].policy != null) CollectionAssert.Contains(InferenceLog.Brains, d.bodies[i].policy.name, "inference line of seat " + (i + 1));
            yield return Until(() => meter.Of(first) != null && meter.Of(first).Steps >= 150, 90, "150 policy steps of seat 1");
            var w = RewardWeights.Current;
            Assert.Less(w.jointTorque, 0f); Assert.Less(w.angVelXy, 0f); Assert.Less(w.actionRate, 0f); Assert.Less(w.feetSlip, 0f); Assert.Less(w.jointVel, 0f);
            var sums = new double[RewardMeter.TermCount];
            for (int i = 0; i < d.Playing; i++) { var e = meter.Of(d.bodies[i]); if (e == null) continue; for (int t = 0; t < sums.Length; t++) sums[t] += e.Sum[t]; }
            var line = new System.Text.StringBuilder("penalties charged over " + d.Playing + " fighters:");
            for (int t = 0; t < sums.Length; t++) line.Append(' ').Append(RewardMeter.TermNames[t]).Append(' ').Append(sums[t].ToString("0.0000"));
            for (int i = 0; i < d.Playing; i++)
            {
                var e = meter.Of(d.bodies[i]); if (e == null || e.Steps == 0) continue;
                line.Append($"\n  seat {i + 1} {d.bodies[i].body}: {e.Steps} steps, mean per step torque² {e.Raw[0] / e.Steps:0} ang vel² {e.Raw[1] / e.Steps:0.00} action rate {e.Raw[2] / e.Steps:0.00} foot slip {e.Raw[3] / e.Steps:0.00} m/s (max {e.MaxSlip:0.0} at step {e.MaxSlipStep}, {e.SlipSteps} steps with a foot down) joint vel² {e.Raw[4] / e.Steps:0.0}");
            }
            Debug.Log(line.ToString());
            for (int t = 0; t < sums.Length; t++) Assert.Less(sums[t], 0.0, RewardMeter.TermNames[t] + " was never charged");
            for (int i = 0; i < d.Playing; i++) { var e = meter.Of(d.bodies[i]); if (e != null) Assert.Less(e.MaxSlip, 2 * RewardMeter.TeleportSpeed, "foot slip counted a teleport"); }
            var seat0 = meter.Of(first);
            Assert.Less(seat0.Total, 0.0, "seat 1's episode reward");
            double parts = 0; for (int t = 0; t < RewardMeter.TermCount; t++) parts += seat0.Sum[t];
            Assert.AreEqual(seat0.Total, parts, 1e-9, "the total is the sum of the terms");
            // gaze tracking (render only: the physics has no neck): heads turn toward their targets during the fight
            var life = Object.FindAnyObjectByType<LifeSigns>();
            Assert.IsNotNull(life, "LifeSigns");
            float maxYaw = 0f; for (int i = 0; i < d.Playing; i++) maxYaw = Mathf.Max(maxYaw, Mathf.Abs(life.LookOf(d.bodies[i]).x));
            Debug.Log($"largest head yaw toward a target: {maxYaw:0.0} deg");
            Assert.Greater(maxYaw, 1f, "no head turned toward its target");
            d.EnterMenu();
            guard.AssertClean("penalty terms");
        }

        [UnityTest, Timeout(180000)]
        public IEnumerator TabOverlayShowsTheReadoutAndBenchmarksTheSimulation()
        {
            using var guard = new ConsoleGuard();
            yield return Menu();
            var d = MatchDirector.Instance; var hud = Object.FindAnyObjectByType<HudView>();
            var replay = Object.FindAnyObjectByType<ReplaySystem>(); if (replay != null) replay.autoDelay = 999f;
            d.QuickMatch(); d.SkipIntro();
            yield return Until(() => d.RoundTime >= 1.0, 60, "one second of fighting");
            Assert.IsFalse(hud.OverlayShown);
            hud.ToggleOverlay();
            yield return new WaitForSecondsRealtime(0.6f);
            Assert.IsTrue(hud.OverlayShown);
            string text = hud.OverlayText; Debug.Log("[overlay]\n" + text);
            StringAssert.Contains("FPS", text); StringAssert.Contains("Inference latency", text);
            StringAssert.Contains("Episode reward", text); StringAssert.Contains("Curriculum", text);
            StringAssert.DoesNotContain("Inference latency -", text);
            // the game's own physics steps while the benchmark runs (RoundTime is no measure: the round may end and the
            // next one start from 0 during the 3 s benchmark when the CPU is busy)
            long gameSteps = 0;
            System.EventHandler<Mujoco.MjStepArgs> count = (_, _) => gameSteps++;
            Mujoco.MjScene.Instance.postUpdateEvent += count;
            hud.BenchmarkTraining();
            Assert.IsTrue(hud.Benchmark.Running);
            yield return Until(() => !hud.Benchmark.Running, 30, "the benchmark");
            Assert.Greater(hud.Benchmark.StepsPerSecond, 0.0);
            Assert.Greater(hud.Benchmark.SamplesPerSecond, 0.0);
            yield return new WaitForSecondsRealtime(0.6f);
            StringAssert.Contains("physics steps/s", hud.OverlayText);
            StringAssert.Contains("not a training run", hud.OverlayText);
            Mujoco.MjScene.Instance.postUpdateEvent -= count;
            Debug.Log($"game physics steps during the benchmark: {gameSteps}");
            Assert.Greater(gameSteps, 100, "the game kept simulating during the benchmark");
            StringAssert.Contains("Benchmark on this device: CPU ", hud.OverlayText);
            StringAssert.Contains("Measured:", hud.OverlayText);
            hud.ToggleOverlay(); Assert.IsFalse(hud.OverlayShown);
            d.EnterMenu();
            guard.AssertClean("tab overlay");
        }

        [UnityTest, Timeout(900000)]
        public IEnumerator DemoModeStartsAfterThirtySecondsIdleAndInputEndsIt()
        {
            using var guard = new ConsoleGuard();
            DemoMode.Suppressed = false;
            Keyboard kb = null;
            try
            {
                yield return Menu();
                var d = MatchDirector.Instance; var hud = Object.FindAnyObjectByType<HudView>();
                var replay = Object.FindAnyObjectByType<ReplaySystem>(); if (replay != null) replay.autoDelay = 999f;
                Assert.AreEqual(30f, DemoSettings.Current.idleSeconds, 1e-3);
                float t0 = Time.realtimeSinceStartup;
                DemoMode demo = null;
                // 30 s of idle frames; frames over 0.25 s do not count (IdleClock), so a loaded machine needs longer: fail
                // only when the idle clock stops advancing for 60 s (or after 600 s in all)
                float lastIdle = -1f, lastMove = Time.realtimeSinceStartup;
                while ((demo = Object.FindAnyObjectByType<DemoMode>()) == null || !demo.Active)
                {
                    float idle = demo != null ? demo.IdleSeconds : 0f;
                    if (idle > lastIdle + 0.01f) { lastIdle = idle; lastMove = Time.realtimeSinceStartup; }
                    if (Time.realtimeSinceStartup - lastMove > 60f) Assert.Fail($"the idle clock stopped at {lastIdle:0.0} s for 60 s");
                    if (Time.realtimeSinceStartup - t0 > 600f) Assert.Fail($"no demo after 600 s (idle clock {lastIdle:0.0} s)");
                    yield return null;
                }
                float waited = Time.realtimeSinceStartup - t0;
                Debug.Log($"demo on after {waited:0.0} s idle");
                Assert.GreaterOrEqual(waited, 29.5f);
                Assert.AreEqual(MatchState.Fight, d.State);
                Assert.IsTrue(hud.HudHiddenForDemo); Assert.IsTrue(hud.DemoChipShown);
                for (int i = 0; i < d.Playing; i++) Assert.IsNotNull(d.bodies[i].policy, "every demo fighter is a trained brain");
                int shots = demo.ShotsShown;
                // the dwell counts frames up to 0.25 s only (IdleClock): on a loaded machine (16 bodies, other Unity jobs)
                // many frames are longer, so the cap is generous; the dwell itself is pinned by ShowcaseClockTests
                yield return Until(() => demo.ShotsShown > shots, DemoSettings.Current.dwellSeconds + 60, "the next showcase shot");
                kb = InputSystem.AddDevice<Keyboard>();
                yield return null;
                InputSystem.QueueStateEvent(kb, new KeyboardState(Key.Escape));
                yield return Until(() => !demo.Active, 5, "a key press to end the demo");
                InputSystem.QueueStateEvent(kb, new KeyboardState());
                yield return Until(() => d.State == MatchState.Menu, 10, "the menu after the demo");
                Assert.IsFalse(hud.HudHiddenForDemo); Assert.IsFalse(hud.DemoChipShown);
                guard.AssertClean("demo mode");
            }
            finally
            {
                DemoMode.Suppressed = true;
                if (kb != null) InputSystem.RemoveDevice(kb);
            }
        }
    }
}
