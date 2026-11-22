using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// The Tab overlay (HudView.Overlay.cs): how often it refreshes, the inference-latency averaging, the training
    /// speed benchmark's length, and the curriculum each body's shipped brain came through (the stage shown is the last
    /// one: every game brain is the body's _hill rung). Data/Resources/OverlaySettings.asset.
    /// </summary>
    [CreateAssetMenu(menuName = "PoHillKing/Overlay Settings", fileName = "OverlaySettings")]
    public class OverlaySettings : ScriptableObject
    {
        [Tooltip("Seconds between overlay text refreshes")] public float refreshSeconds = 0.25f;
        [Tooltip("Inference latency: weight of the newest sample in the rolling average (0..1)")] [Range(0.01f, 1f)] public float latencySmoothing = 0.1f;
        [Tooltip("Wall-clock seconds the training-speed benchmark steps a copy of the scene")] public float benchmarkSeconds = 3f;
        [Tooltip("Milliseconds of benchmark stepping per frame, so the page keeps drawing")] public float benchmarkSliceMs = 60f;
        [Tooltip("The human bodies' training ladder, first stage to last (training/koth/env.py rungs)")]
        public string[] curriculum = { "r0b stand", "r1b walk", "fight (r4att)", "league (r4league)", "hill" };
        [Tooltip("Stage of the shipped brains (index into curriculum)")] public int shippedStage = 4;

        public const string ResourcePath = "OverlaySettings";
        static OverlaySettings _current;
        public static OverlaySettings Current
        {
            get
            {
                if (_current == null) _current = Resources.Load<OverlaySettings>(ResourcePath);
                if (_current == null) _current = CreateInstance<OverlaySettings>();
                return _current;
            }
        }

        public string StageLabel(string body)
        {
            int n = curriculum != null ? curriculum.Length : 0; int k = Mathf.Clamp(shippedStage, 0, Mathf.Max(0, n - 1));
            return n == 0 ? "-" : $"{body}_{curriculum[k].Split(' ')[0]} · stage {k + 1} of {n} ({curriculum[k]})";
        }
    }
}
