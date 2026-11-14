using System;

namespace PoKingHill
{
    /// <summary>
    /// The match's music, synthesized sample by sample (no audio files), for ImpactSynth's audio callback. One number
    /// drives it, Intensity 0..1 (how far the round has gone: fighters gone, the sea's height, someone on the rim):
    ///   drone     a low D with a fifth that opens up as the intensity rises; all there is in the menu
    ///   drums     a taiko pattern on a 16-step bar that fills in over four levels, 92 to 132 beats a minute
    ///   pulse     a plucked D minor figure on the eighths from a third of the way up
    ///   ticks     off-beat noise ticks in the last half
    ///   stinger   Sting(true) a rising major chord for the winner, Sting(false) a falling low one for a draw
    /// Pure arithmetic on its own state: no Unity calls, no allocation, safe on the audio thread. Intensity and Playing
    /// are written from the main thread (single floats; a torn read is harmless).
    /// </summary>
    public sealed class MusicBed
    {
        public volatile float Intensity;          // 0..1
        public volatile bool Drums;               // false: drone only (menu, results)
        volatile int _sting;                      // 1 = win, 2 = draw, consumed by the audio thread

        readonly float _rate;
        double _beat;                             // position in sixteenths
        int _step = -1;
        float _i;                                 // smoothed intensity
        // drone
        float _d1, _d2, _d3, _dLp;
        // drum
        float _kEnv, _kPhase, _kHz, _kAmp;
        // tick
        float _tEnv, _tHp;
        // pluck
        float _pEnv, _pPhase, _pHz, _pLp;
        // stinger
        float _sEnv, _sT; int _sKind; readonly float[] _sPh = new float[4];
        uint _rng = 0x9E3779B9u;

        const float D2 = 73.416f;
        static readonly float[] Scale = { 146.83f, 174.61f, 220.00f, 293.66f, 261.63f, 220.00f, 174.61f, 220.00f };   // D3 F3 A3 D4 C4 A3 F3 A3
        // sixteenth steps that carry a drum, per level; value = accent
        static readonly float[][] Patterns =
        {
            new float[] { 1f, 0, 0, 0, 0, 0, 0, 0, 0.7f, 0, 0, 0, 0, 0, 0, 0 },
            new float[] { 1f, 0, 0, 0, 0, 0, 0.6f, 0, 0.8f, 0, 0, 0, 0, 0, 0.6f, 0 },
            new float[] { 1f, 0, 0, 0.5f, 0, 0, 0.7f, 0, 0.9f, 0, 0, 0.5f, 0, 0, 0.7f, 0 },
            new float[] { 1f, 0, 0.5f, 0.6f, 0, 0, 0.8f, 0, 0.9f, 0, 0.5f, 0.6f, 0, 0.5f, 0.8f, 0.6f },
        };

        public MusicBed(int sampleRate) { _rate = Math.Max(8000, sampleRate); }

        /// <summary>The end of a round: a chord for the winner (true) or a low fall for a draw.</summary>
        public void Sting(bool win) => _sting = win ? 1 : 2;

        public static int Level(float intensity) => intensity < 0.25f ? 0 : intensity < 0.5f ? 1 : intensity < 0.75f ? 2 : 3;
        public static float Bpm(float intensity) => 92f + 40f * Math.Clamp(intensity, 0f, 1f);

        float Noise() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return (_rng & 0xFFFF) / 32768f - 1f; }
        static float Wrap(float p) => p >= 6.2831853f ? p - 6.2831853f : p;

