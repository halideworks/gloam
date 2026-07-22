using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using HDRGammaController.Core;

namespace HDRGammaController.ViewModels
{
    /// <summary>Long-lived dashboard state for Game Lab and the active session receipt.</summary>
    public sealed class GamerModeItem : ObservableObject
    {
        private const int RecentProfileLimit = 5;
        public ObservableCollection<GamerProfileEditorItem> Profiles { get; } = new();
        public ObservableCollection<string> RunningApps { get; } = new();
        public ObservableCollection<GamerDisplayOption> AvailableDisplays { get; } = new();
        public ObservableCollection<DiscoveredGameItem> DiscoveredGames { get; } = new();
        public ObservableCollection<GamerProfileEditorItem> FilteredProfiles { get; } = new();
        public ObservableCollection<DiscoveredGameItem> FilteredDiscoveredGames { get; } = new();
        private readonly Dictionary<string, string> _runningAppPaths =
            new(StringComparer.OrdinalIgnoreCase);

        private string _newAppText = string.Empty;
        private string _activeSessionSummary = "Waiting for a saved game";
        private string _activeSessionDetails = "Switch to a game and its look will turn on automatically.";
        private bool _hasActiveSession;
        private string _editorStatus = "Changes save automatically and update the active game.";
        private bool _isScanningForGames;
        private string _discoveryStatus =
            "Reads Steam, Epic, GOG, and Xbox library records. Gloam never opens or modifies a game.";
        private string _profileSearchText = string.Empty;
        private string _discoverySearchText = string.Empty;
        private GamerProfileEditorItem? _selectedProfile;
        private bool _largeAddArmed;
        private bool _clearProfilesArmed;
        private bool _showAllProfiles;
        private GamerLibraryPresetOption? _selectedLibraryPreset;

        public GamerModeItem()
        {
            Profiles.CollectionChanged += OnProfilesChanged;
            DiscoveredGames.CollectionChanged += OnDiscoveredGamesChanged;
        }

        public bool HasProfiles => Profiles.Count > 0;
        public string ProfileCountText => Profiles.Count == 1 ? "1 GAME" : $"{Profiles.Count} GAMES";
        public bool HasDiscoveryResults => DiscoveredGames.Count > 0;
        public bool HasFilteredProfiles => FilteredProfiles.Count > 0;
        public string FilteredProfileCountText => !string.IsNullOrWhiteSpace(ProfileSearchText)
            ? $"{FilteredProfiles.Count} RESULTS"
            : _showAllProfiles
                ? $"ALL · {Profiles.Count}"
                : $"RECENT · {FilteredProfiles.Count}";
        public string ShowAllProfilesLabel => _showAllProfiles
            ? "Recent only"
            : "View all";
        public IReadOnlyList<GamerLibraryPresetOption> AvailableLibraryPresets { get; } =
        [
            new("Accurate · all games", GamerPictureIntent.Reference, false),
            new("Competitive · all games", GamerPictureIntent.CompetitiveClarity, false),
            new("Night Play · all games", GamerPictureIntent.NightOps, false),
            new("HDR Cinema · detected HDR games", GamerPictureIntent.CinematicHdr, true)
        ];
        public int SelectedDiscoveryCount => DiscoveredGames.Count(item => item.IsSelected && !item.AlreadyAdded);
        public string AddDiscoveredButtonLabel => _largeAddArmed
            ? $"Confirm adding {SelectedDiscoveryCount} games"
            : SelectedDiscoveryCount switch
            {
                0 => "Add selected games",
                1 => "Add 1 selected game",
                _ => $"Add {SelectedDiscoveryCount} selected games"
            };
        public string ClearProfilesLabel => _clearProfilesArmed
            ? $"Confirm remove all {Profiles.Count}"
            : "Clear game library";

        public GamerLibraryPresetOption? SelectedLibraryPreset
        {
            get => _selectedLibraryPreset;
            set
            {
                if (SetProperty(ref _selectedLibraryPreset, value))
                    OnPropertyChanged(nameof(HasSelectedLibraryPreset));
            }
        }

