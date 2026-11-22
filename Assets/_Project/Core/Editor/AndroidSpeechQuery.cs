#if UNITY_ANDROID
using System.IO;
using UnityEditor.Android;

namespace PoKingHill.EditorTools
{
    /// <summary>
    /// Android 11+ hides other packages from an app unless its manifest asks: without this query VoiceCaster's
    /// TextToSpeech cannot find the system's speech engine. Adds
    /// &lt;queries&gt;&lt;intent&gt;&lt;action android:name="android.intent.action.TTS_SERVICE"/&gt;&lt;/intent&gt;&lt;/queries&gt;
    /// to the generated launcher manifest (no custom AndroidManifest.xml to keep in step with Unity's).
    /// </summary>
    public class AndroidSpeechQuery : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 10;
        const string Query = "<queries><intent><action android:name=\"android.intent.action.TTS_SERVICE\" /></intent></queries>";

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            // path is the unityLibrary module; the manifest that is merged into the app is in its src/main
            string manifest = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifest)) return;
            string xml = File.ReadAllText(manifest);
            if (xml.Contains("android.intent.action.TTS_SERVICE")) return;
            int at = xml.LastIndexOf("</manifest>", System.StringComparison.Ordinal); if (at < 0) return;
            File.WriteAllText(manifest, xml.Insert(at, "  " + Query + "\n"));
        }
    }
}
#endif
