using UnityEngine;
using UnityEngine.Rendering.Universal;

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
        public bool PipActive => _pipCam != null && _pipActive;
        public RenderTexture PipTexture => _pipRt;

        float _photoYaw = 35f, _photoPitch = 18f, _photoDist = 3.2f;
        bool _photoUiHidden;
        bool _pipActive;
        public bool PhotoUiHidden => PhotoMode && _photoUiHidden;

        Camera _pipCam;
        RenderTexture _pipRt;
        Transform _pipTarget;
        float _pipUntil;
        float _savedNearClip = -1f;
        Camera _savedNearClipCam;

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
            RestoreNearClip();
            SetPhotoMode(false);
            PovEnabled = false;
            if (_pipCam != null) _pipCam.enabled = false;
            if (_pipRt != null) { _pipRt.Release(); Destroy(_pipRt); _pipRt = null; }
        }

        void RestoreNearClip()
        {
            if (_savedNearClipCam != null && _savedNearClip > 0f)
                _savedNearClipCam.nearClipPlane = _savedNearClip;
            _savedNearClip = -1f;
            _savedNearClipCam = null;
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
            var desc = new RenderTextureDescriptor(320, 180, RenderTextureFormat.ARGB32, 24)
            {
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
                msaaSamples = 1
            };
            _pipRt = new RenderTexture(desc) { name = "KothPipKoRT" };
            if (!_pipRt.Create()) { Destroy(_pipRt); _pipRt = null; return; }
            var go = new GameObject("PipKoCamera");
            go.transform.SetParent(transform, false);
            _pipCam = go.AddComponent<Camera>();
            _pipCam.targetTexture = _pipRt;
            _pipCam.fieldOfView = 45f;
            _pipCam.depth = -5f;
            var main = Camera.main;
            if (main != null)
            {
                _pipCam.cullingMask = main.cullingMask;
                _pipCam.clearFlags = main.clearFlags;
                _pipCam.backgroundColor = main.backgroundColor;
                _pipCam.nearClipPlane = main.nearClipPlane;
                _pipCam.farClipPlane = main.farClipPlane;
            }
            else
            {
                _pipCam.clearFlags = CameraClearFlags.SolidColor;
                _pipCam.backgroundColor = new Color(0.08f, 0.12f, 0.18f, 1f);
            }
            var urpData = _pipCam.GetUniversalAdditionalCameraData();
            urpData.renderType = CameraRenderType.Base;
            urpData.renderPostProcessing = false;
            urpData.renderShadows = false;
            _pipCam.enabled = false;
        }

        public void TogglePov()
        {
            PovEnabled = !PovEnabled;
            if (!PovEnabled) RestoreNearClip();
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
                RestoreNearClip();
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
                if (director.State == MatchState.Fight && (PovSeat < 0 || PovSeat >= director.Playing || director.IsOut(PovSeat)))
                {
                    var alive = director.Alive;
                    if (alive != null && alive.Count > 0)
                    {
                        int found = System.Array.IndexOf(director.bodies, alive[0]);
                        if (found >= 0) PovSeat = found;
                    }
                }
                PolicyRunner subject = director.State == MatchState.Replay && ReplaySystem.Focus != null
                    ? ReplaySystem.Focus
                    : (PovSeat >= 0 && PovSeat < director.Playing ? director.bodies[PovSeat] : null);
                if (subject == null) return;
                if (_savedNearClipCam != cam)
                {
                    RestoreNearClip();
                    _savedNearClipCam = cam;
                    _savedNearClip = cam.nearClipPlane;
                }
                cam.nearClipPlane = Mathf.Max(_savedNearClip, 0.38f);

                var skin = FindSkin(subject);
                Vector3 headBase = skin != null && skin.HeadBone != null
                    ? skin.HeadBone.position
                    : subject.PelvisTransform.position + Vector3.up * 0.72f;
                var aliveList = director.Alive;
                Vector3 target = new(0f, Mathf.Max(0.75f, headBase.y), 0f);
                bool oppValid = subject.opponent != null && subject.opponent.PelvisTransform != null &&
                                (director.State == MatchState.Replay || aliveList == null || ContainsRunner(aliveList, subject.opponent));
                if (oppValid)
                {
                    target = subject.opponent.PelvisTransform.position + Vector3.up * 0.72f;
                }
                else
                {
                    float bestD = float.MaxValue;
                    if (aliveList != null)
                    {
                        for (int i = 0; i < aliveList.Count; i++)
                        {
                            if (aliveList[i] == null || aliveList[i] == subject || aliveList[i].PelvisTransform == null) continue;
                            float d2 = (aliveList[i].PelvisTransform.position - headBase).sqrMagnitude;
                            if (d2 < bestD) { bestD = d2; target = aliveList[i].PelvisTransform.position + Vector3.up * 0.72f; }
                        }
                    }
                }
                Vector3 toTarget = (target - headBase).sqrMagnitude > 0.01f
                    ? (target - headBase).normalized : Vector3.forward;
                Vector3 flatFwd = new(toTarget.x, 0f, toTarget.z);
                flatFwd = flatFwd.sqrMagnitude > 1e-4f ? flatFwd.normalized : Vector3.forward;
                Vector3 eye = headBase + Vector3.up * 0.34f + flatFwd * 0.50f;
                if ((target - eye).sqrMagnitude > 0.04f)
                    cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation((target - eye).normalized, Vector3.up));
            }
            else
            {
                RestoreNearClip();
            }
        }

        void UpdatePip()
        {
            if (_pipCam == null) { _pipActive = false; return; }
            _pipActive = Time.unscaledTime < _pipUntil && _pipTarget != null && director != null && director.State == MatchState.Fight;
            _pipCam.enabled = false;
            if (!_pipActive) return;
            Vector3 p = _pipTarget.position;
            Vector3 outDir = new Vector3(p.x, 0f, p.z).sqrMagnitude > 0.05f
                ? new Vector3(p.x, 0f, p.z).normalized : Vector3.forward;
            Vector3 pos = p + outDir * 2.8f + Vector3.up * 1.4f;
            if (director.sea != null) pos.y = Mathf.Max(pos.y, director.sea.Level + 0.6f);
            _pipCam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation((p - pos).normalized, Vector3.up));
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                _pipCam.Render();
        }

        static bool ContainsRunner(System.Collections.Generic.IReadOnlyList<PolicyRunner> list, PolicyRunner item)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == item) return true;
            return false;
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
