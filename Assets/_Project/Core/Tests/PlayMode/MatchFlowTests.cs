using System.Collections;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoKingHill.Tests
{
    /// <summary>The game's state flow in the real scene: Menu -> (Intro ->) Fight -> Result -> Menu, with no error or
    /// exception logged (the test runner fails on any unexpected error log), and in a plain quick match no warning either. Physics runs at 4x speed to keep the test
    /// short. The intro (tale of the tape, GameSettings.Intro) is skipped where the test is about the rounds.</summary>
    public class MatchFlowTests
    {
        static IEnumerator Until(System.Func<bool> ok, float seconds, string what)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!ok()) { if (Time.realtimeSinceStartup > end) Assert.Fail("timed out waiting for " + what); yield return null; }
        }

        [UnityTest, Timeout(240000)]
        public IEnumerator MenuFightResultMenu()
        {
            using var guard = new ConsoleGuard(allWarnings: true);      // a normal match logs no warning at all
            SceneManager.LoadScene("Game");
            yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60, "the menu");
            var d = MatchDirector.Instance;
            d.pauseBetweenRounds = 1f; Time.timeScale = 4f;
            var replay = Object.FindAnyObjectByType<ReplaySystem>(); if (replay != null) replay.autoDelay = 999f;
            d.QuickMatch(); d.SkipIntro();
            Assert.AreEqual(MatchState.Fight, d.State);
            Assert.AreEqual(MatchDirector.PlayersPerMatch, d.Playing);
            BroadcastCamera.FollowSeat = 0;                             // a tapped badge: the camera follows seat 0
            yield return Until(() => d.State == MatchState.Result, 150, "the round to end");
            Assert.IsNotNull(d.LastResult.Agents); Assert.AreEqual(d.Playing, d.LastResult.Agents.Length);
            d.Continue();
            yield return Until(() => d.State == MatchState.Menu, 20, "the menu after one round");
            Time.timeScale = 1f;
            guard.AssertClean("a quick match");
        }

        [UnityTest, Timeout(120000)]
        public IEnumerator CenteredSixteenPlayerStartKeepsMostFightersStandingForTwoSeconds()
        {
            SceneManager.LoadScene("Game");
            yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60, "the menu");
            var d = MatchDirector.Instance;
            d.QuickMatch(); d.SkipIntro();
            Assert.AreEqual(MatchState.Fight, d.State);
            Assert.AreEqual(MatchDirector.PlayersPerMatch, d.Playing);

            var bio = Object.FindAnyObjectByType<Biometrics>();
            Assert.NotNull(bio);
            Assert.IsTrue(Mujoco.MjScene.Instance.SyncTransformsOncePerFrame);
            int leastStanding = d.Playing;
            for (int sample = 0; sample < 4; sample++)
            {
                double target = (sample + 1) * 0.5;
                yield return Until(() => d.RoundTime >= target, 45, $"simulation to reach {target:F1}s");
                int standing = 0;
                for (int i = 0; i < d.Playing; i++)
                {
                    var body = d.bodies[i];
                    var vitals = bio.Of(body);
                    var pos = body.PelvisTransform.position;
                    double radius = System.Math.Sqrt(pos.x * pos.x + pos.z * pos.z);
                    double height = pos.y - body.Spec.arena.Ground(radius);
                    if (!d.IsOut(i) && vitals.upright > 0.9f && height > 0.85 * body.Spec.key_root_z) standing++;
                }
                leastStanding = Mathf.Min(leastStanding, standing);
                Debug.Log($"Centered 16-player start at {d.RoundTime:F3} simulated seconds: {standing}/16 upright");
            }

            Assert.GreaterOrEqual(leastStanding, MatchDirector.PlayersPerMatch / 2, "at least half the fighters should stay physically upright during the first two simulated seconds");
            Assert.AreEqual(MatchState.Fight, d.State, "the match should still be active");
        }

        [UnityTest, Explicit("Ten-game observation run; launch explicitly with the Unity test filter."), Timeout(1800000)]
        public IEnumerator ObserveTenSixteenPlayerGames()
        {
            SceneManager.LoadScene("Game");
            yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60, "the menu");
            var d = MatchDirector.Instance;
            var bio = Object.FindAnyObjectByType<Biometrics>();
            Assert.NotNull(bio);
            var replay = Object.FindAnyObjectByType<ReplaySystem>();
            if (replay != null) replay.autoDelay = 999f;
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "ten-game-observation.csv"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "game,winner,body,end,seconds,first_down_seconds,down_events,recoveries,remaining,upright_0.5s,upright_1s,upright_2s,upright_5s\n");
            float previousScale = Time.timeScale;
            var previousRandom = Random.state;
            bool previousIntro = GameSettings.Intro;
            int downs = 0, recoveries = 0;
            double firstDown = -1;
            System.Action<PolicyRunner> onDown = r => { downs++; if (firstDown < 0) firstDown = d.RoundTime; };
            System.Action<PolicyRunner> onRecovered = r => recoveries++;
            MatchEvents.Down += onDown;
            MatchEvents.Recovered += onRecovered;
            try
            {
                GameSettings.Intro = false;
                Time.timeScale = 4f;
                for (int game = 1; game <= 10; game++)
                {
                    Random.InitState(261113 + game);
                    downs = recoveries = 0; firstDown = -1;
                    d.EnterMenu(); d.QuickMatch(); d.SkipIntro();
                    d.pauseBetweenRounds = 999f;
                    Assert.AreEqual(MatchDirector.PlayersPerMatch, d.Playing);
                    var times = new[] { 0.5, 1.0, 2.0, 5.0 };
                    var upright = new[] { -1, -1, -1, -1 };
                    int sample = 0;
                    float deadline = Time.realtimeSinceStartup + 150f;
                    while (d.State == MatchState.Fight)
                    {
                        Assert.Less(Time.realtimeSinceStartup, deadline, $"Game {game} did not finish within 150 real seconds");
                        if (sample < times.Length && d.RoundTime >= times[sample])
                        {
                            int standing = 0;
                            for (int i = 0; i < d.Playing; i++)
                            {
                                var body = d.bodies[i];
                                var vitals = bio.Of(body);
                                Assert.NotNull(vitals);
                                var pos = body.PelvisTransform.position;
                                double radius = System.Math.Sqrt(pos.x * pos.x + pos.z * pos.z);
                                double height = pos.y - body.Spec.arena.Ground(radius);
                                if (!d.IsOut(i) && vitals.upright > 0.9f && height > 0.85 * body.Spec.key_root_z) standing++;
                            }
                            upright[sample] = standing;
                            Debug.Log($"[TenGames] game={game} sim={d.RoundTime:F3}s upright={standing}/16 down={downs} out={16 - d.Alive.Count}");
                            sample++;
                        }
                        yield return null;
                    }
                    Assert.AreEqual(MatchState.Result, d.State);
                    var result = d.LastResult;
                    Assert.AreEqual(16, result.Agents.Length);
                    string bodyName = result.Winner >= 0 ? d.bodies[result.Winner].body : "none";
                    string row = string.Join(",", game, $"\"{result.WinnerName.Replace("\"", "\"\"")}\"", bodyName, result.End,
                        result.Seconds.ToString("F3", CultureInfo.InvariantCulture), firstDown.ToString("F3", CultureInfo.InvariantCulture),
                        downs, recoveries, d.Alive.Count, upright[0], upright[1], upright[2], upright[3]);
                    File.AppendAllText(path, row + "\n");
                    Debug.Log("[TenGames] RESULT " + row);
                }
            }
            finally
            {
                MatchEvents.Down -= onDown;
                MatchEvents.Recovered -= onRecovered;
                Time.timeScale = previousScale;
                Random.state = previousRandom;
                GameSettings.Intro = previousIntro;
            }
        }

        [UnityTest, Timeout(120000)]
        public IEnumerator ReturningToTheMenuMidRoundIsClean()
        {
            SceneManager.LoadScene("Game");
            yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60, "the menu");
            var d = MatchDirector.Instance;
            d.Launch(); yield return new WaitForSeconds(1f);
            d.EnterMenu();                    // from the intro or the round, whichever is on
            Assert.AreEqual(MatchState.Menu, d.State);
            yield return null;
            d.QuickMatch(); d.SkipIntro(); yield return new WaitForSeconds(0.5f);
            Assert.AreEqual(MatchState.Fight, d.State);
            d.NewRound();                     // reset mid-round
            Assert.AreEqual(MatchState.Fight, d.State);
        }

        /// <summary>The tale of the tape plays when it is on: Intro with every fighter standing and no round clock, then the
        /// first round on its own, brains armed. The setting is restored afterwards.</summary>
        [UnityTest, Timeout(120000)]
        public IEnumerator IntroLeadsIntoTheFirstRound()
        {
            bool was = GameSettings.Intro;
            try
            {
                GameSettings.Intro = true;
                SceneManager.LoadScene("Game");
                yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60, "the menu");
                var d = MatchDirector.Instance;
                d.QuickMatch();
                Assert.AreEqual(MatchState.Intro, d.State);
                Assert.AreEqual(0.0, d.RoundTime, "no round clock during the intro");
                Assert.IsNull(d.bodies[0].policy, "brains stand passively during the intro");
                yield return Until(() => d.IntroFeatured >= 0, 10, "the first fighter's card");
                yield return Until(() => d.State == MatchState.Fight, 20, "the round after the intro");
                Assert.IsNotNull(d.bodies[0].policy, "brains armed for the round");
            }
            finally { GameSettings.Intro = was; }
        }

        [UnityTest, Timeout(120000)]
        public IEnumerator CharacterVoiceMatrixHasProceduralFallbacks()
        {
            SceneManager.LoadScene("Game");
            yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60, "the menu");
            var pool = Object.FindAnyObjectByType<SoundPool>();
            Assert.IsNotNull(pool);
            Assert.AreEqual(48, pool.VoiceClipCount);
            int played = pool.Played, fallbacks = pool.VoiceFallbacks;
            pool.PlayVoice("kim", SoundPool.Emotion.Pain, Vector3.zero, 0.5f);
            pool.PlayVoice("unlisted", SoundPool.Emotion.Fear, Vector3.zero, 0.5f);
            Assert.AreEqual(played + 2, pool.Played);
            Assert.AreEqual(fallbacks + 1, pool.VoiceFallbacks);
            // the standard's 5 x 5 matrix: no WAVs yet, so every cell is synthesized once and cached
            int synth = pool.VoiceSynthesized;
            pool.Play("Kim", SoundPool.VoiceEmotion.Hurt, 3, Vector3.zero, 0.5f, "kim");
            pool.Play("Kim", SoundPool.VoiceEmotion.Hurt, 3, Vector3.zero, 0.5f, "kim");
            Assert.AreEqual(played + 4, pool.Played);
            Assert.LessOrEqual(pool.VoiceSynthesized, synth + 1, "a cell is synthesized at most once");
            Assert.AreSame(pool.VoiceClip("Kim", SoundPool.VoiceEmotion.Hurt, 3, "kim"), pool.VoiceClip("kim", SoundPool.VoiceEmotion.Hurt, 3));
        }
    }
}
