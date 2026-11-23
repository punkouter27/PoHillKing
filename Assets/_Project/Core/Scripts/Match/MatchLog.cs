using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Mujoco;
using UnityEngine;

namespace PoKingHill
{
    /// <summary>Elo rating per agent name, kept in persistentDataPath/ladder.json. Every completed hill round is rated by
    /// finishing order (Record), demo and CPU-only matches included: all agents are brains. The tournament standard: a new
    /// agent starts at 1000, K = 32. Ratings already in ladder.json (written under the older 1500 / K 24) are kept as
    /// they are; only agents the file does not know yet start at 1000.</summary>
    public static class Ladder
    {
        public const float Start = 1000f; public const float K = 32f;
        // wins/losses (decided rounds against another agent), knock-outs and assists (ContactLedger) came later: files
        // written before them read as 0.
        [Serializable] class Entry { public string name; public float rating; public int rounds, wins, losses, kos, assists; }
        [Serializable] class File { public List<Entry> agents = new(); }
        static Dictionary<string, Entry> _byName;
        /// <summary>Tests point the ladder at a scratch file so they never touch the player's ratings.</summary>
        public static string FileOverride { get => _override; set { _override = value; _byName = null; } }
        static string _override;
        static string PathOf => _override ?? System.IO.Path.Combine(Application.persistentDataPath, "ladder.json");

        static Dictionary<string, Entry> All()
        {
            if (_byName != null) return _byName;
            _byName = new Dictionary<string, Entry>();
            try { if (System.IO.File.Exists(PathOf)) foreach (var e in JsonUtility.FromJson<File>(System.IO.File.ReadAllText(PathOf)).agents) _byName[e.name] = e; }
            catch (Exception ex) { Debug.LogWarning("[Ladder] could not read " + PathOf + ": " + ex.Message); }
            return _byName;
        }
        static Entry Get(string name) { var all = All(); if (!all.TryGetValue(name, out var e)) all[name] = e = new Entry { name = name, rating = Start }; return e; }
        public static float Rating(string name) => Get(name).rating;
        public static int Rounds(string name) => Get(name).rounds;
        public static int Wins(string name) => Get(name).wins;
        public static int Losses(string name) => Get(name).losses;
        public static int Knockouts(string name) => Get(name).kos;
        public static int Assists(string name) => Get(name).assists;

        /// <summary>Knock-outs and assists one agent was credited with in a round (saved with the next Record).</summary>
        public static void AddCredits(string name, int kos, int assists) { if (kos == 0 && assists == 0) return; var e = Get(name); e.kos += kos; e.assists += assists; Save(); }

        /// <summary>Finishing place per seated body (0 = best, equal = tied): the round's winner first; then whoever was
        /// still in at the end (tied; with no winner they share first place); then the knocked-out, the later out the
        /// better. koAt = knock-out time per body (s, negative = not out); null = nobody but the winner placed.</summary>
        public static int[] Placements(int winner, int n, double[] koAt)
        {
            var key = new double[n];       // higher = better
            for (int i = 0; i < n; i++)
            {
                double ko = koAt != null && i < koAt.Length ? koAt[i] : -1;
                key[i] = i == winner ? double.MaxValue : ko < 0 ? double.MaxValue / 2 : ko;
            }
            var rank = new int[n];
            for (int i = 0; i < n; i++) { int better = 0; for (int j = 0; j < n; j++) if (key[j] > key[i]) better++; rank[i] = better; }
            return rank;
        }

