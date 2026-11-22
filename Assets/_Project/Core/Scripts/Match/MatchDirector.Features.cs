using System;
using System.Collections.Generic;
using Mujoco;
using UnityEngine;

namespace PoKingHill
{
    public unsafe partial class MatchDirector
    {
        /// <summary>One heat or final match in a 4x4 Heats-and-Final tournament (#7).</summary>
        public sealed class HeatMatch
        {
            public string Label;
            public int[] Entrants;
            public int Winner = -1;
            public int Tries;
        }

        public float boxThrowCooldown = 5f;
        public IReadOnlyList<HeatMatch> HeatsBracket => _heats;
        public int HeatIndex => _heatAt;
        public bool InHeatsTournament => _heats.Count > 0;
        public bool IsDailyMatch => _dailyMatchActive;
        public bool IsRankedMatch => statsRounds == 0 && GameSettings.Modifier == ArenaModifier.Standard;
        public float BoxCooldownRemaining => Mathf.Max(0f, _boxReadyAt - Time.unscaledTime);
        public bool CanThrowBox => statsRounds == 0 && State == MatchState.Fight && _projectiles != null && BoxCooldownRemaining <= 0f;

        readonly List<HeatMatch> _heats = new();
        int _heatAt;
        bool _heatsLaunching;
        bool _dailyMatchActive, _dailyLaunching;
        bool _featureHooksReady;
        ProjectilePool _projectiles;
        float _boxReadyAt;

        // Modifier baseline cache (#17)
        bool _modCached;
        double _baseGravZ;
        readonly List<int> _arenaGeoms = new();
        readonly List<double> _baseArenaFriction = new();
        ArenaModifier _appliedModifier = ArenaModifier.Standard;

        void EnsureFeatureHooks()
        {
            if (_featureHooksReady) return;
            _featureHooksReady = true;
            MatchEvents.RoundEnded += OnFeatureRoundEnded;
            MatchEvents.Impact += OnFeatureImpact;
            if (GetComponent<PolicyBatcher>() == null) gameObject.AddComponent<PolicyBatcher>();
            _projectiles = FindAnyObjectByType<ProjectilePool>();
        }

        void TickFeatures()
        {
            EnsureFeatureHooks();
            if (State == MatchState.Menu || statsRounds > 0) RestoreModifier();
        }

        void OnNewRoundFeatures()
        {
            EnsureFeatureHooks();
            _boxReadyAt = 0f;
            if (statsRounds > 0) RestoreModifier();
            else ApplyModifier(GameSettings.Modifier);
        }

        void OnFeatureRoundEnded(RoundResult r)
        {
            if (this == null || !isActiveAndEnabled)
            {
                MatchEvents.RoundEnded -= OnFeatureRoundEnded;
                MatchEvents.Impact -= OnFeatureImpact;
                return;
            }
            if (statsRounds > 0) return;

            string winnerName = r.Winner >= 0 ? AgentNameAtBody(r.Winner) : null;
            string winnerBadge = r.Winner >= 0 && r.Winner < _agentAt.Length && _agentAt[r.Winner] >= 0 ? BadgeOf(_agentAt[r.Winner]) : winnerName;
            string pickTarget = r.Winner >= 0 && string.Equals(PredictionBook.CurrentPick, winnerName, StringComparison.OrdinalIgnoreCase)
                ? winnerName
                : winnerBadge;
            PredictionBook.Settle(pickTarget);

            if (IsRankedMatch && _playing >= 2)
            {
                var entrants = new List<string>(_playing);
                for (int i = 0; i < _playing; i++) entrants.Add(AgentNameAtBody(i));
                RivalryBook.RecordMatchResult(winnerName, entrants);
            }

            if (_dailyMatchActive && LastRoundOfMatch)
            {
                _dailyMatchActive = false;
                string champ = string.IsNullOrEmpty(winnerBadge) ? "Draw" : winnerBadge;
                int dailyScore = (r.Winner >= 0 ? 100 : 25) + Mathf.RoundToInt(Mathf.Max(0f, 120f - r.Seconds) * 2f) + Mathf.Max(0, PredictionBook.LastDelta);
                DailyChallenge.SubmitLocal("Player", champ, dailyScore, r.Seconds);
                if (!string.IsNullOrEmpty(GameSettings.DailyApiUrl))
                {
                    StartCoroutine(DailyChallenge.PostRemote(new DailyChallenge.Entry
                    {
                        date = DailyChallenge.TodayKey(),
                        player = "Player",
                        champion = champ,
                        score = dailyScore,
                        durationSec = r.Seconds
                    }));
                }
            }

            if (_heats.Count > 0) RecordHeatWinner(r.Winner);
        }

