using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoKingHill.EditorTools
{
    /// <summary>
    /// The game in a browser: a WebGL player in Builds/WebGL, served from the Azure Static Web App
    /// "pohillking" (resource group PoHillKing) by Tools/deploy_webgl.ps1. Menu Tools/PoHillKing/Build WebGL (browser), or headless:
    ///   Unity.exe -batchmode -quit -projectPath . -buildTarget WebGL -executeMethod PoKingHill.EditorTools.WebGLBuild.Build
    /// The outcome is the "WEBGL BUILD RESULT:" log line (and the exit code in batch mode).
    ///
    /// Gzip with the decompression fallback: Static Web Apps does not send the Content-Encoding
    /// header a compressed Unity build otherwise needs, so the loader unpacks the files itself.
    /// The canvas is 540 x 960.
    /// The MCP bridge's UNITY_MCP_* defines are stripped for the build, as for Android, so its
    /// runtime (and the NuGet closure behind it) stays out of the player.
    ///
    /// MuJoCo is statically linked: Assets/Plugins/WebGL/MuJoCo holds it compiled to wasm with this
    /// editor's own Emscripten (how: README.md beside it), and org.mujoco's MujocoLib binds to
    /// "__Internal" on WebGL. Those .a files are pinned to WebGL here, since Android's IL2CPP
    /// would otherwise try to link them too.
    /// </summary>
    public static class WebGLBuild
    {
        const string OUTPUT = "Builds/WebGL";
        const string MUJOCO_WASM = "Assets/Plugins/WebGL/MuJoCo";

        [MenuItem("Tools/PoHillKing/Build WebGL (browser)")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) { Finish("Aborted — exit Play mode first", false); return; }

            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.defaultWebScreenWidth = 540;
            PlayerSettings.defaultWebScreenHeight = 960;
            PinMujocoToWebGL();
            PlayerSettings.WebGL.template = "PROJECT:Po";

            string defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.WebGL);
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.WebGL,
                string.Join(";", defines.Split(';').Where(d => !d.Trim().StartsWith("UNITY_MCP_"))));
            BuildSummary summary;
            try
            {
                summary = BuildPipeline.BuildPlayer(scenes, OUTPUT, BuildTarget.WebGL, BuildOptions.None).summary;
            }
            finally
            {
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.WebGL, defines);
            }
            Finish($"{summary.result} | v{PlayerSettings.bundleVersion} | {scenes.Length} scenes | {summary.totalErrors} errors | " +
                   $"{summary.totalSize / (1024 * 1024)} MB | {summary.totalTime.TotalMinutes:0.0} min | {OUTPUT}",
                   summary.result == BuildResult.Succeeded);
        }

        static void PinMujocoToWebGL()
        {
            foreach (string path in Directory.GetFiles(MUJOCO_WASM, "*.a"))
            {
                var importer = (PluginImporter)AssetImporter.GetAtPath(path.Replace('\\', '/'));
                if (!importer.GetCompatibleWithAnyPlatform() && !importer.GetCompatibleWithEditor() &&
                    importer.GetCompatibleWithPlatform(BuildTarget.WebGL)) continue;
                importer.SetCompatibleWithAnyPlatform(false);
                importer.SetCompatibleWithEditor(false);
                importer.SetCompatibleWithPlatform(BuildTarget.WebGL, true);
                importer.SaveAndReimport();
            }
        }

        static void Finish(string message, bool ok)
        {
            if (ok) Debug.Log("WEBGL BUILD RESULT: " + message);
            else Debug.LogError("WEBGL BUILD RESULT: " + message);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