        /// <summary>One completed round, rated by finishing order (the tournament standard's demo / multi-player rule,
        /// the same for every hill match: quick match, FFA 8, heats, daily seed, demo mode, bracket bouts). An agent
        /// seated on several bodies counts once, at its BEST place, and never plays itself. Every pair of distinct agents
        /// is one Elo game (ahead = win, same place = draw) with K scaled by 1/(N-1), N = distinct agents, so a 1v1
        /// bout is plain Elo and a 16-body match moves a rating about as much as one bout. Mirror-only rounds change
        /// nothing. Ratings update simultaneously from the pre-round values and are saved at once.</summary>
        public static void Record(in RoundResult r, Agent[] roster, double[] koAt = null)
        {
            int n = r.Agents.Length;
            var place = Placements(r.Winner, n, koAt);
            var best = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
            {
                int a = r.Agents[i];
                if (a < 0 || a >= roster.Length) continue;
                string name = roster[a].name;
                best[name] = best.TryGetValue(name, out int b) ? Math.Min(b, place[i]) : place[i];
            }
            if (best.Count < 2) return;
            var names = new List<string>(best.Keys);
            float k = K / (names.Count - 1);
            var delta = new Dictionary<string, float>();
            foreach (var name in names) delta[name] = 0f;
            for (int i = 0; i < names.Count; i++)
                for (int j = i + 1; j < names.Count; j++)
                {
                    string na = names[i], nb = names[j];
                    float sa = best[na] < best[nb] ? 1f : best[na] > best[nb] ? 0f : 0.5f;
                    float ea = 1f / (1f + Mathf.Pow(10f, (Rating(nb) - Rating(na)) / 400f));
                    delta[na] += k * (sa - ea); delta[nb] -= k * (sa - ea);
                }
            string winner = r.Winner >= 0 && r.Winner < n && r.Agents[r.Winner] >= 0 && r.Agents[r.Winner] < roster.Length ? roster[r.Agents[r.Winner]].name : null;
            foreach (var name in names)
            {
                var e = Get(name); e.rating += delta[name]; e.rounds++;
                if (winner != null) { if (name == winner) e.wins++; else e.losses++; }
            }
            Save();
        }

        static void Save()
        {
            try { var f = new File(); f.agents.AddRange(All().Values); System.IO.File.WriteAllText(PathOf, JsonUtility.ToJson(f, true)); }
            catch (Exception ex) { Debug.LogWarning("[Ladder] could not write " + PathOf + ": " + ex.Message); }
        }
    }

    /// <summary>Counts a round at most once, and only when it was started and not abandoned (pure, EditMode-tested).</summary>
    public sealed class RoundGate
    {
        bool _armed;
        public void Arm() => _armed = true;
        public void Abort() => _armed = false;
        public bool TryConsume() { bool ok = _armed; _armed = false; return ok; }
    }

    /// <summary>
    /// Round telemetry and the ladder. Samples every seated fighter at 10 Hz in the physics callback into preallocated
    /// buffers (pelvis position, uprightness, mean |action|), and when a round ends writes one JSON line to
    /// persistentDataPath/telemetry/&lt;YYMMDD&gt;.jsonl: agents, outcome, knock-out times, path length, top speed,
    /// action effort, final radius and the sampled kinematics, plus the frame rate the round was shown at (QualityTier).
    /// </summary>
    public unsafe class MatchLog : MonoBehaviour
    {
        public MatchDirector director;
        public ContactLedger ledger;                    // optional: knock-out credits in the telemetry and the ladder
        public QualityTier perf;                        // optional: frame rate, frame time and quality tier per round
        const int Hz = 10, MaxSeconds = 90, MaxBodies = MatchDirector.PlayersPerMatch;
        readonly float[,] _kin = new float[MaxBodies, MaxSeconds * Hz * 4];     // x, y, z, up per sample
        readonly double[] _path = new double[MaxBodies], _topSpeed = new double[MaxBodies], _effort = new double[MaxBodies], _koAt = new double[MaxBodies];
        readonly double[] _lastX = new double[MaxBodies], _lastY = new double[MaxBodies];
        int _samples, _tick, _effortTicks;
        readonly float[] _ratingDelta = new float[MaxBodies];
        public static string Folder => Path.Combine(Application.persistentDataPath, "telemetry");

