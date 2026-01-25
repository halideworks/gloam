using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;
using HDRGammaController.Interop;

namespace HDRGammaController.ViewModels
{
    public class TrayViewModel
    {
        public event Action<string, string>? NotificationRequested;
        
        private readonly MonitorManager _monitorManager;
        private readonly ProfileManager _profileManager;
        private readonly DispwinRunner _dispwinRunner;
        private readonly SettingsManager _settingsManager;
        private readonly HotkeyManager? _hotkeyManager;
        private readonly NightModeService _nightModeService;
        private readonly UpdateService _updateService;
        
        private readonly Dictionary<int, Action<MonitorInfo>> _hotkeyActions = new Dictionary<int, Action<MonitorInfo>>();
        private int _panicId = -1;
        private int _nightModeToggleId = -1;
        private bool _nightModeManuallyDisabled = false;
        
        private readonly AppDetectionService _appDetectionService;
        private HashSet<IntPtr> _blockedMonitors = new HashSet<IntPtr>();
        private List<MonitorInfo> _activeMonitors = new List<MonitorInfo>();

        private Dictionary<string, MonitorInfo> _savedConfigs = new Dictionary<string, MonitorInfo>();

        public ObservableCollection<object> TrayItems { get; } = new ObservableCollection<object>();

        public ICommand ExitCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand StartupCommand { get; }
        public ICommand DashboardCommand { get; }
        public ICommand CalibrateCommand { get; }

        public TrayViewModel(HotkeyManager? hotkeyManager = null)
        {
            _monitorManager = new MonitorManager();
            _settingsManager = new SettingsManager();
            
            // Initialize Night Mode Service
            _nightModeService = new NightModeService(_settingsManager.NightMode);
            _nightModeService.BlendChanged += (blend) => 
            {
                // Dispatch to UI thread if needed (though ApplyAll primarily runs dispwin which is blocking/background)
                // WPF Observables need UI thread, but ApplyAll primarily affects hardware. 
                // However, TrayItems might update. Better invoke.
                Application.Current.Dispatcher.Invoke(() => ApplyAll());
            };
            
            _settingsManager.NightModeChanged += (newSettings) => _nightModeService.UpdateSettings(newSettings);
            
            // Start service
            _nightModeService.Start();
            
            // App Detection
            _appDetectionService = new AppDetectionService();
            _appDetectionService.ForegroundAppChanged += OnForegroundAppChanged;
            _appDetectionService.Start();

            // Assumes template is in the same directory (needs to be sourced by user)
            string profileTemplatePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "srgb_to_gamma2p2_100_mhc2.icm");
            _profileManager = new ProfileManager(profileTemplatePath);
            _dispwinRunner = new DispwinRunner(); // Auto-detects
            _hotkeyManager = hotkeyManager;

            ExitCommand = new RelayCommand(_ => Application.Current.Shutdown());
            RefreshCommand = new RelayCommand(_ => RefreshMonitors());
            StartupCommand = new RelayCommand(_ => ToggleStartup());
            DashboardCommand = new RelayCommand(_ => OpenDashboard());
            CalibrateCommand = new RelayCommand(_ => OpenCalibration());

            RefreshMonitors();
            
            // Apply saved profiles on startup
            ApplyAll();
            
            if (_hotkeyManager != null)
            {
                RegisterHotkeys();
                _hotkeyManager.HotkeyPressed += OnHotkeyPressed;
            }
            
            _updateService = new UpdateService();
            CheckForUpdates();
        }
        
        private async void CheckForUpdates()
        {
            var info = await _updateService.CheckForUpdatesAsync();
            if (info.IsUpdateAvailable)
            {
                 NotificationRequested?.Invoke("Update Available", 
                     $"A new version ({info.Version}) is available.\nClick here to download.");
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
                _nightModeManuallyDisabled = !_nightModeManuallyDisabled;
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
            IntPtr hwnd = User32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            
            IntPtr hMonitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST);
            
            foreach(var item in TrayItems)
            {
                if (item is MonitorViewModel vm && vm.Model.HMonitor == hMonitor)
                {
                    return vm.Model;
                }
            }
            return null;
        }

        public async void HandleDisplayChange()
        {
            await Task.Delay(1500);
            RefreshMonitors();
            ApplyAll();
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
                
                // Diff check to avoid spamming updates
                if (!_blockedMonitors.SetEquals(newBlocked))
                {
                    _blockedMonitors = newBlocked;
                    Console.WriteLine($"TrayViewModel: Block state changed. App={appName}, Blocked Count={_blockedMonitors.Count}");
                    Application.Current.Dispatcher.Invoke(() => ApplyAll());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnForegroundAppChanged: {ex.Message}");
            }
        }

        public async void HandleResume()
        {
            await Task.Delay(3000); 
            RefreshMonitors();
            ApplyAll();
        }
        
