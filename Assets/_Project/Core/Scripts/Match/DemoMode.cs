using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;

namespace PoKingHill
{
    /// <summary>
    /// Demo (attract) mode: after DemoSettings.idleSeconds (30 s) on the menu with no keyboard, mouse, touch or gamepad
    /// input, a quick match of every brain starts (inference only: every fighter is its trained policy), the HUD hides
    /// behind a Demo chip and the camera cycles DemoSettings.shots, DemoSettings.dwellSeconds each. When the match ends it
    /// starts another. Any input ends the demo: back to the menu, with the HUD and the camera mode as they were.
    /// Inert in the statistics gates and with autoLaunch.
    /// </summary>
    public class DemoMode : MonoBehaviour
    {
        public MatchDirector director;
        public HudView hud;
        public DemoSettings settings;

        /// <summary>The PlayMode suite turns the demo off (ShowcaseSuite) so a test idling on the menu is not taken over;
        /// ShowcasePlayTests turns it back on for its own test.</summary>
        public static bool Suppressed { get; set; }
        public bool Active { get; private set; }
        /// <summary>Seconds on the menu without input (0 outside the menu), counted by an IdleClock: frames longer than
        /// 0.25 s (a load, a background tab, a hitch) are not idle time.</summary>
        public float IdleSeconds => _idle.Seconds;
        public int ShotIndex => _shot;
        public int ShotsShown { get; private set; }

        IdleClock _idle, _dwell; int _shot;      // _idle: the current idle stretch on the menu; _dwell: the current shot
        BroadcastCamera.CameraMode _prevMode;
        bool _pressed; System.IDisposable _anyButton;

        // Every button press of any device (keys, mouse buttons, touches, gamepad), whenever the Input System processes it.
        void OnEnable() => _anyButton = InputSystem.onAnyButtonPress.Call(_ => _pressed = true);
        void OnDisable() { _anyButton?.Dispose(); _anyButton = null; }

        void Update()
        {
            if (director == null || director.statsRounds > 0 || director.autoLaunch) return;
            if (Suppressed) { _idle.Reset(); _pressed = false; if (Active) Stop(); return; }
            if (settings == null) settings = DemoSettings.Current;
            bool input = _pressed || AnyInput(); _pressed = false;
            if (Active)
            {
                if (input) { Stop(); return; }
                if (director.State == MatchState.Menu) { Launch(); return; }    // the demo match ended: another one
                if (director.State == MatchState.Intro) director.SkipIntro();
                if (AdvanceDwell(Time.unscaledDeltaTime)) NextShot();
                return;
            }
            if (director.State != MatchState.Menu || input) { _idle.Reset(); return; }
            // summed frame times without the long frames: a loading hitch or a tab in the background is not idle time
            if (AdvanceIdle(Time.unscaledDeltaTime)) Begin();
        }

        /// <summary>Adds one frame to the idle wait (skipped when longer than 0.25 s); true once it reaches
        /// DemoSettings.idleSeconds.</summary>
        public bool AdvanceIdle(float frameSeconds)
        {
            if (settings == null) settings = DemoSettings.Current;
            return _idle.Advance(frameSeconds) >= settings.idleSeconds;
        }

        public void ResetIdle() => _idle.Reset();

        /// <summary>Adds one frame to the current showcase shot (skipped when longer than 0.25 s); true once the shot has
        /// been on screen for DemoSettings.dwellSeconds (at least 1 s).</summary>
        public bool AdvanceDwell(float frameSeconds)
        {
            if (settings == null) settings = DemoSettings.Current;
            return _dwell.Advance(frameSeconds) >= Mathf.Max(1f, settings.dwellSeconds);
        }

        public void ResetDwell() => _dwell.Reset();

        public void Begin()
        {
            if (Active || director == null) return;
            Active = true; _idle.Reset(); ShotsShown = 0; _shot = -1;
            _prevMode = BroadcastCamera.Mode;
            hud?.SetDemo(true, settings != null ? settings.caption : "DEMO");
            Debug.Log($"[DemoMode] on after {settings.idleSeconds:0.0} s idle ({director.State})");
            Launch();
        }

        public void Stop()
        {
            if (!Active) return;
            Active = false; _idle.Reset();
            BroadcastCamera.Mode = _prevMode; BroadcastCamera.FollowSeat = -1;
            hud?.SetDemo(false, null);
            if (director.State != MatchState.Menu) director.EnterMenu();
            Debug.Log("[DemoMode] off (input)");
        }

        void Launch()
        {
            director.QuickMatch();
            if (director.State == MatchState.Intro) director.SkipIntro();
            NextShot();
        }

        void NextShot()
        {
            var shots = settings.shots != null && settings.shots.Length > 0 ? settings.shots : new[] { BroadcastCamera.CameraMode.Broadcast };
            _shot = (_shot + 1) % shots.Length;
            BroadcastCamera.Mode = shots[_shot];
            // Follow / Orbit: the next fighter still in
            int n = Mathf.Max(1, director.Playing), seat = (BroadcastCamera.FollowSeat + 1) % n;
            for (int k = 0; k < n && director.IsOut(seat); k++) seat = (seat + 1) % n;
            BroadcastCamera.FollowSeat = shots[_shot] == BroadcastCamera.CameraMode.Broadcast ? -1 : seat;
            _dwell.Reset();
            ShotsShown++;
        }

        static bool AnyInput()
        {
            var kb = Keyboard.current; if (kb != null && kb.anyKey.wasPressedThisFrame) return true;
            var mouse = Mouse.current;
            if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame || mouse.scroll.ReadValue().sqrMagnitude > 0f || mouse.delta.ReadValue().sqrMagnitude > 4f)) return true;
            var ts = Touchscreen.current; if (ts != null && ts.primaryTouch.press.wasPressedThisFrame) return true;
            var pad = Gamepad.current;
            if (pad != null && (pad.buttonSouth.wasPressedThisFrame || pad.startButton.wasPressedThisFrame || pad.leftStick.ReadValue().sqrMagnitude > 0.1f)) return true;
            return false;
        }
    }
}
