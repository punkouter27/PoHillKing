using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// The voice matrix's procedural imitation (SoundPool's synth, upgraded): a short human vocalisation by one
    /// character, not a beep. A Rosenberg glottal pulse train at the profile's pitch with jitter, shimmer, subharmonic
    /// rasp and aspiration noise, through four parallel formant resonators that glide between vowel targets (a, e, i,
    /// o, u, schwa, a nasal murmur) scaled by the profile's formantScale; 1 to 4 syllables with consonant onsets (h, s,
    /// f noise, p/t/k bursts, rolled r, n murmur, w glide) and an emotion's prosody:
    ///   angry      "rraah" (+ "hup!") — hard onset, falling pitch, extra rasp
    ///   happy      "heh", "ha-ha", "ha-ha-ha" — bright vowels, rise-fall, light vibrato, laugh pulses
    ///   hurt       a gasp, then "oof"/"ugh" with a downward pitch break; level 5 a long cry
    ///   insult     "nyah-nyah(-nyah)" sing-song with a nasal raised F2; "pfft" first at level 4+
    ///   celebrate  "woo-hoo!" rising whoop, held vowel, wide vibrato; laughter from level 3
    /// Level 1..5 scales loudness, length, pitch range, rasp, breath, syllable count and vibrato. Deterministic: the
    /// noise and jitter are seeded from hash(id, emotion, level, take), the timing comes from the plan alone. Pure C#
    /// (no Unity audio API), so it can run off the main thread; SoundPool caches each clip.
    /// </summary>
    public static class VoiceSynth
    {
        public const int Rate = 44100;

        /// <summary>FNV-1a, stable across runs and platforms (string.GetHashCode is not).</summary>
        public static uint Hash(string s)
        {
            uint h = 2166136261u;
            if (s != null) foreach (char c in s) { h ^= c; h *= 16777619u; }
            return h;
        }

        // F1..F4 (Hz) of an adult male voice; formantScale moves them (women and children ~1.15-1.3)
        static readonly Dictionary<char, float[]> Vowel = new()
        {
            ['a'] = new[] { 730f, 1090f, 2440f, 3400f }, ['e'] = new[] { 530f, 1840f, 2480f, 3500f }, ['i'] = new[] { 300f, 2250f, 3000f, 3600f },
            ['o'] = new[] { 570f, 840f, 2410f, 3300f }, ['u'] = new[] { 320f, 870f, 2240f, 3300f }, ['@'] = new[] { 500f, 1500f, 2500f, 3500f },
            ['n'] = new[] { 260f, 1700f, 2600f, 3500f },
        };
        static readonly float[] FormantGain = { 1f, 0.55f, 0.25f, 0.12f }, FormantBw = { 90f, 110f, 160f, 220f };

        /// <summary>One syllable: onset consonant (' ' none, h s f p t k r n w, 'H' = an unvoiced in-breath), vowel glide
        /// v0 -> v1, coda (' ', p t k f), length (s), pitch start/end (x the profile), loudness, laugh pulsing.</summary>
        struct Syl
        {
            public char onset, v0, v1, coda; public float dur, p0, p1, amp, vib; public bool laugh, nasal, voiced;
            public Syl(char onset, char v0, char v1, float dur, float p0, float p1, float amp = 1f, char coda = ' ')
            { this.onset = onset; this.v0 = v0; this.v1 = v1; this.coda = coda; this.dur = dur; this.p0 = p0; this.p1 = p1; this.amp = amp; vib = 1f; laugh = false; nasal = false; voiced = true; }
        }

        static List<Syl> Plan(SoundPool.VoiceEmotion e, float L)
        {
            var s = new List<Syl>();
            switch (e)
            {
                case SoundPool.VoiceEmotion.Angry:
                    s.Add(new Syl('r', 'a', '@', 0.30f, 1.18f, 0.84f, 1f) { vib = 0.3f });                       // "rraah"
                    if (L >= 0.5f) s.Add(new Syl('h', 'u', '@', 0.12f, 1.02f, 0.90f, 0.85f, 'p'));                // "hup!"
                    if (L >= 1f) s.Add(new Syl('r', 'a', 'a', 0.22f, 1.10f, 0.78f, 0.95f) { vib = 0.3f });
                    break;
                case SoundPool.VoiceEmotion.Happy:
                {
                    int n = 1 + Mathf.RoundToInt(2f * L);                                                         // heh / ha-ha / ha-ha-ha
                    for (int i = 0; i < n; i++)
                    {
                        float arc = Mathf.Sin(Mathf.PI * (i + 1f) / (n + 1f));
                        s.Add(new Syl('h', n == 1 ? 'e' : i % 2 == 0 ? 'a' : 'e', n == 1 ? 'e' : 'a', n == 1 ? 0.22f : 0.13f, 1.02f + 0.18f * arc, 0.98f + 0.12f * arc, 0.9f) { laugh = n > 1, vib = 0.6f });
                    }
                    break;
                }
                case SoundPool.VoiceEmotion.Hurt:
                    s.Add(new Syl('H', 'a', 'a', 0.07f + 0.05f * L, 1f, 1f, 0.45f) { voiced = false });         // sharp in-breath
                    if (L >= 1f) s.Add(new Syl(' ', 'a', 'o', 0.60f, 1.45f, 0.78f, 1f) { vib = 1.6f });          // long cry
                    else s.Add(new Syl(' ', L < 0.5f ? 'o' : '@', 'u', 0.26f, 1.20f, 0.70f, 1f, L < 0.5f ? 'f' : ' '));   // "oof" / "ugh"
                    if (L >= 0.75f) s.Add(new Syl(' ', '@', 'u', 0.20f, 0.85f, 0.62f, 0.6f));                    // groan tail
                    break;
                case SoundPool.VoiceEmotion.Insult:
                {
                    if (L >= 0.75f) s.Add(new Syl('p', '@', '@', 0.13f, 1f, 1f, 0.55f, 'f') { voiced = false }); // "pfft"
                    int n = L >= 0.5f ? 3 : 2;                                                                    // nyah-nyah(-nyah)
                    for (int i = 0; i < n; i++)
                        s.Add(new Syl('n', 'e', 'a', 0.17f, i % 2 == 0 ? 1.22f : 0.96f, i % 2 == 0 ? 1.18f : 0.94f, 0.9f) { nasal = true, vib = 0.2f });
                    break;
                }
                default: // Celebrate
                    s.Add(new Syl('w', 'u', 'o', 0.24f, 0.92f, 1.30f, 0.95f) { vib = 0.5f });                     // "woo"
                    s.Add(new Syl('h', 'u', 'u', 0.34f + 0.30f * L, 1.32f, 1.50f, 1f) { vib = 2.2f });            // "-hoo!" held
                    if (L >= 0.5f)
                        for (int i = 0; i < (L >= 1f ? 3 : 2); i++)
                            s.Add(new Syl('h', 'a', 'a', 0.12f, 1.15f - 0.05f * i, 1.08f - 0.05f * i, 0.7f) { laugh = true });
                    break;
            }
            return s;
        }

        /// <summary>A formant: two-pole band-pass whose centre can move (coefficients refreshed every block).</summary>
        struct Formant
        {
            float a1, a2, g, x1, x2, y1, y2;
            public void Tune(float f, float bw)
            {
                f = Mathf.Clamp(f, 60f, Rate * 0.45f);
                float r = Mathf.Exp(-Mathf.PI * bw / Rate);
                a1 = 2f * r * Mathf.Cos(2f * Mathf.PI * f / Rate); a2 = -r * r; g = (1f - r * r) * 0.5f;
            }
            public float Step(float x) { float y = g * (x - x2) + a1 * y1 + a2 * y2; x2 = x1; x1 = x; y2 = y1; y1 = y; return y; }
        }

        static float OnsetLength(char c) => c switch { 'h' => 0.05f, 's' => 0.08f, 'f' => 0.07f, 'p' or 't' or 'k' => 0.025f, 'n' => 0.06f, 'w' => 0.07f, 'r' => 0.09f, _ => 0f };

        /// <summary>Renders one cell (mono, <see cref="Rate"/> Hz, peak 0.5 at level 1 to 0.9 at level 5).</summary>
        public static float[] Render(VoiceProfile p, string id, SoundPool.VoiceEmotion e, int level, int take = 0)
        {
            p ??= new VoiceProfile();
            level = Mathf.Clamp(level, 1, SoundPool.VoiceLevels);
            float L = (level - 1) / (float)(SoundPool.VoiceLevels - 1);
            var rng = new System.Random((int)Hash($"{SoundPool.VoiceKey(id)}|{e}|{level}|{take}"));
            float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);

            var plan = Plan(e, L);
            float tempo = Mathf.Clamp(p.tempo, 0.5f, 2f), stretch = (0.85f + 0.35f * L) / tempo, range = 0.6f + 0.8f * L, gap = 0.035f / tempo;
            int total = (int)(0.03f * Rate);
            foreach (var syl in plan) total += (int)((syl.dur * stretch + gap) * Rate);
            var x = new float[total];

            float f0 = Mathf.Max(50f, p.pitchHz) * (1f + 0.03f * ((take % 3) - 1));             // a take moves the pitch slightly
            float jitter = 0.005f + 0.01f * L, shimmer = 0.03f + 0.03f * L;
            float rasp = Mathf.Clamp01(p.rasp * (0.5f + 0.7f * L) * (e == SoundPool.VoiceEmotion.Angry ? 1.6f : 1f));
            float breath = Mathf.Clamp01(p.breath * (0.5f + 0.8f * L) * (e == SoundPool.VoiceEmotion.Hurt ? 1.5f : 1f));
            float vibDepth = p.vibratoDepth * (0.5f + 1.0f * L), fs = p.formantScale <= 0f ? 1f : p.formantScale;

            var fm = new Formant[4];
            float phase = 0f, periodAmp = 1f, flowPrev = 0f, hp = 0f, lpNoise = 0f; bool oddPeriod = false;
            int at = 0;
            foreach (var syl in plan)
            {
                int n = (int)(syl.dur * stretch * Rate), on = Mathf.Min(n / 2, (int)(OnsetLength(syl.onset) * Rate / tempo));
                int coda = syl.coda == ' ' ? 0 : Mathf.Min(n / 3, (int)(0.06f * Rate));
                float attack = e == SoundPool.VoiceEmotion.Angry ? 0.004f : 0.015f;
                for (int i = 0; i < n && at + i < total; i++)
                {
                    float t = i / (float)Rate, u = i / (float)n;
                    bool inOnset = i < on, inCoda = i >= n - coda;
                    // formant targets: the onset's own shape (w starts at u, n is a murmur), then the vowel glide
                    if ((i & 15) == 0)
                    {
                        float[] a = Vowel[syl.onset == 'n' && inOnset ? 'n' : syl.onset == 'w' && inOnset ? 'u' : syl.v0];
                        float[] b = Vowel[syl.v1];
                        float k = inOnset ? 0f : Mathf.SmoothStep(0f, 1f, (i - on) / (float)Mathf.Max(1, n - on));
                        for (int j = 0; j < 4; j++)
                        {
                            float f = Mathf.Lerp(a[j], b[j], k) * fs;
                            if (syl.nasal && j == 1) f *= 1.15f;                                       // sneering, nasal
                            fm[j].Tune(f, FormantBw[j] * (1f + 0.4f * breath) * (syl.nasal && j == 0 ? 1.6f : 1f));
                        }
                    }
                    // pitch: the syllable's contour, widened with the level, vibrato, and a downward break when hurt
                    float contour = 1f + (Mathf.Lerp(syl.p0, syl.p1, u) - 1f) * range;
                    if (e == SoundPool.VoiceEmotion.Hurt && syl.voiced && u > 0.4f) contour *= 0.88f;
                    float pitch = f0 * contour * (1f + vibDepth * syl.vib * Mathf.Sin(2f * Mathf.PI * p.vibratoHz * t));

                    // glottal source: Rosenberg flow, differentiated (its derivative is what radiates)
                    phase += pitch / Rate;
                    if (phase >= 1f)
                    {
                        phase -= 1f; oddPeriod = !oddPeriod;
                        periodAmp = (1f + shimmer * Noise()) * (oddPeriod ? 1f - 0.6f * rasp : 1f);     // shimmer + subharmonic rasp
                        phase += jitter * Noise() * 0.5f; if (phase < 0f) phase = 0f;
                    }
                    const float open = 0.42f, close = 0.16f;
                    float flow = phase < open ? 0.5f * (1f - Mathf.Cos(Mathf.PI * phase / open))
                               : phase < open + close ? Mathf.Cos(0.5f * Mathf.PI * (phase - open) / close) : 0f;
                    float glottal = (flow - flowPrev) * (Rate / Mathf.Max(80f, pitch)) * 0.2f * periodAmp;
                    flowPrev = flow;
                    if (syl.onset == 'r' && inOnset || e == SoundPool.VoiceEmotion.Angry) glottal *= 1f + rasp * 0.6f * Mathf.Sin(2f * Mathf.PI * 32f * t);   // rolled r / growl
                    if (syl.laugh) glottal *= 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(Mathf.PI * u * 2f));

                    float wn = Noise();
                    lpNoise += 0.25f * (wn - lpNoise); hp = wn - lpNoise;                                    // high-passed noise for s/f
                    bool voicedNow = syl.voiced && !inCoda && !(inOnset && (syl.onset == 'h' || syl.onset == 's' || syl.onset == 'f' || syl.onset == 'p' || syl.onset == 't' || syl.onset == 'k'));
                    float aspiration = wn * breath * (0.25f + 0.75f * flow) * 0.6f;
                    float exc = voicedNow ? glottal * (syl.onset == 'n' && inOnset ? 0.5f : 1f) + aspiration : 0f;
                    float frication = 0f;
                    if (inOnset)
                    {
                        float k = i / (float)Mathf.Max(1, on);
                        switch (syl.onset)
                        {
                            case 'h': exc += wn * 0.5f * (0.3f + 0.7f * k); break;                              // breathy, through the vowel's formants
                            case 's': frication = hp * 0.35f; break;
                            case 'f': frication = hp * 0.18f; break;
                            case 'p': case 't': case 'k': frication = k < 0.45f ? 0f : wn * 0.6f * Mathf.Exp(-(k - 0.45f) * 12f); break;
                        }
                    }
                    if (!syl.voiced) { exc = 0f; frication = (syl.onset == 'H' ? wn * 0.4f * Mathf.Sin(Mathf.PI * u) : frication + hp * 0.2f * Mathf.Sin(Mathf.PI * u)); if (syl.onset == 'H') exc = wn * 0.25f; }
                    if (inCoda)
                    {
                        float k = (i - (n - coda)) / (float)Mathf.Max(1, coda);
                        frication += syl.coda == 'f' ? hp * 0.2f * (1f - k) : k < 0.3f ? 0f : wn * 0.4f * Mathf.Exp(-(k - 0.3f) * 15f);
                    }

                    float y = 0f;
                    for (int j = 0; j < 4; j++) y += FormantGain[j] * fm[j].Step(exc);
                    float env = Mathf.Clamp01(t / attack) * Mathf.Clamp01((n - i) / (0.04f * Rate)) * syl.amp;
                    x[at + i] = env * y + frication * syl.amp;
                }
                at += n + (int)(gap * Rate);
            }

            float peak = 1e-6f; for (int i = 0; i < total; i++) peak = Mathf.Max(peak, Mathf.Abs(x[i]));
            float target = 0.5f + 0.4f * L;
            for (int i = 0; i < total; i++) x[i] *= target / peak;
            for (int i = total - Mathf.Min(total, 256); i < total; i++) x[i] *= (total - i) / 256f;
            return x;
        }
    }
}
