using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// Fighter POV camera (#9), Photo Mode orbit/zoom on replay pause (#6), and Picture-in-Picture KO cam (#9).
    /// Runs after BroadcastCamera (order 260) so main broadcast cuts remain untouched when extras are inactive.
    /// </summary>
    [DefaultExecutionOrder(260)]
    public class CameraExtras : MonoBehaviour
    {
        public MatchDirector director;
        public ReplaySystem replay;

        public bool PovEnabled { get; private set; }
        public int PovSeat { get; private set; }
        public bool PhotoMode { get; private set; }
        public bool PipActive => _pipCam != null && _pipCam.enabled;
        public RenderTexture PipTexture => _pipRt;

        float _photoYaw = 35f, _photoPitch = 18f, _photoDist = 3.2f;
        bool _photoUiHidden;
        public bool PhotoUiHidden => PhotoMode && _photoUiHidden;

        Camera _pipCam;
        RenderTexture _pipRt;
        Transform _pipTarget;
        float _pipUntil;

        void OnEnable()
        {
            MatchEvents.Knockout += OnKo;
            MatchEvents.RoundStarted += OnRoundStarted;
            MatchEvents.StateChanged += OnStateChanged;
        }

        void OnDisable()
        {
            MatchEvents.Knockout -= OnKo;
            MatchEvents.RoundStarted -= OnRoundStarted;
            MatchEvents.StateChanged -= OnStateChanged;
            SetPhotoMode(false);
            PovEnabled = false;
            if (_pipCam != null) _pipCam.enabled = false;
            if (_pipRt != null) { _pipRt.Release(); Destroy(_pipRt); _pipRt = null; }
        }

        void OnRoundStarted()
        {
            _pipUntil = 0f;
            if (_pipCam != null) _pipCam.enabled = false;
        }

        void OnStateChanged(MatchState state)
        {
            if (state == MatchState.Menu)
            {
                PovEnabled = false;
                SetPhotoMode(false);
                _pipUntil = 0f;
            }
            else if (state != MatchState.Replay && PhotoMode)
            {
                SetPhotoMode(false);
            }
        }

        void OnKo(PolicyRunner victim)
        {
            if (director == null || director.statsRounds > 0 || GameSettings.Quality == Quality.Low || victim == null) return;
            EnsurePipCamera();
            if (_pipCam == null) return;
            _pipTarget = victim.PelvisTransform;
            _pipUntil = Time.unscaledTime + 2.6f;
        }

        void EnsurePipCamera()
        {
            if (_pipCam != null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            _pipRt = new RenderTexture(320, 180, 16) { name = "KothPipKoRT" };
            if (!_pipRt.Create()) { Destroy(_pipRt); _pipRt = null; return; }
            var go = new GameObject("PipKoCamera");
            go.transform.SetParent(transform, false);
            _pipCam = go.AddComponent<Camera>();
            _pipCam.targetTexture = _pipRt;
            _pipCam.fieldOfView = 45f;
            _pipCam.depth = -5f;
            _pipCam.enabled = false;
        }

        public void TogglePov()
        {
            PovEnabled = !PovEnabled;
            if (PovEnabled && director != null && director.Playing > 0)
                PovSeat = Mathf.Clamp(PovSeat, 0, director.Playing - 1);
        }

        public void CyclePovSeat()
        {
            if (director == null || director.Playing <= 0) return;
            PovSeat = (PovSeat + 1) % director.Playing;
            PovEnabled = true;
        }

        public void SetPhotoMode(bool on)
        {
            PhotoMode = on;
            if (replay != null) replay.Hold = on;
            if (!on) _photoUiHidden = false;
        }

        public void TogglePhotoUiHidden()
        {
            if (PhotoMode) _photoUiHidden = !_photoUiHidden;
        }

        public void OrbitPhoto(float deltaYaw, float deltaPitch, float deltaZoom)
        {
            _photoYaw = Mathf.Repeat(_photoYaw + deltaYaw, 360f);
            _photoPitch = Mathf.Clamp(_photoPitch - deltaPitch, -20f, 75f);
            _photoDist = Mathf.Clamp(_photoDist - deltaZoom, 1.3f, 8.5f);
        }

        void LateUpdate()
        {
            if (director == null || director.statsRounds > 0) return;
            UpdatePip();
            var cam = Camera.main;
            if (cam == null) return;

            if (PhotoMode && director.State == MatchState.Replay)
            {
                var focus = ReplaySystem.Focus != null ? ReplaySystem.Focus.PelvisTransform : null;
                if (focus != null)
                {
                    Quaternion rot = Quaternion.Euler(_photoPitch, _photoYaw, 0f);
                    Vector3 lookAt = focus.position + Vector3.up * 0.45f;
                    cam.transform.SetPositionAndRotation(lookAt - rot * Vector3.forward * _photoDist, rot);
                }
                return;
            }

            if (PovEnabled && (director.State == MatchState.Fight || director.State == MatchState.Replay))
            {
                PolicyRunner subject = director.State == MatchState.Replay && ReplaySystem.Focus != null
                    ? ReplaySystem.Focus
                    : (PovSeat >= 0 && PovSeat < director.Playing ? director.bodies[PovSeat] : null);
                if (subject == null) return;
                var skin = FindSkin(subject);
                Vector3 headBase = skin != null && skin.HeadBone != null
                    ? skin.HeadBone.position
                    : subject.PelvisTransform.position + Vector3.up * 0.72f;
                Vector3 target = subject.opponent != null && subject.opponent.PelvisTransform != null
                    ? subject.opponent.PelvisTransform.position + Vector3.up * 0.72f
                    : headBase + (skin != null ? skin.TorsoBody.forward : subject.PelvisTransform.forward) * 3f;
                Vector3 toTarget = (target - headBase).sqrMagnitude > 0.01f
                    ? (target - headBase).normalized : Vector3.forward;
                Vector3 eye = headBase + Vector3.up * 0.14f + toTarget * 0.30f;
                if ((target - eye).sqrMagnitude > 0.04f)
                    cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation((target - eye).normalized, Vector3.up));
            }
        }

        void UpdatePip()
        {
            if (_pipCam == null) return;
            bool on = Time.unscaledTime < _pipUntil && _pipTarget != null && director.State == MatchState.Fight;
            _pipCam.enabled = on;
            if (!on) return;
            Vector3 p = _pipTarget.position;
            Vector3 outDir = new Vector3(p.x, 0f, p.z).sqrMagnitude > 0.05f
                ? new Vector3(p.x, 0f, p.z).normalized : Vector3.forward;
            Vector3 pos = p + outDir * 2.8f + Vector3.up * 1.4f;
            _pipCam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation((p - pos).normalized, Vector3.up));
        }

        static SkinFollower FindSkin(PolicyRunner r)
        {
            if (r == null) return null;
            var skins = FindObjectsByType<SkinFollower>(FindObjectsInactive.Exclude);
            for (int i = 0; i < skins.Length; i++)
                if (skins[i].robotPrefix == r.robotPrefix) return skins[i];
            return null;
        }
    }
}