        void OnFeatureImpact(Vector3 worldPos, float strength, PolicyRunner victim)
        {
            if (this == null || !isActiveAndEnabled || statsRounds > 0 || State != MatchState.Fight) return;
            if (GameSettings.Modifier != ArenaModifier.HeavyKnockback || victim == null || victim.Map == null) return;
            if (!MjScene.InstanceExists || MjScene.Instance.Data == null) return;
            var d = MjScene.Instance.Data;
            int dof = victim.Map.RootDofAdr;
            double* q = d->qpos + victim.Map.RootQposAdr;
            Vector2 radial = new((float)q[0], (float)q[1]);
            if (radial.sqrMagnitude < 1e-4f)
            {
                Vector3 v = worldPos - victim.PelvisTransform.position;
                radial = new Vector2(v.x, v.z);
            }
            if (radial.sqrMagnitude > 1e-6f) radial.Normalize();
            double kick = 1.15 * Mathf.Clamp01(strength);
            d->qvel[dof] = Math.Clamp(d->qvel[dof] + radial.x * kick, -14.0, 14.0);
            d->qvel[dof + 1] = Math.Clamp(d->qvel[dof + 1] + radial.y * kick, -14.0, 14.0);
            d->qvel[dof + 2] = Math.Clamp(d->qvel[dof + 2] + 0.35 * kick, -8.0, 8.0);
        }

        /// <summary>Builds a 4x4 Heats-and-Final bracket from any 4..16 available fighters (#7).</summary>
        public static List<HeatMatch> BuildHeatsSchedule(IReadOnlyList<int> pool)
        {
            var list = new List<int>(pool);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            int heats = Mathf.Clamp(list.Count / 4, 2, 4);
            var schedule = new List<HeatMatch>(heats + 1);
            for (int h = 0; h < heats; h++)
            {
                int take = Math.Min(4, list.Count - h * 4);
                var ent = new int[take];
                for (int k = 0; k < take; k++) ent[k] = list[h * 4 + k];
                schedule.Add(new HeatMatch { Label = $"Heat {h + 1}", Entrants = ent });
            }
            var finalEntrants = new int[heats];
            for (int i = 0; i < heats; i++) finalEntrants[i] = -1;
            schedule.Add(new HeatMatch { Label = "Heats Final", Entrants = finalEntrants });
            return schedule;
        }

        /// <summary>Runs 4 heats of 4 fighters followed by a 4-fighter Final (#7).</summary>
        public void StartHeatsTournament()
        {
            var pool = InGame();
            if (pool.Count < 8)
            {
                if (!RandomizePlayerSlots(true))
                {
                    _status = "Need available fighters for 4x4 Heats";
                    return;
                }
                pool = new List<int>(playerSlots);
            }
            _heats.Clear();
            _heats.AddRange(BuildHeatsSchedule(pool));
            _heatAt = 0;
            PlayHeatMatch();
        }

        /// <summary>Launches a deterministic daily-seeded match (#15).</summary>
        public bool StartDailyMatch()
        {
            var candidates = InGame();
            if (!CanFillSlots(candidates)) candidates = Available();
            if (!CanFillSlots(candidates)) return false;

            var rng = new System.Random(DailyChallenge.TodaySeed(DateTime.UtcNow));
            var slots = new List<int>(bodies.Length);
            foreach (var b in bodies)
            {
                var compatible = new List<int>();
                foreach (int i in candidates)
                    if (i >= 0 && i < roster.Length && roster[i].available && roster[i].policy != null && roster[i].body == b.body)
                        compatible.Add(i);
                slots.Add(compatible[rng.Next(compatible.Count)]);
            }
            for (int i = slots.Count - 1; i > 0; i--)
            {
                int k = rng.Next(0, i + 1);
                (slots[i], slots[k]) = (slots[k], slots[i]);
            }
            _heats.Clear();
            _tournamentMatch = false;
            TournamentChampion = "";
            _dailyLaunching = true;
            try { StartMatch(slots); }
            finally { _dailyLaunching = false; }
            _status = $"Daily Match · Seed {DailyChallenge.TodaySeed()}";
            return true;
        }

