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

namespace HDRGammaController.ViewModels
{
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
        private readonly Action<MonitorInfo, GammaMode, CalibrationSettings?, int?> _applyCallback;
        private readonly Action<double> _blendChangedHandler;
        private readonly AppExclusionItem _appExclusionItem;
        private readonly NightModeSettings _editingNightMode;

        // Throttle refresh to avoid excessive UI updates
        private DateTime _lastRefreshTime = DateTime.MinValue;
        private const int RefreshThrottleMs = 500;
        private bool _refreshPending;
        private List<MonitorInfo>? _cachedMonitors;

        public SettingsManager SettingsManager { get; }

        public ObservableCollection<object> Items { get; } = new ObservableCollection<object>();

        public ICommand RefreshCommand { get; }
        public ICommand ConfigureMonitorCommand { get; }
        public ICommand Pause1hCommand { get; }
        public ICommand Pause4hCommand { get; }
        public ICommand PauseUntilMorningCommand { get; }
        public ICommand AddExcludedAppCommand { get; }
        public ICommand RemoveExcludedAppCommand { get; }
        public ICommand SaveExcludedAppsCommand { get; }

        /// <summary>
        /// Navigation request: the view opens SettingsWindow for the given monitor card.
        /// </summary>
        public event Action<DashboardItem>? ConfigureRequested;

        public DashboardViewModel(
            MonitorManager monitorManager,
            SettingsManager settingsManager,
            NightModeService nightModeService,
            Action<MonitorInfo, GammaMode, CalibrationSettings?, int?> applyCallback)
        {
            _monitorManager = monitorManager;
            SettingsManager = settingsManager;
            _nightModeService = nightModeService;
            _applyCallback = applyCallback;

            _appExclusionItem = new AppExclusionItem();
            foreach (var rule in settingsManager.ExcludedApps)
                _appExclusionItem.ExcludedApps.Add(rule);

            _editingNightMode = settingsManager.NightMode;

            RefreshCommand = new RelayCommand(Refresh);
            ConfigureMonitorCommand = new RelayCommand<DashboardItem>(item =>
            {
                if (item != null) ConfigureRequested?.Invoke(item);
            });
            Pause1hCommand = new RelayCommand(() => _nightModeService.PauseUntil(DateTime.Now.AddHours(1)));
            Pause4hCommand = new RelayCommand(() => _nightModeService.PauseUntil(DateTime.Now.AddHours(4)));
            PauseUntilMorningCommand = new RelayCommand(() => _nightModeService.PauseUntil(DateTime.Today.AddDays(1).AddHours(7))); // 7 AM next day
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
                bool any = _cachedMonitors?.Any(m => CalibrationStateManager.IsDeviceInBypass(m.MonitorDevicePath)) ?? false;
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
                SettingsManager.SetNightMode(updated);
                OnPropertyChanged(nameof(IsNightModeEnabled));
                Refresh();
            }
        }

        /// <summary>
        /// The night-mode settings instance the schedule editor mutates in place;
        /// SaveEditedNightMode persists it.
        /// </summary>
        public NightModeSettings EditingNightMode => _editingNightMode;

        public void SaveEditedNightMode()
        {
            // Note: this triggers Refresh via the NightModeService blend event if active
            SettingsManager.SetNightMode(_editingNightMode);
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
                    _applyCallback(m, profile?.GammaMode ?? m.CurrentGamma, cal, kelvin);
                }
            });
        }

        public void ThrottledRefresh()
        {
            var now = DateTime.Now;
            if ((now - _lastRefreshTime).TotalMilliseconds >= RefreshThrottleMs)
            {
                _lastRefreshTime = now;
                Refresh();
            }
            else if (!_refreshPending)
            {
                _refreshPending = true;
                // Schedule delayed refresh
                _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _refreshPending = false;
                    Refresh();
                }));
            }
        }

        public void Refresh()
        {
            var monitors = _monitorManager.EnumerateMonitors();
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
                double effectiveTemp = baseTemp + offset + blendedShift;
                int kelvin = (int)(6500 + effectiveTemp * 70);

                string tempText = $"{kelvin}K";
                if (_nightModeService.IsNightModeActive) tempText += " (Night)";

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
                    IsCalibrating = CalibrationStateManager.IsDeviceInBypass(m.MonitorDevicePath)
                });
            }

            Items.Add(_appExclusionItem);

            // Async load running apps
            _ = LoadRunningAppsAsync(_appExclusionItem);
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

        public void Dispose()
        {
            _nightModeService.BlendChanged -= _blendChangedHandler;
            _bypassPollTimer.Stop();
        }
    }
}
