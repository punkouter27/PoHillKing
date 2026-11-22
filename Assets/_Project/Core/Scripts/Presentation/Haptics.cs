using UnityEngine;
using UnityEngine.InputSystem;

namespace PoKingHill
{
    /// <summary>
    /// Controller rumble (Gamepad.current) and mobile haptic feedback on heavy hits, KOs and round bells (#4).
    /// Zero allocations per frame and automatically stops motors on disable or window expiry.
    /// </summary>
    public class Haptics : MonoBehaviour
    {
        public MatchDirector director;
        [Range(0f, 1f)] public float maxMotor = 0.85f;
        public float minImpactThreshold = 0.35f;

        float _low, _high, _until, _lastHandheldAt = -10f;

        public (float low, float high, bool active) CurrentState => (_low, _high, Time.unscaledTime < _until);

        void OnEnable()
        {
            MatchEvents.Impact += OnImpact;
            MatchEvents.Knockout += OnKo;
            MatchEvents.RoundEnded += OnEnd;
        }

        void OnDisable()
        {
            MatchEvents.Impact -= OnImpact;
            MatchEvents.Knockout -= OnKo;
            MatchEvents.RoundEnded -= OnEnd;
            StopMotors();
        }

        bool Enabled => GameSettings.Haptics && (director == null || (director.statsRounds == 0 && !director.InMenu)) && Time.timeScale <= 4f;

        public void Pulse(float lowFreq, float highFreq, float durationSec, bool handheldPulse = false)
        {
            if (!GameSettings.Haptics) return;
            float l = Mathf.Clamp01(lowFreq) * maxMotor;
            float h = Mathf.Clamp01(highFreq) * maxMotor;
            if (l >= _low || Time.unscaledTime >= _until) _low = l;
            if (h >= _high || Time.unscaledTime >= _until) _high = h;
            _until = Mathf.Max(_until, Time.unscaledTime + Mathf.Clamp(durationSec, 0.03f, 0.5f));
            var pad = Gamepad.current;
            if (pad != null) pad.SetMotorSpeeds(_low, _high);
            if (handheldPulse && Time.unscaledTime - _lastHandheldAt >= 0.35f)
            {
                _lastHandheldAt = Time.unscaledTime;
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
                Handheld.Vibrate();
#endif
            }
        }

        void OnImpact(Vector3 p, float strength, PolicyRunner victim)
        {
            if (!Enabled || strength < minImpactThreshold) return;
            float s = Mathf.InverseLerp(minImpactThreshold, 1f, Mathf.Clamp01(strength));
            Pulse(0.25f + 0.55f * s, 0.15f + 0.65f * s, 0.07f + 0.12f * s, s >= 0.65f);
        }

        void OnKo(PolicyRunner victim)
        {
            if (!Enabled) return;
            Pulse(0.95f, 0.7f, 0.28f, true);
        }

        void OnEnd(RoundResult r)
        {
            if (!Enabled) return;
            Pulse(0.55f, 0.45f, 0.2f, r.End == RoundEnd.Knockout);
        }

        void Update()
        {
            if (_until <= 0f) return;
            if (!GameSettings.Haptics || Time.unscaledTime >= _until) StopMotors();
        }

        void StopMotors()
        {
            _low = 0f;
            _high = 0f;
            _until = 0f;
            var pad = Gamepad.current;
            if (pad != null) pad.SetMotorSpeeds(0f, 0f);
        }
    }
}