        // Per-body numbers of the current (or just finished) round, for the results sheet.
        public float PathMetres(int body) => body >= 0 && body < MaxBodies ? (float)_path[body] : 0;
        public float TopSpeed(int body) => body >= 0 && body < MaxBodies ? (float)_topSpeed[body] : 0;
        public float KnockedOutAt(int body) => body >= 0 && body < MaxBodies ? (float)_koAt[body] : -1;
        public float RatingDelta(int body) => body >= 0 && body < MaxBodies ? _ratingDelta[body] : 0;

        void OnEnable()
        {
            MatchEvents.RoundStarted += OnStart; MatchEvents.RoundEnded += OnEnd; MatchEvents.Knockout += OnKo; MatchEvents.StateChanged += OnState;
            if (MjScene.InstanceExists) MjScene.Instance.postUpdateEvent += Post;
        }
        void OnDisable()
        {
            MatchEvents.RoundStarted -= OnStart; MatchEvents.RoundEnded -= OnEnd; MatchEvents.Knockout -= OnKo; MatchEvents.StateChanged -= OnState;
            if (MjScene.InstanceExists) MjScene.Instance.postUpdateEvent -= Post;
        }

        /// <summary>Only a round that started and ended is rated, once: armed by RoundStarted, spent by RoundEnded,
        /// disarmed when the menu comes back mid-round (a demo left by input, a match abandoned).</summary>
        public readonly RoundGate gate = new();

        void OnState(MatchState s) { if (s == MatchState.Menu || s == MatchState.Intro) gate.Abort(); }

        void OnStart()
        {
            gate.Arm();
            _samples = 0; _tick = 0; _effortTicks = 0;
            for (int i = 0; i < MaxBodies; i++) { _path[i] = _topSpeed[i] = _effort[i] = 0; _koAt[i] = -1; _lastX[i] = double.NaN; }
        }
        void OnKo(PolicyRunner r) { int i = Array.IndexOf(director.bodies, r); if (i >= 0 && i < MaxBodies) _koAt[i] = director.RoundTime; }

        void Post(object s, MjStepArgs a)
        {
            if (director == null || director.State != MatchState.Fight || director.statsRounds > 0) return;
            if (++_tick % (int)Mathf.Round(1f / (Hz * Time.fixedDeltaTime)) != 0) return;
            var d = MjScene.Instance.Data; int n = Mathf.Min(director.Playing, MaxBodies); bool room = _samples < MaxSeconds * Hz;
            for (int i = 0; i < n; i++)
            {
                var r = director.bodies[i]; double* q = d->qpos + r.Map.RootQposAdr; double* v = d->qvel + r.Map.RootDofAdr;
                if (!double.IsNaN(_lastX[i])) _path[i] += Math.Sqrt((q[0] - _lastX[i]) * (q[0] - _lastX[i]) + (q[1] - _lastY[i]) * (q[1] - _lastY[i]));
                _lastX[i] = q[0]; _lastY[i] = q[1];
                _topSpeed[i] = Math.Max(_topSpeed[i], Math.Sqrt(v[0] * v[0] + v[1] * v[1]));
                double e = 0; var act = r.LastAction; for (int k = 0; k < act.Length; k++) e += Math.Abs(act[k]); _effort[i] += e / Math.Max(1, act.Length);
                if (room) { int o = 4 * _samples; _kin[i, o] = (float)q[0]; _kin[i, o + 1] = (float)q[1]; _kin[i, o + 2] = (float)q[2]; _kin[i, o + 3] = (float)ObsBuilder.Up(q + 3); }
            }
            _effortTicks++; if (room) _samples++;
        }

