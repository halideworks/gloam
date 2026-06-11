using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using HDRGammaController.Core;
using HDRGammaController.ViewModels;

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HDRGammaController
{
    public partial class DashboardWindow : Window, INotifyPropertyChanged
    {
        // Frozen brushes: WPF can short-circuit thread-affinity checks on frozen Freezables
        // and skip per-use validation. Allocating a fresh SolidColorBrush every RefreshMonitors()
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
        private readonly SettingsManager _settingsManager;
        private readonly NightModeService _nightModeService;
        private readonly Action<MonitorInfo, GammaMode, CalibrationSettings?, int?> _applyCallback;
        private ObservableCollection<AppExclusionRule> _excludedApps;
        private NightModeSettings _editingNightMode;

        // Throttle refresh to avoid excessive UI updates
        private DateTime _lastRefreshTime = DateTime.MinValue;
        private const int RefreshThrottleMs = 500;
        private bool _refreshPending = false;
        private List<MonitorInfo>? _cachedMonitors;
        
        public event PropertyChangedEventHandler? PropertyChanged;
        
        public bool IsNightModeEnabled
        {
            get => _settingsManager.NightMode.Enabled;
            set
            {
                if (value == _settingsManager.NightMode.Enabled) return;
                var updated = _settingsManager.NightMode;
                updated.Enabled = value;
                _settingsManager.SetNightMode(updated);
                OnPropertyChanged(nameof(IsNightModeEnabled));
            }
        }

        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        
        public DashboardWindow(
            MonitorManager monitorManager, 
            SettingsManager settingsManager,
            NightModeService nightModeService,
            Action<MonitorInfo, GammaMode, CalibrationSettings?, int?> applyCallback)
        {
            InitializeComponent();
            _monitorManager = monitorManager;
            _settingsManager = settingsManager;
            _nightModeService = nightModeService;
            _applyCallback = applyCallback;
            
            // Re-refresh when simple blend changes (for live update) - throttled
            _nightModeService.BlendChanged += (val) => Dispatcher.BeginInvoke(new Action(ThrottledRefresh));

            // Init App Exclusion
            _excludedApps = new ObservableCollection<AppExclusionRule>(_settingsManager.ExcludedApps);
            
            DataContext = this;
            
            // Init Schedule Editor (Global Settings)
            _editingNightMode = _settingsManager.NightMode;
            ScheduleEditor.Initialize(_editingNightMode);
            
            ScheduleEditor.ScheduleChanged += () =>
            {
                 // Update Settings
                 // We update the local copy then save it
                 _settingsManager.SetNightMode(_editingNightMode);
                 // Note: this triggers RefreshMonitors via NightModeService event if active
            };
            
            ScheduleEditor.PreviewTemperatureRequested += async (kelvin) =>
            {
                // Use cached monitors during preview to avoid repeated enumeration
                var monitors = _cachedMonitors ?? _monitorManager.EnumerateMonitors();

                // Offload heavy gamma ramp application to background thread
                await Task.Run(() =>
                {
                    foreach (var m in monitors)
                    {
                        var profile = _settingsManager.GetMonitorProfile(m.MonitorDevicePath);
                        var cal = profile?.ToCalibrationSettings() ?? new CalibrationSettings();
                        _applyCallback(m, profile?.GammaMode ?? m.CurrentGamma, cal, kelvin);
                    }
                });
            };

            RefreshMonitors();
        }

        private void ThrottledRefresh()
        {
            var now = DateTime.Now;
            if ((now - _lastRefreshTime).TotalMilliseconds >= RefreshThrottleMs)
            {
                _lastRefreshTime = now;
                RefreshMonitors();
            }
            else if (!_refreshPending)
            {
                _refreshPending = true;
                // Schedule delayed refresh
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _refreshPending = false;
                    RefreshMonitors();
                }));
            }
        }

        private void RefreshMonitors()
        {
            var monitors = _monitorManager.EnumerateMonitors();
            _cachedMonitors = monitors; // Cache for preview operations
            var items = new List<object>(); // Heterogeneous list
            
            // Night mode data
            int nightKelvin = _nightModeService.CurrentNightKelvin;
            double blendedShift = (nightKelvin - 6500) / 70.0;
            
            foreach (var m in monitors)
            {
                // Load current state
                var profile = _settingsManager.GetMonitorProfile(m.MonitorDevicePath);
                
                // Determine display properties
                bool isHdr = m.IsHdrActive;
                string badgeText = isHdr ? "HDR" : "SDR";
                Brush badgeColor = isHdr ? HdrBadgeBrush : SdrBadgeBrush;
                
                double brightness = profile?.Brightness ?? 100;
                GammaMode gamma = profile?.GammaMode ?? m.CurrentGamma;
                
                // Calculate Effective Temperature
                double baseTemp = profile?.Temperature ?? 0;
                double offset = profile?.TemperatureOffset ?? 0;
                double effectiveTemp = baseTemp + offset + blendedShift;
                int kelvin = (int)(6500 + effectiveTemp * 70);

                string tempText = $"{kelvin}K";
                if (_nightModeService.IsNightModeActive) tempText += " (Night)";
                
                items.Add(new DashboardItem
                {
                    Model = m,
                    FriendlyName = m.FriendlyName,
                    BadgeText = badgeText,
                    BadgeColor = badgeColor,
                    CurrentGamma = gamma,
                    CurrentBrightness = brightness,
                    CurrentTemperatureText = tempText
                });
            }
            
            // Add App Exclusion Card
            var appItem = new AppExclusionItem
            {
                ExcludedApps = _excludedApps,
                // RunningApps is init empty
            };
            items.Add(appItem);
            
            // Async load running apps
            LoadRunningApps(appItem);
            
            MonitorList.ItemsSource = items;
        }

        private void LoadRunningApps(AppExclusionItem item)
        {
            Task.Run(() => 
            {
                var apps = GetRunningApps();
                Dispatcher.Invoke(() => 
                {
                    item.RunningApps.Clear();
                    foreach(var app in apps) item.RunningApps.Add(app);
                });
            });
        }

        private List<string> GetRunningApps()
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
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshMonitors();
        }

        private void Configure_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button btn && btn.Tag is DashboardItem item)
                {
                    // Open SettingsWindow for this monitor
                    var allMonitors = (MonitorList.ItemsSource as List<object>)?.OfType<DashboardItem>().Select(i => i.Model).ToList();
                    
                    var settingsWindow = new SettingsWindow(
                        item.Model, 
                        allMonitors ?? new List<MonitorInfo> { item.Model }, 
                        _settingsManager, 
                        (mon, mode, cal, nightOverride) => 
                        {
                            _applyCallback(mon, mode, cal, nightOverride);
                            if (!nightOverride.HasValue)
                            {
                                RefreshMonitors();
                            }
                        });
                        
                    settingsWindow.Owner = this;
                    settingsWindow.ShowDialog();
                    RefreshMonitors();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening settings: {ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void NightModeToggle_Click(object sender, RoutedEventArgs e)
        {
            var nm = _settingsManager.NightMode;
            nm.Enabled = !nm.Enabled;
            _settingsManager.SetNightMode(nm);
            
            OnPropertyChanged(nameof(IsNightModeEnabled));
            RefreshMonitors();
        }

        private void NightModeToggle_RightClick(object sender, MouseButtonEventArgs e)
        {
             NightModeToggle.ContextMenu.IsOpen = true;
             e.Handled = true;
        }

        private void Pause1h_Click(object sender, RoutedEventArgs e) => _nightModeService.PauseUntil(DateTime.Now.AddHours(1));
        private void Pause4h_Click(object sender, RoutedEventArgs e) => _nightModeService.PauseUntil(DateTime.Now.AddHours(4));
        private void PauseUntilMorning_Click(object sender, RoutedEventArgs e) => _nightModeService.PauseUntil(DateTime.Today.AddDays(1).AddHours(7)); // 7 AM next day

        private void ExcludedAppMode_Click(object sender, RoutedEventArgs e)
        {
            // Just save the current list state
            _settingsManager.SetExcludedApps(_excludedApps.ToList());
        }

        private void AddExcludedApp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is ComboBox combo)
            {
                var app = combo.Text;
                if (combo.SelectedItem is string selected) app = selected;
                AddExcludedApp(app);
                combo.Text = "";
            }
        }
        
        private void AddExcludedApp(string appName)
        {
             string app = appName?.Trim() ?? "";
             if (string.IsNullOrWhiteSpace(app) || app == "Select running app...") return;
             if (!app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app += ".exe";
             
             // Check if exists
             if (!_excludedApps.Any(r => r.AppName.Equals(app, StringComparison.OrdinalIgnoreCase)))
             {
                 var rule = new AppExclusionRule { AppName = app, FullDisable = false };
                 _excludedApps.Add(rule);
                 _settingsManager.SetExcludedApps(_excludedApps.ToList());
             }
        }
        
        private void RemoveExcludedApp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string appName)
            {
                var rule = _excludedApps.FirstOrDefault(r => r.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));
                if (rule != null)
                {
                    _excludedApps.Remove(rule);
                    _settingsManager.SetExcludedApps(_excludedApps.ToList());
                }
            }
        }
    }
}
