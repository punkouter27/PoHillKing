using System;
using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

namespace PoKingHill
{
    /// <summary>
    /// Keyless Open-Meteo live weather sync for AtmosphereController (#14).
    /// Visual sky/clouds/wind only; never runs in stats gates and falls back cleanly when offline.
    /// </summary>
    public class LiveAtmosphere : MonoBehaviour
    {
        [Serializable]
        class OpenMeteoResponse
        {
            public CurrentBlock current;
        }

        [Serializable]
        class CurrentBlock
        {
            public float cloud_cover;
            public float wind_speed_10m;
            public float precipitation;
            public int weather_code;
        }

        public struct WeatherSample
        {
            public float CloudCover01;
            public float Storm01;
            public float WindKmh;
            public int WeatherCode;
            public string Summary;
        }

        public AtmosphereController atmosphere;
        public MatchDirector director;
        [Tooltip("Default summit coordinates (Jeju Hallasan ridge).")]
        public float latitude = 33.36f, longitude = 126.53f;
        public float refreshMinutes = 15f;

        public WeatherSample? Latest { get; private set; }
        public string StatusText { get; private set; } = "Offline / Local";

        float _nextFetchAt = -1f;
        bool _fetching;

        void OnEnable()
        {
            GameSettings.Changed += OnSettingsChanged;
            OnSettingsChanged();
        }

        void OnDisable()
        {
            GameSettings.Changed -= OnSettingsChanged;
        }

        void OnSettingsChanged()
        {
            if (!GameSettings.LiveWeather)
            {
                atmosphere?.ClearWeatherOverride();
                StatusText = "Live weather off";
                return;
            }
            if (director != null && director.statsRounds > 0) return;
            _nextFetchAt = 0f;
        }

        void Update()
        {
            if (!GameSettings.LiveWeather || _fetching) return;
            if (director != null && director.statsRounds > 0) return;
            if (_nextFetchAt >= 0f && Time.unscaledTime >= _nextFetchAt)
            {
                _nextFetchAt = Time.unscaledTime + refreshMinutes * 60f;
                StartCoroutine(FetchWeather());
            }
        }

        public void RefreshNow()
        {
            if (!GameSettings.LiveWeather || _fetching) return;
            _nextFetchAt = Time.unscaledTime + refreshMinutes * 60f;
            StartCoroutine(FetchWeather());
        }

        public static bool TryParseOpenMeteo(string json, out WeatherSample sample)
        {
            sample = default;
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                var resp = JsonUtility.FromJson<OpenMeteoResponse>(json);
                if (resp == null || resp.current == null) return false;
                var c = resp.current;
                float cloud = Mathf.Clamp01(c.cloud_cover / 100f);
                float storm = c.weather_code >= 95 ? 0.95f
                    : c.weather_code >= 61 || c.precipitation >= 1.5f ? 0.72f
                    : c.weather_code >= 51 || c.precipitation > 0f ? 0.42f
                    : Mathf.Max(0f, (cloud - 0.65f) * 0.6f);
                string cond = c.weather_code >= 95 ? "Thunderstorm"
                    : c.weather_code >= 51 ? "Rain"
                    : cloud > 0.7f ? "Overcast"
                    : cloud > 0.3f ? "Partly Cloudy" : "Clear";
                sample = new WeatherSample
                {
                    CloudCover01 = cloud,
                    Storm01 = Mathf.Clamp01(storm),
                    WindKmh = Mathf.Max(0f, c.wind_speed_10m),
                    WeatherCode = c.weather_code,
                    Summary = $"{cond} · Wind {c.wind_speed_10m:0} km/h"
                };
                return true;
            }
            catch { return false; }
        }

        IEnumerator FetchWeather()
        {
            _fetching = true;
            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}&current=cloud_cover,wind_speed_10m,precipitation,weather_code",
                latitude, longitude);
            using var req = UnityWebRequest.Get(url);
            req.timeout = 6;
            yield return req.SendWebRequest();
            _fetching = false;

            if (req.result == UnityWebRequest.Result.Success && TryParseOpenMeteo(req.downloadHandler.text, out var sample))
            {
                Latest = sample;
                StatusText = sample.Summary;
                if (atmosphere == null) atmosphere = FindAnyObjectByType<AtmosphereController>();
                atmosphere?.ApplyWeatherOverride(sample.CloudCover01, sample.Storm01);
            }
            else
            {
                StatusText = "Weather offline (using preset)";
            }
        }
    }
}
