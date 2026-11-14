using System.Collections.Generic;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// Procedural GPU-instanced spectator ring on the surrounding ruins/hills (#13).
    /// Zero PhysX components and zero per-frame managed allocations.
    /// </summary>
    public class CrowdRing : MonoBehaviour
    {
        public MatchDirector director;
        [Range(8, 120)] public int maxSpectators = 48;
        public float minRadius = 5.6f;
        public float maxRadius = 8.4f;
        public float ringY = -2.2f;

        struct Member
        {
            public Vector3 BasePos;
            public float YawDeg;
            public float Phase;
            public float Scale;
        }

        Member[] _members;
        Matrix4x4[] _matrices;
        Vector4[] _colors;
        Mesh _mesh;
        Material _crowdMat, _stoneMat;
        MaterialPropertyBlock _mpb;
        float _excitement, _cheerUntil;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        void OnEnable()
        {
            BuildResources();
            MatchEvents.Impact += OnImpact;
            MatchEvents.Knockout += OnKo;
            MatchEvents.RoundEnded += OnEnd;
        }

        void OnDisable()
        {
            MatchEvents.Impact -= OnImpact;
            MatchEvents.Knockout -= OnKo;
            MatchEvents.RoundEnded -= OnEnd;
        }

        void OnImpact(Vector3 p, float strength, PolicyRunner v)
        {
            _excitement = Mathf.Max(_excitement, Mathf.Clamp01(strength * 0.9f));
        }

        void OnKo(PolicyRunner v)
        {
            _excitement = 1f;
            _cheerUntil = Time.unscaledTime + 2.2f;
        }

        void OnEnd(RoundResult r)
        {
            _excitement = 1f;
            _cheerUntil = Time.unscaledTime + 3.2f;
        }

        void BuildResources()
        {
            if (_mesh == null) _mesh = BuildSpectatorMesh();
            var sh = Shader.Find("PoKingHill/CrowdSpectator")
                     ?? Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Universal Render Pipeline/Simple Lit");
            if (_crowdMat == null)
                _crowdMat = new Material(sh) { enableInstancing = true, name = "KothCrowdMat" };
            if (_stoneMat == null)
            {
                _stoneMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? sh) { enableInstancing = true, name = "KothCrowdStoneMat" };
                _stoneMat.SetColor(BaseColorId, new Color(0.28f, 0.27f, 0.25f, 1f));
            }
            _mpb ??= new MaterialPropertyBlock();

            int n = Mathf.Clamp(maxSpectators, 8, 120);
            _members = new Member[n];
            _matrices = new Matrix4x4[n];
            _colors = new Vector4[n];

            var rng = new System.Random(20261114);
            for (int i = 0; i < n; i++)
            {
                float frac = (float)i / n;
                float ang = frac * Mathf.PI * 2f + ((float)rng.NextDouble() - 0.5f) * 0.08f;
                int tier = i % 3;
                float r = Mathf.Lerp(minRadius, maxRadius, tier * 0.42f + (float)rng.NextDouble() * 0.16f);
                float y = ringY + (2 - tier) * 0.35f + ((float)rng.NextDouble() - 0.5f) * 0.08f;
                Vector3 pos = new(Mathf.Cos(ang) * r, y, Mathf.Sin(ang) * r);
                _members[i] = new Member
                {
                    BasePos = pos,
                    YawDeg = -ang * Mathf.Rad2Deg - 90f,
                    Phase = (float)rng.NextDouble() * Mathf.PI * 2f,
                    Scale = 0.88f + (float)rng.NextDouble() * 0.24f
                };
                Color c = Color.HSVToRGB((float)rng.NextDouble(), 0.48f + (float)rng.NextDouble() * 0.25f, 0.62f + (float)rng.NextDouble() * 0.28f);
                _colors[i] = c;
            }
            _mpb.SetVectorArray(BaseColorId, _colors);
        }

        int ActiveCount()
        {
            return GameSettings.Quality switch
            {
                Quality.Low => Mathf.Min(24, _members.Length),
                Quality.Medium => Mathf.Min(48, _members.Length),
                _ => _members.Length
            };
        }

        Matrix4x4[] _stoneMatrices;

        void Update()
        {
            if (_mesh == null || _crowdMat == null || _members == null) return;
            if (director != null && director.statsRounds > 0) return;

            _excitement = Mathf.MoveTowards(_excitement, 0f, Time.unscaledDeltaTime * 0.7f);
            bool cheer = Time.unscaledTime < _cheerUntil;
            float energy = cheer ? 1f : _excitement;
            float seaLevel = director != null && director.sea != null ? director.sea.Level : -10f;

            int n = ActiveCount();
            if (_stoneMatrices == null || _stoneMatrices.Length != _matrices.Length)
                _stoneMatrices = new Matrix4x4[_matrices.Length];
            int visible = 0;
            float t = Time.unscaledTime;
            for (int i = 0; i < n; i++)
            {
                ref readonly var m = ref _members[i];
                if (m.BasePos.y < seaLevel + 0.1f) continue;
                float wave = Mathf.Sin(t * (3.2f + energy * 7.5f) + m.Phase);
                float bounce = Mathf.Max(0f, wave) * (0.04f + 0.32f * energy);
                float lean = wave * (2f + 12f * energy);
                Vector3 pos = m.BasePos + new Vector3(0f, bounce, 0f);
                Quaternion rot = Quaternion.Euler(lean, m.YawDeg, 0f);
                _stoneMatrices[visible] = Matrix4x4.TRS(m.BasePos, Quaternion.Euler(0f, m.YawDeg, 0f), Vector3.one * m.Scale);
                _matrices[visible] = Matrix4x4.TRS(pos, rot, Vector3.one * m.Scale);
                visible++;
            }
            if (visible == 0) return;

            var stoneRp = new RenderParams(_stoneMat) { receiveShadows = false, shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off };
            var crowdRp = new RenderParams(_crowdMat) { matProps = _mpb, receiveShadows = false, shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off };
            Graphics.RenderMeshInstanced(stoneRp, _mesh, 0, _stoneMatrices, visible);
            Graphics.RenderMeshInstanced(crowdRp, _mesh, 1, _matrices, visible);
        }

        static Mesh BuildSpectatorMesh()
        {
            // Submesh 0: tall stone pillar anchored deep below the waterline; Submesh 1: colorful humanoid spectator.
            var verts = new List<Vector3>(192);
            var norms = new List<Vector3>(192);
            var stoneTris = new List<int>(96);
            var crowdTris = new List<int>(256);
            AddBox(verts, norms, stoneTris, new Vector3(0f, -4.5f, 0f), new Vector3(0.58f, 9.0f, 0.58f));
            AddBox(verts, norms, stoneTris, new Vector3(0f, -0.06f, 0f), new Vector3(0.76f, 0.14f, 0.76f));
            AddBox(verts, norms, crowdTris, new Vector3(0f, 0.48f, 0f), new Vector3(0.36f, 0.86f, 0.22f));
            AddBox(verts, norms, crowdTris, new Vector3(-0.23f, 0.76f, 0f), new Vector3(0.11f, 0.42f, 0.11f));
            AddBox(verts, norms, crowdTris, new Vector3(0.23f, 0.76f, 0f), new Vector3(0.11f, 0.42f, 0.11f));
            AddBox(verts, norms, crowdTris, new Vector3(0f, 1.12f, 0f), new Vector3(0.22f, 0.24f, 0.22f));
            var mesh = new Mesh { name = "ProceduralSpectator", subMeshCount = 2 };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetTriangles(stoneTris, 0);
            mesh.SetTriangles(crowdTris, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddBox(List<Vector3> verts, List<Vector3> norms, List<int> tris, Vector3 center, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            Vector3[] fn = { Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up, Vector3.down };
            Vector3[] fu = { Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.forward, Vector3.forward };
            for (int f = 0; f < 6; f++)
            {
                Vector3 n = fn[f], u = fu[f], r = Vector3.Cross(u, n);
                Vector3 c = center + Vector3.Scale(n, h);
                float ru = Mathf.Abs(Vector3.Dot(r, h)), uu = Mathf.Abs(Vector3.Dot(u, h));
                int v0 = verts.Count;
                verts.Add(c - r * ru - u * uu);
                verts.Add(c + r * ru - u * uu);
                verts.Add(c + r * ru + u * uu);
                verts.Add(c - r * ru + u * uu);
                for (int k = 0; k < 4; k++) norms.Add(n);
                tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 1);
                tris.Add(v0); tris.Add(v0 + 3); tris.Add(v0 + 2);
            }
        }
    }
}