        public bool HasSelectedLibraryPreset => SelectedLibraryPreset != null;

        public string ProfileSearchText
        {
            get => _profileSearchText;
            set
            {
                if (SetProperty(ref _profileSearchText, value ?? string.Empty))
                    RefreshProfileFilter();
            }
        }

        public string DiscoverySearchText
        {
            get => _discoverySearchText;
            set
            {
                if (SetProperty(ref _discoverySearchText, value ?? string.Empty))
                    RefreshDiscoveryFilter();
            }
        }

        public GamerProfileEditorItem? SelectedProfile
        {
            get => _selectedProfile;
            set => SetProperty(ref _selectedProfile, value);
        }

        /// <summary>
        /// Keeps a freshly added or externally activated profile visible even when it is not
        /// part of the ordinary six-item recent slice. This avoids the especially confusing
        /// state where Add succeeds but the editor appears not to change.
        /// </summary>
        public void SelectAndRevealProfile(GamerProfileEditorItem profile)
        {
            if (!Profiles.Contains(profile)) return;
            SelectedProfile = profile;
            RefreshProfileFilter();
        }

        public void SetRunningAppPaths(IReadOnlyDictionary<string, string> paths)
        {
            _runningAppPaths.Clear();
            foreach ((string appName, string executablePath) in paths)
            {
                if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(executablePath))
                    continue;
                _runningAppPaths[appName] = executablePath;
            }
        }

        public bool TryGetRunningAppPath(string appName, out string executablePath) =>
            _runningAppPaths.TryGetValue(
                AppExclusionRule.NormalizeAppName(appName), out executablePath!);

        public void ToggleShowAllProfiles()
        {
            _showAllProfiles = !_showAllProfiles;
            OnPropertyChanged(nameof(ShowAllProfilesLabel));
            RefreshProfileFilter();
        }