        /// <summary>The next stereo sample, roughly within -1..1 before the caller's gain.</summary>
        public void Next(out float left, out float right)
        {
            const float tau = 6.2831853f;
            _i += (Intensity - _i) * (1f / (0.8f * _rate));
            int sting = _sting; if (sting != 0) { _sting = 0; _sKind = sting; _sEnv = 1f; _sT = 0f; }

            // sequencer: sixteenths
            _beat += Bpm(_i) * 4.0 / (60.0 * _rate);
            int step = (int)_beat;
            if (step != _step)
            {
                _step = step; if (_beat >= 1024.0) { _beat -= 1024.0; _step = (int)_beat; step = _step; }
                if (Drums)
                {
                    float acc = Patterns[Level(_i)][step & 15];
                    if (acc > 0f) { _kEnv = 1f; _kAmp = acc; _kHz = 118f; _kPhase = 0f; }
                    if (_i > 0.5f && (step & 1) == 1) _tEnv = 0.35f + 0.65f * (_i - 0.5f) * 2f;
                    if (_i > 0.33f && (step & 1) == 0) { _pEnv = Math.Min(1f, (_i - 0.33f) * 2.2f); _pHz = Scale[(step >> 1) & 7]; _pPhase = 0f; }
                }
            }

            // drone: root, a slightly sharp copy and the fifth, saw-ish, low-passed
            _d1 = Wrap(_d1 + tau * D2 / _rate); _d2 = Wrap(_d2 + tau * D2 * 1.0045f / _rate); _d3 = Wrap(_d3 + tau * D2 * 1.4983f / _rate);
            float saw = Saw(_d1) + Saw(_d2) + (0.25f + 0.75f * _i) * Saw(_d3);
            _dLp += (0.012f + 0.03f * _i) * (saw - _dLp);
            float drone = 0.28f * _dLp;

            // drum: a sine falling from 118 Hz to 52 Hz, with a noise thump at the start
            float drum = 0f;
            if (_kEnv > 1e-4f)
            {
                _kHz += (52f - _kHz) * (1f / (0.035f * _rate)); _kPhase = Wrap(_kPhase + tau * _kHz / _rate);
                drum = _kAmp * _kEnv * ((float)Math.Sin(_kPhase) + 0.25f * _kEnv * _kEnv * Noise());
                _kEnv *= 1f - 1f / (0.16f * _rate);
            }
            // tick: high-passed noise
            float tick = 0f;
            if (_tEnv > 1e-4f) { float n = Noise(); _tHp += 0.6f * (n - _tHp); tick = 0.22f * _tEnv * (n - _tHp); _tEnv *= 1f - 1f / (0.018f * _rate); }
            // pluck: a triangle whose brightness dies with it
            float pluck = 0f;
            if (_pEnv > 1e-4f)
            {
                _pPhase = Wrap(_pPhase + tau * _pHz / _rate);
                float tri = 2f * Math.Abs(_pPhase / 3.14159265f - 1f) - 1f;
                _pLp += (0.05f + 0.35f * _pEnv) * (tri - _pLp); pluck = 0.3f * _pEnv * _pLp; _pEnv *= 1f - 1f / (0.22f * _rate);
            }
            // stinger
            float st = 0f;
            if (_sEnv > 1e-4f)
            {
                _sT += 1f / _rate; bool win = _sKind == 1;
                for (int k = 0; k < 4; k++)
                {
                    float hz = win ? Win[k] : Draw[k] * (1f - 0.06f * Math.Min(1f, _sT));       // a draw sags
                    _sPh[k] = Wrap(_sPh[k] + tau * hz / _rate);
                    float on = Math.Clamp((_sT - (win ? 0.09f * k : 0f)) / 0.05f, 0f, 1f);       // the winner's chord climbs in
                    st += on * ((float)Math.Sin(_sPh[k]) + 0.3f * (float)Math.Sin(2f * _sPh[k]));
                }
                st *= 0.16f * _sEnv; if (_sT > (win ? 0.9f : 0.5f)) _sEnv *= 1f - 1f / (0.9f * _rate);
            }

            float mid = drone + 0.75f * drum + st;
            left = mid + 0.8f * pluck + 0.6f * tick; right = mid + 0.6f * pluck + 0.9f * tick;
        }

        static readonly float[] Win = { 146.83f, 185.00f, 220.00f, 293.66f };     // D major
        static readonly float[] Draw = { 73.42f, 87.31f, 110.00f, 146.83f };      // D minor, low
        static float Saw(float phase) => phase / 3.14159265f - 1f;
    }
}
