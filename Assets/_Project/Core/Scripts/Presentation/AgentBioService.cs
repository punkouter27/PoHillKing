using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace PoKingHill
{
    /// <summary>
    /// Fighter country metadata, flag codes and cached Wikipedia REST summary cards (#16).
    /// Offline fallback works without network access.
    /// </summary>
    public static class AgentBioService
    {
        public readonly struct Info
        {
            public readonly string CountryCode;
            public readonly string CountryName;
            public readonly string WikiTitle;
            public readonly string FallbackSummary;
            public Info(string code, string country, string wikiTitle, string fallback)
            {
                CountryCode = code; CountryName = country; WikiTitle = wikiTitle; FallbackSummary = fallback;
            }
        }

        [Serializable]
        class WikiRestSummary
        {
            public string extract;
        }

        static readonly Dictionary<string, Info> Meta = new(StringComparer.OrdinalIgnoreCase)
        {
            ["kim"] = new Info("KR", "South Korea", "King_of_the_Hill_(game)", "Balance-focused summit technician trained in bipedal locomotion and rim defense."),
            ["trump"] = new Info("US", "United States", "Sumo", "102 kg bulldozer grappler built around low center-of-mass momentum and forward shoves."),
            ["grandpa"] = new Info("GB", "United Kingdom", "Tug_of_war", "86 kg seasoned tactician that leans inside guard and redirects opponent momentum."),
            ["brawler"] = new Info("BR", "Brazil", "Greco-Roman_wrestling", "118 kg broad-shouldered heavyweight designed for close-range clinch and throw battles."),
            ["g1"] = new Info("CN", "China", "Humanoid_robot", "Unitree G1 humanoid baseline benchmark fighter for control and standing parity."),
        };

        static readonly Dictionary<string, string> _cachedSummaries = new(StringComparer.OrdinalIgnoreCase);

        static string CanonicalBody(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            int sp = body.IndexOf(' ');
            return sp > 0 ? body.Substring(0, sp) : body;
        }

        public static Info MetaOf(string body)
        {
            string key = CanonicalBody(body);
            if (!string.IsNullOrEmpty(key) && Meta.TryGetValue(key, out var info)) return info;
            return new Info("UN", "Earth", "King_of_the_Hill_(game)", "Autonomous physics fighter competing for summit dominance.");
        }

        public static string SummaryOf(string body)
        {
            string key = CanonicalBody(body);
            if (!string.IsNullOrEmpty(key) && _cachedSummaries.TryGetValue(key, out var cached)) return cached;
            return MetaOf(key).FallbackSummary;
        }

        public static bool TryParseWikiExtract(string json, out string extract)
        {
            extract = null;
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                var parsed = JsonUtility.FromJson<WikiRestSummary>(json);
                if (parsed == null || string.IsNullOrWhiteSpace(parsed.extract)) return false;
                string s = parsed.extract.Trim();
                if (s.Length > 180) s = s.Substring(0, 177).TrimEnd() + "...";
                extract = s;
                return true;
            }
            catch { return false; }
        }

        public static IEnumerator EnsureSummary(string body, Action<string> onReady = null)
        {
            if (string.IsNullOrEmpty(body)) yield break;
            if (_cachedSummaries.TryGetValue(body, out var existing)) { onReady?.Invoke(existing); yield break; }

            string disk = Path.Combine(Application.persistentDataPath, "wiki_" + body.ToLowerInvariant() + ".txt");
            if (File.Exists(disk))
            {
                try
                {
                    string txt = File.ReadAllText(disk);
                    if (!string.IsNullOrWhiteSpace(txt)) { _cachedSummaries[body] = txt; onReady?.Invoke(txt); yield break; }
                }
                catch { }
            }

            var info = MetaOf(body);
            string url = "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(info.WikiTitle);
            using var req = UnityWebRequest.Get(url);
            req.timeout = 5;
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success && TryParseWikiExtract(req.downloadHandler.text, out var extract))
            {
                _cachedSummaries[body] = extract;
                try { File.WriteAllText(disk, extract); } catch { }
                onReady?.Invoke(extract);
                yield break;
            }
            _cachedSummaries[body] = info.FallbackSummary;
            onReady?.Invoke(info.FallbackSummary);
        }
    }
}
