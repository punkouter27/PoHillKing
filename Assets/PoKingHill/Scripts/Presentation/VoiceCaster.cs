using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace PoKingHill
{
    /// <summary>
    /// Speaks the Commentator's caption lines (GameSettings.Voice) with the device's own speech synthesis: the
    /// browser's speechSynthesis on the web (Plugins/WebGL/KothSpeech.jslib, on-device voices only), Android's
    /// TextToSpeech in the Android player. No key, no server, no audio files; nothing is sent anywhere. On Windows and
    /// in the editor there is no voice (Available is false and the menu's switch is locked): the captions still show.
    /// A line that arrives while another is spoken is dropped unless it matters more (a knock-out or the result over a
    /// hit or a fall), so the voice never runs behind the fight.
    /// </summary>
    public class VoiceCaster : MonoBehaviour
    {
        [Range(0.5f, 2f)] public float rate = 1.15f;
        [Range(0f, 2f)] public float pitch = 1f;
        /// <summary>Lines handed to the device's voice (verification).</summary>
        public int Spoken { get; private set; }
        public int Dropped { get; private set; }
        float _busyUntil; int _priority;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int KothSpeechAvailable();
        [DllImport("__Internal")] static extern int KothSpeechBusy();
        [DllImport("__Internal")] static extern void KothSpeechStop();
        [DllImport("__Internal")] static extern int KothSpeechSay(string text, float rate, float pitch, float volume, int flush);
        public static bool Available => KothSpeechAvailable() != 0;
        static bool Busy => KothSpeechBusy() != 0;
        static void Stop() => KothSpeechStop();
        static bool Say(string text, float rate, float pitch, float volume, bool flush) => KothSpeechSay(text, rate, pitch, volume, flush ? 1 : 0) != 0;
#elif UNITY_ANDROID && !UNITY_EDITOR
        // android.speech.tts.TextToSpeech: ready once its OnInitListener reports SUCCESS (0)
        class Init : AndroidJavaProxy
        {
            public Init() : base("android.speech.tts.TextToSpeech$OnInitListener") { }
            public void onInit(int status) { _ok = status == 0; }
        }
        static AndroidJavaObject _tts; static bool _ok, _tried;
        static void Ensure()
        {
            if (_tried) return; _tried = true;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new Init());
            }
            catch (System.Exception e) { Debug.LogWarning("[VoiceCaster] no text-to-speech on this device: " + e.Message); _tts = null; }
        }
        public static bool Available { get { Ensure(); return _tts != null; } }
        static bool Busy { get { try { return _ok && _tts.Call<bool>("isSpeaking"); } catch { return false; } } }
        static void Stop() { try { if (_ok) _tts.Call<int>("stop"); } catch { } }
        static bool Say(string text, float rate, float pitch, float volume, bool flush)
        {
            Ensure(); if (_tts == null || !_ok) return false;
            try
            {
                _tts.Call<int>("setSpeechRate", rate); _tts.Call<int>("setPitch", pitch);
                using var bundle = new AndroidJavaObject("android.os.Bundle");
                bundle.Call("putFloat", "volume", volume);                       // TextToSpeech.Engine.KEY_PARAM_VOLUME
                return _tts.Call<int>("speak", text, flush ? 0 : 1, bundle, "koth") == 0;      // QUEUE_FLUSH / QUEUE_ADD
            }
            catch (System.Exception e) { Debug.LogWarning("[VoiceCaster] speak failed: " + e.Message); return false; }
        }
#else
        public static bool Available => false;
        static bool Busy => false;
        static void Stop() { }
        static bool Say(string text, float rate, float pitch, float volume, bool flush) => false;
#endif

        void OnEnable() { MatchEvents.Caption += OnCaption; MatchEvents.StateChanged += OnState; }
        void OnDisable() { MatchEvents.Caption -= OnCaption; MatchEvents.StateChanged -= OnState; if (Available) Stop(); }
        void OnState(MatchState s) { if (s == MatchState.Menu && Available) { Stop(); _busyUntil = 0f; } }

        /// <summary>How much a line matters: the result and knock-outs over the round's start, over hits and falls.</summary>
        public static int Priority(string line)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            if (line.Contains("king of the hill") || line.Contains("Crown for") || line.Contains("takes it") || line.Contains("Nobody keeps") || line.Contains("A draw")) return 3;
            if (line.Contains("off") || line.Contains("sea!") || line.Contains("goes over") || line.Contains("tumbles") || line.Contains("finishes")) return 2;
            return 1;
        }

        /// <summary>Seconds a line takes to say at this rate (about 2.6 words a second at rate 1), for the editor and for
        /// devices that do not report when they are done.</summary>
        public static float Duration(string line, float rate) => string.IsNullOrEmpty(line) ? 0f : 0.35f + line.Split(' ').Length / (2.6f * Mathf.Max(0.5f, rate));

        void OnCaption(string line)
        {
            if (!GameSettings.Voice || !Available || string.IsNullOrWhiteSpace(line)) return;
            int p = Priority(line); bool busy = Busy || Time.unscaledTime < _busyUntil;
            if (busy && p <= _priority) { Dropped++; return; }
            if (!Say(line, rate, pitch, Mathf.Clamp01(GameSettings.MasterVolume * 1.4f), busy)) return;
            Spoken++; _priority = p; _busyUntil = Time.unscaledTime + Duration(line, rate);
        }

        void Update() { if (_priority != 0 && Time.unscaledTime >= _busyUntil && !Busy) _priority = 0; }
    }
}
