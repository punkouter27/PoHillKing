using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoKingHill.Tests
{
    /// <summary>Shoves and jabs (MatchDirector.Shoves) in the real scene: with GameSettings.Strikes on, an 8-fighter quick
    /// match throws strikes and some land; with it off, none are thrown.</summary>
    public class StrikeTests
    {
        static IEnumerator Until(System.Func<bool> ok, float seconds, string what)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!ok()) { if (Time.realtimeSinceStartup > end) Assert.Fail("timed out waiting for " + what); yield return null; }
        }

        static IEnumerator Fight(bool strikes, System.Action<MatchDirector> done)
        {
            GameSettings.Strikes = strikes;
            SceneManager.LoadScene("Game");
            yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60, "the menu");
            var d = MatchDirector.Instance;
            Time.timeScale = 4f;
            d.QuickMatch(); d.SkipIntro();
            Assert.AreEqual(MatchState.Fight, d.State);
            yield return Until(() => d.State != MatchState.Fight || d.RoundTime >= 10.0, 120, "10 simulated seconds");
            Time.timeScale = 1f;
            done(d);
        }

        [UnityTest, Timeout(240000)]
        public IEnumerator FightersInReachThrowAndLandStrikes()
        {
            bool was = GameSettings.Strikes;
            try
            {
                int thrown = 0, landed = 0;
                yield return Fight(true, d => { thrown = d.StrikesThrown; landed = d.StrikesLanded; });
                Debug.Log($"strikes on: {thrown} thrown, {landed} landed in an 8-fighter quick match");
                Assert.Greater(thrown, 0, "fighters within reach should throw strikes");
                Assert.Greater(landed, 0, "some strikes should reach a torso");
                yield return Fight(false, d => { thrown = d.StrikesThrown; });
                Assert.AreEqual(0, thrown, "no strikes with the setting off");
            }
            finally { GameSettings.Strikes = was; Time.timeScale = 1f; }
        }
    }
}
