using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;

namespace HDRGammaController.ViewModels
{
    public delegate void ApplyCalibrationRequest(
        MonitorInfo monitor,
        GammaMode mode,
        CalibrationSettings? calibration,
        int? nightKelvinOverride,
        NightModeSettings? nightModeSettingsOverride = null);

    public class DashboardViewModel : ObservableObject, IDisposable
    {
        // Frozen brushes: WPF can short-circuit thread-affinity checks on frozen Freezables
        // and skip per-use validation. Allocating a fresh SolidColorBrush every Refresh()
        // otherwise produced steady GC pressure on live night-mode updates.
        private static readonly Brush HdrBadgeBrush = CreateFrozen(Color.FromRgb(0, 120, 212));
        private static readonly Brush SdrBadgeBrush = CreateFrozen(Color.FromRgb(100, 100, 100));

        private static Brush CreateFrozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private readonly MonitorManager _monitorManager;
        private readonly NightModeService _nightModeService;
        private readonly UpdateService _updateService;
        private readonly ApplyCalibrationRequest _applyCallback;
        private readonly Action<double> _blendChangedHandler;
        private readonly AppExclusionItem _appExclusionItem;
        private readonly GammaApplyService? _gamerApplyService;
        private readonly GamerModeCoordinator? _gamerModeCoordinator;
        private readonly Action<IReadOnlyList<GamerSessionSnapshot>>? _gamerSessionsChangedHandler;
        private NightModeSettings _editingNightMode;

        // True while we're persisting _editingNightMode ourselves, so a Refresh triggered by
        // our own SetNightMode (via the BlendChanged round-trip) doesn't clobber the in-flight
        // edit with a freshly-read snapshot. See SaveEditedNightMode for how the flag is
        // cleared: the round-trip lands via Dispatcher.BeginInvoke AFTER SaveEditedNightMode
        // returns, so a plain try/finally reset never actually covered it.
        private bool _savingNightMode;

        // Monotonically increasing save generation. Each SaveEditedNightMode bumps it and
        // queues a low-priority reset of _savingNightMode; the generation check makes the
        // reset a no-op if a newer save has started in the meantime.
        private int _saveGeneration;

        // Throttle refresh to avoid excessive UI updates
        private DateTime _lastRefreshTime = DateTime.MinValue;
        private const int RefreshThrottleMs = 500;
        private bool _refreshPending;
        private List<MonitorInfo>? _cachedMonitors;
        private int _runningAppScanGeneration;
        private volatile bool _isDisposed;
        private readonly DispatcherTimer _gamerAutoSaveTimer;
        private readonly DispatcherTimer _runningAppRefreshTimer;
        private bool _suppressGamerProfileSave;
        private bool _savingGamerProfiles;
        private readonly Action _gamerSettingsChangedHandler;
        private readonly Action<string, DateTime> _gamerProfileUsedHandler;
        private readonly GameDiscoveryService _gameDiscoveryService = new();
        private CancellationTokenSource? _gameDiscoveryCancellation;
        private int _gameDiscoveryGeneration;

        public SettingsManager SettingsManager { get; }

        /// <summary>App version (e.g. "v1.0.1") shown in the dashboard title bar.</summary>
        public string AppVersion => _updateService.DisplayVersion;

        public ObservableCollection<object> Items { get; } = new ObservableCollection<object>();
        public GamerModeItem GamerMode { get; }

        public ICommand RefreshCommand { get; }
        public ICommand ConfigureMonitorCommand { get; }
        public ICommand Pause1hCommand { get; }
        public ICommand Pause4hCommand { get; }
        public ICommand PauseUntilMorningCommand { get; }
        public ICommand CycleNightModeCommand { get; }
        public ICommand AddExcludedAppCommand { get; }
        public ICommand RemoveExcludedAppCommand { get; }
        public ICommand SaveExcludedAppsCommand { get; }
        public ICommand AddGamerProfileCommand { get; }
        public ICommand RemoveGamerProfileCommand { get; }
        public ICommand SaveGamerProfilesCommand { get; }
        public ICommand ToggleGamerModeCommand { get; }
        public ICommand ScanForGamesCommand { get; }
        public ICommand AddDiscoveredGamesCommand { get; }
        public ICommand ClearGamerProfilesCommand { get; }
        public ICommand ToggleShowAllGamerProfilesCommand { get; }
        public ICommand DismissGameDiscoveryCommand { get; }
        public ICommand ApplyGamerLibraryPresetCommand { get; }

        public IReadOnlyList<NightModeAlgorithmOption> AvailableNightModeAlgorithms { get; } = NightModeAlgorithmOption.DefaultOptions;

        public NightModeAlgorithm SelectedNightModeAlgorithm
        {
            get => _editingNightMode.Algorithm;
            set
            {
                if (_editingNightMode.Algorithm == value) return;
                _editingNightMode.Algorithm = value;
                SaveEditedNightMode();
                RefreshNightRenderingBindings();
                NightRenderingEdited?.Invoke();
            }
        }

        public string SelectedNightModeAlgorithmDescription => SelectedNightModeAlgorithm switch
        {
            NightModeAlgorithm.Perceptual => "Balanced color preservation",
            NightModeAlgorithm.UltraNight => "Amber/red maximum protection",
            NightModeAlgorithm.AccurateCIE1931 => "Full CIE white-point shift",
            NightModeAlgorithm.Standard => "Classic warm tint",
            _ => string.Empty
        };

        /// <summary>
        /// Navigation request: the view opens SettingsWindow for the given monitor card.
        /// </summary>
        public event Action<DashboardItem>? ConfigureRequested;

        public event Action? NightRenderingEdited;

        /// <summary>
        /// Raised when Refresh() replaced <see cref="EditingNightMode"/> with a freshly
        /// read snapshot (e.g. the header Off/Auto/Manual toggle or a tray change wrote
        /// settings). The window must re-run ScheduleEditor.Initialize with the new
        /// instance; otherwise the editor keeps mutating the orphaned old clone and the
        /// next SaveEditedNightMode persists the new snapshot, silently discarding the
        /// user's graph edits.
        /// </summary>
        public event Action? EditingNightModeReplaced;

