using System;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>
    /// Time-of-day and storm skybox + directional sun lighting controller (#5).
    /// Supports Auto, Dawn, Noon, Sunset, Night and Storm presets, plus optional LiveAtmosphere weather override.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class AtmosphereController : MonoBehaviour
    {
        public struct Preset
        {
            public Color Top, Horizon, Bottom, Sun, Fog, Ambient;
            public Vector3 SunEuler;
            public float Intensity, Stars, Storm, Clouds;
        }

        public MatchDirector director;
        public Light sun;
        public float blendSpeed = 1.5f;

        Material _skyMat;
        Preset _current, _target;
        bool _initialized;
        int _autoStep;
        float _weatherCloudOverride = -1f, _weatherStormOverride = -1f;

        static readonly int SkyTop = Shader.PropertyToID("_SkyTop");
        static readonly int SkyHorizon = Shader.PropertyToID("_SkyHorizon");
        static readonly int SkyBottom = Shader.PropertyToID("_SkyBottom");
        static readonly int SunColor = Shader.PropertyToID("_SunColor");
        static readonly int SunDir = Shader.PropertyToID("_SunDir");
        static readonly int StarStrength = Shader.PropertyToID("_StarStrength");
        static readonly int StormMix = Shader.PropertyToID("_StormMix");
        static readonly int CloudDensity = Shader.PropertyToID("_CloudDensity");

        public static Preset PresetFor(AtmosphereMode mode, DateTime localNow, int cycleIndex = 0)
        {
            if (mode == AtmosphereMode.Auto)
            {
                int hour = localNow.Hour;
                if (cycleIndex > 0)
                    mode = (AtmosphereMode)(1 + ((cycleIndex - 1) % 5));
                else if (hour >= 5 && hour < 8) mode = AtmosphereMode.Dawn;
                else if (hour >= 8 && hour < 17) mode = AtmosphereMode.Noon;
                else if (hour >= 17 && hour < 20) mode = AtmosphereMode.Sunset;
                else mode = AtmosphereMode.Night;
            }
            return mode switch
            {
                AtmosphereMode.Dawn => new Preset
                {
                    Top = new Color(0.18f, 0.28f, 0.56f), Horizon = new Color(0.95f, 0.64f, 0.44f), Bottom = new Color(0.16f, 0.14f, 0.22f),
                    Sun = new Color(1.0f, 0.80f, 0.58f), Fog = new Color(0.68f, 0.56f, 0.54f), Ambient = new Color(0.38f, 0.34f, 0.42f),
                    SunEuler = new Vector3(18f, 48f, 0f), Intensity = 1.05f, Stars = 0.25f, Storm = 0.05f, Clouds = 0.30f
                },
                AtmosphereMode.Sunset => new Preset
                {
                    Top = new Color(0.16f, 0.16f, 0.44f), Horizon = new Color(0.96f, 0.45f, 0.28f), Bottom = new Color(0.12f, 0.08f, 0.16f),
                    Sun = new Color(1.0f, 0.62f, 0.34f), Fog = new Color(0.58f, 0.38f, 0.36f), Ambient = new Color(0.36f, 0.26f, 0.32f),
                    SunEuler = new Vector3(14f, -62f, 0f), Intensity = 0.95f, Stars = 0.45f, Storm = 0.10f, Clouds = 0.38f
                },
                AtmosphereMode.Night => new Preset
                {
                    Top = new Color(0.02f, 0.04f, 0.12f), Horizon = new Color(0.08f, 0.14f, 0.28f), Bottom = new Color(0.02f, 0.03f, 0.06f),
                    Sun = new Color(0.55f, 0.68f, 0.96f), Fog = new Color(0.07f, 0.11f, 0.20f), Ambient = new Color(0.18f, 0.22f, 0.34f),
                    SunEuler = new Vector3(32f, 120f, 0f), Intensity = 0.55f, Stars = 1.0f, Storm = 0.05f, Clouds = 0.20f
                },
                AtmosphereMode.Storm => new Preset
                {
                    Top = new Color(0.11f, 0.14f, 0.20f), Horizon = new Color(0.30f, 0.36f, 0.44f), Bottom = new Color(0.07f, 0.09f, 0.12f),
                    Sun = new Color(0.68f, 0.74f, 0.85f), Fog = new Color(0.28f, 0.33f, 0.40f), Ambient = new Color(0.25f, 0.28f, 0.34f),
                    SunEuler = new Vector3(42f, 15f, 0f), Intensity = 0.65f, Stars = 0f, Storm = 0.85f, Clouds = 0.95f
                },
                _ => new Preset
                {
                    Top = new Color(0.14f, 0.38f, 0.76f), Horizon = new Color(0.60f, 0.78f, 0.92f), Bottom = new Color(0.10f, 0.18f, 0.28f),
                    Sun = new Color(1.0f, 0.96f, 0.88f), Fog = new Color(0.56f, 0.68f, 0.78f), Ambient = new Color(0.36f, 0.40f, 0.46f),
                    SunEuler = new Vector3(52f, -35f, 0f), Intensity = 1.15f, Stars = 0f, Storm = 0.0f, Clouds = 0.24f
                }
            };
        }

        public void ApplyWeatherOverride(float cloudCover01, float storm01)
        {
            _weatherCloudOverride = Mathf.Clamp01(cloudCover01);
            _weatherStormOverride = Mathf.Clamp01(storm01);
            RefreshTarget();
        }

        public void ClearWeatherOverride()
        {
            _weatherCloudOverride = -1f;
            _weatherStormOverride = -1f;
            RefreshTarget();
        }

        void OnEnable()
        {
            MatchEvents.RoundStarted += OnRoundStarted;
            GameSettings.Changed += RefreshTarget;
            EnsureSkyMaterial();
            RefreshTarget();
            _current = _target;
            _initialized = true;
            ApplyToScene(_current);
        }

        void OnDisable()
        {
            MatchEvents.RoundStarted -= OnRoundStarted;
            GameSettings.Changed -= RefreshTarget;
        }

        void OnRoundStarted()
        {
            _autoStep++;
            RefreshTarget();
        }

        void EnsureSkyMaterial()
        {
            if (_skyMat != null) return;
            var shader = Shader.Find("PoKingHill/SkyGradient");
            if (shader != null)
            {
                _skyMat = new Material(shader) { name = "KothDynamicSky" };
                RenderSettings.skybox = _skyMat;
            }
            if (sun == null) sun = FindAnyObjectByType<Light>();
        }

        public void RefreshTarget()
        {
            _target = PresetFor(GameSettings.Atmosphere, DateTime.Now, _autoStep);
            if (_weatherCloudOverride >= 0f) _target.Clouds = _weatherCloudOverride;
            if (_weatherStormOverride >= 0f) _target.Storm = Mathf.Max(_target.Storm, _weatherStormOverride);
        }

        void Update()
        {
            if (!_initialized) return;
            float t = 1f - Mathf.Exp(-blendSpeed * Time.unscaledDeltaTime);
            _current.Top = Color.Lerp(_current.Top, _target.Top, t);
            _current.Horizon = Color.Lerp(_current.Horizon, _target.Horizon, t);
            _current.Bottom = Color.Lerp(_current.Bottom, _target.Bottom, t);
            _current.Sun = Color.Lerp(_current.Sun, _target.Sun, t);
            _current.Fog = Color.Lerp(_current.Fog, _target.Fog, t);
            _current.Ambient = Color.Lerp(_current.Ambient, _target.Ambient, t);
            _current.SunEuler = Vector3.Lerp(_current.SunEuler, _target.SunEuler, t);
            _current.Intensity = Mathf.Lerp(_current.Intensity, _target.Intensity, t);
            _current.Stars = Mathf.Lerp(_current.Stars, _target.Stars, t);
            _current.Storm = Mathf.Lerp(_current.Storm, _target.Storm, t);
            _current.Clouds = Mathf.Lerp(_current.Clouds, _target.Clouds, t);
            ApplyToScene(_current);
        }

        void ApplyToScene(in Preset p)
        {
            float seaStorm = 0f;
            if (director != null && director.sea != null && director.State == MatchState.Fight)
                seaStorm = Mathf.Clamp01((director.sea.Level + director.sea.startDepth) / Mathf.Max(0.1f, director.sea.startDepth));
            float storm = Mathf.Max(p.Storm, seaStorm * seaStorm);

            if (sun != null)
            {
                sun.transform.rotation = Quaternion.Euler(p.SunEuler);
                sun.color = Color.Lerp(p.Sun, new Color(0.62f, 0.68f, 0.80f), storm * 0.75f);
                sun.intensity = Mathf.Lerp(p.Intensity, p.Intensity * 0.55f, storm);
            }
            if (_skyMat != null)
            {
                _skyMat.SetColor(SkyTop, Color.Lerp(p.Top, new Color(0.08f, 0.11f, 0.16f), storm * 0.7f));
                _skyMat.SetColor(SkyHorizon, Color.Lerp(p.Horizon, new Color(0.32f, 0.37f, 0.44f), storm * 0.8f));
                _skyMat.SetColor(SkyBottom, p.Bottom);
                _skyMat.SetColor(SunColor, p.Sun);
                Vector3 dir = sun != null ? -sun.transform.forward : Vector3.up;
                _skyMat.SetVector(SunDir, new Vector4(dir.x, dir.y, dir.z, 0f));
                _skyMat.SetFloat(StarStrength, p.Stars);
                _skyMat.SetFloat(StormMix, storm);
                _skyMat.SetFloat(CloudDensity, Mathf.Max(p.Clouds, storm * 0.9f));
            }
            RenderSettings.ambientLight = p.Ambient;
            if (GameSettings.Atmosphere != AtmosphereMode.Auto || _weatherStormOverride >= 0f)
                RenderSettings.fogColor = Color.Lerp(p.Fog, new Color(0.36f, 0.41f, 0.48f), storm * 0.8f);
        }
    }
}