        public void UpdateProfileRecency(string appName, DateTime usedUtc)
        {
            GamerProfileEditorItem? profile = Profiles.FirstOrDefault(profile =>
                profile.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));
            if (profile == null) return;
            profile.SetLastUsedUtc(usedUtc);
            SelectedProfile = profile;
            RefreshProfileFilter();
        }

        public bool IsScanningForGames
        {
            get => _isScanningForGames;
            set => SetProperty(ref _isScanningForGames, value);
        }

        public string DiscoveryStatus
        {
            get => _discoveryStatus;
            set => SetProperty(ref _discoveryStatus, value);
        }

        public string NewAppText
        {
            get => _newAppText;
            set => SetProperty(ref _newAppText, value);
        }

        public string ActiveSessionSummary
        {
            get => _activeSessionSummary;
            private set => SetProperty(ref _activeSessionSummary, value);
        }

        public string ActiveSessionDetails
        {
            get => _activeSessionDetails;
            private set => SetProperty(ref _activeSessionDetails, value);
        }

        public bool HasActiveSession
        {
            get => _hasActiveSession;
            private set => SetProperty(ref _hasActiveSession, value);
        }

        public string EditorStatus
        {
            get => _editorStatus;
            private set => SetProperty(ref _editorStatus, value);
        }

        public void ShowSaving() => EditorStatus = "Applying changes…";

        public void ShowMessage(string message) => EditorStatus = message;

        public void ShowSaved() =>
            EditorStatus = $"Applied · {DateTime.Now:h:mm:ss tt}";

        public void ShowSaveFailed() =>
            EditorStatus = "Not saved · check that Gloam can write its settings file";

        public void SetSessions(IReadOnlyList<GamerSessionSnapshot>? sessions)
        {
            sessions ??= Array.Empty<GamerSessionSnapshot>();
            HasActiveSession = sessions.Count > 0;
            if (sessions.Count == 0)
            {
                ActiveSessionSummary = "Waiting for a saved game";
                ActiveSessionDetails = "Switch to a game and its look will turn on automatically.";
                return;
            }

            GamerSessionSnapshot first = sessions[0];
            string title = string.IsNullOrWhiteSpace(first.DisplayName) ? first.AppName : first.DisplayName;
            ActiveSessionSummary = sessions.Count == 1
                ? $"{title} is active"
                : $"{title} is active on {sessions.Count} displays";

            var lines = new List<string>();
            foreach (GamerSessionSnapshot session in sessions)
            {
                string monitor = string.IsNullOrWhiteSpace(session.MonitorName)
                    ? session.MonitorDevicePath : session.MonitorName;
                string shadow = session.ShadowDetailStrength <= 0.0001
                    ? "shadows reference"
                    : $"shadows +{session.ShadowDetailStrength * 100:F0}% below {session.ShadowDetailPivot * 100:F0}% linear";
                string rendering =
                    $"{IntentLabel(session.PictureIntent)} · {GammaLabel(session.EffectiveGamma)} · {shadow} · {NightPolicyLabel(session.NightPolicy)}";
                string findings = session.Diagnostics.Count == 0
                    ? "signal chain verified from available Windows output facts"
                    : string.Join("  ", session.Diagnostics.Select(d =>
                        $"[{SeverityLabel(d.Severity)}] {d.Message}"));
                lines.Add($"{monitor}: {rendering}. {findings}");
            }
            ActiveSessionDetails = string.Join(Environment.NewLine, lines);
        }

        public void SetDiscoveryResults(IReadOnlyList<DiscoveredGame> games, ISet<string> savedApps)
        {
            var selectedPaths = DiscoveredGames
                .Where(item => item.IsSelected)
                .Select(item => item.ExecutablePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            DiscoveredGames.Clear();
            foreach (DiscoveredGame game in games)
            {
                bool alreadyAdded = savedApps.Contains(game.ExecutableName);
                if (!alreadyAdded)
                {
                    var item = new DiscoveredGameItem(game, alreadyAdded: false)
                    {
                        IsSelected = selectedPaths.Contains(game.ExecutablePath)
                    };
                    DiscoveredGames.Add(item);
                }
            }
            ResetLargeAddConfirmation();
            RefreshDiscoveryFilter();
        }

        public void ArmLargeAddConfirmation()
        {
            _largeAddArmed = true;
            OnPropertyChanged(nameof(IsLargeAddArmed));
            OnPropertyChanged(nameof(AddDiscoveredButtonLabel));
        }

        public bool IsLargeAddArmed => _largeAddArmed;

        public void ResetLargeAddConfirmation()
        {
            _largeAddArmed = false;
            OnPropertyChanged(nameof(IsLargeAddArmed));
            OnPropertyChanged(nameof(AddDiscoveredButtonLabel));
        }

        public bool ToggleClearProfilesConfirmation()
        {
            if (!_clearProfilesArmed)
            {
                _clearProfilesArmed = true;
                OnPropertyChanged(nameof(ClearProfilesLabel));
                return false;
            }

            _clearProfilesArmed = false;
            OnPropertyChanged(nameof(ClearProfilesLabel));
            return true;
        }

        public void ResetClearProfilesConfirmation()
        {
            _clearProfilesArmed = false;
            OnPropertyChanged(nameof(ClearProfilesLabel));
        }

        private void OnProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (GamerProfileEditorItem profile in e.OldItems)
                    profile.PropertyChanged -= OnProfilePropertyChanged;
            if (e.NewItems != null)
                foreach (GamerProfileEditorItem profile in e.NewItems)
                    profile.PropertyChanged += OnProfilePropertyChanged;

            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(ProfileCountText));
            OnPropertyChanged(nameof(ClearProfilesLabel));
            OnPropertyChanged(nameof(ShowAllProfilesLabel));
            RefreshProfileFilter();
        }

        private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(GamerProfileEditorItem.DisplayName)
                or nameof(GamerProfileEditorItem.GameTitle)
                or nameof(GamerProfileEditorItem.LastUsedUtc))
                RefreshProfileFilter();
        }

        private void RefreshProfileFilter()
        {
            GamerProfileEditorItem? previous = SelectedProfile;
            string query = ProfileSearchText.Trim();
            IEnumerable<GamerProfileEditorItem> matches;
            if (query.Length > 0)
            {
                matches = Profiles.Where(profile =>
                    profile.GameTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    profile.AppName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(profile => profile.GameTitle, StringComparer.CurrentCultureIgnoreCase);
            }
            else if (_showAllProfiles)
            {
                matches = Profiles.OrderBy(
                    profile => profile.GameTitle, StringComparer.CurrentCultureIgnoreCase);
            }
            else
            {
                matches = Profiles
                    .OrderByDescending(profile => profile.LastUsedUtc.HasValue)
                    .ThenByDescending(profile => profile.LastUsedUtc ?? DateTime.MinValue)
                    .ThenBy(profile => profile.GameTitle, StringComparer.CurrentCultureIgnoreCase)
                    .Take(RecentProfileLimit)
                    .ToList();
            }

            List<GamerProfileEditorItem> visible = matches.ToList();
            if (query.Length == 0 && !_showAllProfiles && previous != null &&
                Profiles.Contains(previous) && !visible.Contains(previous))
            {
                visible.Insert(0, previous);
                if (visible.Count > RecentProfileLimit) visible.RemoveAt(visible.Count - 1);
            }

            FilteredProfiles.Clear();
            foreach (GamerProfileEditorItem profile in visible)
                FilteredProfiles.Add(profile);

            SelectedProfile = previous != null && FilteredProfiles.Contains(previous)
                ? previous
                : FilteredProfiles.FirstOrDefault();
            OnPropertyChanged(nameof(HasFilteredProfiles));
            OnPropertyChanged(nameof(FilteredProfileCountText));
        }

        private void OnDiscoveredGamesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (DiscoveredGameItem item in e.OldItems)
                    item.PropertyChanged -= OnDiscoveredGamePropertyChanged;
            if (e.NewItems != null)
                foreach (DiscoveredGameItem item in e.NewItems)
                    item.PropertyChanged += OnDiscoveredGamePropertyChanged;

            OnPropertyChanged(nameof(HasDiscoveryResults));
            RefreshDiscoveryFilter();
            NotifyDiscoverySelectionChanged();
        }

        private void OnDiscoveredGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DiscoveredGameItem.IsSelected))
            {
                ResetLargeAddConfirmation();
                NotifyDiscoverySelectionChanged();
            }
        }

        private void RefreshDiscoveryFilter()
        {
            string query = DiscoverySearchText.Trim();
            FilteredDiscoveredGames.Clear();
            foreach (DiscoveredGameItem item in DiscoveredGames.Where(item =>
                query.Length == 0 ||
                item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.ExecutableName.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredDiscoveredGames.Add(item);
            }
        }

        private void NotifyDiscoverySelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedDiscoveryCount));
            OnPropertyChanged(nameof(AddDiscoveredButtonLabel));
        }

        private static string IntentLabel(GamerPictureIntent intent) => intent switch
        {
            GamerPictureIntent.CompetitiveClarity => "Competitive Clarity",
            GamerPictureIntent.CinematicHdr => "Cinematic HDR",
            GamerPictureIntent.NightOps => "Night Ops",
            _ => intent.ToString()
        };

        private static string SeverityLabel(GamerDiagnosticSeverity severity) => severity switch
        {
            GamerDiagnosticSeverity.Critical => "CHECK",
            GamerDiagnosticSeverity.Warning => "WARN",
            _ => "INFO"
        };

        private static string GammaLabel(GammaMode gamma) => gamma switch
        {
            GammaMode.Gamma22 => "Gamma 2.2",
            GammaMode.Gamma24 => "Gamma 2.4",
            _ => "Windows Default"
        };

        private static string NightPolicyLabel(GamerNightPolicy policy) => policy switch
        {
            GamerNightPolicy.ForceDaylight => "Daylight 6500K",
            GamerNightPolicy.NightOps => "Spectral Night Ops",
            _ => "Schedule"
        };
    }

    public sealed record GamerDisplayOption(string DevicePath, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record GamerChoice<T>(T Value, string Label, string Description = "") where T : struct, Enum
    {
        // The shared ComboBox template renders SelectionBoxItem directly. Keeping this
        // fallback human-readable prevents record debug text from leaking into the UI even
        // when WPF does not propagate DisplayMemberPath into the collapsed presenter.
        public override string ToString() => Label;
    }

    public sealed record GamerLibraryPresetOption(
        string Label,
        GamerPictureIntent Intent,
        bool HdrCapableOnly)
    {
        public override string ToString() => Label;
    }

    public sealed class DiscoveredGameItem : ObservableObject
    {
        private bool _isSelected;

        public DiscoveredGameItem(DiscoveredGame game, bool alreadyAdded)
        {
            Game = game;
            AlreadyAdded = alreadyAdded;
        }

        public DiscoveredGame Game { get; }
        public string DisplayName => Game.DisplayName;
        public string ExecutableName => Game.ExecutableName;
        public string ExecutablePath => Game.ExecutablePath;
        public string Library => Game.Library;
        public GamerHdrCapability HdrCapability => Game.HdrCapability;
        public bool HasDetectedHdr => Game.HdrCapability == GamerHdrCapability.Detected;
        public string? HdrEvidence => Game.HdrEvidence;
        public bool AlreadyAdded { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (!AlreadyAdded) SetProperty(ref _isSelected, value);
            }
        }
    }

    /// <summary>Observable editor wrapper around the Core persistence model.</summary>
    public sealed class GamerProfileEditorItem : ObservableObject
    {
        private GamerProfileRule _profile;

        public GamerProfileEditorItem(GamerProfileRule profile)
        {
            _profile = (profile ?? throw new ArgumentNullException(nameof(profile))).Sanitized();
        }

        public IReadOnlyList<GamerChoice<GamerPictureIntent>> AvailablePictureIntents { get; } =
        [
            new(GamerPictureIntent.Reference, "Accurate", "Keeps the game faithful and uses your measured display correction."),
            new(GamerPictureIntent.CompetitiveClarity, "Competitive", "Reveals dark movement while bright areas stay unchanged."),
            new(GamerPictureIntent.CinematicHdr, "HDR Cinema", "Protects the HDR signal and warns when Windows is set up incorrectly."),
            new(GamerPictureIntent.NightOps, "Night Play", "Warms late sessions while preserving useful color separation."),
            new(GamerPictureIntent.Custom, "Custom")
        ];
        public IReadOnlyList<GamerChoice<GamerPictureIntent>> AvailableQuickLooks { get; } =
        [
            new(GamerPictureIntent.Reference, "Accurate", "Faithful color"),
            new(GamerPictureIntent.CompetitiveClarity, "Competitive", "See into shadows"),
            new(GamerPictureIntent.CinematicHdr, "HDR Cinema", "Preserve HDR"),
            new(GamerPictureIntent.NightOps, "Night Play", "Easy on your eyes")
        ];
        public IReadOnlyList<GamerChoice<GamerNightPolicy>> AvailableNightPolicies { get; } =
        [
            new(GamerNightPolicy.FollowSchedule, "Follow schedule"),
            new(GamerNightPolicy.ForceDaylight, "Force daylight (6500K)"),
            new(GamerNightPolicy.NightOps, "Spectral Night Ops")
        ];
        public IReadOnlyList<GamerChoice<GamerHdrExpectation>> AvailableHdrExpectations { get; } =
        [
            new(GamerHdrExpectation.Automatic, "Automatic"),
            new(GamerHdrExpectation.RequireSdr, "Require SDR"),
            new(GamerHdrExpectation.RequireHdr, "Require HDR")
        ];
        public IReadOnlyList<GamerChoice<GamerHdrCapability>> AvailableHdrCapabilities { get; } =
        [
            new(GamerHdrCapability.Unknown, "Not detected", "Excluded from automatic HDR bulk changes."),
            new(GamerHdrCapability.Detected, "Detected automatically", "A local game configuration advertises HDR output."),
            new(GamerHdrCapability.UserConfirmed, "Yes, supports HDR", "Include this game in HDR bulk changes."),
            new(GamerHdrCapability.SdrOnly, "No, SDR only", "Never include this game in HDR bulk changes.")
        ];
        public IReadOnlyList<GamerChoice<GamerDisplayScope>> AvailableDisplayScopes { get; } =
        [
            new(GamerDisplayScope.WindowDisplays, "Game window display(s)"),
            new(GamerDisplayScope.AllDisplays, "All displays"),
            new(GamerDisplayScope.SpecificDisplay, "One specific display")
        ];
        public IReadOnlyList<GamerChoice<GammaMode>> AvailableGammaModes { get; } =
        [
            new(GammaMode.Gamma22, "Gamma 2.2"),
            new(GammaMode.Gamma24, "Gamma 2.4"),
            new(GammaMode.WindowsDefault, "Windows Default")
        ];

        public string AppName => _profile.AppName;
        public string? ExecutablePath => _profile.ExecutablePath;
        public bool HasExecutablePath => !string.IsNullOrWhiteSpace(_profile.ExecutablePath);
        public string ExecutableIdentityLabel => HasExecutablePath
            ? "Verified executable"
            : "Unverified path · profile disabled";
        public DateTime? LastUsedUtc => _profile.LastUsedUtc;

        public bool SetExecutablePath(string? executablePath)
        {
            GamerProfileRule updated = _profile.Clone();
            updated.ExecutablePath = executablePath;
            string? sanitized = updated.Sanitized().ExecutablePath;
            if (string.Equals(_profile.ExecutablePath, sanitized, StringComparison.OrdinalIgnoreCase))
                return false;
            _profile.ExecutablePath = sanitized;
            OnPropertyChanged(nameof(ExecutablePath));
            OnPropertyChanged(nameof(HasExecutablePath));
            OnPropertyChanged(nameof(ExecutableIdentityLabel));
            return true;
        }

        public bool SetHdrCapabilityFromDiscovery(GamerHdrCapability capability)
        {
            if (capability != GamerHdrCapability.Detected ||
                _profile.HdrCapability != GamerHdrCapability.Unknown)
                return false;
            _profile.HdrCapability = capability;
            OnPropertyChanged(nameof(HdrCapability));
            OnPropertyChanged(nameof(IsHdrCapable));
            OnPropertyChanged(nameof(HdrSupportLabel));
            return true;
        }

        public void SetLastUsedUtc(DateTime usedUtc)
        {
            usedUtc = usedUtc.ToUniversalTime();
            if (_profile.LastUsedUtc == usedUtc) return;
            _profile.LastUsedUtc = usedUtc;
            OnPropertyChanged(nameof(LastUsedUtc));
        }

        public string IntentDescription => PictureIntent switch
        {
            GamerPictureIntent.Reference =>
                "Faithful output with your measured display correction. Night warmth stays out of the game.",
            GamerPictureIntent.CompetitiveClarity =>
                "Makes dark movement easier to spot without washing out the rest of the image.",
            GamerPictureIntent.CinematicHdr =>
                "Leaves the HDR curve intact and checks that Windows is sending a real HDR signal.",
            GamerPictureIntent.NightOps =>
                "Warms the screen for late sessions while keeping teams, HUD colors, and effects distinct.",
            _ => "Uses the detailed settings under Advanced."
        };

        public string EffectSummary
        {
            get
            {
                string gamma = OverrideGamma ? GammaLabel(GammaMode) : "monitor gamma";
                string shadows = ShadowDetailPercent <= 0.01
                    ? "reference shadows"
                    : $"shadow visibility {ShadowDetailPercent:F0}%";
                string night = NightPolicy switch
                {
                    GamerNightPolicy.ForceDaylight => "6500K while active",
                    GamerNightPolicy.NightOps => $"Night Ops {NightOpsKelvin}K",
                    _ => "follows night schedule"
                };
                return $"{gamma} · {shadows} · {night}";
            }
        }

        public string DisplayName
        {
            get => _profile.DisplayName;
            set
            {
                if (_profile.DisplayName == value) return;
                _profile.DisplayName = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GameTitle));
            }
        }

        public string GameTitle => string.IsNullOrWhiteSpace(DisplayName)
            ? Path.GetFileNameWithoutExtension(AppName)
            : DisplayName;

        public bool Enabled
        {
            get => _profile.Enabled;
            set { if (_profile.Enabled != value) { _profile.Enabled = value; OnPropertyChanged(); } }
        }

        public GamerPictureIntent PictureIntent
        {
            get => _profile.PictureIntent;
            set
            {
                if (_profile.PictureIntent == value) return;
                GamerPresetCatalog.Apply(_profile, value);
                RaiseAllRenderingProperties();
            }
        }

        public GamerDisplayScope DisplayScope
        {
            get => _profile.DisplayScope;
            set
            {
                if (_profile.DisplayScope == value) return;
                _profile.DisplayScope = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSpecificDisplay));
                OnPropertyChanged(nameof(EffectSummary));
            }
        }

        public bool IsSpecificDisplay => DisplayScope == GamerDisplayScope.SpecificDisplay;

        public string? MonitorDevicePath
        {
            get => _profile.MonitorDevicePath;
            set
            {
                if (string.Equals(_profile.MonitorDevicePath, value, StringComparison.OrdinalIgnoreCase)) return;
                _profile.MonitorDevicePath = value;
                OnPropertyChanged();
            }
        }

        public GamerHdrExpectation HdrExpectation
        {
            get => _profile.HdrExpectation;
            set
            {
                if (_profile.HdrExpectation == value) return;
                _profile.HdrExpectation = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHdrCapable));
                OnPropertyChanged(nameof(HdrSupportLabel));
            }
        }

        public GamerHdrCapability HdrCapability
        {
            get => _profile.HdrCapability;
            set
            {
                if (_profile.HdrCapability == value) return;
                _profile.HdrCapability = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHdrCapable));
                OnPropertyChanged(nameof(HdrSupportLabel));
            }
        }

        public bool IsHdrCapable => _profile.IsHdrCapable();

        public string HdrSupportLabel => _profile.HdrCapability switch
        {
            GamerHdrCapability.Detected => "HDR detected",
            GamerHdrCapability.UserConfirmed => "HDR confirmed",
            GamerHdrCapability.SdrOnly => "SDR only",
            _ when _profile.HdrExpectation == GamerHdrExpectation.RequireHdr => "HDR profile",
            _ => "HDR unknown"
        };

        public bool OverrideGamma
        {
            get => _profile.OverrideGamma;
            set
            {
                if (_profile.OverrideGamma == value) return;
                _profile.OverrideGamma = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectSummary));
            }
        }

        public GammaMode GammaMode
        {
            get => _profile.GammaMode;
            set
            {
                if (_profile.GammaMode == value) return;
                _profile.GammaMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectSummary));
            }
        }

        public double ShadowDetailPercent
        {
            get => _profile.ShadowDetailStrength * 100.0;
            set
            {
                double normalized = double.IsFinite(value) ? Math.Clamp(value / 100.0, 0.0, 1.0) : 0.0;
                if (Math.Abs(_profile.ShadowDetailStrength - normalized) < 0.0001) return;
                _profile.ShadowDetailStrength = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShadowDetailLabel));
                OnPropertyChanged(nameof(ShadowDetailPlainLabel));
                OnPropertyChanged(nameof(EffectSummary));
            }
        }

        public string ShadowDetailLabel => $"{ShadowDetailPercent:F0}% · protected above {_profile.ShadowDetailPivot * 100:F0}% linear";

        public string ShadowDetailPlainLabel => ShadowDetailPercent switch
        {
            < 5 => "Original shadows",
            < 35 => "A little more detail",
            < 70 => "Clearer dark areas",
            _ => "Maximum visibility"
        };

        public GamerNightPolicy NightPolicy
        {
            get => _profile.NightPolicy;
            set
            {
                if (_profile.NightPolicy == value) return;
                _profile.NightPolicy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNightOps));
                OnPropertyChanged(nameof(EffectSummary));
            }
        }

        public bool IsNightOps => NightPolicy == GamerNightPolicy.NightOps;

        public int NightOpsKelvin
        {
            get => _profile.NightOpsKelvin;
            set
            {
                int clamped = Math.Clamp(value, 1900, 6500);
                if (_profile.NightOpsKelvin != clamped)
                {
                    _profile.NightOpsKelvin = clamped;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EffectSummary));
                }
            }
        }

        public double NightOpsStrengthPercent
        {
            get => _profile.NightOpsStrength * 100.0;
            set
            {
                double normalized = double.IsFinite(value) ? Math.Clamp(value / 100.0, 0.0, 1.0) : 0.55;
                if (Math.Abs(_profile.NightOpsStrength - normalized) < 0.0001) return;
                _profile.NightOpsStrength = normalized;
                OnPropertyChanged();
            }
        }

        public double NightOpsMelanopicCeiling
        {
            get => _profile.NightOpsMelanopicCeiling;
            set
            {
                double clamped = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1000.0) : 0.0;
                if (Math.Abs(_profile.NightOpsMelanopicCeiling - clamped) < 0.0001) return;
                _profile.NightOpsMelanopicCeiling = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNightOpsMelanopicCeilingEnabled));
            }
        }

        public bool IsNightOpsMelanopicCeilingEnabled
        {
            get => NightOpsMelanopicCeiling > 0.0;
            set
            {
                if (value == IsNightOpsMelanopicCeilingEnabled) return;
                NightOpsMelanopicCeiling = value ? 10.0 : 0.0;
                OnPropertyChanged();
            }
        }

        public bool GameplayLock
        {
            get => _profile.GameplayLock;
            set { if (_profile.GameplayLock != value) { _profile.GameplayLock = value; OnPropertyChanged(); } }
        }

        public double PaperWhiteNits
        {
            get => _profile.PaperWhiteNits;
            set
            {
                double clamped = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1000.0) : 0.0;
                if (Math.Abs(_profile.PaperWhiteNits - clamped) < 0.0001) return;
                _profile.PaperWhiteNits = clamped;
                OnPropertyChanged();
            }
        }

        public double PeakNits
        {
            get => _profile.PeakNits;
            set
            {
                double clamped = double.IsFinite(value) ? Math.Clamp(value, 0.0, 10000.0) : 0.0;
                if (Math.Abs(_profile.PeakNits - clamped) < 0.0001) return;
                _profile.PeakNits = clamped;
                OnPropertyChanged();
            }
        }

        public GamerProfileRule ToRule() => _profile.Sanitized();

        private void RaiseAllRenderingProperties()
        {
            OnPropertyChanged(nameof(PictureIntent));
            OnPropertyChanged(nameof(IntentDescription));
            OnPropertyChanged(nameof(EffectSummary));
            OnPropertyChanged(nameof(GammaMode));
            OnPropertyChanged(nameof(OverrideGamma));
            OnPropertyChanged(nameof(ShadowDetailPercent));
            OnPropertyChanged(nameof(ShadowDetailLabel));
            OnPropertyChanged(nameof(ShadowDetailPlainLabel));
            OnPropertyChanged(nameof(NightPolicy));
            OnPropertyChanged(nameof(IsNightOps));
            OnPropertyChanged(nameof(NightOpsKelvin));
            OnPropertyChanged(nameof(NightOpsStrengthPercent));
            OnPropertyChanged(nameof(NightOpsMelanopicCeiling));
            OnPropertyChanged(nameof(IsNightOpsMelanopicCeilingEnabled));
            OnPropertyChanged(nameof(HdrExpectation));
            OnPropertyChanged(nameof(IsHdrCapable));
            OnPropertyChanged(nameof(HdrSupportLabel));
            OnPropertyChanged(nameof(GameplayLock));
        }

        private static string GammaLabel(GammaMode mode) => mode switch
        {
            GammaMode.Gamma22 => "Gamma 2.2",
            GammaMode.Gamma24 => "Gamma 2.4",
            _ => "Windows Default"
        };
    }
}