        public bool IsGamerModeEnabled
        {
            get => _gamerModeCoordinator?.Enabled ?? SettingsManager.GamerModeEnabled;
            set
            {
                if (value == IsGamerModeEnabled) return;
                bool saved = _gamerModeCoordinator?.TrySetEnabled(value)
                    ?? SettingsManager.TrySetGamerModeEnabled(value);
                if (!saved)
                {
                    GamerMode.ShowSaveFailed();
                    RaiseGamerModeStateProperties();
                    return;
                }
                RaiseGamerModeStateProperties();
            }
        }

        public string GamerModeStateTitle => IsGamerModeEnabled ? "GAME MODE · ON" : "GAME MODE · PAUSED";

        public string GamerModeStateDetails => IsGamerModeEnabled
            ? "Saved looks switch on when each game takes focus."
            : "All game profiles are bypassed. Your settings stay saved.";

        public string GamerModeToggleLabel => IsGamerModeEnabled ? "Pause all profiles" : "Resume game mode";

        public bool IsGameLabSectionExpanded
        {
            get => SettingsManager.GetUiSectionExpanded("Dashboard.GameLab", true);
            set => SetSectionExpanded("Dashboard.GameLab", value, nameof(IsGameLabSectionExpanded));
        }

        public bool IsNightModeControlsExpanded
        {
            get => SettingsManager.GetUiSectionExpanded("Dashboard.NightModeControls", true);
            set => SetSectionExpanded("Dashboard.NightModeControls", value, nameof(IsNightModeControlsExpanded));
        }

        public bool IsCircadianSectionExpanded
        {
            get => SettingsManager.GetUiSectionExpanded("Dashboard.Circadian", true);
            set => SetSectionExpanded("Dashboard.Circadian", value, nameof(IsCircadianSectionExpanded));
        }

        public bool IsCircadianAdvancedExpanded
        {
            get => SettingsManager.GetUiSectionExpanded("Dashboard.CircadianAdvanced", false);
            set => SetSectionExpanded("Dashboard.CircadianAdvanced", value, nameof(IsCircadianAdvancedExpanded));
        }

        public bool IsAddRunningGameExpanded
        {
            get => SettingsManager.GetUiSectionExpanded("GameLab.AddRunningGame", false);
            set => SetSectionExpanded("GameLab.AddRunningGame", value, nameof(IsAddRunningGameExpanded));
        }

        public bool IsGameProfileAdvancedExpanded
        {
            get => SettingsManager.GetUiSectionExpanded("GameLab.ProfileAdvanced", false);
            set => SetSectionExpanded("GameLab.ProfileAdvanced", value, nameof(IsGameProfileAdvancedExpanded));
        }

        public DashboardViewModel(
            MonitorManager monitorManager,
            SettingsManager settingsManager,
            NightModeService nightModeService,
            UpdateService updateService,
            ApplyCalibrationRequest applyCallback,
            GammaApplyService? gamerApplyService = null,
            GamerModeCoordinator? gamerModeCoordinator = null)
        {
            _monitorManager = monitorManager;
            SettingsManager = settingsManager;
            _nightModeService = nightModeService;
            _updateService = updateService;
            _applyCallback = applyCallback;
            _gamerApplyService = gamerApplyService;
            _gamerModeCoordinator = gamerModeCoordinator;

            _appExclusionItem = new AppExclusionItem();
            foreach (var rule in settingsManager.ExcludedApps)
                _appExclusionItem.ExcludedApps.Add(rule);

            GamerMode = new GamerModeItem();
            foreach (var profile in settingsManager.GamerProfiles)
                GamerMode.Profiles.Add(new GamerProfileEditorItem(profile));
            GamerMode.SetSessions(gamerApplyService?.ActiveGamerSessions);
            GamerMode.Profiles.CollectionChanged += OnGamerProfilesCollectionChanged;
            foreach (GamerProfileEditorItem profile in GamerMode.Profiles)
                profile.PropertyChanged += OnGamerProfileEdited;

            _editingNightMode = settingsManager.NightMode;

            RefreshCommand = new RelayCommand(() => Refresh());
            ConfigureMonitorCommand = new RelayCommand<DashboardItem>(item =>
            {
                if (item != null) ConfigureRequested?.Invoke(item);
            });
            Pause1hCommand = new RelayCommand(() => _nightModeService.PauseUntil(DateTime.Now.AddHours(1)));
            Pause4hCommand = new RelayCommand(() => _nightModeService.PauseUntil(DateTime.Now.AddHours(4)));
            PauseUntilMorningCommand = new RelayCommand(() => _nightModeService.PauseUntil(NextMorning(DateTime.Now)));
            CycleNightModeCommand = new RelayCommand(() => NightModeModeIndex = (NightModeModeIndex + 1) % 3);
            AddExcludedAppCommand = new RelayCommand<AppExclusionItem>(AddExcludedApp);
            RemoveExcludedAppCommand = new RelayCommand<AppExclusionRule>(RemoveExcludedApp);
            SaveExcludedAppsCommand = new RelayCommand(SaveExcludedApps);
            AddGamerProfileCommand = new RelayCommand(AddGamerProfile);
            RemoveGamerProfileCommand = new RelayCommand<GamerProfileEditorItem>(RemoveGamerProfile);
            SaveGamerProfilesCommand = new RelayCommand(() => SaveGamerProfiles());
            ToggleGamerModeCommand = new RelayCommand(() => IsGamerModeEnabled = !IsGamerModeEnabled);
            ScanForGamesCommand = new AsyncRelayCommand(ScanForGamesAsync);
            AddDiscoveredGamesCommand = new RelayCommand(AddDiscoveredGames);
            ClearGamerProfilesCommand = new RelayCommand(ClearGamerProfiles);
            ToggleShowAllGamerProfilesCommand = new RelayCommand(GamerMode.ToggleShowAllProfiles);
            DismissGameDiscoveryCommand = new RelayCommand(DismissGameDiscovery);
            ApplyGamerLibraryPresetCommand = new RelayCommand(ApplyGamerLibraryPreset);

            // Profile controls are live. A short debounce coalesces slider movement and a
            // preset's multi-property update into one settings write + one foreground
            // re-evaluation instead of requiring a separate Save button.
            _gamerAutoSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            _gamerAutoSaveTimer.Tick += (_, _) =>
            {
                _gamerAutoSaveTimer.Stop();
                SaveGamerProfiles();
            };

            // Running game choices stay current while either Game Lab surface is open.
            // Reconciliation preserves existing item identity, so an open dropdown or typed
            // executable is not cleared by the refresh.
            _runningAppRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _runningAppRefreshTimer.Tick += (_, _) => RefreshGamerChoices();
            _runningAppRefreshTimer.Start();

            _gamerSettingsChangedHandler = OnExternalGamerSettingsChanged;
            SettingsManager.GamerSettingsChanged += _gamerSettingsChangedHandler;
            _gamerProfileUsedHandler = OnGamerProfileUsed;
            SettingsManager.GamerProfileUsed += _gamerProfileUsedHandler;

            if (_gamerApplyService != null)
            {
                _gamerSessionsChangedHandler = sessions =>
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (_isDisposed || dispatcher == null || dispatcher.HasShutdownStarted) return;
                    dispatcher.BeginInvoke(new Action(() => GamerMode.SetSessions(sessions)));
                };
                _gamerApplyService.GamerSessionsChanged += _gamerSessionsChangedHandler;
            }

