using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace PoKingHill
{
    public partial class HudView
    {
        // ------------------------------------------------------------------ Tab overlay (RL readout) and demo chip
        // Tab, or the RL corner button on touch screens, toggles a panel with FPS, the followed fighter's episode reward
        // (RewardMeter: the training penalties, weights in RewardWeights), the brains' inference latency (rolling
        // average), the curriculum stage of the shipped brains (OverlaySettings) and a button that benchmarks the
        // local simulation speed (SimBenchmark). Always available, not only with the developer tools.
        VisualElement _rl, _demoChip; Label _rlText, _demoText, _versionLabel; Button _rlBtn, _rlBench;
        float _rlNext, _latencyAvg = -1f;
        SimBenchmark _bench; RewardMeter _meter; DemoMode _demo;

        /// <summary>A browser page (Assets/WebGLTemplates/Po) draws the title and the version outside the canvas, so the
        /// HUD does not repeat them there.</summary>
        public static bool PageDrawsChrome => Application.platform == RuntimePlatform.WebGLPlayer;
        public bool OverlayShown => _rl != null && _rl.style.display == DisplayStyle.Flex;
        public string OverlayText => _rlText != null ? _rlText.text : "";
        public bool OverlayButtonShown => _rlBtn != null && _rlBtn.style.display == DisplayStyle.Flex;
        public SimBenchmark Benchmark => _bench;
        public bool DemoChipShown => _demoChip != null && _demoChip.style.display == DisplayStyle.Flex;
        public bool HudHiddenForDemo => _column != null && _column.style.display == DisplayStyle.None;

        void BuildOverlay()
        {
            if (PageDrawsChrome) { _title.style.visibility = Visibility.Hidden; if (_versionLabel != null) _versionLabel.style.visibility = Visibility.Hidden; }
            _rl = El("rl-panel", _column); _rl.style.display = DisplayStyle.None; _rl.pickingMode = PickingMode.Position;
            Text("RL overlay · Tab", "rl-head", _rl);
            _rlText = Text("", "rl-text", _rl);
            _rlBench = Btn("Benchmark training speed", "secondary compact", BenchmarkTraining, _rl);
            _rlBtn = Btn("RL", "icon-btn rl-btn", ToggleOverlay, _menuBtn.parent); _rlBtn.tooltip = "RL overlay (Tab)";
            _rlBtn.RemoveFromHierarchy(); _menuBtn.parent.Insert(0, _rlBtn);          // left of the menu button
            _rlBtn.style.display = Touchscreen.current != null ? DisplayStyle.Flex : DisplayStyle.None;
            _demoChip = El("demo-chip", _root); _demoChip.pickingMode = PickingMode.Ignore; _demoChip.style.display = DisplayStyle.None;
            _demoText = Text("DEMO", "demo-text", _demoChip);
        }

        void EnsureOverlayParts()
        {
            if (director == null) return;
            if (_bench == null) { _bench = director.GetComponent<SimBenchmark>(); if (_bench == null) _bench = director.gameObject.AddComponent<SimBenchmark>(); }
            if (_meter == null)
            {
                _meter = director.GetComponent<RewardMeter>();
                if (_meter == null) { _meter = director.gameObject.AddComponent<RewardMeter>(); _meter.director = director; }
            }
            if (_demo == null)
            {
                _demo = director.GetComponent<DemoMode>();
                if (_demo == null) { _demo = director.gameObject.AddComponent<DemoMode>(); _demo.director = director; _demo.hud = this; }
            }
        }

        public void ToggleOverlay()
        {
            bool on = !OverlayShown; _rl.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (on) { _rlNext = 0f; UpdateOverlay(); }
        }

        public void BenchmarkTraining()
        {
            if (_bench == null || director == null) return;
            var s = OverlaySettings.Current; int fighters = 0;
            for (int i = 0; i < director.Playing && i < director.bodies.Length; i++) if (director.bodies[i] != null && director.bodies[i].policy != null) fighters++;
            if (fighters == 0) fighters = director.bodies.Length;
            int dec = director.bodies.Length > 0 && director.bodies[0] != null && director.bodies[0].Spec != null ? director.bodies[0].Spec.decimation : 10;
            _bench.Begin(s.benchmarkSeconds, s.benchmarkSliceMs, fighters, dec);
            _rlNext = 0f;
        }

        /// <summary>Demo mode: the HUD hides behind one chip (DemoMode).</summary>
        public void SetDemo(bool on, string caption)
        {
            if (_column == null) return;
            _column.style.display = on ? DisplayStyle.None : DisplayStyle.Flex;
            _demoChip.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            if (caption != null) _demoText.text = caption;
        }

        void UpdateOverlay()
        {
            EnsureOverlayParts();
            if (director == null) return;
            // inference latency: the brains that ran, averaged, then a rolling average over time
            double sum = 0; int n = 0;
            for (int i = 0; i < director.Playing && i < director.bodies.Length; i++) { var b = director.bodies[i]; if (b != null && b.LastInferenceMs > 0) { sum += b.LastInferenceMs; n++; } }
            var s = OverlaySettings.Current;
            if (n > 0) { float now = (float)(sum / n); _latencyAvg = _latencyAvg < 0 ? now : Mathf.Lerp(_latencyAvg, now, s.latencySmoothing); }
            if (!OverlayShown || Time.unscaledTime < _rlNext) return;
            _rlNext = Time.unscaledTime + s.refreshSeconds;
            int seat = BroadcastCamera.FollowSeat >= 0 && BroadcastCamera.FollowSeat < director.Playing ? BroadcastCamera.FollowSeat : 0;
            var sb = new System.Text.StringBuilder();
            sb.Append($"FPS {_fpsAvg:0} · physics tick {director.StepMs:0.00} ms\n");
            sb.Append(_latencyAvg >= 0 ? $"Inference latency {_latencyAvg:0.00} ms per brain (rolling average)\n" : "Inference latency - (no brain has run yet)\n");
            var body = director.bodies.Length > seat ? director.bodies[seat] : null;
            var ep = _meter != null ? _meter.Of(body) : null;
            string who = director.Playing > 0 ? $"{director.BadgeOf(director.AgentOf(seat))} (seat {seat + 1})" : "-";
            if (ep != null && director.Playing > 0)
            {
                sb.Append($"Episode reward {ep.Total:0.000} · {who} · {ep.Steps} steps\n  ");
                for (int t = 0; t < RewardMeter.TermCount; t++) sb.Append(RewardMeter.TermNames[t]).Append(' ').Append(ep.Sum[t].ToString("0.000")).Append(t < RewardMeter.TermCount - 1 ? " · " : "\n");
            }
            else sb.Append("Episode reward - (no round running)\n");
            sb.Append("Curriculum ").Append(s.StageLabel(body != null ? body.body : "body")).Append('\n');
            if (_bench != null && _bench.Result.Length > 0) sb.Append(_bench.Result);
            _rlText.text = sb.ToString().TrimEnd('\n');
            _rlBench.SetEnabled(_bench == null || !_bench.Running);
        }
    }
}
