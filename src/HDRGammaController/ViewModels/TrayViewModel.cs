using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;
using HDRGammaController.Interop;

namespace HDRGammaController.ViewModels
{
    public class TrayViewModel : ObservableObject, IDisposable
    {
        public event Action<string, string>? NotificationRequested;

        private readonly MonitorManager _monitorManager;
        private readonly ProfileManager _profileManager;
        private readonly DispwinRunner _dispwinRunner;
        private readonly SettingsManager _settingsManager;
        private readonly HotkeyManager? _hotkeyManager;
        private readonly NightModeService _nightModeService;
        private readonly GammaApplyService _applyService;
        private readonly UpdateService _updateService;

        private readonly Dictionary<int, Action<MonitorInfo>> _hotkeyActions = new Dictionary<int, Action<MonitorInfo>>();
        private int _panicId = -1;
        private int _nightModeToggleId = -1;

        private readonly AppDetectionService _appDetectionService;
        private List<MonitorInfo> _activeMonitors = new List<MonitorInfo>();

        private Dictionary<string, MonitorInfo> _savedConfigs = new Dictionary<string, MonitorInfo>();

        public ObservableCollection<object> TrayItems { get; } = new ObservableCollection<object>();

        public ICommand ExitCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand StartupCommand { get; }
        public ICommand DashboardCommand { get; }
        public ICommand CalibrateCommand { get; }

        public TrayViewModel(
            MonitorManager monitorManager,
            SettingsManager settingsManager,
            NightModeService nightModeService,
            ProfileManager profileManager,
            DispwinRunner dispwinRunner,
            GammaApplyService applyService,
            AppDetectionService appDetectionService,
            UpdateService updateService,
            HotkeyManager? hotkeyManager = null)
        {
            // Dependencies are constructed by the DI container (App.ConfigureServices)
            // and must all be assigned before starting any service: NightModeService and
            // AppDetectionService both fire their events synchronously from Start(),
            // and those handlers reach the apply service. The wiring/start order in
            // this constructor body is load-bearing; do not reorder it.
            _monitorManager = monitorManager;
            _settingsManager = settingsManager;
            _nightModeService = nightModeService;
            _profileManager = profileManager;
            _dispwinRunner = dispwinRunner;
            _hotkeyManager = hotkeyManager;
            _applyService = applyService;
            _appDetectionService = appDetectionService;
            _updateService = updateService;

            _dispwinRunner.DispwinUnavailable += () =>
                Application.Current.Dispatcher.Invoke(() =>
                    NotificationRequested?.Invoke("ArgyllCMS Not Found",
                        "Native gamma apply failed and dispwin.exe is unavailable.\n" +
                        "Open Calibrate Display to download ArgyllCMS."));

            _nightModeService.BlendChanged += (blend) =>
            {
                // The hardware apply is thread-agnostic, but ApplyAll reads TrayItems,
                // which belongs to the UI thread.
                Application.Current.Dispatcher.Invoke(() => ApplyAll());
            };
            _settingsManager.NightModeChanged += (newSettings) => _nightModeService.UpdateSettings(newSettings);

            _appDetectionService.ForegroundAppChanged += OnForegroundAppChanged;

            ExitCommand = new RelayCommand(() => Application.Current.Shutdown());
            RefreshCommand = new RelayCommand(RefreshMonitors);
            StartupCommand = new RelayCommand(ToggleStartup);
            DashboardCommand = new RelayCommand(OpenDashboard);
            CalibrateCommand = new RelayCommand(OpenCalibration);

            RefreshMonitors();

            _nightModeService.Start();
            _appDetectionService.Start();

            // Apply saved profiles on startup
            ApplyAll();

            if (_hotkeyManager != null)
            {
                RegisterHotkeys();
                _hotkeyManager.HotkeyPressed += OnHotkeyPressed;
            }

            CheckForUpdates();
        }
        
        /// <summary>
        /// URL to open when the user clicks the update notification balloon.
        /// Null until an update has actually been detected.
        /// </summary>
        public string? PendingUpdateUrl { get; private set; }