        void PlayHeatMatch()
        {
            if (_heatAt < 0 || _heatAt >= _heats.Count) { _heats.Clear(); EnterMenu(); return; }
            var m = _heats[_heatAt];
            var list = new List<int>(m.Entrants.Length);
            for (int i = 0; i < m.Entrants.Length; i++) if (m.Entrants[i] >= 0) list.Add(m.Entrants[i]);
            _heatsLaunching = true;
            try { StartMatch(list); }
            finally { _heatsLaunching = false; }
            _status = $"{m.Label} ({_heatAt + 1}/{_heats.Count})";
        }

        void RecordHeatWinner(int winnerBody)
        {
            if (_heatAt < 0 || _heatAt >= _heats.Count) return;
            var m = _heats[_heatAt];
            if (winnerBody < 0 && m.Tries++ == 0)
            {
                _status = m.Label + ": tie, replaying heat";
                return;
            }
            int w;
            if (winnerBody >= 0 && winnerBody < _agentAt.Length && _agentAt[winnerBody] >= 0)
            {
                w = _agentAt[winnerBody];
            }
            else
            {
                w = m.Entrants[0];
                double bestR = double.MinValue;
                for (int i = 0; i < m.Entrants.Length; i++)
                {
                    int idx = m.Entrants[i];
                    if (idx < 0) continue;
                    double r = Ladder.Rating(NameOf(idx, "?"));
                    if (r > bestR) { bestR = r; w = idx; }
                }
            }
            m.Winner = w;
            if (m.Label != "Heats Final")
            {
                var final = _heats[^1];
                if (_heatAt < final.Entrants.Length) final.Entrants[_heatAt] = w;
            }
            else
            {
                TournamentChampion = NameOf(w, "?");
                _status = $"Heats Champion: {TournamentChampion}";
            }
        }

        bool AdvanceHeats()
        {
            if (_heats.Count == 0) return false;
            var m = _heats[_heatAt];
            if (m.Winner < 0) { PlayHeatMatch(); return true; }
            if (++_heatAt < _heats.Count) { PlayHeatMatch(); return true; }
            _heats.Clear();
            EnterMenu();
            return true;
        }

        /// <summary>Throws a physics box from the rim with a cooldown (#17).</summary>
        public bool ThrowSpectatorBox()
        {
            EnsureFeatureHooks();
            if (!CanThrowBox || !MjScene.InstanceExists || MjScene.Instance.Data == null) return false;
            var d = MjScene.Instance.Data;
            Vector3 target = new(0f, 0f, 0.9f);
            if (_alive != null && _alive.Count > 0)
            {
                var pick = _alive[UnityEngine.Random.Range(0, _alive.Count)];
                if (pick != null && pick.Map != null)
                {
                    double* q = d->qpos + pick.Map.RootQposAdr;
                    target = new Vector3((float)q[0], (float)q[1], (float)q[2] + 0.25f);
                }
            }
            _projectiles.FireAt(target, 9.5f, 3.2f);
            _boxReadyAt = Time.unscaledTime + boxThrowCooldown;
            MatchEvents.RaiseCaption("Spectator box incoming!");
            return true;
        }

        void CacheModifierBaseline(MujocoLib.mjModel_* m)
        {
            if (_modCached || m == null) return;
            _modCached = true;
            _baseGravZ = m->opt.gravity[2];
            _arenaGeoms.Clear();
            _baseArenaFriction.Clear();
            for (int g = 0; g < (int)m->ngeom; g++)
            {
                int b = m->geom_bodyid[g];
                string bname = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)MujocoLib.mj_id2name(m, (int)MujocoLib.mjtObj.mjOBJ_BODY, b)) ?? "";
                if (b == 0 || bname.IndexOf("arena", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _arenaGeoms.Add(g);
                    _baseArenaFriction.Add(m->geom_friction[3 * g]);
                }
            }
        }

        public void ApplyModifier(ArenaModifier mod)
        {
            if (!MjScene.InstanceExists || MjScene.Instance.Model == null) return;
            var m = MjScene.Instance.Model;
            CacheModifierBaseline(m);
            m->opt.gravity[2] = mod == ArenaModifier.MoonGravity ? _baseGravZ * 0.45 : _baseGravZ;
            double fScale = mod == ArenaModifier.IceSummit ? 0.22 : 1.0;
            for (int i = 0; i < _arenaGeoms.Count; i++)
            {
                int g = _arenaGeoms[i];
                m->geom_friction[3 * g] = Math.Max(0.05, _baseArenaFriction[i] * fScale);
            }
            _appliedModifier = mod;
        }

        public void RestoreModifier()
        {
            if (!_modCached || _appliedModifier == ArenaModifier.Standard) return;
            ApplyModifier(ArenaModifier.Standard);
        }
    }
}
