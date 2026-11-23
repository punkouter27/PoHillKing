using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PoKingHill.Tests
{
    /// <summary>
    /// End-to-end PlayMode verification of the new gameplay, camera, batching, tournament,
    /// atmosphere, and HUD features in Game.unity, saving visual verification screenshots
    /// to Temp/features-261114 (the reviewed captures are in SCREENSHOTS/features-261114).
    /// </summary>
    public class FeaturePlayModeTests
    {
        static IEnumerator Until(System.Func<bool> ok, float seconds, string what)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!ok())
            {
                if (Time.realtimeSinceStartup > end) Assert.Fail("timed out waiting for " + what);
                yield return null;
            }
        }

        static IEnumerator Snap(string dir, string name)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            yield return null;
            var cam = Camera.main;
            if (cam == null) yield break;
            var prev = cam.targetTexture;
            var rt = RenderTexture.GetTemporary(720, 1280, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(720, 1280, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 720, 1280), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = prev;
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(tex);
            }
            yield return null;
        }

        [UnityTest, Timeout(240000)]
        public IEnumerator FeaturesRuntimeBatchingHeatsCamerasAndScreenshots()
        {
            var prevAtmo = GameSettings.Atmosphere;
            var prevMod = GameSettings.Modifier;
            var prevIntro = GameSettings.Intro;
            // Captures go to the project's Temp folder: tests never write into tracked files (SCREENSHOTS/ holds the reviewed set).
            string shotDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", "features-261114");

            try
            {
                using var guard = new ConsoleGuard();
                SceneManager.LoadScene("Game");
                yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60, "the menu");

                var d = MatchDirector.Instance;
                var hud = Object.FindAnyObjectByType<HudView>();
                Assert.IsNotNull(hud);
                Assert.IsNotNull(PolicyBatcher.Instance);
                Assert.IsTrue(PolicyBatcher.Active);
                var camExtras = Object.FindAnyObjectByType<CameraExtras>();
                Assert.IsNotNull(camExtras);
                var atmo = Object.FindAnyObjectByType<AtmosphereController>();
                Assert.IsNotNull(atmo);
                var crowd = Object.FindAnyObjectByType<CrowdRing>();
                Assert.IsNotNull(crowd);

                // 1. Capture Menu (Play tab) & Settings (with new Atmosphere / Modifiers / Daily) tabs cleanly
                GameSettings.Atmosphere = AtmosphereMode.Sunset;
                atmo.RefreshTarget();
                hud.Tab = 0;
                hud.SimulateScreen(1080, 2340);
                yield return null;
                CollectionAssert.IsEmpty(hud.LayoutProblems());
                hud.ClearSimulation();
                yield return Snap(shotDir, "01_menu_play.png");

                hud.Tab = 1;
                hud.Section = 1;
                hud.SimulateScreen(1080, 2340);
                yield return null;
                CollectionAssert.IsEmpty(hud.LayoutProblems());
                hud.ClearSimulation();
                yield return Snap(shotDir, "02_menu_settings.png");

                hud.Tab = 2;
                hud.SimulateScreen(1080, 2340);
                yield return null;
                CollectionAssert.IsEmpty(hud.LayoutProblems());
                hud.ClearSimulation();
                yield return Snap(shotDir, "03_menu_agents.png");
                hud.Tab = 0;

                // 2. QuickMatch -> Intro with Prediction + FlagBadge + Bio
                GameSettings.Intro = true;
                GameSettings.Modifier = ArenaModifier.Standard;
                d.pauseBetweenRounds = 1f;
                var replay = Object.FindAnyObjectByType<ReplaySystem>();
                if (replay != null) replay.autoDelay = 999f;

                d.QuickMatch();
                Assert.AreEqual(MatchState.Intro, d.State);
                hud.SimulateScreen(1080, 2340);
                yield return null;
                yield return null;
                CollectionAssert.IsEmpty(hud.LayoutProblems());
                hud.ClearSimulation();
                yield return Snap(shotDir, "04_intro_predict_bio.png");

                // 3. SkipIntro -> Fight: verify PolicyBatcher batching, CrowdRing, Spectator Box Throw, POV toggle
                d.SkipIntro();
                Assert.AreEqual(MatchState.Fight, d.State);
                yield return Until(() => PolicyBatcher.Instance.LastBatchCount > 0 && d.RoundTime >= 0.4, 20, "batched inference ticks");
                Assert.Greater(PolicyBatcher.Instance.LastBatchCount, 0);
                Assert.Less(PolicyBatcher.Instance.LastScheduleCount, d.Playing);

                Assert.IsTrue(d.ThrowSpectatorBox(), "First spectator box throw should succeed");
                Assert.IsFalse(d.ThrowSpectatorBox(), "Second spectator box throw should respect cooldown");
                yield return new WaitForSecondsRealtime(0.25f);
                yield return Snap(shotDir, "05_fight_batched_crowd.png");

                camExtras.TogglePov();
                Assert.IsTrue(camExtras.PovEnabled);
                yield return new WaitForSecondsRealtime(0.25f);
                yield return Snap(shotDir, "06_fight_pov.png");
                camExtras.TogglePov();

                // 4. Heats 4x4 Bracket & Daily Seeded Match smoke transitions
                d.EnterMenu();
                yield return Until(() => d.State == MatchState.Menu, 10, "return to menu");
                GameSettings.Intro = false;
                d.StartHeatsTournament();
                Assert.AreEqual(MatchState.Fight, d.State);
                Assert.IsTrue(d.InHeatsTournament);
                Assert.AreEqual(4, d.Playing);

                // the tournament standard: FFA with exactly 8, and the 16-slot bracket (1v1 bouts, saved to a scratch file)
                d.EnterMenu();
                yield return Until(() => d.State == MatchState.Menu, 10, "menu before FFA 8");
                Assert.IsTrue(d.StartFfa());
                Assert.AreEqual(MatchDirector.FfaPlayers, d.Playing);
                d.EnterMenu();
                yield return Until(() => d.State == MatchState.Menu, 10, "menu before the bracket");
                EliminationBracket.PathOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "koth_bracket_playmode.json");
                d.StartTournament();
                Assert.IsTrue(d.InTournament);
                Assert.AreEqual(MatchState.Fight, d.State);
                Assert.AreEqual(2, d.Playing);
                Assert.AreNotEqual(d.AgentOf(0), d.AgentOf(1), "a bout is two different agents");

                d.EnterMenu();
                yield return Until(() => d.State == MatchState.Menu, 10, "menu before daily");
                Assert.IsTrue(d.StartDailyMatch());
                Assert.AreEqual(MatchState.Fight, d.State);

                d.EnterMenu();
                yield return Until(() => d.State == MatchState.Menu, 10, "menu cleanup");
                guard.AssertClean("all 20 new features runtime flow");
            }
            finally
            {
                GameSettings.Atmosphere = prevAtmo;
                GameSettings.Modifier = prevMod;
                GameSettings.Intro = prevIntro;
                EliminationBracket.DeleteSave();
                EliminationBracket.PathOverride = null;
            }
        }
    }
}