        void OnEnd(RoundResult r)
        {
            if (director == null || director.statsRounds > 0) return;
            if (director.IsRankedMatch && gate.TryConsume())
            {
                for (int i = 0; i < r.Agents.Length && i < MaxBodies; i++) _ratingDelta[i] = -Ladder.Rating(director.NameOf(r.Agents[i], "Robot"));
                Ladder.Record(r, director.roster, _koAt);
                for (int i = 0; i < r.Agents.Length && i < MaxBodies; i++) _ratingDelta[i] += Ladder.Rating(director.NameOf(r.Agents[i], "Robot"));
                if (ledger != null)
                    for (int i = 0; i < r.Agents.Length && i < director.bodies.Length; i++)
                        if (r.Agents[i] >= 0) Ladder.AddCredits(director.NameOf(r.Agents[i], "Robot"), ledger.Knockouts(director.bodies[i]), ledger.Assists(director.bodies[i]));
            }
            else
            {
                Array.Clear(_ratingDelta, 0, _ratingDelta.Length);
            }
            try { Write(r); } catch (Exception ex) { Debug.LogWarning("[MatchLog] " + ex.Message); }
        }

        unsafe void Write(in RoundResult r)
        {
            var inv = CultureInfo.InvariantCulture; string F(double x) => x.ToString("0.###", inv);
            var d = MjScene.Instance.Data; var sb = new StringBuilder(4096);
            string stamp = DateTime.UtcNow.ToString("o", inv);
            sb.Append("{\"utc\":\"").Append(stamp).Append("\",\"version\":\"").Append(BuildInfo.Version)
              .Append("\",\"map\":\"").Append(director.maps.Length > director.selectedMap ? director.maps[director.selectedMap] : "").Append('"')
              .Append(",\"end\":\"").Append(r.End).Append("\",\"seconds\":").Append(F(r.Seconds)).Append(",\"winner\":").Append(r.Winner)
              .Append(",\"hz\":").Append(Hz).Append(",\"fighters\":[");
            for (int i = 0; i < r.Agents.Length && i < MaxBodies; i++)
            {
                var f = director.bodies[i]; double* q = d->qpos + f.Map.RootQposAdr; int agent = r.Agents[i];
                string name = director.NameOf(agent, "Robot");
                if (i > 0) sb.Append(',');
                sb.Append("{\"agent\":\"").Append(name).Append("\",\"body\":\"").Append(f.robotPrefix).Append("\",\"result\":\"")
                  .Append(r.Winner < 0 ? "tie" : r.Winner == i ? "win" : "loss").Append("\",\"rating\":").Append(F(Ladder.Rating(name)))
                  .Append(",\"ko_s\":").Append(F(_koAt[i])).Append(",\"path_m\":").Append(F(_path[i])).Append(",\"top_speed\":").Append(F(_topSpeed[i]))
                  .Append(",\"effort\":").Append(F(_effort[i] / Math.Max(1, _effortTicks))).Append(",\"final_r\":").Append(F(Math.Sqrt(q[0] * q[0] + q[1] * q[1])));
                if (ledger != null)
                    sb.Append(",\"kos\":").Append(ledger.Knockouts(f)).Append(",\"assists\":").Append(ledger.Assists(f)).Append(",\"pushed_ns\":").Append(F(ledger.Pushed(f)));
                sb.Append(",\"kin\":[");
                for (int k = 0; k < 4 * _samples; k++) { if (k > 0) sb.Append(','); sb.Append(F(_kin[i, k])); }
                sb.Append("]}");
            }
            sb.Append(']');
            if (perf != null)
                sb.Append(",\"perf\":{\"fps\":").Append(F(perf.Fps)).Append(",\"frame_ms\":").Append(F(perf.FrameMs)).Append(",\"p95_ms\":").Append(F(perf.FrameP95Ms))
                  .Append(",\"gc_kb_frame\":").Append(F(perf.GcKbPerFrame)).Append(",\"quality\":\"").Append(perf.Current).Append("\"}");
            sb.Append('}');
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, DateTime.Now.ToString("yyMMdd") + ".jsonl"), sb.Append('\n').ToString());
        }
    }
}
