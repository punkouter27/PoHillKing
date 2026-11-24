using System.Collections.Generic;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// One console line per brain (ONNX policy) the first time it produces an action in this session:
    /// "[PoHillKing] &lt;brain&gt;.onnx inference running (…)". It is the proof, in a browser console, that the trained
    /// policies really run (AGENTS.md section 3: checked in the local WebGL build at 16:9 and 9:16). Called from the
    /// physics tick: after the first line per brain it is one HashSet lookup, no allocation.
    /// </summary>
    public static class InferenceLog
    {
        public const string Marker = "inference running";
        static readonly HashSet<Object> Seen = new();
        static readonly List<string> Logged = new();

        /// <summary>The brains logged so far (names without .onnx), in order.</summary>
        public static IReadOnlyList<string> Brains => Logged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Clear();

        /// <summary>Logs the line for this brain once; true when it logged now.</summary>
        public static bool Note(Object brain, string body, string how)
        {
            if (brain == null || !Seen.Add(brain)) return false;
            Logged.Add(brain.name);
            Debug.Log($"[PoHillKing] {brain.name}.onnx {Marker} ({body}, {how})");
            return true;
        }

        /// <summary>Tests: forget which brains were logged.</summary>
        public static void Clear() { Seen.Clear(); Logged.Clear(); }
    }
}
