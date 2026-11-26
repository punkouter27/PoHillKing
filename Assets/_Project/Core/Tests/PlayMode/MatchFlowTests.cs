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
            Assert.AreEqual(MatchDirector.FfaPlayers, d.Playing);
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
            d.StartFreeForAll(MatchDirector.PlayersPerMatch); d.SkipIntro();
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
                    d.EnterMenu(); d.StartFreeForAll(MatchDirector.PlayersPerMatch); d.SkipIntro();
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

        /// <summary>The 8-player bracket: seats 01 v 02 duel first (two bodies, the rest parked); the duel always names a winner,
        /// who is entered into semi-final 1, and the next match is seats 03 v 04.</summary>
        [UnityTest, Timeout(300000)]
        public IEnumerator EightPlayerBracketPlaysDuelsAndAdvancesWinners()
        {
            SceneManager.LoadScene("Game");
            yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60, "the menu");
            var d = MatchDirector.Instance;
            bool intro = GameSettings.Intro;
            try
            {
                GameSettings.Intro = false;
                var replay = Object.FindAnyObjectByType<ReplaySystem>(); if (replay != null) replay.autoDelay = 999f;
                var seats = (int[])d.playerSlots.Clone();
                d.StartTournament();
                Assert.IsTrue(d.InTournament);
                Assert.AreEqual(7, d.Bracket.Count);
                Assert.AreEqual(MatchState.Fight, d.State);
                Assert.AreEqual(2, d.Playing, "a bracket match is a duel");
                Assert.AreEqual(seats[0], d.AgentOf(0)); Assert.AreEqual(seats[1], d.AgentOf(1));

                d.pauseBetweenRounds = 600f; Time.timeScale = 6f;
                yield return Until(() => d.State == MatchState.Result, 240, "the first duel to end");
                Time.timeScale = 1f;
                var first = d.Bracket[0];
                Assert.GreaterOrEqual(d.LastResult.Winner, 0, "a bracket duel always has a winner on the result screen");
                Assert.AreEqual(d.LastResult.Winner, first.winner, "body 0 is seat 01, body 1 seat 02");
                Assert.AreEqual(first.winner, d.Bracket[4].a, "the winner goes on to semi-final 1");
                Assert.AreEqual("", d.TournamentChampion);
                d.Continue();
                yield return Until(() => d.State == MatchState.Fight, 20, "the next bracket match");
                Assert.AreEqual(2, d.Playing);
                Assert.AreEqual(1, d.BracketIndex, "no replays: the second duel is next");
                Assert.AreEqual(seats[2], d.AgentOf(0)); Assert.AreEqual(seats[3], d.AgentOf(1));
                d.EnterMenu();
                Assert.IsFalse(d.InTournament);
            }
            finally { GameSettings.Intro = intro; Time.timeScale = 1f; }
        }

        /// <summary>A whole 8-player bracket at 4x speed (a duel nobody wins by a push ends when the sea arrives, or at a 1.5 s
        /// full time without a sea, and goes to whoever is nearest the centre): all 7 duels name a winner and the tournament
        /// ends with one champion.</summary>
        [UnityTest, Timeout(2400000)]
        public IEnumerator EightPlayerBracketAlwaysEndsWithOneChampion()
        {
            SceneManager.LoadScene("Game");
            yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60, "the menu");
            var d = MatchDirector.Instance;
            bool intro = GameSettings.Intro; float secs = d.roundSeconds;
            try
            {
                GameSettings.Intro = false;
                var replay = Object.FindAnyObjectByType<ReplaySystem>(); if (replay != null) replay.autoDelay = 999f;
                d.roundSeconds = 1.5f; d.pauseBetweenRounds = 600f; Time.timeScale = 4f;   // each result waits for Continue
                d.StartTournament();
                int decided = 0, seen = -1; string champion = "";
                // stuck = no duel decided for 150 s of wall time (one 1.5 s duel at 4x takes seconds; a busy machine slows the
                // whole bracket, not one duel), so the cap is per duel rather than one window for all 7
                const float StallSeconds = 150f;
                float lastProgress = Time.realtimeSinceStartup, t0 = lastProgress;
                while (!(decided == 7 && d.State == MatchState.Menu))
                {
                    if (Time.realtimeSinceStartup - lastProgress > StallSeconds)
                        Assert.Fail($"stuck after {decided} duels ({StallSeconds:0} s without a result): state {d.State}, bracket {d.BracketIndex}, status '{d.Status}'");
                    if (d.State == MatchState.Result && d.InTournament && d.BracketIndex != seen)
                    {
                        seen = d.BracketIndex; decided++; lastProgress = Time.realtimeSinceStartup;
                        Assert.GreaterOrEqual(d.LastResult.Winner, 0, $"duel {seen + 1} has a winner");
                        Assert.GreaterOrEqual(d.Bracket[seen].winner, 0);
                        champion = d.TournamentChampion;
                        d.Continue();
                    }
                    yield return null;
                }
                Assert.AreEqual(7, decided);
                Assert.IsNotEmpty(champion, "one champion after the final");
                Debug.Log($"bracket champion: {champion} ({Time.realtimeSinceStartup - t0:0} s wall time)");
            }
            finally { GameSettings.Intro = intro; d.roundSeconds = secs; Time.timeScale = 1f; }
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
