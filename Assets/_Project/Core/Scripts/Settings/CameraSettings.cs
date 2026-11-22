using UnityEngine;

namespace PoKingHill
{
    /// <summary>The broadcast camera's tuning (BroadcastCamera reads it at start): orbit speed and the faller cut-away.
    /// Data/Resources/CameraSettings.asset; the cameras themselves stay in Presentation.prefab.</summary>
    [CreateAssetMenu(menuName = "PoHillKing/Camera Settings", fileName = "CameraSettings")]
    public class CameraSettings : ScriptableObject
    {
        [Tooltip("Orbit speed of the winner / replay / menu shots, degrees per second")] public float orbitDegPerSecond = 18f;
        [Tooltip("Cut to a fallen fighter when its pelvis drops to this many metres above the sea (BroadcastCamera.FallerShot)...")] public float cutAbove = 0.5f;
        [Tooltip("...and leave it once the pelvis is this many metres under the sea")] public float cutUnder = 0.6f;
        [Tooltip("How long a faller cut-away lasts, seconds")] public float cutSeconds = 3f;

        public const string ResourcePath = "CameraSettings";
        static CameraSettings _current;
        public static CameraSettings Current
        {
            get
            {
                if (_current == null) _current = Resources.Load<CameraSettings>(ResourcePath);
                if (_current == null) _current = CreateInstance<CameraSettings>();
                return _current;
            }
        }
    }
}
