using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace PoKingHill
{
    /// <summary>
    /// Deterministic UTC-day seeded match lineup and local/Azure leaderboard storage (#15).
    /// </summary>
    public static class DailyChallenge
    {
        [Serializable]
        public class Entry
        {
            public string date;
            public string player;
            public string champion;
            public int score;
            public float durationSec;
        }

        [Serializable]
        class BoardData
        {
            public List<Entry> entries = new();
        }

        static string _fileOverride;
        public static string FileOverride
        {
            get => _fileOverride;
            set { _fileOverride = value; _loaded = false; _data = null; }
        }

        static BoardData _data;
        static bool _loaded;

        static string PathOnDisk => string.IsNullOrEmpty(_fileOverride)
            ? Path.Combine(Application.persistentDataPath, "daily_leaderboard.json")
            : _fileOverride;

        public static int TodaySeed(DateTime? utcNow = null)
        {
            var d = (utcNow ?? DateTime.UtcNow).Date;
            return d.Year * 10000 + d.Month * 100 + d.Day;
        }

        public static string TodayKey(DateTime? utcNow = null)
        {
            var d = (utcNow ?? DateTime.UtcNow).Date;
            return d.ToString("yyyy-MM-dd");
        }

        /// <summary>Deterministic Fisher-Yates lineup from available agents for a UTC date.</summary>
        public static List<int> SelectOrder(IReadOnlyList<int> pool, DateTime? utcNow = null)
        {
            var res = new List<int>(pool.Count);
            for (int i = 0; i < pool.Count; i++) res.Add(pool[i]);
            var rng = new System.Random(TodaySeed(utcNow));
            for (int i = res.Count - 1; i > 0; i--)
            {
                int j = rng.Next(0, i + 1);
                (res[i], res[j]) = (res[j], res[i]);
            }
            return res;
        }

        public static void SubmitLocal(string player, string champion, int score, float durationSec, DateTime? utcNow = null)
        {
            Load();
            string key = TodayKey(utcNow);
            _data.entries.Add(new Entry
            {
                date = key,
                player = string.IsNullOrWhiteSpace(player) ? "Player" : player.Trim(),
                champion = champion ?? "?",
                score = Math.Max(0, score),
                durationSec = Math.Max(0f, durationSec)
            });
            _data.entries.Sort((a, b) =>
            {
                int c = b.score.CompareTo(a.score);
                return c != 0 ? c : a.durationSec.CompareTo(b.durationSec);
            });
            if (_data.entries.Count > 32) _data.entries.RemoveRange(32, _data.entries.Count - 32);
            Save();
        }

        public static List<Entry> TopToday(DateTime? utcNow = null, int max = 8)
        {
            Load();
            string key = TodayKey(utcNow);
            var list = new List<Entry>(max);
            for (int i = 0; i < _data.entries.Count && list.Count < max; i++)
                if (_data.entries[i].date == key) list.Add(_data.entries[i]);
            return list;
        }

        public static List<Entry> ParseServerJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return new List<Entry>();
            try
            {
                string wrapped = json.TrimStart().StartsWith("[") ? "{\"entries\":" + json + "}" : json;
                var b = JsonUtility.FromJson<BoardData>(wrapped);
                return b?.entries ?? new List<Entry>();
            }
            catch { return new List<Entry>(); }
        }

        public static IEnumerator PostRemote(Entry entry, Action<bool> onDone = null)
        {
            string baseUrl = GameSettings.DailyApiUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl)) { onDone?.Invoke(false); yield break; }
            string body = JsonUtility.ToJson(entry);
            using var req = new UnityWebRequest(baseUrl + "/daily-score", "POST");
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 6;
            yield return req.SendWebRequest();
            onDone?.Invoke(req.result == UnityWebRequest.Result.Success);
        }

        static void Load()
        {
            if (_loaded && _data != null) return;
            _loaded = true;
            _data = new BoardData();
            try
            {
                if (File.Exists(PathOnDisk))
                {
                    var parsed = JsonUtility.FromJson<BoardData>(File.ReadAllText(PathOnDisk));
                    if (parsed != null && parsed.entries != null) _data = parsed;
                }
            }
            catch (Exception e) { Debug.LogWarning("[DailyChallenge] load skipped: " + e.Message); }
        }

        static void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(PathOnDisk);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = PathOnDisk + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(_data, true));
                if (File.Exists(PathOnDisk)) File.Delete(PathOnDisk);
                File.Move(tmp, PathOnDisk);
            }
            catch (Exception e) { Debug.LogWarning("[DailyChallenge] save failed: " + e.Message); }
        }

        public static void ResetAll()
        {
            _data = new BoardData();
            _loaded = true;
            Save();
        }
    }
}
