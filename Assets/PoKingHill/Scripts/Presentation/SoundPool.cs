using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// 3D one-shot voices. A pool of AudioSources is moved to where a hit happens and plays a short clip there, so the
    /// listener (the camera) hears distance roll-off, direction and doppler from Unity's spatializer. The clips are
    /// synthesized from code when the scene starts (still no audio files), three variants of each:
    ///   Thud       a falling low sine and a noise burst (a body landing or taking a shove)
    ///   Clank      four inharmonic decaying partials (metal on metal, G1 against G1)
    ///   Smack      a short band-limited noise slap over a low thump (a hit on a human)
    ///   character voices       a body-specific formant bank crossed with Strain, Pain, Fear and Triumph
    ///   Splash     filtered noise with bubbles
    ///   Thunder    a bright crack, then a long brown-noise rumble (MatchFx's lightning, delayed by distance)
    /// Every source has a low-pass that ImpactSynth closes while the camera is under water or time is slowed (TimeWarp).
    /// Play is allocation-free.
    /// </summary>
    public class SoundPool : MonoBehaviour
    {
        public enum Kind { Thud, Clank, Smack, GruntLow, GruntHigh, Splash, Thunder }
        public enum Emotion { Strain, Pain, Fear, Triumph }
        public int voices = 10;
        [Tooltip("Distance (m) inside which a voice plays at full volume")] public float minDistance = 4f;
        public float maxDistance = 120f;

        /// <summary>Low-pass cutoff for every voice (Hz), set by ImpactSynth.</summary>
        public float cutoff = 22000f;
        public int Played { get; private set; }

        AudioClip[][] _clips; AudioClip[][][] _voiceClips; AudioSource[] _src; AudioLowPassFilter[] _lp; int _next; uint _rng = 2463534242u;
        const int Rate = 44100;
        static readonly string[] VoiceBodies = { "kim", "trump", "grandpa", "g1" };
        public int VoiceClipCount => _voiceClips == null ? 0 : VoiceBodies.Length * System.Enum.GetValues(typeof(Emotion)).Length * 3;
        public int VoiceFallbacks { get; private set; }

        void Awake()
        {
            int kinds = System.Enum.GetValues(typeof(Kind)).Length; _clips = new AudioClip[kinds][];
            for (int k = 0; k < kinds; k++)
            {
                _clips[k] = new AudioClip[3];
                for (int v = 0; v < 3; v++) _clips[k][v] = Make((Kind)k, v);
            }
            int emotions = System.Enum.GetValues(typeof(Emotion)).Length;
            _voiceClips = new AudioClip[VoiceBodies.Length][][];
            for (int b = 0; b < VoiceBodies.Length; b++)
            {
                _voiceClips[b] = new AudioClip[emotions][];
                for (int e = 0; e < emotions; e++)
                {
                    _voiceClips[b][e] = new AudioClip[3];
                    for (int v = 0; v < 3; v++) _voiceClips[b][e][v] = MakeVoice(b, (Emotion)e, v);
                }
            }
            _src = new AudioSource[voices]; _lp = new AudioLowPassFilter[voices];
            for (int i = 0; i < voices; i++)
            {
                var go = new GameObject("voice " + i); go.transform.SetParent(transform, false);
                var s = go.AddComponent<AudioSource>(); s.playOnAwake = false; s.spatialBlend = 1f; s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = minDistance; s.maxDistance = maxDistance; s.dopplerLevel = 1f; s.spread = 40f;
                _src[i] = s; _lp[i] = go.AddComponent<AudioLowPassFilter>(); _lp[i].cutoffFrequency = 22000f;
            }
        }

        void Update()
        {
            if (_lp == null) return;
            for (int i = 0; i < _lp.Length; i++) if (Mathf.Abs(_lp[i].cutoffFrequency - cutoff) > 1f) _lp[i].cutoffFrequency = cutoff;
        }

        /// <summary>Plays a variant of <paramref name="kind"/> at a world position. gain 0..1 already includes the mix
        /// channel; delay in seconds (thunder travels at 343 m/s).</summary>
        public void Play(Kind kind, Vector3 world, float gain, float pitch = 1f, float delay = 0f)
        {
            if (_src == null || _src.Length == 0 || gain <= 0.002f) return;
            PlayClip(_clips[(int)kind][_rngNext() % 3], world, gain, pitch, delay);
        }

        public void PlayVoice(string body, Emotion emotion, Vector3 world, float gain, float pitch = 1f)
        {
            if (_src == null || _src.Length == 0 || gain <= 0.002f) return;
            int b = System.Array.IndexOf(VoiceBodies, body);
            if (b < 0) { b = VoiceBodies.Length - 1; VoiceFallbacks++; }
            int e = (int)emotion;
            if (e < 0 || e >= _voiceClips[b].Length) { e = (int)Emotion.Strain; VoiceFallbacks++; }
            PlayClip(_voiceClips[b][e][_rngNext() % 3], world, gain, pitch, 0f);
        }

        uint _rngNext()
        {
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            return _rng;
        }

        void PlayClip(AudioClip clip, Vector3 world, float gain, float pitch, float delay)
        {
            // the next free voice, else the oldest one (round robin)
            int pick = -1;
            for (int k = 0; k < _src.Length; k++) { int i = (_next + k) % _src.Length; if (!_src[i].isPlaying) { pick = i; break; } }
            if (pick < 0) pick = _next;
            _next = (pick + 1) % _src.Length;
            var s = _src[pick];
            uint random = _rngNext();
            s.Stop(); s.clip = clip; s.transform.position = world;
            s.volume = Mathf.Clamp01(gain); s.pitch = pitch * (0.96f + 0.08f * ((random >> 8) & 0xFF) / 255f);
            if (delay > 0f) s.PlayDelayed(delay); else s.Play();
            Played++;
        }

        // ------------------------------------------------------------------ synthesis
        AudioClip MakeVoice(int body, Emotion emotion, int variant)
        {
            float[] pitch = { 235f, 118f, 148f, 172f };
            float[] f1 = { 760f, 560f, 660f, 650f }, f2 = { 1350f, 1060f, 1240f, 1160f };
            float duration = emotion == Emotion.Triumph ? 0.52f : emotion == Emotion.Fear ? 0.28f : 0.36f;
            int n = (int)(duration * Rate); var samples = new float[n]; var random = new System.Random(1700 + body * 97 + (int)emotion * 31 + variant);
            float v = variant / 2f, basePitch = pitch[body] * (0.94f + 0.12f * v), phase = 0f;
            var a = new Reson(f1[body] * (0.96f + 0.08f * v), 100f);
            var b = new Reson(f2[body] * (0.96f + 0.08f * v), 150f);
            float Noise() => (float)(random.NextDouble() * 2 - 1);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate, x = t / duration;
                float contour = emotion switch
                {
                    Emotion.Fear => Mathf.Lerp(1.35f, 0.98f, x) * (1f + 0.08f * Mathf.Sin(2 * Mathf.PI * 12f * t)),
                    Emotion.Triumph => Mathf.Lerp(0.82f, 1.22f, x),
                    Emotion.Pain => Mathf.Lerp(1.18f, 0.68f, x),
                    _ => Mathf.Lerp(1.02f, 0.82f, x)
                };
                phase += basePitch * contour / Rate;
                float pulse = 0f;
                if (phase >= 1f) { phase -= 1f; pulse = 1f; }
                float voiced = a.Step(pulse + 0.035f * Noise()) + 0.62f * b.Step(pulse + 0.025f * Noise());
                float breath = Noise() * (emotion == Emotion.Fear ? 0.15f : emotion == Emotion.Pain ? 0.11f : 0.045f);
                float envelope = Mathf.Clamp01(t / 0.014f) * Mathf.Exp(-Mathf.Max(0f, t - 0.045f) / (emotion == Emotion.Triumph ? 0.24f : 0.11f));
                samples[i] = envelope * (voiced + breath);
            }
            float peak = 1e-6f; for (int i = 0; i < n; i++) peak = Mathf.Max(peak, Mathf.Abs(samples[i]));
            for (int i = 0; i < n; i++) samples[i] *= 0.88f / peak;
            for (int i = n - Mathf.Min(n, 256); i < n; i++) samples[i] *= (n - i) / 256f;
            var clip = AudioClip.Create($"{VoiceBodies[body]} {emotion} {variant}", n, 1, Rate, false); clip.SetData(samples, 0);
            return clip;
        }

        static AudioClip Make(Kind k, int variant)
        {
            float seconds = k switch { Kind.Thud => 0.4f, Kind.Clank => 0.7f, Kind.Smack => 0.22f, Kind.Splash => 1.0f, Kind.Thunder => 4.5f, _ => 0.36f };
            int n = (int)(seconds * Rate); var x = new float[n];
            var rnd = new System.Random(1000 * (int)k + variant);
            float Noise() => (float)(rnd.NextDouble() * 2 - 1);
            float v = variant / 2f;          // 0, 0.5, 1
            switch (k)
            {
                case Kind.Thud:
                {
                    float ph = 0, lp = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float t = i / (float)Rate, f = Mathf.Lerp(95f, 48f, Mathf.Clamp01(t / 0.25f)) * (0.9f + 0.2f * v);
                        ph += 2 * Mathf.PI * f / Rate; lp += 0.12f * (Noise() - lp);
                        x[i] = 0.9f * Mathf.Sin(ph) * Mathf.Exp(-t / 0.10f) + 0.8f * lp * Mathf.Exp(-t / 0.018f);
                    }
                    break;
                }
                case Kind.Clank:
                {
                    float f0 = Mathf.Lerp(290f, 380f, v); float[] ratio = { 1f, 2.76f, 5.40f, 8.93f }, tau = { 0.32f, 0.22f, 0.12f, 0.06f }, amp = { 1f, 0.6f, 0.35f, 0.2f };
                    for (int i = 0; i < n; i++)
                    {
                        float t = i / (float)Rate, s = 0;
                        for (int p = 0; p < 4; p++) s += amp[p] * Mathf.Sin(2 * Mathf.PI * f0 * ratio[p] * t) * Mathf.Exp(-t / tau[p]);
                        x[i] = 0.45f * s + 0.5f * Noise() * Mathf.Exp(-t / 0.004f);
                    }
                    break;
                }
                case Kind.Smack:
                {
                    float lp = 0, lp2 = 0, ph = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float t = i / (float)Rate; lp += 0.35f * (Noise() - lp); lp2 += 0.35f * (lp - lp2);
                        ph += 2 * Mathf.PI * Mathf.Lerp(130f, 90f, v) / Rate;
                        x[i] = 1.2f * (lp - 0.6f * lp2) * Mathf.Exp(-t / 0.025f) + 0.6f * Mathf.Sin(ph) * Mathf.Exp(-t / 0.06f);
                    }
                    break;
                }
                case Kind.GruntLow:
                case Kind.GruntHigh:
                {
                    bool low = k == Kind.GruntLow;
                    float f0a = low ? 118f : 235f, f0b = low ? 84f : 180f, F1 = (low ? 620f : 760f) * (0.95f + 0.1f * v), F2 = (low ? 1080f : 1350f) * (0.95f + 0.1f * v);
                    var r1 = new Reson(F1, 90f); var r2 = new Reson(F2, 130f); float ph = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float t = i / (float)Rate, f0 = Mathf.Lerp(f0a, f0b, t / seconds); ph += f0 / Rate;
                        float pulse = 0; if (ph >= 1f) { ph -= 1f; pulse = 1f; }
                        float src = pulse + 0.05f * Noise();
                        float env = Mathf.Clamp01(t / 0.015f) * Mathf.Exp(-Mathf.Max(0, t - 0.05f) / 0.09f);
                        x[i] = env * (r1.Step(src) + 0.6f * r2.Step(src));
                    }
                    break;
                }
                case Kind.Splash:
                {
                    float lp = 0, bub = 0, bubF = 600f, bubT = -1;
                    for (int i = 0; i < n; i++)
                    {
                        float t = i / (float)Rate; lp += 0.10f * (Noise() - lp);
                        if (rnd.NextDouble() < 0.0006) { bubT = t; bubF = 400f + (float)rnd.NextDouble() * 900f; }
                        if (bubT >= 0) { float bt = t - bubT; bub = Mathf.Sin(2 * Mathf.PI * bubF * (1 + 2 * bt) * bt) * Mathf.Exp(-bt / 0.03f); }
                        x[i] = 1.5f * lp * Mathf.Exp(-t / 0.3f) * Mathf.Clamp01(t / 0.01f) + 0.25f * bub * Mathf.Exp(-t / 0.6f);
                    }
                    break;
                }
                case Kind.Thunder:
                {
                    float brown = 0, crack = 0, env = 0, envT = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float t = i / (float)Rate; brown = Mathf.Clamp(brown + 0.02f * Noise(), -1f, 1f) * 0.9995f;
                        crack += 0.6f * (Noise() - crack);
                        if (t > envT) { envT = t + 0.08f + (float)rnd.NextDouble() * 0.25f; env = 0.4f + 0.6f * (float)rnd.NextDouble(); }
                        float rumble = brown * env * Mathf.Exp(-t / Mathf.Lerp(1.1f, 1.6f, v)) * Mathf.Clamp01(t / 0.12f);
                        x[i] = 0.7f * crack * Mathf.Exp(-t / 0.05f) + 2.2f * rumble;
                    }
                    break;
                }
            }
            float peak = 1e-6f; for (int i = 0; i < n; i++) peak = Mathf.Max(peak, Mathf.Abs(x[i]));
            for (int i = 0; i < n; i++) x[i] *= 0.9f / peak;
            for (int i = n - Mathf.Min(n, 256); i < n; i++) x[i] *= (n - i) / 256f;     // no click at the end
            var clip = AudioClip.Create($"{k} {variant}", n, 1, Rate, false); clip.SetData(x, 0);
            return clip;
        }

        /// <summary>Two-pole resonator (a formant): centre frequency and bandwidth in Hz.</summary>
        struct Reson
        {
            float a1, a2, g, y1, y2;
            public Reson(float f, float bw)
            {
                float r = Mathf.Exp(-Mathf.PI * bw / Rate); a1 = 2 * r * Mathf.Cos(2 * Mathf.PI * f / Rate); a2 = -r * r; g = 1 - r; y1 = y2 = 0;
            }
            public float Step(float x) { float y = g * x + a1 * y1 + a2 * y2; y2 = y1; y1 = y; return y; }
        }
    }
}