            // Re-refresh when blend changes (for live update) - throttled. Kept as a stored
            // handler so Dispose can unsubscribe; the service outlives this window.
            _blendChangedHandler = _ =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (!_isDisposed && dispatcher != null && !dispatcher.HasShutdownStarted)
                    dispatcher.BeginInvoke(new Action(ThrottledRefresh));
            };
            _nightModeService.BlendChanged += _blendChangedHandler;

            // Calibration bypass has no event (and night mode is paused during it, so no
            // blend refreshes fire either); poll cheaply so an open dashboard notices
            // calibration starting/ending on any monitor.
            _bypassPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _bypassPollTimer.Tick += (_, _) =>
            {
                bool any = _cachedMonitors?.Any(m => CalibrationStateManager.IsDeviceInBypass(m)) ?? false;
                if (any != _lastBypassState)
                {
                    _lastBypassState = any;
                    Refresh();
                }
            };
            _bypassPollTimer.Start();

            Refresh();
        }

        private readonly DispatcherTimer _bypassPollTimer;
        private bool _lastBypassState;

        public bool IsNightModeEnabled
        {
            get => SettingsManager.NightMode.Enabled;
            set
            {
                if (value == SettingsManager.NightMode.Enabled) return;
                var updated = SettingsManager.NightMode;
                updated.Enabled = value;
                if (!value) updated.ManualOverrideEnabled = false;
                SettingsManager.SetNightMode(updated);
                OnNightModeModeChanged();
                Refresh();
            }
        }

        public int NightModeModeIndex
        {
            get
            {
                var settings = SettingsManager.NightMode;
                if (!settings.Enabled) return 0;
                return settings.ManualOverrideEnabled ? 2 : 1;
            }
            set
            {
                value = Math.Clamp(value, 0, 2);
                if (value == NightModeModeIndex) return;

                var updated = SettingsManager.NightMode;
                updated.Enabled = value != 0;
                updated.ManualOverrideEnabled = value == 2;
                SettingsManager.SetNightMode(updated);
                OnNightModeModeChanged();
                Refresh();
            }
        }

        public string NightModeModeDescription
        {
            get
            {
                var settings = SettingsManager.NightMode;
                if (!settings.Enabled) return "Daylight is forced; schedule and manual warmth are disabled.";
                if (settings.ManualOverrideEnabled) return $"Forcing {settings.GetManualOverrideKelvin()}K until you switch back to Auto or Off.";
                return "Schedule controls warmth automatically.";
            }
        }

        public string NightModeModeLabel => NightModeModeIndex switch
        {
            0 => "Night Mode: Off",
            1 => "Night Mode: Auto",
            _ => "Night Mode: Manual"
        };

        private void OnNightModeModeChanged()
        {
            OnPropertyChanged(nameof(IsNightModeEnabled));
            OnPropertyChanged(nameof(NightModeModeIndex));
            OnPropertyChanged(nameof(NightModeModeLabel));
            OnPropertyChanged(nameof(NightModeModeDescription));
        }

        /// <summary>
        /// The night-mode settings instance the schedule editor mutates in place;
        /// SaveEditedNightMode persists it.
        /// </summary>
        public NightModeSettings EditingNightMode => _editingNightMode;

        public void SaveEditedNightMode()
        {
            // Guard: a Refresh triggered by the BlendChanged round-trip from our own save
            // must not overwrite _editingNightMode mid-persist with a re-read snapshot.
            //
            // Mechanism: SetNightMode fires NightModeChanged -> NightModeService.UpdateSettings
            // -> BlendChanged, whose handler queues ThrottledRefresh via Dispatcher.BeginInvoke
            // (and the throttled path re-queues at Background priority). Both land AFTER this
            // method returns, so resetting the flag in a finally block here left the guard
            // ineffective. Instead, the reset is queued at ContextIdle priority - below the
            // Normal/Background priorities the round-trip refreshes run at - so it executes
            // only after those refreshes have drained. The generation check keeps overlapping
            // saves correct: only the most recent save's reset actually clears the flag.
            int generation = ++_saveGeneration;
            _savingNightMode = true;
            try
            {
                SettingsManager.SetNightMode(_editingNightMode);
            }
            finally
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    _savingNightMode = false; // no dispatcher (tests/shutdown): reset inline
                }
                else
                {
                    dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                    {
                        if (generation == _saveGeneration)
                            _savingNightMode = false;
                    }));
                }
            }
        }

        public void RefreshNightRenderingBindings()
        {
            OnPropertyChanged(nameof(SelectedNightModeAlgorithm));
            OnPropertyChanged(nameof(SelectedNightModeAlgorithmDescription));
        }

        public List<MonitorInfo> GetMonitorSnapshot()
            => Items.OfType<DashboardItem>().Select(i => i.Model).ToList();

        /// <summary>
        /// Apply callback handed to SettingsWindow: forwards to the tray apply path and
        /// refreshes the cards unless this is just a live temperature preview.
        /// </summary>
        public void ApplyFromSettings(MonitorInfo monitor, GammaMode mode, CalibrationSettings? calibration, int? nightKelvinOverride)
        {
            _applyCallback(monitor, mode, calibration, nightKelvinOverride);
            if (!nightKelvinOverride.HasValue)
            {
                Refresh();
            }
        }

        public async Task PreviewTemperatureAsync(int? kelvin)
        {
            // Use cached monitors during preview to avoid repeated enumeration
            var monitors = _cachedMonitors ?? _monitorManager.EnumerateMonitors();

            // Offload heavy gamma ramp application to background thread
            await Task.Run(() =>
            {
                foreach (var m in monitors)
                {
                    var profile = SettingsManager.GetMonitorProfile(m.MonitorDevicePath);
                    var cal = profile?.ToCalibrationSettings() ?? new CalibrationSettings();
                    _applyCallback(m, profile?.GammaMode ?? m.CurrentGamma, cal, kelvin, _editingNightMode);
                }
            });
        }

        public void ThrottledRefresh()
        {
            if (_isDisposed) return;

            var now = DateTime.Now;
            if ((now - _lastRefreshTime).TotalMilliseconds >= RefreshThrottleMs)
            {
                _lastRefreshTime = now;
                Refresh(reEnumerate: false);
            }
            else if (!_refreshPending)
            {
                _refreshPending = true;
                // Schedule delayed refresh
                _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _refreshPending = false;
                    if (!_isDisposed)
                        Refresh(reEnumerate: false);
                }));
            }
        }

        /// <param name="reEnumerate">
        /// When true, re-scan the hardware via DXGI (use for open / manual / display change).
        /// When false (night-mode blend ticks), reuse the cached monitor list: a blend tick
        /// only changes tint values, and re-enumerating the hardware on every tick was the
        /// dominant cost behind the apply/enumeration storm that could hang the display.
        /// </param>
        public void Refresh(bool reEnumerate = true)
        {
            if (_isDisposed) return;

            // If night mode changed from elsewhere (tray hotkey toggle, another window, the
            // night-mode timer's blend tick), re-read the authoritative snapshot so the
            // schedule editor doesn't show stale values that Save would then clobber. Skip
            // when WE are the source of the change (SaveEditedNightMode round-trip).
            if (!_savingNightMode)
            {
                var latest = SettingsManager.NightMode;
                if (!NightModeSettingsEqual(_editingNightMode, latest))
                {
                    _editingNightMode = latest;
                    // The schedule editor holds a reference to the old instance; tell the
                    // window to re-Initialize it or its edits will mutate an orphaned clone.
                    EditingNightModeReplaced?.Invoke();
                }
            }
            OnNightModeModeChanged();
            RefreshNightRenderingBindings();

            var monitors = (reEnumerate || _cachedMonitors == null)
                ? _monitorManager.EnumerateMonitors()
                : _cachedMonitors;
            _cachedMonitors = monitors; // Cache for preview operations
            if (reEnumerate)
                SyncGamerDisplays(GamerMode.AvailableDisplays, monitors);

            // Night mode data
            int nightKelvin = _nightModeService.CurrentNightKelvin;
            double blendedShift = (nightKelvin - 6500) / 70.0;

            var refreshedItems = new List<DashboardItem>(monitors.Count);
            foreach (var m in monitors)
            {
                // Load current state
                var profile = SettingsManager.GetMonitorProfile(m.MonitorDevicePath);

                // Determine display properties
                bool isHdr = m.IsHdrActive;

                double brightness = profile?.Brightness ?? 100;
                GammaMode gamma = profile?.GammaMode ?? m.CurrentGamma;

                // Calculate Effective Temperature
                double baseTemp = profile?.Temperature ?? 0;
                double offset = profile?.TemperatureOffset ?? 0;
                string tempText = FormatEffectiveTemperatureText(
                    baseTemp,
                    offset,
                    blendedShift,
                    _nightModeService.IsNightModeActive);

                refreshedItems.Add(new DashboardItem
                {
                    Model = m,
                    FriendlyName = m.FriendlyName,
                    BadgeText = isHdr ? "HDR" : "SDR",
                    BadgeColor = isHdr ? HdrBadgeBrush : SdrBadgeBrush,
                    CurrentGamma = gamma,
                    CurrentBrightness = brightness,
                    CurrentTemperatureText = tempText,
                    // Mid-calibration the corrections are bypassed and the panel runs
                    // native; the card fades the (inactive) settings and shows a badge.
                    IsCalibrating = CalibrationStateManager.IsDeviceInBypass(m)
                });
            }

            SyncDashboardItems(Items, refreshedItems, _appExclusionItem);

            // Blend ticks only change the monitor-card tint text. Re-scanning processes on
            // every tick used to clear the ComboBox ItemsSource while a choice was selected,
            // which could erase NewAppText before the Add command read it.
            if (reEnumerate)
                _ = LoadRunningAppsAsync(_appExclusionItem);
        }

        /// <summary>
        /// Updates monitor cards without removing the long-lived app-exclusion item. WPF
        /// keeps that item's generated container (and therefore an open ComboBox popup)
        /// alive across night-mode blend refreshes.
        /// </summary>
        internal static void SyncDashboardItems(
            ObservableCollection<object> items,
            IReadOnlyList<DashboardItem> refreshedItems,
            AppExclusionItem appExclusionItem)
        {
            var available = items.OfType<DashboardItem>().ToList();
            var used = new HashSet<DashboardItem>();
            var desired = new List<object>(refreshedItems.Count + 1);

            foreach (var refreshed in refreshedItems)
            {
                var existing = available.FirstOrDefault(item =>
                    !used.Contains(item) && SameMonitor(item.Model, refreshed.Model));

                if (existing == null)
                {
                    desired.Add(refreshed);
                    continue;
                }

                existing.UpdateFrom(refreshed);
                used.Add(existing);
                desired.Add(existing);
            }

            desired.Add(appExclusionItem);

            for (int index = 0; index < desired.Count; index++)
            {
                if (index < items.Count && ReferenceEquals(items[index], desired[index]))
                    continue;

                int existingIndex = IndexOfReference(items, desired[index], index + 1);
                if (existingIndex >= 0)
                    items.Move(existingIndex, index);
                else
                    items.Insert(index, desired[index]);
            }

            while (items.Count > desired.Count)
                items.RemoveAt(items.Count - 1);
        }

        private static bool SameMonitor(MonitorInfo left, MonitorInfo right)
        {
            if (!string.IsNullOrEmpty(left.MonitorDevicePath) || !string.IsNullOrEmpty(right.MonitorDevicePath))
                return string.Equals(left.MonitorDevicePath, right.MonitorDevicePath, StringComparison.OrdinalIgnoreCase);

            return string.Equals(left.DeviceName, right.DeviceName, StringComparison.OrdinalIgnoreCase)
                && left.OutputId == right.OutputId;
        }

        private static int IndexOfReference(ObservableCollection<object> items, object target, int startIndex)
        {
            for (int index = Math.Max(0, startIndex); index < items.Count; index++)
            {
                if (ReferenceEquals(items[index], target))
                    return index;
            }

            return -1;
        }

        internal static string FormatEffectiveTemperatureText(
            double baseTemperatureScale,
            double monitorOffsetScale,
            double nightShiftScale,
            bool nightModeActive)
        {
            double userAndOffset = ColorAdjustments.ComposeTemperatureScaleMired(
                baseTemperatureScale,
                monitorOffsetScale);
            double effectiveTemperature = ColorAdjustments.ComposeTemperatureScaleMired(
                userAndOffset,
                nightShiftScale);
            int kelvin = ColorAdjustments.TemperatureScaleToKelvin(effectiveTemperature);
            return nightModeActive ? $"{kelvin}K (Night)" : $"{kelvin}K";
        }

        private async Task LoadRunningAppsAsync(AppExclusionItem item)
        {
            int generation = Interlocked.Increment(ref _runningAppScanGeneration);
            RunningAppScan apps = await Task.Run(GetRunningApps);
            if (_isDisposed || generation != Volatile.Read(ref _runningAppScanGeneration))
                return;

            SyncRunningApps(item.RunningApps, apps.AppNames);
            List<string> safeGameApps = apps.AppNames
                .Where(app => GamerExecutableSafety.IsSafeProfileTarget(app))
                .ToList();
            SyncRunningApps(GamerMode.RunningApps, safeGameApps);
            GamerMode.SetRunningAppPaths(apps.ExecutablePaths);
        }

        public void RefreshGamerChoices()
        {
            if (_isDisposed) return;
            _ = LoadRunningAppsAsync(_appExclusionItem);
        }

        public void SuggestGamerApp(string? appName)
        {
            string normalized = AppExclusionRule.NormalizeAppName(appName);
            if (normalized.Length == 0 || !GamerExecutableSafety.IsSafeProfileTarget(normalized))
                return;
            GamerProfileEditorItem? existing = GamerMode.Profiles.FirstOrDefault(profile =>
                profile.AppName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                GamerMode.SelectAndRevealProfile(existing);
                return;
            }
            if (string.IsNullOrWhiteSpace(GamerMode.NewAppText))
                GamerMode.NewAppText = normalized;
        }

        /// <summary>
        /// Reconciles the process list without a collection Reset. Retained entries keep
        /// their identity, so a selected running app is not transiently removed and written
        /// back to the editable ComboBox as an empty string.
        /// </summary>
        internal static void SyncRunningApps(
            ObservableCollection<string> runningApps,
            IReadOnlyList<string> refreshedApps)
        {
            for (int index = 0; index < refreshedApps.Count; index++)
            {
                string app = refreshedApps[index];
                if (index < runningApps.Count &&
                    string.Equals(runningApps[index], app, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int existingIndex = -1;
                for (int candidate = index + 1; candidate < runningApps.Count; candidate++)
                {
                    if (string.Equals(runningApps[candidate], app, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIndex = candidate;
                        break;
                    }
                }

                if (existingIndex >= 0)
                    runningApps.Move(existingIndex, index);
                else
                    runningApps.Insert(index, app);
            }

            while (runningApps.Count > refreshedApps.Count)
                runningApps.RemoveAt(runningApps.Count - 1);
        }

        internal static void SyncGamerDisplays(
            ObservableCollection<GamerDisplayOption> displays,
            IReadOnlyList<MonitorInfo> monitors)
        {
            var desired = monitors
                .Where(monitor => !string.IsNullOrWhiteSpace(monitor.MonitorDevicePath))
                .GroupBy(monitor => monitor.MonitorDevicePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(monitor => new GamerDisplayOption(
                    monitor.MonitorDevicePath,
                    string.IsNullOrWhiteSpace(monitor.FriendlyName)
                        ? monitor.MonitorDevicePath
                        : monitor.FriendlyName))
                .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = displays.Count - 1; i >= 0; i--)
            {
                if (!desired.Any(option => option.DevicePath.Equals(
                    displays[i].DevicePath, StringComparison.OrdinalIgnoreCase)))
                    displays.RemoveAt(i);
            }

            for (int target = 0; target < desired.Count; target++)
            {
                GamerDisplayOption option = desired[target];
                int existing = -1;
                for (int i = target; i < displays.Count; i++)
                {
                    if (displays[i].DevicePath.Equals(option.DevicePath, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = i;
                        break;
                    }
                }

                if (existing < 0)
                {
                    displays.Insert(target, option);
                }
                else
                {
                    if (existing != target) displays.Move(existing, target);
                    if (displays[target].Label != option.Label) displays[target] = option;
                }
            }
        }

        private sealed record RunningAppScan(
            List<string> AppNames,
            Dictionary<string, string> ExecutablePaths);

        private static RunningAppScan GetRunningApps()
        {
            try
            {
                var appNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var executablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            if (process.MainWindowHandle != IntPtr.Zero &&
                                !string.IsNullOrEmpty(process.MainWindowTitle))
                            {
                                string appName = process.ProcessName.ToLowerInvariant() + ".exe";
                                appNames.Add(appName);
                                try
                                {
                                    string? path = process.MainModule?.FileName;
                                    if (!string.IsNullOrWhiteSpace(path))
                                        executablePaths[appName] = Path.GetFullPath(path);
                                }
                                catch
                                {
                                    // Elevated/protected processes remain available to the
                                    // night-mode exclusion picker, but cannot be trusted as a
                                    // path-bound game profile.
                                }
                            }
                        }
                        catch
                        {
                            // Access denied or the process exited during enumeration.
                        }
                    }
                }

                return new RunningAppScan(
                    appNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
                    executablePaths);
            }
            catch
            {
                return new RunningAppScan(
                    new List<string>(),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }
        }

        private void AddExcludedApp(AppExclusionItem? item)
        {
            if (item != null && TryAddExcludedApp(item))
                SaveExcludedApps();
        }

        internal static bool TryAddExcludedApp(AppExclusionItem item)
        {
            string app = AppExclusionRule.NormalizeAppName(item.NewAppText);
            if (app.Length == 0) return false;

            bool added = false;
            if (!item.ExcludedApps.Any(rule =>
                rule.AppName.Equals(app, StringComparison.OrdinalIgnoreCase)))
            {
                item.ExcludedApps.Add(new AppExclusionRule
                {
                    AppName = app,
                    FullDisable = false
                });
                added = true;
            }

            item.NewAppText = string.Empty;
            return added;
        }

        private void RemoveExcludedApp(AppExclusionRule? rule)
        {
            if (rule == null) return;
            if (_appExclusionItem.ExcludedApps.Remove(rule))
            {
                SaveExcludedApps();
            }
        }

        private void SaveExcludedApps()
            => SettingsManager.SetExcludedApps(_appExclusionItem.ExcludedApps.ToList());

        private async Task ScanForGamesAsync()
        {
            if (GamerMode.IsScanningForGames) return;
            _gameDiscoveryCancellation?.Cancel();
            _gameDiscoveryCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _gameDiscoveryCancellation = cancellation;
            int generation = Interlocked.Increment(ref _gameDiscoveryGeneration);
            GamerMode.IsScanningForGames = true;
            GamerMode.DiscoveryStatus = "Checking your launcher libraries…";
            try
            {
                var progress = new Progress<GameDiscoveryProgress>(update =>
                {
                    if (_isDisposed || cancellation.IsCancellationRequested ||
                        generation != Volatile.Read(ref _gameDiscoveryGeneration)) return;
                    var saved = GamerMode.Profiles
                        .Select(profile => profile.AppName)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    GamerMode.SetDiscoveryResults(update.Games, saved);
                    string cacheText = update.UsedCache ? " · unchanged library reused" : string.Empty;
                    GamerMode.DiscoveryStatus =
                        $"Checking {update.Stage} · {GamerMode.DiscoveredGames.Count} new games found{cacheText}";
                });
                IReadOnlyList<DiscoveredGame> games = await Task.Run(
                    () => _gameDiscoveryService.Scan(cancellation.Token, progress),
                    cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                int metadataUpdates = 0;
                _suppressGamerProfileSave = true;
                try
                {
                    foreach (DiscoveredGame game in games)
                    {
                        GamerProfileEditorItem? existing = GamerMode.Profiles.FirstOrDefault(profile =>
                            profile.AppName.Equals(game.ExecutableName, StringComparison.OrdinalIgnoreCase));
                        if (existing == null) continue;
                        bool updated = existing.SetExecutablePath(game.ExecutablePath);
                        updated |= existing.SetHdrCapabilityFromDiscovery(game.HdrCapability);
                        if (updated) metadataUpdates++;
                    }
                }
                finally
                {
                    _suppressGamerProfileSave = false;
                }
                if (metadataUpdates > 0) SaveGamerProfiles();

                var savedApps = GamerMode.Profiles
                    .Select(profile => profile.AppName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                GamerMode.SetDiscoveryResults(games, savedApps);
                int newGames = GamerMode.DiscoveredGames.Count;
                int hdrGames = games.Count(game => game.HdrCapability == GamerHdrCapability.Detected);
                string hdrSuffix = hdrGames > 0
                    ? $" HDR support was detected locally for {hdrGames}."
                    : string.Empty;
                GamerMode.DiscoveryStatus = newGames switch
                {
                    0 when games.Count > 0 => "Every game found by the scanner is already in your library." + hdrSuffix,
                    0 => "No supported launcher games were found. You can still add the running game below.",
                    1 => "Found 1 game. Review it before adding." + hdrSuffix,
                    _ => $"Found {newGames} new games. Nothing is checked automatically; add only the profiles you want." + hdrSuffix
                };
            }
            catch (OperationCanceledException)
            {
                if (!_isDisposed && generation == Volatile.Read(ref _gameDiscoveryGeneration))
                    GamerMode.DiscoveryStatus = "Game scan stopped.";
            }
            catch (Exception ex)
            {
                Log.Error($"Game discovery failed: {ex.Message}");
                GamerMode.DiscoveryStatus = "Game scan could not finish. You can still add a running game below.";
            }
            finally
            {
                if (generation == Volatile.Read(ref _gameDiscoveryGeneration))
                {
                    GamerMode.IsScanningForGames = false;
                    if (ReferenceEquals(_gameDiscoveryCancellation, cancellation))
                        _gameDiscoveryCancellation = null;
                }
                cancellation.Dispose();
            }
        }

        private void AddDiscoveredGames()
        {
            List<DiscoveredGameItem> selected = GamerMode.DiscoveredGames
                .Where(item => item.IsSelected && !item.AlreadyAdded)
                .ToList();
            if (selected.Count == 0)
            {
                GamerMode.DiscoveryStatus = "Choose at least one new game to add.";
                return;
            }

            if (selected.Count > 20 && !GamerMode.IsLargeAddArmed)
            {
                GamerMode.ArmLargeAddConfirmation();
                GamerMode.DiscoveryStatus =
                    $"Adding {selected.Count} profiles will create a large library. Click the confirmation button only if you want every selected game.";
                return;
            }

            int added = 0;
            GamerProfileEditorItem? firstAdded = null;
            _suppressGamerProfileSave = true;
            try
            {
                foreach (DiscoveredGameItem item in selected)
                {
                    if (!GamerExecutableSafety.IsSafeProfileTarget(item.ExecutableName, item.DisplayName))
                        continue;
                    if (GamerMode.Profiles.Any(profile =>
                        profile.AppName.Equals(item.ExecutableName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    GamerProfileRule rule = GamerPresetCatalog.Create(
                        item.ExecutableName, GamerPictureIntent.CompetitiveClarity);
                    rule.ExecutablePath = item.ExecutablePath;
                    rule.DisplayName = item.DisplayName;
                    rule.HdrCapability = item.HdrCapability;
                    var editor = new GamerProfileEditorItem(rule);
                    GamerMode.Profiles.Add(editor);
                    firstAdded ??= editor;
                    added++;
                }
            }
            finally
            {
                _suppressGamerProfileSave = false;
            }

            if (added > 0) SaveGamerProfiles();
            if (firstAdded != null) GamerMode.SelectAndRevealProfile(firstAdded);
            bool pausedLargeImport = added > 20 && IsGamerModeEnabled;
            if (pausedLargeImport)
                IsGamerModeEnabled = false;
            GamerMode.DiscoveredGames.Clear();
            GamerMode.ResetLargeAddConfirmation();
            GamerMode.DiscoveryStatus = pausedLargeImport
                ? $"Added {added} games. Game Mode is paused so you can review the library before anything activates."
                : added == 1
                    ? "Added 1 game with the Competitive look. You can change it below."
                    : $"Added {added} games with the Competitive look. You can tune each one below.";
        }

        private void AddGamerProfile()
        {
            if (TryAddGamerProfile(GamerMode))
                SaveGamerProfiles();
        }

        internal static bool TryAddGamerProfile(GamerModeItem item)
        {
            string raw = item.NewAppText.Trim();
            string app = AppExclusionRule.NormalizeAppName(raw);
            if (app.Length == 0) return false;
            string? rejection = GamerExecutableSafety.RejectionReason(app);
            if (rejection != null)
            {
                item.ShowMessage(rejection);
                return false;
            }
            GamerProfileEditorItem? existing = item.Profiles.FirstOrDefault(profile =>
                profile.AppName.Equals(app, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                item.NewAppText = string.Empty;
                item.SelectAndRevealProfile(existing);
                item.ShowMessage($"{existing.GameTitle} is already in your library.");
                return false;
            }

            string? executablePath = null;
            try
            {
                if (File.Exists(raw))
                    executablePath = Path.GetFullPath(raw);
                else if (item.TryGetRunningAppPath(app, out string runningPath) &&
                         File.Exists(runningPath))
                    executablePath = Path.GetFullPath(runningPath);
            }
            catch
            {
                executablePath = null;
            }

            GamerProfileRule rule = GamerPresetCatalog.Create(
                app, GamerPictureIntent.CompetitiveClarity);
            rule.ExecutablePath = executablePath;
            if (executablePath != null)
                rule.HdrCapability = GameDiscoveryService.DetectHdrCapability(executablePath).Capability;
            if (executablePath == null)
                rule.Enabled = false;

            var editor = new GamerProfileEditorItem(rule);
            item.Profiles.Add(editor);
            item.SelectAndRevealProfile(editor);
            item.NewAppText = string.Empty;
            item.ShowMessage(executablePath != null
                ? $"Added {editor.GameTitle} with a verified executable path."
                : $"Added {editor.GameTitle} disabled. Run the game or use the library scanner to verify its path.");
            return true;
        }

        private void DismissGameDiscovery()
        {
            Interlocked.Increment(ref _gameDiscoveryGeneration);
            _gameDiscoveryCancellation?.Cancel();
            _gameDiscoveryCancellation = null;
            GamerMode.IsScanningForGames = false;
            GamerMode.DiscoveredGames.Clear();
            GamerMode.DiscoverySearchText = string.Empty;
            GamerMode.ResetLargeAddConfirmation();
            GamerMode.DiscoveryStatus =
                "Reads Steam, Epic, GOG, and Xbox library records. Nothing is added automatically.";
        }

        private void RemoveGamerProfile(GamerProfileEditorItem? profile)
        {
            if (profile == null) return;
            if (GamerMode.Profiles.Remove(profile))
                SaveGamerProfiles();
        }

        private void ApplyGamerLibraryPreset()
        {
            GamerLibraryPresetOption? selection = GamerMode.SelectedLibraryPreset;
            if (selection == null)
            {
                GamerMode.ShowMessage("Choose a library default first.");
                return;
            }

            List<GamerProfileEditorItem> targets = GamerMode.Profiles
                .Where(profile => !selection.HdrCapableOnly || profile.IsHdrCapable)
                .ToList();
            if (targets.Count == 0)
            {
                GamerMode.ShowMessage(
                    "No HDR-capable games are known yet. Scan your libraries or mark HDR support in Advanced.");
                return;
            }

            _gamerAutoSaveTimer.Stop();
            _suppressGamerProfileSave = true;
            try
            {
                foreach (GamerProfileEditorItem profile in targets)
                    profile.PictureIntent = selection.Intent;
            }
            finally
            {
                _suppressGamerProfileSave = false;
            }

            if (SaveGamerProfiles())
            {
                string targetDescription = selection.HdrCapableOnly ? "HDR-capable games" : "games";
                GamerMode.ShowMessage(
                    $"Applied {selection.Label.Split('·')[0].Trim()} to {targets.Count} {targetDescription}.");
                GamerMode.SelectedLibraryPreset = null;
            }
        }

        private void ClearGamerProfiles()
        {
            if (GamerMode.Profiles.Count == 0) return;
            if (!GamerMode.ToggleClearProfilesConfirmation())
            {
                GamerMode.ShowMessage(
                    $"This removes all {GamerMode.Profiles.Count} saved game profiles. Click the red confirmation again to continue.");
                return;
            }

            _suppressGamerProfileSave = true;
            try
            {
                GamerMode.Profiles.Clear();
            }
            finally
            {
                _suppressGamerProfileSave = false;
            }
            SaveGamerProfiles();
            GamerMode.ShowMessage("Game library cleared.");
        }

        private void SetSectionExpanded(string key, bool expanded, string propertyName)
        {
            if (SettingsManager.GetUiSectionExpanded(key, !expanded) == expanded) return;
            SettingsManager.SetUiSectionExpanded(key, expanded);
            OnPropertyChanged(propertyName);
        }

        private void OnGamerProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (GamerProfileEditorItem profile in e.OldItems)
                    profile.PropertyChanged -= OnGamerProfileEdited;
            }
            if (e.NewItems != null)
            {
                foreach (GamerProfileEditorItem profile in e.NewItems)
                    profile.PropertyChanged += OnGamerProfileEdited;
            }
            if (!_suppressGamerProfileSave)
                QueueGamerProfileSave();
        }

        private void OnGamerProfileEdited(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GamerProfileEditorItem.LastUsedUtc)) return;
            if (!_suppressGamerProfileSave)
                QueueGamerProfileSave();
        }

        private void OnGamerProfileUsed(string appName, DateTime usedUtc)
        {
            if (_isDisposed) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(new Action(() => GamerMode.UpdateProfileRecency(appName, usedUtc)));
        }

        private void QueueGamerProfileSave()
        {
            if (_isDisposed) return;
            GamerMode.ShowSaving();
            _gamerAutoSaveTimer.Stop();
            _gamerAutoSaveTimer.Start();
        }

        private bool SaveGamerProfiles()
        {
            if (_savingGamerProfiles) return false;
            _gamerAutoSaveTimer.Stop();
            _savingGamerProfiles = true;
            try
            {
                bool saved = _gamerModeCoordinator?.TrySetProfiles(
                        GamerMode.Profiles.Select(profile => profile.ToRule()))
                    ?? SettingsManager.TrySetGamerProfiles(
                        GamerMode.Profiles.Select(profile => profile.ToRule()));
                if (saved)
                    GamerMode.ShowSaved();
                else
                    GamerMode.ShowSaveFailed();
                return saved;
            }
            finally
            {
                _savingGamerProfiles = false;
            }
        }

        private void OnExternalGamerSettingsChanged()
        {
            if (_isDisposed || _savingGamerProfiles) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(new Action(SyncGamerProfilesFromSettings));
        }

        private void SyncGamerProfilesFromSettings()
        {
            if (_isDisposed || _savingGamerProfiles) return;
            RaiseGamerModeStateProperties();
            List<GamerProfileRule> stored = SettingsManager.GamerProfiles;
            bool alreadyCurrent = stored.Count == GamerMode.Profiles.Count &&
                stored.Zip(GamerMode.Profiles, (rule, editor) => editor.ToRule().SemanticallyEquals(rule)).All(equal => equal);
            if (alreadyCurrent) return;

            _suppressGamerProfileSave = true;
            try
            {
                GamerMode.Profiles.Clear();
                foreach (GamerProfileRule profile in stored)
                    GamerMode.Profiles.Add(new GamerProfileEditorItem(profile));
            }
            finally
            {
                _suppressGamerProfileSave = false;
            }
        }

        private void RaiseGamerModeStateProperties()
        {
            OnPropertyChanged(nameof(IsGamerModeEnabled));
            OnPropertyChanged(nameof(GamerModeStateTitle));
            OnPropertyChanged(nameof(GamerModeStateDetails));
            OnPropertyChanged(nameof(GamerModeToggleLabel));
        }

        /// <summary>
        /// The next 7 AM strictly after <paramref name="now"/>: today's 7 AM when it has
        /// not passed yet (e.g. "pause until morning" clicked at 1 AM), otherwise
        /// tomorrow's. The old DateTime.Today.AddDays(1) form always targeted tomorrow,
        /// which paused ~30 hours when clicked after midnight.
        /// </summary>
        internal static DateTime NextMorning(DateTime now)
        {
            var morning = now.Date.AddHours(7);
            if (now >= morning) morning = morning.AddDays(1);
            return morning;
        }

        /// <summary>
        /// Field-wise comparison of two night-mode snapshots. Used by Refresh to decide
        /// whether to replace the in-editor snapshot: we only swap it in when the saved
        /// state actually differs, so we never yank a value the user is mid-edit on for a
        /// transient field (e.g. live kelvin blend) while still catching real external edits.
        /// </summary>
        private static bool NightModeSettingsEqual(NightModeSettings a, NightModeSettings b)
        {
            if (ReferenceEquals(a, b)) return true;
            return a.Enabled == b.Enabled
                && a.UseAutoSchedule == b.UseAutoSchedule
                && a.Latitude == b.Latitude
                && a.ManualOverrideEnabled == b.ManualOverrideEnabled
                && a.Longitude == b.Longitude
                && a.StartTime == b.StartTime
                && a.EndTime == b.EndTime
                && a.TemperatureKelvin == b.TemperatureKelvin
                && a.Algorithm == b.Algorithm
                && a.UseUltraWarmMode == b.UseUltraWarmMode
                && Math.Abs(a.PerceptualStrength - b.PerceptualStrength) < 0.000001
                && a.FadeMinutes == b.FadeMinutes;
            // Schedule list intentionally excluded: the editor mutates it in place and
            // comparing list contents would force a swap on every programmatic refresh.
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            if (_gamerAutoSaveTimer.IsEnabled)
                SaveGamerProfiles();
            _isDisposed = true;
            Interlocked.Increment(ref _gameDiscoveryGeneration);
            _gameDiscoveryCancellation?.Cancel();
            _gameDiscoveryCancellation?.Dispose();
            _gameDiscoveryCancellation = null;
            if (_gamerApplyService != null && _gamerSessionsChangedHandler != null)
                _gamerApplyService.GamerSessionsChanged -= _gamerSessionsChangedHandler;
            Interlocked.Increment(ref _runningAppScanGeneration);
            SettingsManager.GamerSettingsChanged -= _gamerSettingsChangedHandler;
            SettingsManager.GamerProfileUsed -= _gamerProfileUsedHandler;
            GamerMode.Profiles.CollectionChanged -= OnGamerProfilesCollectionChanged;
            foreach (GamerProfileEditorItem profile in GamerMode.Profiles)
                profile.PropertyChanged -= OnGamerProfileEdited;
            _nightModeService.BlendChanged -= _blendChangedHandler;
            _bypassPollTimer.Stop();
            _gamerAutoSaveTimer.Stop();
            _runningAppRefreshTimer.Stop();
            GC.SuppressFinalize(this);
        }
    }
}
