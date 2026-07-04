using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
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

        public SettingsManager SettingsManager { get; }

        /// <summary>App version (e.g. "v1.0.1") shown in the dashboard title bar.</summary>
        public string AppVersion => _updateService.DisplayVersion;

        public ObservableCollection<object> Items { get; } = new ObservableCollection<object>();

        public ICommand RefreshCommand { get; }
        public ICommand ConfigureMonitorCommand { get; }
        public ICommand Pause1hCommand { get; }
        public ICommand Pause4hCommand { get; }
        public ICommand PauseUntilMorningCommand { get; }
        public ICommand CycleNightModeCommand { get; }
        public ICommand AddExcludedAppCommand { get; }
        public ICommand RemoveExcludedAppCommand { get; }
        public ICommand SaveExcludedAppsCommand { get; }

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

        public DashboardViewModel(
            MonitorManager monitorManager,
            SettingsManager settingsManager,
            NightModeService nightModeService,
            UpdateService updateService,
            ApplyCalibrationRequest applyCallback)
        {
            _monitorManager = monitorManager;
            SettingsManager = settingsManager;
            _nightModeService = nightModeService;
            _updateService = updateService;
            _applyCallback = applyCallback;

            _appExclusionItem = new AppExclusionItem();
            foreach (var rule in settingsManager.ExcludedApps)
                _appExclusionItem.ExcludedApps.Add(rule);

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

            // Re-refresh when blend changes (for live update) - throttled. Kept as a stored
            // handler so Dispose can unsubscribe; the service outlives this window.
            _blendChangedHandler = _ => Application.Current.Dispatcher.BeginInvoke(new Action(ThrottledRefresh));
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

            // Night mode data
            int nightKelvin = _nightModeService.CurrentNightKelvin;
            double blendedShift = (nightKelvin - 6500) / 70.0;

            Items.Clear();

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

                Items.Add(new DashboardItem
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

            Items.Add(_appExclusionItem);

            // Async load running apps
            _ = LoadRunningAppsAsync(_appExclusionItem);
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
            var apps = await Task.Run(GetRunningApps);
            item.RunningApps.Clear();
            foreach (var app in apps) item.RunningApps.Add(app);
        }

        private static List<string> GetRunningApps()
        {
            try
            {
                return Process.GetProcesses()
                    .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle))
                    .Select(p =>
                    {
                        try { return p.ProcessName.ToLowerInvariant() + ".exe"; }
                        catch { return null; }
                    })
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        private void AddExcludedApp(AppExclusionItem? item)
        {
            if (item == null) return;

            string app = item.NewAppText?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(app) || app == AppExclusionItem.Placeholder) return;
            if (!app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app += ".exe";

            // Check if exists
            if (!item.ExcludedApps.Any(r => r.AppName.Equals(app, StringComparison.OrdinalIgnoreCase)))
            {
                var rule = new AppExclusionRule { AppName = app, FullDisable = false };
                item.ExcludedApps.Add(rule);
                SaveExcludedApps();
            }

            item.NewAppText = "";
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
            _nightModeService.BlendChanged -= _blendChangedHandler;
            _bypassPollTimer.Stop();
        }
    }
}
