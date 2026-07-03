using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;
using HDRGammaController.Interop;
using Velopack;

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
        private readonly IToastService? _toastService;
        private readonly DispatcherTimer _updateCheckTimer;

        private readonly Dictionary<int, Action<MonitorInfo>> _hotkeyActions = new Dictionary<int, Action<MonitorInfo>>();
        private int _panicId = -1;
        private int _nightModeToggleId = -1;

        private readonly AppDetectionService _appDetectionService;
        // Guarded by _activeMonitorsLock: RefreshMonitors (UI thread) mutates this while
        // OnForegroundAppChanged can enumerate it from the foreground-app hook thread.
        private List<MonitorInfo> _activeMonitors = new List<MonitorInfo>();
        private readonly object _activeMonitorsLock = new();

        private Dictionary<string, MonitorInfo> _savedConfigs = new Dictionary<string, MonitorInfo>();

        public ObservableCollection<object> TrayItems { get; } = new ObservableCollection<object>();

        public ICommand ExitCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand StartupCommand { get; }
        public ICommand DashboardCommand { get; }
        public ICommand CalibrateCommand { get; }
        public ICommand ExportDiagnosticsCommand { get; }
        public string AppVersion => _updateService.DisplayVersion;
        public string TrayToolTipText => $"Gloam {AppVersion}";

        public TrayViewModel(
            MonitorManager monitorManager,
            SettingsManager settingsManager,
            NightModeService nightModeService,
            ProfileManager profileManager,
            DispwinRunner dispwinRunner,
            GammaApplyService applyService,
            AppDetectionService appDetectionService,
            UpdateService updateService,
            IToastService? toastService = null,
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
            _toastService = toastService;

            _dispwinRunner.DispwinUnavailable += () =>
                OnUiThread(() =>
                    NotificationRequested?.Invoke("ArgyllCMS Not Found",
                        "Native gamma apply failed and dispwin.exe is unavailable.\n" +
                        "Open Calibrate Display to download ArgyllCMS."));

            _nightModeService.BlendChanged += (blend) =>
            {
                // The hardware apply is thread-agnostic, but ApplyAll reads TrayItems,
                // which belongs to the UI thread.
                OnUiThread(() => ApplyAll());
            };
            _settingsManager.NightModeChanged += (newSettings) => _nightModeService.UpdateSettings(newSettings);

            _appDetectionService.ForegroundAppChanged += OnForegroundAppChanged;

            ExitCommand = new RelayCommand(() => Application.Current.Shutdown());
            RefreshCommand = new RelayCommand(RefreshMonitors);
            StartupCommand = new RelayCommand(ToggleStartup);
            DashboardCommand = new RelayCommand(OpenDashboard);
            CalibrateCommand = new RelayCommand(OpenCalibration);
            ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);

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

            NotifyIfUpdated();
            CheckForUpdates();

            _updateCheckTimer = new DispatcherTimer
            {
                Interval = UpdateService.SuccessfulCheckInterval
            };
            _updateCheckTimer.Tick += (_, _) => CheckForUpdates();
            _updateCheckTimer.Start();
        }
        
        // The downloaded-and-ready update awaiting apply. Null until one is detected and
        // fully downloaded. Scheduled silently for the next normal app exit/restart.
        private UpdateInfo? _pendingUpdate;
        private bool _updateScheduled;
        private bool _updateCheckInProgress;

        private async void CheckForUpdates()
        {
            if (_updateCheckInProgress) return;
            _updateCheckInProgress = true;

            // async void: any escaped exception would crash the process via the dispatcher.
            try
            {
                // Dev (F5) and the portable zip have no Velopack install to update.
                if (!_updateService.IsInstalled) return;

                if (_updateService.TrySchedulePendingUpdateOnExit())
                {
                    _pendingUpdate = null;
                    _updateScheduled = true;
                    NotifyUpdateReady(_updateService.StateSnapshot.LastScheduledVersion, null);
                    return;
                }

                var info = await _updateService.CheckForUpdatesAsync();
                if (info == null)
                {
                    NotifyIfUpdateFailuresPersist();
                    return; // up to date, throttled, or failed and logged
                }

                string version = UpdateService.VersionLabel(info);
                NotifyUpdateAvailable(version);

                // Download silently in the background - no prompt, no manual download step.
                if (!await _updateService.DownloadUpdatesAsync(info))
                {
                    NotifyIfUpdateFailuresPersist();
                    return;
                }

                _pendingUpdate = info;
                _updateScheduled = _updateService.ApplyUpdatesOnExit(info);
                Log.Info(_updateScheduled
                    ? $"TrayViewModel: update {version} downloaded and scheduled for the next app restart."
                    : $"TrayViewModel: update {version} downloaded but could not be scheduled yet; will retry on exit.");
                NotifyUpdateReady(version, info);
            }
            catch (Exception ex)
            {
                Log.Error($"TrayViewModel.CheckForUpdates: {ex.Message}");
            }
            finally
            {
                _updateCheckInProgress = false;
            }
        }

        private void NotifyIfUpdated()
        {
            string? toVersion = _updateService.UpdatedToVersion;
            if (string.IsNullOrWhiteSpace(toVersion)) return;
            if (!_updateService.ShouldNotifyUpdated(toVersion)) return;

            string? fromVersion = _updateService.UpdatedFromVersion;
            string message = string.IsNullOrWhiteSpace(fromVersion)
                ? $"Gloam is now running version {toVersion}."
                : $"Updated from version {fromVersion} to {toVersion}.";

            if (ShowNotification("Gloam updated", message, ToastKind.Success))
                _updateService.MarkUpdatedNotified(toVersion);
        }

        private void NotifyUpdateAvailable(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            if (!_updateService.ShouldNotifyUpdateAvailable(version)) return;

            if (ShowNotification("Update available", $"Version {version} is available. Downloading in the background.", ToastKind.Info))
                _updateService.MarkUpdateAvailableNotified(version);
        }

        private void NotifyUpdateReady(string? version, UpdateInfo? info)
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            if (!_updateService.ShouldNotifyUpdateReady(version)) return;

            string message = _updateScheduled
                ? $"Version {version} is downloaded and will install when Gloam restarts."
                : $"Version {version} is downloaded. Gloam will retry install scheduling on exit.";

            bool shown = false;
            if (info != null)
            {
                shown = ShowNotification("Update ready", message, ToastKind.Success, "Restart now", () =>
                {
                    try
                    {
                        _updateService.ApplyUpdatesAndRestart(info);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"TrayViewModel: restart update apply failed: {ex.Message}");
                        _toastService?.Show("Update will retry later", "Gloam could not restart into the update yet.", ToastKind.Warning);
                    }
                });
            }
            else
            {
                shown = ShowNotification("Update ready", message, ToastKind.Success, "Exit now", () =>
                {
                    try { Application.Current.Shutdown(); }
                    catch (Exception ex) { Log.Error($"TrayViewModel: update exit action failed: {ex.Message}"); }
                });
            }

            if (shown)
                _updateService.MarkUpdateReadyNotified(version);
        }

        private void NotifyIfUpdateFailuresPersist()
        {
            if (!_updateService.ShouldNotifyPersistentFailure())
                return;

            _toastService?.Show("Update will retry later",
                "Gloam could not check for updates. It will keep trying in the background.",
                ToastKind.Info);
            _updateService.MarkFailureNotified();
        }

        private bool ShowNotification(string title, string message, ToastKind kind)
        {
            if (_toastService != null)
            {
                _toastService.Show(title, message, kind);
                return true;
            }

            NotificationRequested?.Invoke(title, message);
            return true;
        }

        private bool ShowNotification(string title, string message, ToastKind kind, string actionLabel, Action action)
        {
            if (_toastService != null)
            {
                _toastService.Show(title, message, kind, actionLabel, action);
                return true;
            }

            NotificationRequested?.Invoke(title, message);
            return true;
        }

        /// <summary>
        /// Marshals an action onto the UI thread WITHOUT blocking the caller, and no-ops if the
        /// dispatcher is gone or shutting down. The night-mode timer and the foreground-window
        /// hook fire on background threads. A synchronous Dispatcher.Invoke from there stalls the
        /// background thread whenever the UI thread is busy or in a modal loop (e.g. a window
        /// DragMove, whose OS-driven move loop does not pump the dispatcher), and an Invoke after
        /// shutdown throws on the background thread, tearing down the process. BeginInvoke avoids
        /// both: it queues and returns immediately, so the apply runs when the UI thread is free.
        /// </summary>
        private static void OnUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;
            try
            {
                dispatcher.BeginInvoke(action);
            }
            catch (Exception ex)
            {
                // Most likely the dispatcher began shutting down between the check and the
                // BeginInvoke. Log and move on rather than letting it escape the timer/hook thread.
                Log.Error($"TrayViewModel.OnUiThread: {ex.Message}");
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
                // Toggle night mode on/off. Not persisted across restarts (avoids a stuck-off
                // footgun where night mode silently never returns); the toast makes the current
                // state visible on every press.
                bool nowDisabled = (_applyService.NightModeManuallyDisabled = !_applyService.NightModeManuallyDisabled);
                ApplyAll(); // Re-apply all calibrations with night mode toggled
                _toastService?.Show("Gloam",
                    nowDisabled ? "Night warmth disabled - day mode" : "Night warmth enabled",
                    nowDisabled ? ToastKind.Info : ToastKind.Success);
                return;
            }

            if (_hotkeyActions.TryGetValue(id, out var action))
            {
                var monitor = GetFocusedMonitor();
                if (monitor != null)
                {
                     action(monitor);
                }
                else
                {
                    // #18: previously this dropped the apply silently. Tell the user why
                    // nothing changed instead.
                    _toastService?.Show("Gloam", "No monitor under cursor", ToastKind.Warning);
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
                // Re-evaluate the night-mode schedule for 'now' BEFORE re-applying: after a
                // suspend/resume (or a display-change burst that straddled a trigger) the
                // service's cached kelvin can be hours stale, and ApplyAll would re-assert
                // that stale warmth until the next timer tick. Feeding the persisted
                // settings back in forces an immediate state update + timer reschedule.
                _nightModeService.UpdateSettings(_settingsManager.NightMode);
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
                // Snapshot the monitor list under the lock: RefreshMonitors (UI thread) can be
                // rebuilding _activeMonitors while this handler runs on the foreground-app hook
                // thread, and a concurrent List mutation throws / yields torn reads.
                List<MonitorInfo> activeMonitors;
                lock (_activeMonitorsLock)
                {
                    activeMonitors = _activeMonitors;
                }

                // Check if app is in exclusion list
                var rule = _settingsManager.ExcludedApps.FirstOrDefault(r => r.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));

                var newBlocked = new HashSet<IntPtr>();

                if (rule != null)
                {
                    if (rule.FullDisable)
                    {
                        // Block ALL active monitors
                        foreach(var m in activeMonitors) newBlocked.Add(m.HMonitor);
                    }
                    else if (appBounds.HasValue)
                    {
                        // Smart Mode: Block intersecting
                        var r1 = appBounds.Value;
                        foreach (var monitor in activeMonitors)
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
                    OnUiThread(() => ApplyAll());
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
        
        public void RequestApply(
            MonitorInfo monitor,
            GammaMode mode,
            CalibrationSettings? manualCalibration = null,
            int? nightKelvinOverride = null,
            NightModeSettings? nightModeSettingsOverride = null)
            => _applyService.RequestApply(monitor, mode, manualCalibration, nightKelvinOverride, nightModeSettingsOverride);

        private void ApplyAll()
            => _applyService.ApplyAll(TrayItems.OfType<MonitorViewModel>().Select(vm => vm.Model));

        private void PanicAll()
        {
            _applyService.ClearAll(TrayItems.OfType<MonitorViewModel>().Select(vm => vm.Model));
            // #22: replaced a parentless MessageBox.Show (called from the hotkey hook thread,
            // which could appear behind other windows / on the wrong monitor) with a themed,
            // non-modal toast. It confirms the recovery without stealing focus.
            _toastService?.Show("Gloam", "Gamma cleared - safe mode", ToastKind.Warning);
        }

        private ActionViewModel? _startupItem;

        private static string StartupLabel()
            => StartupManager.IsStartupEnabled ? "✓ Start with Windows" : "Start with Windows";

        private void ToggleStartup()
        {
            StartupManager.IsStartupEnabled = !StartupManager.IsStartupEnabled;
            // Update the checkmark in place; rebuilding the menu would tear the
            // items out from under the still-open tray menu.
            if (_startupItem != null) _startupItem.Header = StartupLabel();
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
            var dashboard = new DashboardWindow(_monitorManager, _settingsManager, _nightModeService, _updateService, RequestApply);
            dashboard.Show();
        }

        private void OpenCalibration()
            => OpenCalibration(preferredMonitorDevicePath: null, calibrationUiState: null, reusableColorimeterService: null);

        private void OpenCalibration(
            string? preferredMonitorDevicePath,
            CalibrationWindow.UiState? calibrationUiState = null,
            ColorimeterService? reusableColorimeterService = null)
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

            List<MonitorInfo> activeMonitors;
            lock (_activeMonitorsLock) { activeMonitors = _activeMonitors; }
            var setupWindow = new CalibrationSetupWindow(
                activeMonitors,
                _settingsManager,
                preferredMonitorDevicePath,
                reusableColorimeterService);
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
                WindowBoundsPersistence.CopyBounds(calibrationWindow, setupWindow);
                calibrationWindow.ApplyUiState(calibrationUiState);

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
                var selectedMonitorPath = setupWindow.SelectedMonitor.MonitorDevicePath;
                calibrationWindow.BackRequested += (s, e) =>
                {
                    var uiState = calibrationWindow.CaptureUiState();
                    var colorimeterService = calibrationWindow.ColorimeterService;
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        OpenCalibration(selectedMonitorPath, uiState, colorimeterService)));
                };

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

        private void ExportDiagnostics()
        {
            try
            {
                List<MonitorInfo> activeMonitors;
                lock (_activeMonitorsLock) { activeMonitors = _activeMonitors.ToList(); }

                bool includeReports = false;
                var owner = Application.Current?.Windows
                    .OfType<Window>()
                    .FirstOrDefault(w => w.IsActive && w.IsVisible)
                    ?? Application.Current?.MainWindow;
                if (owner != null)
                {
                    includeReports = ConfirmDialog.Confirm(owner,
                        "Diagnostics Export",
                        "Include saved calibration report snapshots and detailed verification CSVs in the support bundle?\n\n" +
                        "Choose Summary Only for the smallest bundle.",
                        confirmLabel: "Include Reports",
                        cancelLabel: "Summary Only");
                }

                string outputDir = Path.Combine(AppPaths.DataDir, "Diagnostics");
                string path = new DiagnosticsBundle().Create(outputDir, activeMonitors, _settingsManager, includeReports);
                Log.Info($"TrayViewModel: diagnostics bundle exported to {path}");

                string message = $"Saved to {path}";
                if (_toastService != null)
                    _toastService.Show("Diagnostics exported", message, ToastKind.Success);
                else
                    NotificationRequested?.Invoke("Diagnostics exported", message);
            }
            catch (Exception ex)
            {
                Log.Error($"TrayViewModel.ExportDiagnostics: {ex}");
                _toastService?.Show("Diagnostics failed", ex.Message, ToastKind.Error);
                NotificationRequested?.Invoke("Diagnostics failed", ex.Message);
            }
        }

        public void Dispose()
        {
            // If an update was downloaded but scheduling failed earlier, retry on the way
            // out so the next launch can land on the new version.
            if (_pendingUpdate != null && !_updateScheduled)
            {
                _updateScheduled = _updateService.ApplyUpdatesOnExit(_pendingUpdate);
            }

            // Stops the night-mode and ramp-guard timers and unhooks the
            // foreground-window event hook.
            _updateCheckTimer.Stop();
            _applyService.Dispose();
            _nightModeService.Dispose();
            _appDetectionService.Dispose();
        }

        public void RefreshMonitors()
        {
            Log.Info("TrayViewModel: Refreshing monitors...");
            TrayItems.Clear();
            var enumerated = _monitorManager.EnumerateMonitors();
            // Publish the new list atomically: OnForegroundAppChanged may snapshot it from the
            // foreground-app hook thread at any moment. A new list reference is published in
            // one write, so readers never see a half-built list.
            lock (_activeMonitorsLock)
            {
                _activeMonitors = enumerated;
            }
            var monitors = enumerated;
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
                    vm.OnApplyWithCalibration = (monitor, mode, calibration, nightKelvinOverride) =>
                        RequestApply(monitor, mode, calibration, nightKelvinOverride);
                    vm.GetAllMonitors = () => monitors;
                    TrayItems.Add(vm);
                    index++;
                }
            }
            
            // Startup toggle with checkmark
            _startupItem = new ActionViewModel(StartupLabel(), StartupCommand, staysOpenOnClick: true);
            TrayItems.Add(_startupItem);
            TrayItems.Add(new ActionViewModel("Export Diagnostics", ExportDiagnosticsCommand));
            TrayItems.Add(new ActionViewModel("Refresh", RefreshCommand, staysOpenOnClick: true));
            TrayItems.Add(new ActionViewModel("Exit", ExitCommand));
        }
    }
}