        private async void CheckForUpdates()
        {
            // async void: any escaped exception would crash the process via the dispatcher.
            try
            {
                var info = await _updateService.CheckForUpdatesAsync();
                if (info.IsUpdateAvailable)
                {
                     PendingUpdateUrl = !string.IsNullOrEmpty(info.ReleaseUrl)
                         ? info.ReleaseUrl
                         : $"https://github.com/davidtorcivia/gloam/releases";
                     NotificationRequested?.Invoke("Update Available",
                         $"A new version ({info.Version}) is available.\nClick here to download.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"TrayViewModel.CheckForUpdates: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens the release page for a pending update. Wired to the tray balloon
        /// click; without this the balloon's "Click here to download" did nothing.
        /// </summary>
        public void OpenPendingUpdate()
        {
            string? url = PendingUpdateUrl;
            if (string.IsNullOrEmpty(url)) return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Error($"TrayViewModel.OpenPendingUpdate: {ex.Message}");
            }
        }
        
        private void RegisterHotkeys()
        {
            if (_hotkeyManager == null) return;

            // Win+Shift+F1 -> Gamma 2.2
            int id22 = _hotkeyManager.Register(Key.F1, ModifierKeys.Windows | ModifierKeys.Shift);
            if (id22 > 0) _hotkeyActions[id22] = m => ApplyProfile(m, GammaMode.Gamma22);
            
            // Win+Shift+F2 -> Gamma 2.4
            int id24 = _hotkeyManager.Register(Key.F2, ModifierKeys.Windows | ModifierKeys.Shift);
            if (id24 > 0) _hotkeyActions[id24] = m => ApplyProfile(m, GammaMode.Gamma24);
            
            // Win+Shift+F3 -> Default
            int idDef = _hotkeyManager.Register(Key.F3, ModifierKeys.Windows | ModifierKeys.Shift);
            if (idDef > 0) _hotkeyActions[idDef] = m => ApplyProfile(m, GammaMode.WindowsDefault);
            
            // Panic: Win+Shift+F4
            _panicId = _hotkeyManager.Register(Key.F4, ModifierKeys.Windows | ModifierKeys.Shift);
            
            // Night Mode Toggle: Win+Shift+N
            _nightModeToggleId = _hotkeyManager.Register(Key.N, ModifierKeys.Windows | ModifierKeys.Shift);
        }

        private void OnHotkeyPressed(int id)
        {
            if (id == _panicId)
            {
                PanicAll();
                return;
            }
            
            if (id == _nightModeToggleId)
            {
                // Toggle night mode on/off
                _applyService.NightModeManuallyDisabled = !_applyService.NightModeManuallyDisabled;
                ApplyAll(); // Re-apply all calibrations with night mode toggled
                return;
            }

            if (_hotkeyActions.TryGetValue(id, out var action))
            {
                var monitor = GetFocusedMonitor();
                if (monitor != null)
                {
                     action(monitor);
                }
            }
        }
        
        private MonitorInfo? GetFocusedMonitor()
        {
            // A global hotkey should act on the monitor the user is pointing at: the
            // mouse cursor. The focused window's monitor is only the fallback - using
            // it first meant the hotkey always hit wherever the active window lived
            // (usually the primary), regardless of where the user actually was.
            IntPtr hMonitor = IntPtr.Zero;
            if (User32.GetCursorPos(out var cursor))
            {
                hMonitor = User32.MonitorFromPoint(cursor, User32.MONITOR_DEFAULTTONEAREST);
            }
            if (hMonitor == IntPtr.Zero)
            {
                IntPtr hwnd = User32.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return null;
                hMonitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST);
            }

            // First try handle identity (cheap), but HMONITOR values captured at DXGI
            // enumeration go stale after display-configuration changes while the
            // window's handle is current - so fall back to matching the monitor's
            // desktop bounds, which are stable across re-enumeration.
            foreach (var item in TrayItems)
            {
                if (item is MonitorViewModel vm && vm.Model.HMonitor == hMonitor)
                {
                    return vm.Model;
                }
            }

            if (User32.TryGetMonitorBounds(hMonitor, out var rect))
            {
                foreach (var item in TrayItems)
                {
                    if (item is MonitorViewModel vm)
                    {
                        var b = vm.Model.MonitorBounds;
                        if (b.Left == rect.Left && b.Top == rect.Top &&
                            b.Right == rect.Right && b.Bottom == rect.Bottom)
                        {
                            Log.Info($"TrayViewModel: focused monitor matched by bounds (stale HMONITOR) for {vm.Model.FriendlyName}");
                            return vm.Model;
                        }
                    }
                }
            }

            Log.Info($"TrayViewModel: no focused monitor match for HMONITOR {hMonitor} among {TrayItems.OfType<MonitorViewModel>().Count()} monitors");
            return null;
        }

        // Debounce sequence for display-change/resume events. Windows fires
        // WM_DISPLAYCHANGE in bursts during HDR mode transitions; without debouncing,
        // each burst event queued its own RefreshMonitors+ApplyAll, and the overlapping
        // re-applies were visible as flicker.
        private int _displayEventSeq;

        public async void HandleDisplayChange() => await HandleDisplayEventAsync(1500);

        public async void HandleResume() => await HandleDisplayEventAsync(3000);

        private async Task HandleDisplayEventAsync(int settleDelayMs)
        {
            int seq = Interlocked.Increment(ref _displayEventSeq);
            await Task.Delay(settleDelayMs);
            if (seq != Volatile.Read(ref _displayEventSeq)) return; // superseded by a newer event

            try
            {
                // Windows may have reset the gamma ramps; don't let the identical-LUT
                // dedupe skip the re-apply.
                _applyService.InvalidateAppliedState();
                RefreshMonitors();
                ApplyAll();
            }
            catch (Exception ex)
            {
                Log.Error($"TrayViewModel.HandleDisplayEvent: {ex.Message}");
            }
        }


        private void OnForegroundAppChanged(string appName, Dxgi.RECT? appBounds)
        {
            try 
            {
                // Check if app is in exclusion list
                var rule = _settingsManager.ExcludedApps.FirstOrDefault(r => r.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));
                
                var newBlocked = new HashSet<IntPtr>();

                if (rule != null)
                {
                    if (rule.FullDisable)
                    {
                        // Block ALL active monitors
                        foreach(var m in _activeMonitors) newBlocked.Add(m.HMonitor);
                    }
                    else if (appBounds.HasValue)
                    {
                        // Smart Mode: Block intersecting
                        var r1 = appBounds.Value;
                        foreach (var monitor in _activeMonitors)
                        {
                            var r2 = monitor.MonitorBounds;
                            bool intersects = r1.Left < r2.Right && r2.Left < r1.Right &&
                                              r1.Top < r2.Bottom && r2.Top < r1.Bottom;
                            if (intersects) newBlocked.Add(monitor.HMonitor);
                        }
                    }
                    else
                    {
                        // Excluded but no bounds? Maybe block primary or all? 
                        // Let's safe fallback to all if we can't detect bounds (e.g. minimized but active?)
                        // Or maybe just Ignore.
                        // For now, if no bounds, we assume it's not effectively on screen or we can't decide.
                    }
                }
                
                // Diff check (in the service) avoids spamming updates
                if (_applyService.UpdateBlockedMonitors(newBlocked))
                {
                    Log.Info($"TrayViewModel: Block state changed. App={appName}, Blocked Count={newBlocked.Count}");
                    Application.Current.Dispatcher.Invoke(() => ApplyAll());
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error in OnForegroundAppChanged: {ex.Message}");
            }
        }

        private void ApplyProfile(MonitorInfo monitor, GammaMode mode)
        {
            RequestApply(monitor, mode);
            
            // Refresh to update checkmarks
            RefreshMonitors();
        }
        
        public void RequestApply(MonitorInfo monitor, GammaMode mode, CalibrationSettings? manualCalibration = null, int? nightKelvinOverride = null)
            => _applyService.RequestApply(monitor, mode, manualCalibration, nightKelvinOverride);

        private void ApplyAll()
            => _applyService.ApplyAll(TrayItems.OfType<MonitorViewModel>().Select(vm => vm.Model));

        private void PanicAll()
        {
            _applyService.ClearAll(TrayItems.OfType<MonitorViewModel>().Select(vm => vm.Model));
            MessageBox.Show("Panic Mode Activated: All gamma tables cleared.", "Gloam", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ToggleStartup()
        {
            StartupManager.IsStartupEnabled = !StartupManager.IsStartupEnabled;
            RefreshMonitors(); // Refresh to update the checkmark
        }
        
        private void OnMonitorProfileChanged(MonitorInfo monitor, GammaMode mode)
        {
            // Persist to settings
            if (!string.IsNullOrEmpty(monitor.MonitorDevicePath))
            {
                _settingsManager.SetProfileForMonitor(monitor.MonitorDevicePath, mode);
            }
        }

        private void OpenDashboard()
        {
            var dashboard = new DashboardWindow(_monitorManager, _settingsManager, _nightModeService, RequestApply);
            dashboard.Show();
        }

        private void OpenCalibration()
        {
            // Only one probe exists: if a calibration flow is already open (setup wizard,
            // live calibration, or a report window mid-verify), bring it to the front
            // instead of starting a second flow for another monitor.
            foreach (Window window in Application.Current.Windows)
            {
                bool inUse = window is CalibrationSetupWindow or CalibrationWindow
                    || (window is CalibrationReportWindow report && report.IsVerifyRunning);
                if (!inUse) continue;

                Log.Info($"TrayViewModel: calibration already in progress ({window.GetType().Name}); focusing the existing window instead of opening a new flow.");
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
                return;
            }

            var setupWindow = new CalibrationSetupWindow(_activeMonitors, _settingsManager);
            var dialogResult = setupWindow.ShowDialog();

            if (dialogResult == true &&
                setupWindow.SelectedTarget != null &&
                setupWindow.ColorimeterService != null &&
                setupWindow.SelectedMonitor != null)
            {
                // Create state manager to handle bypass/restore during calibration
                var stateManager = new CalibrationStateManager(_dispwinRunner, _nightModeService);

                // Get current settings for the selected monitor
                var profile = _settingsManager.GetMonitorProfile(setupWindow.SelectedMonitor.MonitorDevicePath);
                var currentMode = profile?.GammaMode ?? setupWindow.SelectedMonitor.CurrentGamma;
                var currentSettings = profile?.ToCalibrationSettings();

                var calibrationWindow = new CalibrationWindow(
                    setupWindow.ColorimeterService,
                    setupWindow.SelectedTarget,
                    setupWindow.SelectedPreset,
                    stateManager,
                    setupWindow.SelectedMonitor,
                    currentMode,
                    currentSettings,
                    _settingsManager);

                // Handle calibration completion to refresh our state
                calibrationWindow.CalibrationCompleted += (s, e) =>
                {
                    if (e.Success)
                    {
                        // Refresh monitors to pick up any new calibration data
                        RefreshMonitors();
                    }
                };

                // Back: reopen the setup dialog after this window finishes closing
                // (BeginInvoke so the modal setup doesn't block the close).
                calibrationWindow.BackRequested += (s, e) =>
                    Application.Current.Dispatcher.BeginInvoke(new Action(OpenCalibration));

                // When the calibration window closes, re-assert the correct live gamma through
                // the apply path. This composes any freshly-installed MHC2 calibration with the
                // user's gamma mode + night mode, and overwrites any leftover closed-loop
                // correction the ramp guard might otherwise keep re-applying.
                calibrationWindow.Closed += (s, e) =>
                {
                    _applyService.InvalidateAppliedState();
                    ApplyAll();
                };

                calibrationWindow.Show();
            }
        }

        public void Dispose()
        {
            // Stops the night-mode and ramp-guard timers and unhooks the
            // foreground-window event hook.
            _applyService.Dispose();
            _nightModeService.Dispose();
            _appDetectionService.Dispose();
        }

        public void RefreshMonitors()
        {
            Log.Info("TrayViewModel: Refreshing monitors...");
            TrayItems.Clear();
            _activeMonitors = _monitorManager.EnumerateMonitors();
            var monitors = _activeMonitors;
            Log.Info($"TrayViewModel: Enumerated {monitors.Count} monitors.");
            
            if (monitors.Count == 0)
            {
                TrayItems.Add(new ActionViewModel("No Monitors Found", RefreshCommand)); 
            }
            else
            {
                TrayItems.Add(new ActionViewModel("Open Dashboard...", DashboardCommand));
                TrayItems.Add(new ActionViewModel("Calibrate Display...", CalibrateCommand));
                TrayItems.Add(new ActionViewModel("───────────", null));
                
                int index = 1;
                foreach (var m in monitors)
                {
                    // Restore persistent state from settings file
                    var savedMode = _settingsManager.GetProfileForMonitor(m.MonitorDevicePath);
                    if (savedMode.HasValue)
                    {
                        m.CurrentGamma = savedMode.Value;
                    }
                    else if (!string.IsNullOrEmpty(m.MonitorDevicePath) && 
                        _savedConfigs.TryGetValue(m.MonitorDevicePath, out var saved))
                    {
                        // Fallback to in-memory cache
                        m.CurrentGamma = saved.CurrentGamma;
                        m.SdrWhiteLevel = saved.SdrWhiteLevel;
                    }
                    
                    // Update saved mapping
                    if (!string.IsNullOrEmpty(m.MonitorDevicePath))
                    {
                        _savedConfigs[m.MonitorDevicePath] = m;
                    }

                    var vm = new MonitorViewModel(m, _profileManager, _dispwinRunner, index, _settingsManager);
                    // Point MonitorViewModel to use our centralized RequestApply which handles Night Mode
                    vm.OnApplyWithCalibration = RequestApply;
                    vm.GetAllMonitors = () => monitors;
                    TrayItems.Add(vm);
                    index++;
                }
            }
            
            // Startup toggle with checkmark
            string startupLabel = StartupManager.IsStartupEnabled ? "✓ Start with Windows" : "Start with Windows";
            TrayItems.Add(new ActionViewModel(startupLabel, StartupCommand));
            TrayItems.Add(new ActionViewModel("Refresh", RefreshCommand));
            TrayItems.Add(new ActionViewModel("Exit", ExitCommand));
        }
    }
}
