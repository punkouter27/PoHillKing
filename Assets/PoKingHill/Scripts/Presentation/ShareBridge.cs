using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// Captures Photo Mode PNG snapshots (#6) and shares Replay KO highlight clips (#8) across WebGL, Mobile and Desktop.
    /// </summary>
    public class ShareBridge : MonoBehaviour
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void KothShareImage(string filename, string base64Png);
        [DllImport("__Internal")] static extern int KothRecordClip(int durationMs, string title);
#endif
        public static ShareBridge Instance { get; private set; }
        public string LastSavedPath { get; private set; }

        void OnEnable() => Instance = this;
        void OnDisable() { if (Instance == this) Instance = null; }

        public void CapturePhotoPng(Action<string> onDone = null)
        {
            StartCoroutine(CaptureRoutine("Photos", "koth-photo", onDone));
        }

        public void ShareHighlightClip(string title, int durationMs = 3500, Action<string> onDone = null)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (KothRecordClip(durationMs, string.IsNullOrEmpty(title) ? "PoKingHill KO" : title) != 0)
            {
                MatchEvents.RaiseCaption("Recording replay highlight...");
                onDone?.Invoke("webgl-clip");
                return;
            }
#endif
            StartCoroutine(CaptureRoutine("Highlights", "koth-ko", onDone));
        }

        IEnumerator CaptureRoutine(string folder, string prefix, Action<string> onDone)
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try
            {
                tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (tex == null) yield break;
                byte[] png = tex.EncodeToPNG();
                string fileName = $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png";
#if UNITY_WEBGL && !UNITY_EDITOR
                KothShareImage(fileName, Convert.ToBase64String(png));
                LastSavedPath = fileName;
                MatchEvents.RaiseCaption("Shared " + fileName);
                onDone?.Invoke(fileName);
#else
                string dir = Path.Combine(Application.persistentDataPath, folder);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, fileName);
                File.WriteAllBytes(path, png);
                LastSavedPath = path;
                MatchEvents.RaiseCaption("Saved " + fileName);
                onDone?.Invoke(path);
#endif
            }
            finally
            {
                if (tex != null) Destroy(tex);
            }
        }
    }
}
