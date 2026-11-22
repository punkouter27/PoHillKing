using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// MIT-clean Verlet spring chain for visual hair, tie or cloth transforms with optional wind response (#12).
    /// Strictly visual (LateUpdate only); never touches MuJoCo or PhysX state.
    /// </summary>
    [DefaultExecutionOrder(220)]
    public class SpringBone : MonoBehaviour
    {
        public Vector3 localAxis = Vector3.down;
        [Range(0.01f, 0.99f)] public float stiffness = 0.25f;
        [Range(0.01f, 0.99f)] public float drag = 0.22f;
        public Vector3 gravity = new(0f, -2.5f, 0f);
        public float windScale = 0.6f;

        Vector3 _prevTip, _currTip;
        Quaternion _restLocalRot;
        float _boneLength;
        bool _initialized;

        static int _kothWindId;
        static int KothWindId => _kothWindId != 0 ? _kothWindId : (_kothWindId = Shader.PropertyToID("_KothWind"));

        void InitRest()
        {
            _restLocalRot = transform.localRotation;
            _boneLength = transform.childCount > 0
                ? Mathf.Max(0.04f, Vector3.Distance(transform.position, transform.GetChild(0).position))
                : 0.14f;
        }

        void OnEnable()
        {
            InitRest();
            ResetChain();
        }

        public void ResetChain()
        {
            if (_boneLength <= 0f) InitRest();
            Vector3 worldAxis = transform.TransformDirection(localAxis.normalized);
            _currTip = _prevTip = transform.position + worldAxis * _boneLength;
            _initialized = true;
        }

        void LateUpdate()
        {
            Step(Time.deltaTime, Shader.GetGlobalVector(KothWindId));
        }

        /// <summary>Advance one visual spring step; exposed for deterministic EditMode unit testing.</summary>
        public Vector3 Step(float dt, Vector4 wind = default)
        {
            if (dt <= 0f) return _currTip;
            if (!_initialized) OnEnable();
            transform.localRotation = _restLocalRot;

            Vector3 origin = transform.position;
            Vector3 restDir = transform.TransformDirection(localAxis.normalized);
            Vector3 restTip = origin + restDir * _boneLength;
            if ((_currTip - origin).sqrMagnitude > 2.0f) { ResetChain(); return _currTip; }

            float h = Mathf.Min(dt, 0.033f);
            Vector3 vel = (_currTip - _prevTip) * (1f - drag);
            _prevTip = _currTip;

            Vector3 force = gravity + new Vector3(wind.x, 0f, wind.z) * (wind.w * windScale);
            _currTip += vel + force * (h * h);
            _currTip = Vector3.Lerp(_currTip, restTip, Mathf.Clamp01(stiffness * h * 18f));
            Vector3 dir = _currTip - origin;
            _currTip = dir.sqrMagnitude > 1e-6f ? origin + dir.normalized * _boneLength : restTip;

            Quaternion aim = Quaternion.FromToRotation(restDir, (_currTip - origin).normalized);
            transform.rotation = aim * transform.rotation;
            return _currTip;
        }
    }
}
