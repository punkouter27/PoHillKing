using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoKingHill
{
    public partial class HudView
    {
        bool _featuresBuilt;
        VisualElement _actionBar, _photoBar, _pipBox;
        Image _pipImg;
        Button _boxBtn, _povBtn, _photoBtn, _photoHideBtn;
        CameraExtras _camExtras;
        ShareBridge _share;
        LiveAtmosphere _liveWeather;
        int _pickCursor;
        bool _photoDrag;
        Vector2 _photoDragPos;

        void EnsureHudFeatures()
        {
            if (_featuresBuilt || _safe == null) return;
            _featuresBuilt = true;

            _camExtras = FindAnyObjectByType<CameraExtras>();
            if (_camExtras == null && director != null)
            {
                _camExtras = director.gameObject.AddComponent<CameraExtras>();
                _camExtras.director = director;
                _camExtras.replay = replay;
            }
            _share = FindAnyObjectByType<ShareBridge>();
            if (_share == null && director != null) _share = director.gameObject.AddComponent<ShareBridge>();
            _liveWeather = FindAnyObjectByType<LiveAtmosphere>();
            if (_liveWeather == null && director != null)
            {
                _liveWeather = director.gameObject.AddComponent<LiveAtmosphere>();
                _liveWeather.director = director;
            }
            if (director != null && director.GetComponent<AtmosphereController>() == null)
            {
                var atmo = director.gameObject.AddComponent<AtmosphereController>();
                atmo.director = director;
                if (_liveWeather != null) _liveWeather.atmosphere = atmo;
            }
            if (director != null && director.GetComponent<Haptics>() == null)
            {
                var h = director.gameObject.AddComponent<Haptics>();
                h.director = director;
            }
            if (director != null && director.GetComponent<CrowdRing>() == null)
            {
                var cr = director.gameObject.AddComponent<CrowdRing>();
                cr.director = director;
            }

            _actionBar = El("action-bar");
            _boxBtn = Btn("Throw box", "action-pill", () => director?.ThrowSpectatorBox(), _actionBar);
            _povBtn = Btn("POV", "action-pill", () => { _camExtras?.TogglePov(); }, _actionBar);
            _photoBtn = Btn("Photo", "action-pill", () => { _camExtras?.SetPhotoMode(!_camExtras.PhotoMode); }, _actionBar);
            int idx = _replayBar != null ? _safe.IndexOf(_replayBar) : _safe.childCount - 1;
            _safe.Insert(Mathf.Max(0, idx), _actionBar);

            _photoBar = El("photo-bar");
            Btn("Snap", "", () => _share?.CapturePhotoPng(), _photoBar);
            _photoHideBtn = Btn("Hide UI", "", () => _camExtras?.TogglePhotoUiHidden(), _photoBar);
            Btn("Done", "", () => _camExtras?.SetPhotoMode(false), _photoBar);
            _safe.Insert(Mathf.Max(0, _safe.IndexOf(_actionBar)), _photoBar);

            _pipBox = El("pip-box", _safe);
            _pipImg = new Image { scaleMode = ScaleMode.ScaleToFit };
            _pipImg.AddToClassList("pip-img");
            _pipBox.Add(_pipImg);
            Text("KO CAM", "pip-tag", _pipBox);
            _pipBox.style.display = DisplayStyle.None;

            _safe.RegisterCallback<PointerDownEvent>(OnPhotoPointerDown);
            _safe.RegisterCallback<PointerMoveEvent>(OnPhotoPointerMove);
            _safe.RegisterCallback<PointerUpEvent>(_ => _photoDrag = false);
            _safe.RegisterCallback<WheelEvent>(OnPhotoWheel);
        }

        void OnPhotoPointerDown(PointerDownEvent e)
        {
            if (_camExtras == null || !_camExtras.PhotoMode) return;
            if (e.target is Button) return;
            _photoDrag = true;
            _photoDragPos = e.position;
        }

        void OnPhotoPointerMove(PointerMoveEvent e)
        {
            if (!_photoDrag || _camExtras == null || !_camExtras.PhotoMode) return;
            Vector2 d = (Vector2)e.position - _photoDragPos;
            _photoDragPos = e.position;
            _camExtras.OrbitPhoto(d.x * 0.35f, d.y * 0.25f, 0f);
        }

        void OnPhotoWheel(WheelEvent e)
        {
            if (_camExtras == null || !_camExtras.PhotoMode) return;
            _camExtras.OrbitPhoto(0f, 0f, -e.delta.y * 0.15f);
        }

        void UpdateHudFeatures()
        {
            if (!_featuresBuilt || director == null) return;
            bool fight = director.State == MatchState.Fight;
            bool replaying = ReplaySystem.Active && director.State == MatchState.Replay;
            bool photo = _camExtras != null && _camExtras.PhotoMode && replaying;
            bool uiHidden = _camExtras != null && _camExtras.PhotoUiHidden;

            _actionBar.style.display = (fight || replaying) && !uiHidden && !_sim ? DisplayStyle.Flex : DisplayStyle.None;
            _boxBtn.style.display = fight && director.statsRounds == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (_boxBtn.style.display == DisplayStyle.Flex)
            {
                float cd = director.BoxCooldownRemaining;
                _boxBtn.text = cd > 0.1f ? $"Box {cd:0.0}s" : "Throw box";
                _boxBtn.SetEnabled(director.CanThrowBox);
            }
            _povBtn.style.display = fight || (replaying && !photo) ? DisplayStyle.Flex : DisplayStyle.None;
            _povBtn.EnableInClassList("active", _camExtras != null && _camExtras.PovEnabled);
            _photoBtn.style.display = replaying ? DisplayStyle.Flex : DisplayStyle.None;
            _photoBtn.EnableInClassList("active", photo);

            _photoBar.style.display = photo ? DisplayStyle.Flex : DisplayStyle.None;
            if (_photoHideBtn != null) _photoHideBtn.text = uiHidden ? "Show UI" : "Hide UI";

            bool pip = _camExtras != null && _camExtras.PipActive && _camExtras.PipTexture != null && !uiHidden;
            _pipBox.style.display = pip ? DisplayStyle.Flex : DisplayStyle.None;
            if (pip && _pipImg.image != _camExtras.PipTexture) _pipImg.image = _camExtras.PipTexture;

            if (_scoreboard != null) _scoreboard.style.visibility = uiHidden ? Visibility.Hidden : Visibility.Visible;
            if (_topBar != null) _topBar.style.visibility = uiHidden ? Visibility.Hidden : Visibility.Visible;
        }

        void AppendSheetFeatures()
        {
            if (_sheetBody == null || director == null) return;
            if (_sheetKind == SheetKind.Menu)
            {
                if (_tab == 0) AppendPlayFeatures();
                else if (_tab == 1) AppendAgentsFeatures();
                else AppendSettingsFeatures();
            }
            else if (_sheetKind == SheetKind.Result)
            {
                AppendResultFeatures();
            }
        }

        void AppendPlayFeatures()
        {
            var row2 = El("row spaced", _sheetBody);
            Btn("4x4 Heats", "secondary compact", () => director.StartHeatsTournament(), row2);
            Btn("Daily seed", "secondary compact", () => director.StartDailyMatch(), row2);

            var pickRow = El("pick-row", _sheetBody);
            string pickText = string.IsNullOrEmpty(PredictionBook.CurrentPick)
                ? $"Pick: none · Streak {PredictionBook.Streak} ({PredictionBook.Points} pts)"
                : $"Pick: {PredictionBook.CurrentPick} · Streak {PredictionBook.Streak} ({PredictionBook.Points} pts)";
            Text(pickText, "", pickRow);
            Btn("Cycle pick", "action-pill", () =>
            {
                var list = AvailableAgents();
                if (list.Count == 0) return;
                _pickCursor = (_pickCursor + 1) % (list.Count + 1);
                if (_pickCursor == list.Count) PredictionBook.ClearPick();
                else PredictionBook.SetPick(list[_pickCursor].name, 1f / Mathf.Max(2, list.Count));
                RebuildSheet();
            }, pickRow);
        }

        void AppendAgentsFeatures()
        {
            var list = AvailableAgents();
            if (list.Count == 0) return;
            var featured = list[Mathf.Clamp(director.selectedA, 0, list.Count - 1)];
            var meta = AgentBioService.MetaOf(featured.body);
            var (wins, matches, kos) = RivalryBook.CareerOf(featured.name);
            var (nem, losses, nemKos) = RivalryBook.NemesisOf(featured.name);

            var flagRow = El("flag-row", _sheetBody);
            flagRow.Add(new FlagBadge(meta.CountryCode));
            string nemText = string.IsNullOrEmpty(nem) ? "No nemesis yet" : $"Nemesis: {nem} ({losses}W/{nemKos}KO)";
            Text($"{featured.Badge} ({meta.CountryName}) · {wins}W/{matches}M/{kos}KO · {nemText}", "bio-line", flagRow);
            Text(AgentBioService.SummaryOf(featured.body), "bio-line", _sheetBody);
            StartCoroutine(AgentBioService.EnsureSummary(featured.body));
        }

        void AppendSettingsFeatures()
        {
            if (_section == 0)
            {
                Text("Arena modifier (unranked when not Standard)", "hint", _sheetBody);
                Seg(new[] { "Standard", "Moon", "Ice", "Heavy" }, (int)GameSettings.Modifier,
                    i => GameSettings.Modifier = (ArenaModifier)i);
                var grid = El("grid", _sheetBody);
                Chip(grid, "Haptics / Rumble", GameSettings.Haptics, v => GameSettings.Haptics = v);
            }
            else if (_section == 1)
            {
                Text("Atmosphere sky", "hint", _sheetBody);
                var atmoSeg = Seg(new[] { "Auto", "Dawn", "Noon", "Sunset", "Night", "Storm" }, (int)GameSettings.Atmosphere,
                    i => GameSettings.Atmosphere = (AtmosphereMode)i);
                atmoSeg.AddToClassList("wrap");
                var grid = El("grid", _sheetBody);
                Chip(grid, "Live weather", GameSettings.LiveWeather, v => GameSettings.LiveWeather = v);
            }
        }

        void AppendResultFeatures()
        {
            if (!string.IsNullOrEmpty(PredictionBook.LastOutcome))
                Text(PredictionBook.LastOutcome, "bio-line", _sheetBody);

            if (replay != null && !string.IsNullOrEmpty(replay.HighlightSummary))
                Text(replay.HighlightSummary, "bio-line", _sheetBody);

            if (director.InHeatsTournament && director.HeatsBracket.Count > 0)
            {
                var row = El("bracket", _sheetBody);
                for (int i = 0; i < director.HeatsBracket.Count; i++)
                {
                    var hm = director.HeatsBracket[i];
                    string win = hm.Winner >= 0 ? director.NameOf(hm.Winner, "?") : (i == director.HeatIndex ? "Live" : "TBD");
                    Text($"{hm.Label}: {win}", "bio-line", row);
                }
            }

            if (replay != null && replay.Available)
            {
                var shareRow = El("row spaced", _sheetBody);
                Btn("Share KO highlight", "secondary compact", () => _share?.ShareHighlightClip(replay.HighlightSummary), shareRow);
            }
        }

        void AddIntroFeatures(VisualElement introCard, VisualElement grid, Agent entry, string bodyName, string agentName)
        {
            var meta = AgentBioService.MetaOf(bodyName);
            var (nem, losses, _) = RivalryBook.NemesisOf(agentName);
            var flagRow = El("flag-row", introCard);
            int gridIdx = introCard.IndexOf(grid);
            introCard.Insert(Mathf.Max(0, gridIdx), flagRow);
            flagRow.Add(new FlagBadge(meta.CountryCode));
            string nemText = string.IsNullOrEmpty(nem) ? "Undefeated" : $"Nemesis: {nem} ({losses})";
            Text($"{meta.CountryName} · {nemText}", "bio-line", flagRow);

            var bio = Text(AgentBioService.SummaryOf(bodyName), "bio-line bio-clip ellipsis", introCard);
            StartCoroutine(AgentBioService.EnsureSummary(bodyName, s =>
            {
                if (bio.panel != null && !string.IsNullOrEmpty(s)) bio.text = s;
            }));
        }

        List<Agent> AvailableAgents()
        {
            var res = new List<Agent>();
            if (director == null || director.roster == null) return res;
            for (int i = 0; i < director.roster.Length; i++)
                if (director.roster[i].available) res.Add(director.roster[i]);
            return res;
        }
    }
}
