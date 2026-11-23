using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PoKingHill.EditorTools
{
    /// <summary>
    /// Keeps Assets/_Project/Data/Resources/Voices/voices.json true: every roster character x 5 emotions x 5 levels,
    /// the WAV file of each cell and whether it exists, the voice profiles already in the file kept. The menu rebuilds
    /// it from the Game scene's roster; dropping or deleting a WAV under Voices refreshes the exists flags on import.
    /// </summary>
    public class VoiceManifestGenerator : AssetPostprocessor
    {
        public const string Game = "Assets/_Project/Scenes/Game.unity";
        public static string ManifestPath => VoiceManifest.Root + "/voices.json";

        [MenuItem("PoKingHill/Voices/Rebuild voices.json")]
        public static void RebuildMenu() => Rebuild(RosterFromGameScene());

        /// <summary>The Game scene's roster as (character id, name): the open scene's MatchDirector, else Game.unity
        /// opened additively for the read.</summary>
        public static List<(string id, string name)> RosterFromGameScene()
        {
            var director = UnityEngine.Object.FindAnyObjectByType<MatchDirector>();
            if (director != null) return Characters(director.roster);
            var scene = EditorSceneManager.OpenScene(Game, OpenSceneMode.Additive);
            try
            {
                var d = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MatchDirector>(true)).FirstOrDefault();
                return d != null ? Characters(d.roster) : new List<(string, string)>();
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        public static List<(string id, string name)> Characters(IEnumerable<Agent> roster) =>
            roster.Where(a => a != null).Select(a => (SoundPool.VoiceKey(a.Badge), a.Badge)).ToList();

        static VoiceManifest Load() => File.Exists(ManifestPath) ? VoiceManifest.Parse(File.ReadAllText(ManifestPath)) : null;

        /// <summary>Rewrites voices.json for these characters (kept from the file when null) and prints the summary.
        /// Writes only when something besides the time stamp changed.</summary>
        public static VoiceManifest Rebuild(List<(string id, string name)> roster)
        {
            var previous = Load();
            roster ??= previous?.characters.Select(c => (c.id, c.name)).ToList() ?? new List<(string, string)>();
            var m = VoiceManifest.Build(roster, f => File.Exists(Path.Combine(VoiceManifest.Root, f)), previous, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            if (previous != null)
            {
                string stamp = m.generated; m.generated = previous.generated;
                if (m.ToJson() == previous.ToJson()) { Summary(m, false); return m; }
                m.generated = stamp;
            }
            Directory.CreateDirectory(VoiceManifest.Root);
            File.WriteAllText(ManifestPath, m.ToJson() + "\n");
            AssetDatabase.ImportAsset(ManifestPath);
            Summary(m, true);
            return m;
        }

        static void Summary(VoiceManifest m, bool written)
        {
            var missing = new List<string>();
            int total = m.Count(out int recorded, missing);
            string list = missing.Count == 0 ? "none" : string.Join(", ", missing.Take(12)) + (missing.Count > 12 ? $" and {missing.Count - 12} more (procedural)" : " (procedural)");
            Debug.Log($"[Voices] {(written ? "wrote" : "unchanged")} {ManifestPath}: {recorded} of {total} clips recorded; missing: {list}");
        }

        // A WAV added, moved or deleted under Voices: refresh the exists flags (the characters stay those in the file).
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            bool Touches(string p) => p.StartsWith(VoiceManifest.Root + "/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(Touches) && File.Exists(ManifestPath)) Rebuild(null);
        }
    }
}
