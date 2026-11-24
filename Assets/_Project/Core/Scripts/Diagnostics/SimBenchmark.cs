using System.Diagnostics;
using Mujoco;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// "Benchmark sim speed" in the Tab overlay: how fast THIS device steps the game's MuJoCo world, as a stand-in
    /// for training throughput. It copies the live mjData (all bodies, held joint targets) and steps the copy with
    /// mj_step for OverlaySettings.benchmarkSeconds of wall clock, a slice per frame so the page keeps drawing; the game's
    /// own simulation is untouched. Reported: physics steps/s, and samples/s = steps/s / decimation × fighters (one
    /// policy sample per fighter every 10 physics steps, as in training). It is a local simulation speed, not a training
    /// run: no network, no PPO update, one world on the CPU (training runs 4096 worlds on a GPU with MuJoCo Warp).
    /// </summary>
    public unsafe class SimBenchmark : MonoBehaviour
    {
        public bool Running { get; private set; }
        public string Result { get; private set; } = "";
        public long Steps { get; private set; }
        public double StepsPerSecond { get; private set; }
        public double SamplesPerSecond { get; private set; }

        MujocoLib.mjData_* _copy; double _budgetMs, _sliceMs; int _fighters, _decimation;
        readonly Stopwatch _sw = new();

        /// <summary>Starts a run (ignored while one runs). fighters: bodies with a brain; decimation: physics steps per
        /// policy step.</summary>
        public void Begin(float seconds, float sliceMs, int fighters, int decimation)
        {
            if (Running || !MjScene.InstanceExists || MjScene.Instance.Model == null || MjScene.Instance.Data == null) return;
            var m = MjScene.Instance.Model;
            _copy = MujocoLib.mj_makeData(m);
            MujocoLib.mj_copyData(_copy, m, MjScene.Instance.Data);
            _budgetMs = Mathf.Max(0.2f, seconds) * 1000.0; _sliceMs = Mathf.Clamp(sliceMs, 5f, 200f);
            _fighters = Mathf.Max(1, fighters); _decimation = Mathf.Max(1, decimation);
            Steps = 0; _sw.Reset(); Running = true; Result = "Benchmark running…";
        }

        void Update()
        {
            if (!Running) return;
            var m = MjScene.InstanceExists ? MjScene.Instance.Model : null;
            if (m == null) { Finish(); return; }
            var slice = Stopwatch.StartNew();
            _sw.Start();
            while (slice.Elapsed.TotalMilliseconds < _sliceMs && _sw.Elapsed.TotalMilliseconds < _budgetMs)
            {
                for (int k = 0; k < 10; k++) MujocoLib.mj_step(m, _copy);
                Steps += 10;
            }
            _sw.Stop();
            if (_sw.Elapsed.TotalMilliseconds >= _budgetMs) Finish();
        }

        void Finish()
        {
            double s = _sw.Elapsed.TotalSeconds;
            StepsPerSecond = s > 0 ? Steps / s : 0; SamplesPerSecond = StepsPerSecond / _decimation * _fighters;
            Result = Label(s, StepsPerSecond, SamplesPerSecond, _fighters, Hardware());
            Release(); Running = false;
            UnityEngine.Debug.Log("[SimBenchmark] " + Result);
        }

        /// <summary>The device it ran on as the player sees it: "CPU &lt;name&gt; x&lt;threads&gt;, GPU &lt;name&gt;" (a browser
        /// reports a generic CPU name and its WebGL renderer as the GPU).</summary>
        public static string Hardware()
        {
            string cpu = SystemInfo.processorType, gpu = SystemInfo.graphicsDeviceName;
            if (string.IsNullOrWhiteSpace(cpu)) cpu = "unknown";
            if (string.IsNullOrWhiteSpace(gpu)) gpu = "unknown";
            return $"this device: CPU {cpu.Trim()} x{SystemInfo.processorCount}, GPU {gpu.Trim()}";
        }

        /// <summary>The overlay's result line: the hardware, what was measured and how, and what it is not.</summary>
        public static string Label(double seconds, double stepsPerSecond, double samplesPerSecond, int fighters, string hardware) =>
            $"Benchmark on {hardware}. Measured: {seconds:0.0} s of MuJoCo mj_step on a copy of the live world, CPU main thread, " +
            $"1 world, {fighters} fighters (simulation only: no rendering, no inference): {stepsPerSecond:N0} physics steps/s = " +
            $"{samplesPerSecond:N0} policy samples/s. Local simulation speed, not a training run (training: 4096 worlds on a GPU).";

        void Release() { if (_copy != null) { MujocoLib.mj_deleteData(_copy); _copy = null; } }
        void OnDisable() { if (Running) { Running = false; Result = ""; } Release(); }
    }
}
