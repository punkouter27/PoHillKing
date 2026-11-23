using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mujoco;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoKingHill.Tests
{
    public class ExploreAppProbe
    {
        const string OutDir = @"C:\Users\punko\.gemini\antigravity\brain\c7c75514-7421-4831-bd2e-b9472912f731\scratch\explore";

        static IEnumerator Until(Func<bool> ok, float seconds, string what)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!ok())
            {
                if (Time.realtimeSinceStartup > end)
                {
                    Debug.LogWarning("[EXPLORE TIMEOUT] " + what);
                    yield break;
                }
                yield return null;
            }
        }

        static Button FindButton(VisualElement root, string exactText)
        {
            return root.Query<Button>().Where(b => b.resolvedStyle.display != DisplayStyle.None && b.text == exactText).First();
        }

        static List<Button> FindButtonsStartingWith(VisualElement root, string prefix)
        {
            return root.Query<Button>().Where(b => b.resolvedStyle.display != DisplayStyle.None && b.text != null && b.text.StartsWith(prefix)).ToList();
        }

        static void ClickButton(Button b)
        {
            if (b == null) return;
            var mi = typeof(Clickable).GetMethod("SimulateSingleClick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (mi != null) { mi.Invoke(b.clickable, new object[] { null, 0 }); return; }
            using var ev = ClickEvent.GetPooled();
            ev.target = b;
            b.SendEvent(ev);
        }

        static IEnumerator CaptureComposite(string name, UIDocument doc)
        {
            Directory.CreateDirectory(OutDir);
            string path = Path.Combine(OutDir, name);
            int w = 720, h = 1280;
            var cam = Camera.main;
            var ps = doc != null ? doc.panelSettings : null;
            RenderTexture camRt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            RenderTexture uiRt = ps != null ? RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32) : null;
            var prevCamRt = cam != null ? cam.targetTexture : null;
            var prevUiRt = ps != null ? ps.targetTexture : null;
            var camTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var uiTex = new Texture2D(w, h, TextureFormat.RGBA32, false);

            try
            {
                if (ps != null)
                {
                    var prevActive = RenderTexture.active;
                    RenderTexture.active = uiRt;
                    GL.Clear(true, true, new Color(0, 0, 0, 0));
                    RenderTexture.active = prevActive;
                    ps.targetTexture = uiRt;
                }
                yield return null;

                if (cam != null)
                {
                    cam.targetTexture = camRt;
                    cam.Render();
                    var prev = RenderTexture.active;
                    RenderTexture.active = camRt;
                    camTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    camTex.Apply();
                    RenderTexture.active = prev;
                }

                if (ps != null && uiRt != null)
                {
                    var prev = RenderTexture.active;
                    RenderTexture.active = uiRt;
                    uiTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    uiTex.Apply();
                    RenderTexture.active = prev;

                    Color32[] bg = camTex.GetPixels32();
                    Color32[] fg = uiTex.GetPixels32();
                    for (int i = 0; i < bg.Length; i++)
                    {
                        float a = fg[i].a / 255f;
                        if (a <= 0.001f) continue;
                        // Premultiplied or straight alpha blend check
                        bg[i].r = (byte)Mathf.Clamp(Mathf.RoundToInt(fg[i].r + bg[i].r * (1f - a)), 0, 255);
                        bg[i].g = (byte)Mathf.Clamp(Mathf.RoundToInt(fg[i].g + bg[i].g * (1f - a)), 0, 255);
                        bg[i].b = (byte)Mathf.Clamp(Mathf.RoundToInt(fg[i].b + bg[i].b * (1f - a)), 0, 255);
                        bg[i].a = 255;
                    }
                    camTex.SetPixels32(bg);
                    camTex.Apply();
                }

                File.WriteAllBytes(path, camTex.EncodeToPNG());
            }
            finally
            {
                if (cam != null) cam.targetTexture = prevCamRt;
                if (ps != null) ps.targetTexture = prevUiRt;
                RenderTexture.ReleaseTemporary(camRt);
                if (uiRt != null) RenderTexture.ReleaseTemporary(uiRt);
                UnityEngine.Object.Destroy(camTex);
                UnityEngine.Object.Destroy(uiTex);
            }
            yield return null;
        }

        [UnityTest, Explicit, Timeout(300000)]
        public IEnumerator PlayAndObserveEverything()
        {
            var issues = new List<string>();
            var logs = new List<string>();
            void OnLog(string msg, string stack, LogType t)
            {
                if (t == LogType.Error || t == LogType.Exception || t == LogType.Assert || t == LogType.Warning)
                    logs.Add($"{t}: {msg}");
            }
            Application.logMessageReceived += OnLog;

            bool prevIntro = GameSettings.Intro;
            bool prevReplays = GameSettings.Replays;
            bool prevDev = GameSettings.DevTools;
            bool prevLiveW = GameSettings.LiveWeather;
            AtmosphereMode prevAtmo = GameSettings.Atmosphere;
            ArenaModifier prevMod = GameSettings.Modifier;

            try
            {
                SceneManager.LoadScene("Game");
                yield return Until(() => MatchDirector.Instance != null && MatchDirector.Instance.State == MatchState.Menu, 60f, "menu load");
                var d = MatchDirector.Instance;
                var hud = UnityEngine.Object.FindAnyObjectByType<HudView>();
                var doc = hud.GetComponent<UIDocument>();
                var root = doc.rootVisualElement;
                var camExtras = UnityEngine.Object.FindAnyObjectByType<CameraExtras>();
                var atmo = UnityEngine.Object.FindAnyObjectByType<AtmosphereController>();
                var liveW = UnityEngine.Object.FindAnyObjectByType<LiveAtmosphere>();
                var crowd = UnityEngine.Object.FindAnyObjectByType<CrowdRing>();
                var replay = UnityEngine.Object.FindAnyObjectByType<ReplaySystem>();
                var share = UnityEngine.Object.FindAnyObjectByType<ShareBridge>();

                d.pauseBetweenRounds = 2.0f;
                GameSettings.Intro = true;
                GameSettings.Replays = true;
                GameSettings.DevTools = true;
                GameSettings.Atmosphere = AtmosphereMode.Sunset;
                atmo?.RefreshTarget();
                yield return null;

                // 1. MENU -> PLAY TAB EXPLORATION
                yield return CaptureComposite("01_menu_play.png", doc);
                var shuffleBtn = FindButton(root, "Shuffle 16");
                int beforeSlot0 = d.playerSlots[0];
                ClickButton(shuffleBtn);
                yield return null;
                Debug.Log($"[EXPLORE] Shuffle 16 clicked: slot0 {beforeSlot0} -> {d.playerSlots[0]}");

                var pickBtn = FindButton(root, "Cycle pick");
                ClickButton(pickBtn);
                yield return null;
                string pick1 = PredictionBook.CurrentPick;
                Debug.Log($"[EXPLORE] Cycle pick -> '{pick1}'");
                if (string.IsNullOrEmpty(pick1)) issues.Add("Cycle pick first click did not set a PredictionBook pick.");

                // 2. MENU -> AGENTS TAB EXPLORATION
                ClickButton(FindButton(root, "Agents"));
                yield return null;
                yield return CaptureComposite("02_menu_agents.png", doc);
                var nextBioBtn = FindButton(root, "Next bio");
                if (nextBioBtn == null) issues.Add("Agents tab missing 'Next bio' button.");
                else
                {
                    ClickButton(nextBioBtn);
                    yield return null;
                    yield return CaptureComposite("02b_menu_agents_next.png", doc);
                }

                // Check Wikipedia live fetch for Kim/Trump/Grandpa
                bool wikiDone = false; string wikiOut = "";
                hud.StartCoroutine(AgentBioService.EnsureSummary("kim", s => { wikiOut = s; wikiDone = true; }));
                yield return Until(() => wikiDone, 8f, "wiki fetch kim");
                Debug.Log($"[EXPLORE] Wiki bio kim ({wikiOut?.Length ?? 0} chars): {wikiOut}");

                // 3. MENU -> SETTINGS TAB EXPLORATION (ALL 4 SECTIONS)
                ClickButton(FindButton(root, "Settings"));
                yield return null;
                // Click Moon modifier button and verify MatchDirector modifier state
                ClickButton(FindButton(root, "Moon"));
                yield return null;
                yield return CaptureComposite("03_menu_settings_match.png", doc);
                var appliedField = typeof(MatchDirector).GetField("_appliedModifier", BindingFlags.Instance | BindingFlags.NonPublic);
                Debug.Log($"[EXPLORE] Settings clicked Moon -> GameSettings.Modifier={GameSettings.Modifier}, AppliedModifier={appliedField?.GetValue(d)}");

                ClickButton(FindButton(root, "Picture"));
                yield return null;
                ClickButton(FindButton(root, "Sunset"));
                // Toggle Live weather chip via GameSettings and test Open-Meteo real API
                GameSettings.LiveWeather = true;
                liveW?.RefreshNow();
                yield return new WaitForSecondsRealtime(2.5f);
                yield return CaptureComposite("04_menu_settings_picture.png", doc);
                Debug.Log($"[EXPLORE] Open-Meteo LiveWeather status='{liveW?.StatusText}', latest={liveW?.Latest.HasValue}");

                // Test Reset all settings!
                ClickButton(FindButton(root, "Advanced"));
                yield return null;
                yield return CaptureComposite("05_menu_settings_adv.png", doc);
                ClickButton(FindButton(root, "Reset all settings"));
                yield return null;
                Debug.Log($"[EXPLORE] After ResetAll(): Modifier={GameSettings.Modifier}, Atmo={GameSettings.Atmosphere}, LiveW={GameSettings.LiveWeather}, Haptics={GameSettings.Haptics}");
                if (GameSettings.Modifier != ArenaModifier.Standard || GameSettings.Atmosphere != AtmosphereMode.Auto || GameSettings.LiveWeather || !GameSettings.Haptics)
                    issues.Add($"GameSettings.ResetAll() missed new feature settings: Modifier={GameSettings.Modifier}, Atmo={GameSettings.Atmosphere}, LiveW={GameSettings.LiveWeather}, Haptics={GameSettings.Haptics}");

                // Restore Sunset & Moon for visually interesting Quick Match test
                GameSettings.DevTools = true;
                GameSettings.Intro = true;
                GameSettings.Replays = true;
                GameSettings.Atmosphere = AtmosphereMode.Sunset;
                GameSettings.Modifier = ArenaModifier.MoonGravity;
                atmo?.RefreshTarget();

                // 4. QUICK MATCH -> INTRO -> FIGHT (WITH SPEED 4X)
                var preMoonRatings = new float[d.roster.Length];
                for (int i = 0; i < d.roster.Length; i++) preMoonRatings[i] = Ladder.Rating(d.roster[i].name);

                ClickButton(FindButton(root, "Play"));
                yield return null;
                ClickButton(FindButton(root, "Quick match · 8 fighters"));
                yield return Until(() => d.State == MatchState.Intro, 10f, "intro state");
                yield return new WaitForSecondsRealtime(0.4f);
                yield return CaptureComposite("06_intro.png", doc);

                // Check if prediction pick is even one of the playing fighters in random QuickMatch!
                var playingNames = new List<string>();
                for (int i = 0; i < d.Playing; i++) playingNames.Add(d.AgentNameAtBody(i));
                Debug.Log($"[EXPLORE] Prediction='{PredictionBook.CurrentPick}' in playing? {playingNames.Contains(PredictionBook.CurrentPick)} | playingCount={playingNames.Count}");

                // Skip intro if needed, capture fight
                Time.timeScale = 4f;
                d.SkipIntro();
                yield return Until(() => d.State == MatchState.Fight && d.RoundTime >= 0.6, 15f, "fight active");
                Debug.Log($"[EXPLORE] During Fight (MoonGravity): AppliedModifier={appliedField?.GetValue(d)}, PolicyBatcher batches={PolicyBatcher.Instance?.LastBatchCount}, schedules={PolicyBatcher.Instance?.LastScheduleCount}, stepMs={d.StepMs:0.00}");

                // Click Throw box via UI
                var boxBtn = FindButton(root, "Throw box");
                if (boxBtn == null) issues.Add("Throw box button not found in Fight UI");
                else ClickButton(boxBtn);
                yield return null;
                yield return new WaitForSecondsRealtime(0.2f);
                yield return CaptureComposite("07_fight_box_actionbar.png", doc);

                // Check Debug Panel during Fight!
                var dbgBtn = root.Q<Button>(className: "icon-btn"); // Note bottom-bar has sliders
                hud.SetDebugTab(0);
                // Open debug via Toggle shortcut / reflection or 2 clicks? We'll invoke ToggleDebug via SendEvent on bottom-bar button
                var bottomBtns = root.Q(className: "bottom-bar").Query<Button>().ToList();
                if (bottomBtns.Count > 0) ClickButton(bottomBtns[0]);
                yield return null;
                yield return CaptureComposite("08_fight_debug.png", doc);
                if (bottomBtns.Count > 0) ClickButton(bottomBtns[0]); // close debug
                yield return null;

                // Wait for real Fight -> KO (check PiP KO Cam) -> Result / Replay
                bool sawPip = false;
                float waitEnd = Time.realtimeSinceStartup + 45f;
                while (d.State == MatchState.Fight && Time.realtimeSinceStartup < waitEnd)
                {
                    if (!sawPip && camExtras != null && camExtras.PipActive)
                    {
                        sawPip = true;
                        Debug.Log($"[EXPLORE] PiP KO Cam triggered! PipTexture={camExtras.PipTexture != null}");
                        yield return CaptureComposite("09_fight_pip_kocam.png", doc);
                    }
                    yield return null;
                }
                Debug.Log($"[EXPLORE] Ended fight -> state={d.State}, Winner={d.LastResult.Winner} ({d.LastResult.WinnerName}), How='{d.LastResult.How}', PipSeen={sawPip}");

                // Wait slightly for AutoReplay or trigger Replay
                yield return Until(() => d.State == MatchState.Replay || d.State == MatchState.Result, 10f, "result/replay");
                if (d.State == MatchState.Result && replay != null && replay.Available)
                {
                    replay.Play();
                    yield return null;
                }
                if (d.State == MatchState.Replay)
                {
                    yield return new WaitForSecondsRealtime(0.2f);
                    ClickButton(FindButton(root, "Photo"));
                    yield return null;
                    camExtras.OrbitPhoto(45f, 10f, 1.0f);
                    yield return CaptureComposite("10_replay_photomode.png", doc);

                    // Test Hide UI
                    ClickButton(FindButton(root, "Hide UI"));
                    yield return null;
                    yield return CaptureComposite("11_replay_photo_hideui.png", doc);
                    ClickButton(FindButton(root, "Show UI"));
                    yield return null;

                    // Test Snap button in Photo Mode! Note: ShareBridge uses WaitForEndOfFrame()!
                    string beforeSnap = share?.LastSavedPath;
                    ClickButton(FindButton(root, "Snap"));
                    yield return new WaitForSecondsRealtime(0.4f);
                    Debug.Log($"[EXPLORE] Photo Snap clicked: LastSavedPath='{share?.LastSavedPath}' (was '{beforeSnap}')");

                    ClickButton(FindButton(root, "Done"));
                    yield return null;
                }
                else
                {
                    Debug.Log($"[EXPLORE] Note: No Replay entered after round (State={d.State}, ReplayAvail={replay?.Available})");
                }

                // Wait for Result sheet
                ReplaySystem.StopActive();
                yield return Until(() => d.State == MatchState.Result, 10f, "result state");
                yield return new WaitForSecondsRealtime(0.3f);
                yield return CaptureComposite("12_result.png", doc);

                // Check Result Share KO highlight
                var shareBtn = FindButton(root, "Share KO highlight");
                Debug.Log($"[EXPLORE] Share KO button present: {shareBtn != null}, PredictionOutcome='{PredictionBook.LastOutcome}', Highlight='{replay?.HighlightSummary}'");
                for (int i = 0; i < d.roster.Length; i++)
                {
                    float nowR = Ladder.Rating(d.roster[i].name);
                    if (Mathf.Abs(nowR - preMoonRatings[i]) > 0.01f)
                        issues.Add($"Unranked MoonGravity match altered Elo for {d.roster[i].name}: {preMoonRatings[i]} -> {nowR}");
                }

                // 5. DAILY MATCH COMPLETION & LEADERBOARD SUBMISSION CHECK!
                d.EnterMenu();
                yield return Until(() => d.State == MatchState.Menu, 10f, "menu before daily");
                GameSettings.Intro = false;
                GameSettings.Replays = false;
                GameSettings.Modifier = ArenaModifier.Standard;
                int beforeDailyCount = DailyChallenge.TopToday().Count;
                ClickButton(FindButton(root, "Daily seed"));
                yield return Until(() => d.State == MatchState.Fight, 10f, "daily fight start");
                yield return Until(() => d.State == MatchState.Result, 60f, "daily fight finish");
                int afterDailyCount = DailyChallenge.TopToday().Count;
                Debug.Log($"[EXPLORE] Daily match finished! DailyChallenge.TopToday count before={beforeDailyCount}, after={afterDailyCount}");
                if (afterDailyCount == beforeDailyCount)
                    issues.Add("Completing a Daily Seed match does NOT record a score to DailyChallenge leaderboard (DailyChallenge.SubmitLocal is never called when a daily match finishes).");

                // 6. 4x4 HEATS BRACKET PROGRESSION CHECK!
                d.EnterMenu();
                yield return Until(() => d.State == MatchState.Menu, 10f, "menu before heats");
                ClickButton(FindButton(root, "4x4 Heats"));
                yield return Until(() => d.State == MatchState.Fight, 10f, "heat 1 start");
                Debug.Log($"[EXPLORE] Heats started: InHeats={d.InHeatsTournament}, HeatIndex={d.HeatIndex}/{d.HeatsBracket.Count}, Playing={d.Playing}");
                yield return Until(() => d.State == MatchState.Result, 60f, "heat 1 result");
                d.Continue();
                yield return Until(() => d.State == MatchState.Fight || d.State == MatchState.Menu, 10f, "heat 2 transition");
                Debug.Log($"[EXPLORE] After Heat 1 Continue(): State={d.State}, HeatIndex={d.HeatIndex}/{d.HeatsBracket.Count}, Status='{d.Status}'");

                // Check Shader.Find inclusion risk for builds!
                bool skyShader = Shader.Find("PoKingHill/SkyGradient") != null;
                bool crowdShader = Shader.Find("PoKingHill/CrowdSpectator") != null;
                Debug.Log($"[EXPLORE] Editor Shader.Find: SkyGradient={skyShader}, CrowdSpectator={crowdShader}");

                // Now play a Standard ranked match (longer pauseBetweenRounds so Result/Replay/PhotoMode are captured cleanly)
                d.EnterMenu();
                yield return Until(() => d.State == MatchState.Menu, 10f, "menu before standard match");
                GameSettings.Intro = false;
                GameSettings.Replays = true;
                GameSettings.Modifier = ArenaModifier.Standard;
                d.pauseBetweenRounds = 10f;
                d.QuickMatch();
                yield return Until(() => d.State == MatchState.Fight, 10f, "standard fight start");
                yield return new WaitForSecondsRealtime(0.5f);
                // Measure PolicyBatcher across 10 fixed steps
                int nonZeroBatches = 0, maxBatch = 0;
                for (int f = 0; f < 15; f++)
                {
                    if (PolicyBatcher.Instance != null && PolicyBatcher.Instance.LastBatchCount > 0)
                    {
                        nonZeroBatches++;
                        maxBatch = Mathf.Max(maxBatch, PolicyBatcher.Instance.LastBatchCount);
                    }
                    yield return new WaitForFixedUpdate();
                }
                Debug.Log($"[EXPLORE] Standard Fight PolicyBatcher over 15 FixedUpdates: nonZero={nonZeroBatches}, maxBatch={maxBatch}, maxSchedules={PolicyBatcher.Instance?.LastScheduleCount}");

                // Capture POV with HUD!
                camExtras?.TogglePov();
                yield return null;
                yield return CaptureComposite("13_fight_pov_hud.png", doc);
                camExtras?.TogglePov();

                // Wait for KO -> Result/Replay!
                yield return Until(() => d.State == MatchState.Result || d.State == MatchState.Replay, 45f, "standard fight result/replay");
                Debug.Log($"[EXPLORE] Standard Fight ended: State={d.State}, Winner={d.LastResult.Winner} ({d.LastResult.WinnerName}), ReplayAvail={replay?.Available}, ReplayActive={ReplaySystem.Active}");
                if (d.State == MatchState.Result && replay != null && replay.Available)
                {
                    replay.Play();
                    yield return null;
                }
                if (d.State == MatchState.Replay)
                {
                    yield return new WaitForSecondsRealtime(0.25f);
                    ClickButton(FindButton(root, "Photo"));
                    yield return null;
                    camExtras.OrbitPhoto(40f, 8f, 1.0f);
                    yield return CaptureComposite("10_replay_photomode.png", doc);
                    ClickButton(FindButton(root, "Hide UI"));
                    yield return null;
                    yield return CaptureComposite("11_replay_photo_hideui.png", doc);
                    ClickButton(FindButton(root, "Show UI"));
                    yield return null;
                    ClickButton(FindButton(root, "Done"));
                    yield return null;
                }
                ReplaySystem.StopActive();
                yield return Until(() => d.State == MatchState.Result, 10f, "standard result");
                yield return CaptureComposite("14_standard_result.png", doc);
                Debug.Log($"[EXPLORE] Standard Result: ShareBtn={FindButton(root, "Share KO highlight") != null}, Outcome='{PredictionBook.LastOutcome}'");

                // Print summary
                Debug.Log($"[EXPLORE ISSUES COUNT] {issues.Count}");
                foreach (var iss in issues) Debug.Log("[EXPLORE ISSUE] " + iss);
                Debug.Log($"[EXPLORE LOGS COUNT] {logs.Count}");
                foreach (var l in logs) Debug.Log("[EXPLORE CONSOLE] " + l);
            }
            finally
            {
                Time.timeScale = 1f;
                GameSettings.Intro = prevIntro;
                GameSettings.Replays = prevReplays;
                GameSettings.DevTools = prevDev;
                GameSettings.LiveWeather = prevLiveW;
                GameSettings.Atmosphere = prevAtmo;
                GameSettings.Modifier = prevMod;
                Application.logMessageReceived -= OnLog;
            }
        }
    }
}