        private void ApplyProfile(MonitorInfo monitor, GammaMode mode)
        {
            RequestApply(monitor, mode);
            
            // Refresh to update checkmarks
            RefreshMonitors();
        }
        
        public void RequestApply(MonitorInfo monitor, GammaMode mode, CalibrationSettings? manualCalibration = null, int? nightKelvinOverride = null)
        {
             // Use override if provided (during drag preview), else service's kelvin
            int currentKelvin = nightKelvinOverride ?? _nightModeService.CurrentNightKelvin;
            if (_nightModeManuallyDisabled) currentKelvin = 6500;
            
            // Check for App Exclusion Block
            if (_blockedMonitors.Contains(monitor.HMonitor))
            {
                currentKelvin = 6500;
            }

            // Force active if override provided (to ensure preview works even if service is at 6500)
            bool nightModeActive = nightKelvinOverride.HasValue || currentKelvin < 6450;
            
            try 
            { 
                 // If manual calibration is provided (from live preview), use it.
                 // Otherwise load from profile.
                 CalibrationSettings calibration;
                 double brightness = 100;
                 
                 if (manualCalibration != null)
                 {
                     calibration = manualCalibration;
                     brightness = calibration.Brightness; // Approx
                 }
                 else
                 {
                    var profile = _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath) ?? new MonitorProfileData();
                    calibration = profile.ToCalibrationSettings();
                    brightness = profile.Brightness;
                 }

                // Apply static offset
                calibration.Temperature += calibration.TemperatureOffset;

                // Apply night mode temperature if active
                if (nightModeActive)
                {
                    // Calculate night mode shift (-50 to +50 scale)
                    double nightShift = (currentKelvin - 6500) / 70.0;
                    calibration.Temperature += nightShift;

                    // Apply night mode algorithm and ultra warm settings
                    calibration.Algorithm = _settingsManager.NightMode.Algorithm;
                    calibration.UseUltraWarmMode = _settingsManager.NightMode.UseUltraWarmMode;
                }
                
                // Clamp to extended range: -65.7 to +50 maps to 1900K-10000K
                // This allows night mode schedule to use temps below 3000K
                calibration.Temperature = Math.Clamp(calibration.Temperature, -65.7, 50.0);
                
                Console.WriteLine($"RequestApply: Applying {monitor.FriendlyName} - Gamma={mode}, Brightness={brightness}, Temp={calibration.Temperature:F1}");
                _dispwinRunner.ApplyGamma(monitor, mode, monitor.SdrWhiteLevel, calibration); 
                
                // Update persistent state if this wasn't a manual preview
                if (manualCalibration == null)
                {
                     monitor.CurrentGamma = mode;
                     if (!string.IsNullOrEmpty(monitor.MonitorDevicePath))
                     {
                         _settingsManager.SetProfileForMonitor(monitor.MonitorDevicePath, mode);
                     }
                }
            } catch (Exception ex) 
            {
                Console.WriteLine($"RequestApply error: {ex.Message}");
            }
        }

        private void ApplyAll()
        {
            int currentKelvin = _nightModeService.CurrentNightKelvin;
            if (_nightModeManuallyDisabled) currentKelvin = 6500;
            bool nightModeActive = currentKelvin < 6450;

            foreach(var item in TrayItems)
            {
                if (item is MonitorViewModel vm)
                {
                    // Get saved mode
                    var profile = _settingsManager.GetMonitorProfile(vm.Model.MonitorDevicePath);
                    var mode = profile?.GammaMode ?? vm.Model.CurrentGamma;
                    
                    if (mode == GammaMode.WindowsDefault && !nightModeActive) continue;
                    
                    RequestApply(vm.Model, mode);
                }
            }
        }

        private void PanicAll()
        {
            foreach(var item in TrayItems)
            {
                if (item is MonitorViewModel vm)
                {
                     try { _dispwinRunner.ClearGamma(vm.Model); } catch {}
                }
            }
            MessageBox.Show("Panic Mode Activated: All gamma tables cleared.", "HDR Gamma Controller", MessageBoxButton.OK, MessageBoxImage.Information);
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
            var setupWindow = new CalibrationSetupWindow(_activeMonitors);
            if (setupWindow.ShowDialog() == true &&
                setupWindow.SelectedTarget != null &&
                setupWindow.ColorimeterService != null)
            {
                var calibrationWindow = new CalibrationWindow(
                    setupWindow.ColorimeterService,
                    setupWindow.SelectedTarget,
                    setupWindow.SelectedPreset);

                calibrationWindow.Show();
            }
        }

        public void RefreshMonitors()
        {
            Console.WriteLine("TrayViewModel: Refreshing monitors...");
            TrayItems.Clear();
            _activeMonitors = _monitorManager.EnumerateMonitors();
            var monitors = _activeMonitors;
            Console.WriteLine($"TrayViewModel: Enumerated {monitors.Count} monitors.");
            
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
