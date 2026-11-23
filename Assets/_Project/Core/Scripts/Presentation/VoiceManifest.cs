using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>A character's procedural voice (po-voice-manifest/1 "voice"): what VoiceSynth imitates it with.</summary>
    [Serializable]
    public class VoiceProfile
    {
        public float pitchHz = 150f, formantScale = 1f, breath = 0.2f, rasp = 0.2f, vibratoHz = 5f, vibratoDepth = 0.02f, tempo = 1f;
        public string timbre = "adult";

        public VoiceProfile Clone() => (VoiceProfile)MemberwiseClone();
    }

    [Serializable]
    public class VoiceClipEntry { public int level; public string file; public bool exists; }

    /// <summary>The five emotions' clips of one character, levels 1..5 each.</summary>
    [Serializable]
    public class VoiceClips
    {
        public VoiceClipEntry[] angry, happy, hurt, insult, celebrate;

        public VoiceClipEntry[] Of(SoundPool.VoiceEmotion e) => e switch
        {
            SoundPool.VoiceEmotion.Angry => angry, SoundPool.VoiceEmotion.Happy => happy, SoundPool.VoiceEmotion.Hurt => hurt,
            SoundPool.VoiceEmotion.Insult => insult, _ => celebrate
        };

        public void Set(SoundPool.VoiceEmotion e, VoiceClipEntry[] v)
        {
            switch (e)
            {
                case SoundPool.VoiceEmotion.Angry: angry = v; break;
                case SoundPool.VoiceEmotion.Happy: happy = v; break;
                case SoundPool.VoiceEmotion.Hurt: hurt = v; break;
                case SoundPool.VoiceEmotion.Insult: insult = v; break;
                default: celebrate = v; break;
            }
        }
    }

    [Serializable]
    public class VoiceCharacter
    {
        public string id, name;
        public VoiceProfile voice;
        public VoiceClips clips;

        public VoiceClipEntry Clip(SoundPool.VoiceEmotion e, int level)
        {
            var list = clips?.Of(e);
            if (list == null) return null;
            foreach (var c in list) if (c != null && c.level == level) return c;
            return null;
        }
    }

    /// <summary>
    /// voices.json (Assets/_Project/Data/Resources/Voices/voices.json, schema po-voice-manifest/1): every roster character
    /// x 5 emotions x 5 levels, the WAV each cell uses (relative to root, &lt;id&gt;/&lt;emotion&gt;-&lt;level&gt;.wav), whether
    /// it exists, and the character's procedural voice profile. Written by the editor menu PoKingHill/Voices/Rebuild
    /// voices.json (VoiceManifestGenerator), never by hand; read once by SoundPool.
    /// </summary>
    [Serializable]
    public class VoiceManifest
    {
        public const string Schema = "po-voice-manifest/1";
        public const string Root = "Assets/_Project/Data/Resources/Voices";
        public const string ResourcePath = "Voices/voices";
        public static readonly string[] EmotionNames = { "angry", "happy", "hurt", "insult", "celebrate" };

        public string schema = Schema, game = "PoHillKing", generated = "", root = Root;
        public string[] emotions = EmotionNames;
        public int[] levels = { 1, 2, 3, 4, 5 };
        public string fallback = "procedural";
        public VoiceCharacter[] characters = Array.Empty<VoiceCharacter>();

        public static string FileOf(string id, SoundPool.VoiceEmotion e, int level) =>
            $"{SoundPool.VoiceKey(id)}/{EmotionNames[(int)e]}-{Mathf.Clamp(level, 1, SoundPool.VoiceLevels)}.wav";

        public VoiceCharacter Find(string id)
        {
            if (characters == null || string.IsNullOrEmpty(id)) return null;
            string key = SoundPool.VoiceKey(id);
            foreach (var c in characters) if (c != null && c.id == key) return c;
            return null;
        }

        /// <summary>Null when the text is not a po-voice-manifest/1 file.</summary>
        public static VoiceManifest Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var m = JsonUtility.FromJson<VoiceManifest>(json);
                return m != null && m.schema == Schema && m.characters != null ? m : null;
            }
            catch (Exception) { return null; }
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        /// <summary>The full manifest for these characters (id, display name): every cell listed, exists from the
        /// callback (path relative to root), voice profiles kept from <paramref name="previous"/> when it has the
        /// character, else <see cref="DefaultProfile"/>.</summary>
        public static VoiceManifest Build(IEnumerable<(string id, string name)> roster, Func<string, bool> exists, VoiceManifest previous, string generatedUtc)
        {
            var list = new List<VoiceCharacter>();
            var seen = new HashSet<string>();
            foreach (var (rawId, name) in roster)
            {
                string id = SoundPool.VoiceKey(rawId);
                if (!seen.Add(id)) continue;
                var c = new VoiceCharacter { id = id, name = string.IsNullOrEmpty(name) ? rawId : name, clips = new VoiceClips() };
                c.voice = previous?.Find(id)?.voice ?? DefaultProfile(id);
                foreach (SoundPool.VoiceEmotion e in Enum.GetValues(typeof(SoundPool.VoiceEmotion)))
                {
                    var cells = new VoiceClipEntry[SoundPool.VoiceLevels];
                    for (int l = 1; l <= SoundPool.VoiceLevels; l++)
                    {
                        string file = FileOf(id, e, l);
                        cells[l - 1] = new VoiceClipEntry { level = l, file = file, exists = exists != null && exists(file) };
                    }
                    c.clips.Set(e, cells);
                }
                list.Add(c);
            }
            return new VoiceManifest { generated = generatedUtc ?? "", characters = list.ToArray() };
        }

        public int Count(out int recorded, List<string> missing = null)
        {
            int total = 0; recorded = 0;
            foreach (var c in characters)
                foreach (SoundPool.VoiceEmotion e in Enum.GetValues(typeof(SoundPool.VoiceEmotion)))
                    foreach (var cell in c.clips?.Of(e) ?? Array.Empty<VoiceClipEntry>())
                    {
                        total++;
                        if (cell.exists) recorded++; else missing?.Add(cell.file);
                    }
            return total;
        }

        /// <summary>Each body's voice, taken from the synth parameters the game used before the manifest (pitch, first
        /// two formants: Kim 235 Hz / 760 Hz, Trump 118 / 560, Grandpa 148 / 660, G1 172 / 650); any other character
        /// gets a distinct profile derived from its id.</summary>
        public static VoiceProfile DefaultProfile(string id)
        {
            switch (SoundPool.VoiceKey(id))
            {
                case "kim": return new VoiceProfile { pitchHz = 235f, formantScale = 1.15f, breath = 0.30f, rasp = 0.10f, vibratoHz = 5.8f, vibratoDepth = 0.025f, tempo = 1.10f, timbre = "adult-female" };
                case "trump": return new VoiceProfile { pitchHz = 118f, formantScale = 0.90f, breath = 0.20f, rasp = 0.45f, vibratoHz = 4.6f, vibratoDepth = 0.015f, tempo = 0.90f, timbre = "adult-male-heavy" };
                case "grandpa": return new VoiceProfile { pitchHz = 148f, formantScale = 0.97f, breath = 0.45f, rasp = 0.35f, vibratoHz = 6.5f, vibratoDepth = 0.045f, tempo = 0.80f, timbre = "elderly-male" };
                case "g1": return new VoiceProfile { pitchHz = 172f, formantScale = 1.00f, breath = 0.05f, rasp = 0.60f, vibratoHz = 0f, vibratoDepth = 0f, tempo = 1.20f, timbre = "robot" };
            }
            uint h = VoiceSynth.Hash(id);
            float u(int k) => ((h >> (k * 5)) & 31u) / 31f;
            return new VoiceProfile
            {
                pitchHz = Mathf.Lerp(105f, 250f, u(0)), formantScale = Mathf.Lerp(0.88f, 1.18f, u(1)), breath = Mathf.Lerp(0.1f, 0.45f, u(2)),
                rasp = Mathf.Lerp(0.05f, 0.5f, u(3)), vibratoHz = Mathf.Lerp(4.5f, 6.5f, u(4)), vibratoDepth = Mathf.Lerp(0.01f, 0.04f, u(5)),
                tempo = Mathf.Lerp(0.85f, 1.15f, u(1) * 0.5f + u(4) * 0.5f), timbre = "adult"
            };
        }
    }
}
