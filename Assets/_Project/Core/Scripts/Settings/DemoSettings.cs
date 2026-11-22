using UnityEngine;

namespace PoKingHill
{
    /// <summary>Demo (attract) mode, DemoMode.cs: idle threshold on the menu, the showcase shots it cycles and how long
    /// each stays. Data/Resources/DemoSettings.asset.</summary>
    [CreateAssetMenu(menuName = "PoHillKing/Demo Settings", fileName = "DemoSettings")]
    public class DemoSettings : ScriptableObject
    {
        [Tooltip("Seconds on the menu without keyboard, mouse, touch or gamepad input before the demo starts")] public float idleSeconds = 30f;
        [Tooltip("Seconds each showcase shot stays before the next")] public float dwellSeconds = 7f;
        [Tooltip("Showcase shots, cycled in order (Follow and Orbit pick the next fighter still in)")]
        public BroadcastCamera.CameraMode[] shots = { BroadcastCamera.CameraMode.Broadcast, BroadcastCamera.CameraMode.Orbit, BroadcastCamera.CameraMode.Follow };
        [Tooltip("Text on the chip shown while the demo runs")] public string caption = "DEMO - press any key or tap to play";

        public const string ResourcePath = "DemoSettings";
        static DemoSettings _current;
        public static DemoSettings Current
        {
            get
            {
                if (_current == null) _current = Resources.Load<DemoSettings>(ResourcePath);
                if (_current == null) _current = CreateInstance<DemoSettings>();
                return _current;
            }
        }
    }
}
