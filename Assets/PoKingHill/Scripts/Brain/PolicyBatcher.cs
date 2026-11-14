using System;
using System.Collections.Generic;
using System.Diagnostics;
using Mujoco;
using Unity.InferenceEngine;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// Batches neural inference across all fighters that share the same (ModelAsset, obsDim) on a physics tick (#2).
    /// Recompiles each static-batch-1 ONNX Model into a dynamic-batch graph via FunctionalGraph so up to 16 fighters
    /// run in a single Worker.Schedule call per unique brain/obsDim, with exact parity and zero per-tick allocations.
    /// </summary>
    [DefaultExecutionOrder(-110)]
    public unsafe class PolicyBatcher : MonoBehaviour
    {
        public static PolicyBatcher Instance { get; private set; }
        public static bool Active => Instance != null && Instance.isActiveAndEnabled && Instance.enabled;

        [Tooltip("Enable batched neural inference across bodies sharing (ModelAsset, obsDim).")]
        public bool batchInference = true;

        public int LastBatchCount { get; private set; }
        public int LastScheduleCount { get; private set; }

        readonly struct Key : IEquatable<Key>
        {
            public readonly ModelAsset Asset;
            public readonly int ObsDim;
            public Key(ModelAsset asset, int obsDim) { Asset = asset; ObsDim = obsDim; }
            public bool Equals(Key o) => Asset == o.Asset && ObsDim == o.ObsDim;
            public override bool Equals(object o) => o is Key k && Equals(k);
            public override int GetHashCode() => (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Asset) * 397) ^ ObsDim;
        }

        sealed class Bucket : IDisposable
        {
            public readonly int ObsDim;
            public readonly Worker Worker;
            public readonly List<Item> Items = new(16);
            readonly Dictionary<int, float[]> _bufs = new();
            readonly Dictionary<int, Tensor<float>> _tensors = new();

            public Bucket(ModelAsset asset, int obsDim)
            {
                ObsDim = obsDim;
                Model batched = BuildDynamicModel(asset, obsDim);
                Worker = new Worker(batched, BackendType.CPU);
            }

            public void Dispatch(MujocoLib.mjData_* d)
            {
                int count = Items.Count;
                if (count == 0) return;
                if (!_bufs.TryGetValue(count, out var buf))
                {
                    buf = new float[count * ObsDim];
                    _bufs[count] = buf;
                    _tensors[count] = new Tensor<float>(new TensorShape(count, ObsDim));
                }
                for (int k = 0; k < count; k++)
                    Array.Copy(Items[k].Obs, 0, buf, k * ObsDim, ObsDim);

                var tensor = _tensors[count];
                var sw = Stopwatch.StartNew();
                tensor.Upload(buf);
                Worker.Schedule(tensor);
                var output = Worker.PeekOutput() as Tensor<float>;
                output.CompleteAllPendingOperations();
                var span = output.AsReadOnlySpan();
                double perRunnerMs = sw.Elapsed.TotalMilliseconds / count;
                int stride = span.Length / count;
                for (int k = 0; k < count; k++)
                    Items[k].Runner.ApplyBatchedAction(d, span.Slice(k * stride, stride), Items[k].IsGetup, perRunnerMs);
                Items.Clear();
            }

            public void Dispose()
            {
                foreach (var t in _tensors.Values) t.Dispose();
                _tensors.Clear();
                Worker?.Dispose();
            }
        }

        struct Item
        {
            public PolicyRunner Runner;
            public float[] Obs;
            public bool IsGetup;
        }

        readonly List<PolicyRunner> _runners = new(32);
        readonly Dictionary<Key, Bucket> _buckets = new();
        readonly List<Bucket> _activeBuckets = new(8);
        bool _subscribed;

        /// <summary>Build a dynamic-batch InferenceEngine Model wrapping a static batch-1 ONNX asset.</summary>
        public static Model BuildDynamicModel(ModelAsset asset, int obsDim)
        {
            var src = ModelLoader.Load(asset);
            if (src.inputs.Count > 0)
            {
                // Rewrite batch axis on the loaded source input to dynamic (-1) so Functional.Forward propagates it.
                var inp0 = src.inputs[0];
                inp0.shape = new DynamicTensorShape(-1, obsDim);
                src.inputs[0] = inp0;
            }
            var graph = new FunctionalGraph();
            var x = graph.AddInput(DataType.Float, new DynamicTensorShape(-1, obsDim), src.inputs.Count > 0 ? src.inputs[0].name : "obs");
            var ys = Functional.Forward(src, x);
            graph.AddOutputs(ys);
            return graph.Compile();
        }

        /// <summary>Stateless helper used by tests to verify batch parity against single-worker execution.</summary>
        public static float[][] RunBatchOnce(ModelAsset asset, int obsDim, float[][] batchObs)
        {
            int n = batchObs.Length;
            var model = BuildDynamicModel(asset, obsDim);
            using var worker = new Worker(model, BackendType.CPU);
            var buf = new float[n * obsDim];
            for (int i = 0; i < n; i++) Array.Copy(batchObs[i], 0, buf, i * obsDim, obsDim);
            using var tensor = new Tensor<float>(new TensorShape(n, obsDim), buf);
            worker.Schedule(tensor);
            var output = worker.PeekOutput() as Tensor<float>;
            output.CompleteAllPendingOperations();
            var span = output.AsReadOnlySpan();
            int actDim = span.Length / n;
            var res = new float[n][];
            for (int i = 0; i < n; i++)
            {
                res[i] = new float[actDim];
                span.Slice(i * actDim, actDim).CopyTo(res[i]);
            }
            return res;
        }

        void OnEnable()
        {
            Instance = this;
            RefreshRunners();
            TrySubscribe();
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
            if (_subscribed && MjScene.InstanceExists) MjScene.Instance.preUpdateEvent -= OnPreStep;
            _subscribed = false;
            DisposeBuckets();
        }

        void Update()
        {
            if (!_subscribed) TrySubscribe();
        }

        void TrySubscribe()
        {
            if (_subscribed || !MjScene.InstanceExists) return;
            MjScene.Instance.preUpdateEvent += OnPreStep;
            _subscribed = true;
        }

        public void RefreshRunners()
        {
            _runners.Clear();
            var found = FindObjectsByType<PolicyRunner>(FindObjectsInactive.Include);
            _runners.AddRange(found);
        }

        void OnPreStep(object sender, MjStepArgs args)
        {
            if (!batchInference || !MjScene.InstanceExists) return;
            var d = MjScene.Instance.Data;
            if (d == null) return;

            _activeBuckets.Clear();
            int batchedItems = 0;
            for (int i = 0; i < _runners.Count; i++)
            {
                var r = _runners[i];
                if (r == null || !r.isActiveAndEnabled) continue;
                if (!r.PrepareBatchedTick(d, out var asset, out var obs, out int obsDim, out bool isGetup)) continue;
                if (asset == null) continue;
                var bucket = GetBucket(asset, obsDim);
                if (bucket.Items.Count == 0) _activeBuckets.Add(bucket);
                bucket.Items.Add(new Item { Runner = r, Obs = obs, IsGetup = isGetup });
                batchedItems++;
            }

            for (int b = 0; b < _activeBuckets.Count; b++) _activeBuckets[b].Dispatch(d);
            LastBatchCount = batchedItems;
            LastScheduleCount = _activeBuckets.Count;

            for (int i = 0; i < _runners.Count; i++)
            {
                var r = _runners[i];
                if (r != null && r.isActiveAndEnabled && r.Map != null && !r.paused) r.WriteCtrl(d);
            }
        }

        Bucket GetBucket(ModelAsset asset, int obsDim)
        {
            var key = new Key(asset, obsDim);
            if (!_buckets.TryGetValue(key, out var b))
            {
                b = new Bucket(asset, obsDim);
                _buckets[key] = b;
            }
            return b;
        }

        void DisposeBuckets()
        {
            foreach (var b in _buckets.Values) b.Dispose();
            _buckets.Clear();
        }
    }
}
