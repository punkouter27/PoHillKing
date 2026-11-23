using System;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>Who each brain is pointed at in a free-for-all (brains were trained 1v1): Nearest (as trained) or
    /// Opportunist (shown as Weakest: the one easiest to push off).</summary>
    public enum Targeting { Nearest, Opportunist }
    public enum Overlay { Off, Strain, Colliders }
    /// <summary>Render budget (QualityTier): Auto starts High on desktop, Medium on phones, and steps down in a player
    /// build when the frame rate stays low.</summary>
    public enum Quality { Auto, Low, Medium, High }
    /// <summary>Sky lighting preset for AtmosphereController (#5).</summary>
    public enum AtmosphereMode { Auto, Dawn, Noon, Sunset, Night, Storm }
    /// <summary>Unranked arena physics/gameplay modifiers (#17); always Standard during graduation gates.</summary>
    public enum ArenaModifier { Standard, MoonGravity, IceSummit, HeavyKnockback }

    /// <summary>
    /// Every optional setting, saved in PlayerPrefs. The defaults are the plain game, with the least on screen: one
    /// round per launch, the sea at its normal pace, the fight starts at once (no tale of the tape, no automatic
    /// replay), no developer tools (so no captions, win-odds bar or debug panel either). Read anywhere;
    /// the Settings page of the menu writes them, the debug panel the developer ones.
    /// </summary>
    public static class GameSettings
    {
        public static event Action Changed;

        // Match
        public static int RoundsPerMatch { get => I("rounds", 1); set => Set("rounds", Mathf.Clamp(value, 0, 9)); }
        /// <summary>Multiplier on the sea's arrival time (1 = 20-30 s).</summary>
        public static float SeaPace { get => F("seaPace", 1f); set => Set("seaPace", Mathf.Clamp(value, 0.5f, 2f)); }
        /// <summary>Tale of the tape before a match's first round: a crane shot, then a card per fighter.</summary>
        public static bool Intro { get => B("intro", false); set => Set("intro", value); }
        /// <summary>The knock-out plays again on its own after the round (the results sheet always offers it).</summary>
        public static bool Replays { get => B("replays", false); set => Set("replays", value); }

        // Picture
        public static Quality Quality { get => (Quality)I("quality", (int)Quality.Auto); set => Set("quality", (int)value); }
        /// <summary>Particles, decals, post punch, shockwave, camera shake and cuts to a faller at the water, slow motion
        /// at the rim and hit stop (TimeWarp). Off = the plain picture.</summary>
        public static bool Cinematic { get => B("cinematic", true); set => Set("cinematic", value); }
        /// <summary>Seat colours from a palette told apart with any colour vision (MatchDirector.SeatColors).</summary>
        public static bool ColorSafe { get => B("colorSafe", false); set => Set("colorSafe", value); }

        // Sound: mix channels are multipliers on MasterVolume (hits and footsteps; the wind, surf, crowd and thunder bed)
        public static float MasterVolume { get => F("volume", 0.6f); set => Set("volume", Mathf.Clamp01(value)); }
        public static float SfxVolume { get => F("sfxVolume", 1f); set => Set("sfxVolume", Mathf.Clamp01(value)); }
        public static float AmbienceVolume { get => F("ambVolume", 0.7f); set => Set("ambVolume", Mathf.Clamp01(value)); }
        /// <summary>The synthesized score (MusicBed) that builds through a round.</summary>
        public static float MusicVolume { get => F("musVolume", 0.7f); set => Set("musVolume", Mathf.Clamp01(value)); }
        /// <summary>The commentator's lines are spoken by the device's own speech synthesis (VoiceCaster: the browser on the
        /// web, the system voice on Android; nothing is sent anywhere). Turns the caption line on with it.</summary>
        public static bool Voice { get => B("voice", false); set => Set("voice", value); }

        // Feel, atmosphere, live APIs & unranked modifiers
        /// <summary>Gamepad rumble and mobile vibration on heavy impacts and knockouts (#4).</summary>
        public static bool Haptics { get => B("haptics", true); set => Set("haptics", value); }
        /// <summary>Sky lighting preset or automatic round variation (#5).</summary>
        public static AtmosphereMode Atmosphere { get => (AtmosphereMode)Mathf.Clamp(I("atmosphere", (int)AtmosphereMode.Auto), 0, 5); set => Set("atmosphere", (int)value); }
        /// <summary>Optional Open-Meteo live weather & wind sync for visual atmosphere (#14).</summary>
        public static bool LiveWeather { get => B("liveWeather", false); set => Set("liveWeather", value); }
        /// <summary>Unranked physics modifier for casual play (#17); disables Ladder & Rivalries recording while non-Standard.</summary>
        public static ArenaModifier Modifier { get => (ArenaModifier)Mathf.Clamp(I("modifier", (int)ArenaModifier.Standard), 0, 3); set => Set("modifier", (int)value); }
        /// <summary>Optional Azure Function endpoint for Daily Challenge global leaderboard (#15).</summary>
        public static string DailyApiUrl { get => S("dailyApiUrl", ""); set => SetS("dailyApiUrl", value ?? ""); }

        /// <summary>Fighters in reach throw shoves and jabs (MatchDirector.Shoves), not only walk into each other.</summary>
        public static bool Strikes { get => B("strikes", true); set => Set("strikes", value); }

        // Advanced: extra rules, developer tools
        /// <summary>Who free-for-all brains go for (no effect with two fighters).</summary>
        public static Targeting Targeting { get => (Targeting)Mathf.Clamp(I("targeting", (int)Targeting.Opportunist), 0, 1); set => Set("targeting", (int)value); }
        /// <summary>The FPS pill, the debug button and the two-finger tap that open the debug panel; the options below
        /// apply only while it is on.</summary>
        public static bool DevTools { get => B("devTools", false); set => Set("devTools", value); }

        // Debug panel (developer tools only)
        /// <summary>A second line on the FPS pill: frame time p95 and the quality tier.</summary>
        public static bool TelemetryHud { get => DevTools && B("telemetryHud", false); set => Set("telemetryHud", value); }
        public static Overlay Overlay { get => DevTools ? (Overlay)Mathf.Clamp(I("overlay", (int)Overlay.Off), 0, 2) : Overlay.Off; set => Set("overlay", (int)value); }
        public static bool IntentArrows { get => DevTools && B("intent", false); set => Set("intent", value); }
        public static bool WinOdds { get => DevTools && B("winOdds", false); set => Set("winOdds", value); }
        /// <summary>Caption lines from the Commentator.</summary>
        public static bool Commentary { get => DevTools && B("commentary", false); set => Set("commentary", value); }

        static readonly string[] Keys = { "rounds", "seaPace", "intro", "replays", "targeting", "quality", "cinematic", "colorSafe",
                                          "volume", "sfxVolume", "ambVolume", "musVolume", "voice",
                                          "haptics", "atmosphere", "liveWeather", "modifier", "dailyApiUrl", "strikes",
                                          "devTools", "telemetryHud", "overlay", "intent", "winOdds", "commentary",
                                          // settings that were cut (2026-10-21): cleared by a reset too
                                          "camera", "effects", "slowmo", "cutaways", "hazards", "stamina", "tactician", "getup" };
        /// <summary>Back to the defaults: deletes only this game's keys, never other PlayerPrefs.</summary>
        public static void ResetAll() { foreach (var k in Keys) PlayerPrefs.DeleteKey("koth." + k); PlayerPrefs.Save(); Changed?.Invoke(); }

        static int I(string k, int d) { try { return PlayerPrefs.GetInt("koth." + k, d); } catch { return d; } }
        static float F(string k, float d) { try { return PlayerPrefs.GetFloat("koth." + k, d); } catch { return d; } }
        static string S(string k, string d) { try { return PlayerPrefs.GetString("koth." + k, d); } catch { return d; } }
        static bool B(string k, bool d) => I(k, d ? 1 : 0) != 0;
        static void Set(string k, int v) { PlayerPrefs.SetInt("koth." + k, v); Changed?.Invoke(); }
        static void Set(string k, float v) { PlayerPrefs.SetFloat("koth." + k, v); Changed?.Invoke(); }
        static void SetS(string k, string v) { PlayerPrefs.SetString("koth." + k, v); Changed?.Invoke(); }
        static void Set(string k, bool v) => Set(k, v ? 1 : 0);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetEvents() => Changed = null;
    }

    /// <summary>Build version shown bottom right: YYMMDD of the last commit, written by Editor/BuildVersion.cs.</summary>
    public static class BuildInfo
    {
        static string _v;
        public static string Version => _v ??= LoadVersion();
        static string LoadVersion()
        {
            var t = Resources.Load<TextAsset>("build_version");
            return t != null && t.text.Trim().Length > 0 ? t.text.Trim() : Application.version;
        }
    }
}
