using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Linq;
using HDRGammaController.Core;

namespace HDRGammaController
{
    public partial class SettingsWindow : Window
    {
        private readonly List<MonitorInfo> _monitors;
        private readonly SettingsManager _settingsManager;
        private readonly Action<MonitorInfo, GammaMode, CalibrationSettings?, int?>? _applyCallback;

        private bool _isLoading = true;
        private MonitorInfo _currentMonitor = null!; // Set in constructor
        private MonitorProfileData _currentProfile = null!; // Set by LoadMonitorProfile
        private MonitorProfileData? _savedProfile; // Last saved profile for compare
        private NightModeSettings _currentNightMode = null!; // Set by LoadMonitorProfile
        private Dictionary<string, MonitorProfileData> _pendingChanges = new();
        // ExcludedApps removed
        
        // Debounce timer for live preview - reused to avoid GC pressure
        private DispatcherTimer? _previewTimer;
        private const int PreviewDebounceMs = 100; // Faster response for UI

        public SettingsWindow(
            MonitorInfo initialMonitor,
            List<MonitorInfo> allMonitors,
            SettingsManager settingsManager,
            Action<MonitorInfo, GammaMode, CalibrationSettings?, int?>? applyCallback = null)
        {
            InitializeComponent();
            
            _monitors = allMonitors;
            _currentMonitor = initialMonitor;
            _settingsManager = settingsManager;
            _applyCallback = applyCallback;
            
            // Populate monitor selector
            foreach (var m in _monitors)
            {
                string label = $"{m.FriendlyName} ({(m.IsHdrActive ? "HDR" : "SDR")})";
                MonitorSelector.Items.Add(new ComboBoxItem { Content = label, Tag = m });
                
                if (m.MonitorDevicePath == initialMonitor.MonitorDevicePath)
                {
                    MonitorSelector.SelectedIndex = MonitorSelector.Items.Count - 1;
                }
            }
            
            // Load profile for initial monitor
            LoadMonitorProfile(_currentMonitor);
            _isLoading = false;
        }
        
        // Simplified constructor for single monitor (backwards compatibility)
        public SettingsWindow(
            MonitorInfo monitor, 
            SettingsManager settingsManager,
            Action<MonitorInfo, GammaMode, CalibrationSettings?, int?>? applyCallback = null)
            : this(monitor, new List<MonitorInfo> { monitor }, settingsManager, applyCallback)
        {
        }
        
        private void MonitorSelector_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            
            // Save pending changes for current monitor before switching
            SaveCurrentToPending();
            
            // Switch to new monitor
            var selectedItem = MonitorSelector.SelectedItem as ComboBoxItem;
            if (selectedItem?.Tag is MonitorInfo newMonitor)
            {
                _currentMonitor = newMonitor;
                _isLoading = true;
                LoadMonitorProfile(_currentMonitor);
                _isLoading = false;
            }
        }
        
        private void SaveCurrentToPending()
        {
            UpdateProfileFromUI();
            if (!string.IsNullOrEmpty(_currentMonitor.MonitorDevicePath))
            {
                // Clone to avoid reference issues
                _pendingChanges[_currentMonitor.MonitorDevicePath] = _currentProfile.Clone();
            }
        }
        
        private void LoadMonitorProfile(MonitorInfo monitor)
        {
            Log.Info($"LoadMonitorProfile: Loading for {monitor.MonitorDevicePath?.Substring(0, Math.Min(30, monitor.MonitorDevicePath?.Length ?? 0))}...");
            
            // Load saved profile from settings (for compare feature)
            _savedProfile = _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath)?.Clone();
            if (_savedProfile == null)
            {
                Log.Info($"LoadMonitorProfile: No saved profile found, using defaults");
                // Default to monitor's current state
                _savedProfile = new MonitorProfileData { GammaMode = monitor.CurrentGamma };
            }
            else
            {
                Log.Info($"LoadMonitorProfile: Saved profile found - Brightness={_savedProfile.Brightness}");
            }
            
            // Check pending changes first, then settings file, then monitor's current state
            if (!string.IsNullOrEmpty(monitor.MonitorDevicePath) && 
                _pendingChanges.TryGetValue(monitor.MonitorDevicePath, out var pending))
            {
                _currentProfile = pending;
                Log.Info($"LoadMonitorProfile: Using pending changes - Brightness={pending.Brightness}");
            }
            else
            {
                var saved = _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath);
                if (saved != null)
                {
                    _currentProfile = saved;
                    Log.Info($"LoadMonitorProfile: Using saved profile - Brightness={saved.Brightness}");
                }
                else
                {
                    Log.Info($"LoadMonitorProfile: No saved, using defaults");
                    // Use monitor's current gamma mode as default
                    _currentProfile = new MonitorProfileData { GammaMode = monitor.CurrentGamma };
                }
            }
            
            LoadSettingsUI();
        }
        
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
        
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        
        private void LoadSettingsUI()
        {
            // Gamma mode
            GammaModeCombo.SelectedIndex = _currentProfile.GammaMode switch
            {
                GammaMode.Gamma22 => 0,
                GammaMode.Gamma24 => 1,
                GammaMode.WindowsDefault => 2,
                _ => 0
            };
            
            // Brightness
            BrightnessSlider.Value = _currentProfile.Brightness;
            BrightnessValue.Text = $"{_currentProfile.Brightness:F0}%";
            EnhanceShadowsCheck.IsChecked = !_currentProfile.UseLinearBrightness;
            
            // Temperature/Tint
            TemperatureSlider.Value = _currentProfile.Temperature;
            TemperatureValue.Text = $"{_currentProfile.Temperature:F0}";
            TempOffsetSlider.Value = _currentProfile.TemperatureOffset;
            TempOffsetValue.Text = $"{_currentProfile.TemperatureOffset:F0}";
            TintSlider.Value = _currentProfile.Tint;
            TintValue.Text = $"{_currentProfile.Tint:F0}";
            
            // RGB Gains
            RedGainSlider.Value = _currentProfile.RedGain;
            RedGainValue.Text = $"{_currentProfile.RedGain:F2}";
            GreenGainSlider.Value = _currentProfile.GreenGain;
            GreenGainValue.Text = $"{_currentProfile.GreenGain:F2}";
            BlueGainSlider.Value = _currentProfile.BlueGain;
            BlueGainValue.Text = $"{_currentProfile.BlueGain:F2}";
            
            // Start Per-Monitor Offset Logic (Global managed in Dashboard)
            _currentNightMode = _settingsManager.NightMode;
            // No Schedule Logic here
        }
        
        private void UpdateNightModeOptionsVisibility() { }
        
        private void ScheduleLivePreview()
        {
            if (_isLoading) return;
            if (LivePreviewToggle?.IsChecked != true) return;

            // Reuse single timer instance to avoid GC pressure
            if (_previewTimer == null)
            {
                _previewTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(PreviewDebounceMs)
                };
                _previewTimer.Tick += (s, e) =>
                {
                    _previewTimer?.Stop();
                    // Run preview async to keep UI responsive
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyCurrentPreview));
                };
            }

            _previewTimer.Stop();
            _previewTimer.Start();
        }
        
        private void ApplyCurrentPreview()
        {
            UpdateProfileFromUI();
            _applyCallback?.Invoke(_currentMonitor, _currentProfile.GammaMode, _currentProfile.ToCalibrationSettings(), null);
        }
        
        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading) return;
            BrightnessValue.Text = $"{e.NewValue:F0}%";
            _currentProfile.Brightness = e.NewValue;
            ScheduleLivePreview();
        }
        
        private void TemperatureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading) return;
            TemperatureValue.Text = $"{e.NewValue:F0}";
            _currentProfile.Temperature = e.NewValue;
            ScheduleLivePreview();
        }

        private void TempOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading) return;
            TempOffsetValue.Text = $"{e.NewValue:F0}";
            _currentProfile.TemperatureOffset = e.NewValue;
            ScheduleLivePreview();
        }
        
        private void TintSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading) return;
            TintValue.Text = $"{e.NewValue:F0}";
            _currentProfile.Tint = e.NewValue;
            ScheduleLivePreview();
        }
        
        private void EnhanceShadows_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _currentProfile.UseLinearBrightness = EnhanceShadowsCheck.IsChecked != true;
            ScheduleLivePreview();
        }

        // PreviewNightTemperature Removed
        

        
        private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading) return;
            
            RedGainValue.Text = $"{RedGainSlider.Value:F2}";
            GreenGainValue.Text = $"{GreenGainSlider.Value:F2}";
            BlueGainValue.Text = $"{BlueGainSlider.Value:F2}";
            
            _currentProfile.RedGain = RedGainSlider.Value;
            _currentProfile.GreenGain = GreenGainSlider.Value;
            _currentProfile.BlueGain = BlueGainSlider.Value;
            ScheduleLivePreview();
        }
        
        // NightModeEnabled_Changed Removed
        
        private void GammaModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            _currentProfile.GammaMode = GetSelectedGammaMode();
            ScheduleLivePreview();
        }
        
        private void LivePreviewToggle_Changed(object sender, RoutedEventArgs e)
        {
            // Show Apply button when live preview is off
            if (ApplyButton == null) return;
            ApplyButton.Visibility = LivePreviewToggle.IsChecked == true 
                ? Visibility.Collapsed 
                : Visibility.Visible;
        }
        
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            // Manual apply when live preview is off
            ApplyCurrentPreview();
        }
        
        private void Brightness100_Click(object sender, RoutedEventArgs e) => BrightnessSlider.Value = 100;
        private void Brightness75_Click(object sender, RoutedEventArgs e) => BrightnessSlider.Value = 75;
        private void Brightness50_Click(object sender, RoutedEventArgs e) => BrightnessSlider.Value = 50;
        
        private void ResetRgb_Click(object sender, RoutedEventArgs e)
        {
            RedGainSlider.Value = 1.0;
            GreenGainSlider.Value = 1.0;
            BlueGainSlider.Value = 1.0;
        }
        
        private void ResetAll_Click(object sender, RoutedEventArgs e)
        {
            _currentProfile = new MonitorProfileData();
            _isLoading = true;
            LoadSettingsUI();
            _isLoading = false;
            ScheduleLivePreview();
        }
        
        // Add/Remove ExcludedApp Removed

        private void Compare_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Apply last saved settings while holding
            if (_savedProfile != null)
            {
                _applyCallback?.Invoke(_currentMonitor, _savedProfile.GammaMode, _savedProfile.ToCalibrationSettings(), null);
            }
            e.Handled = true;
        }
        
        private void Compare_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // Return to current edits when released
            ApplyCurrentPreview();
            e.Handled = true;
        }
        
        private GammaMode GetSelectedGammaMode()
        {
            return GammaModeCombo.SelectedIndex switch
            {
                0 => GammaMode.Gamma22,
                1 => GammaMode.Gamma24,
                2 => GammaMode.WindowsDefault,
                _ => GammaMode.Gamma22
            };
        }
        
        private void UpdateProfileFromUI()
        {
            _currentProfile.GammaMode = GetSelectedGammaMode();
            // Whole-number values: the raw slider doubles (59.71698113207549%) are
            // meaningless precision for these controls and ugly in the UI/logs.
            _currentProfile.Brightness = Math.Round(BrightnessSlider.Value);
            _currentProfile.UseLinearBrightness = EnhanceShadowsCheck.IsChecked != true;
            _currentProfile.Temperature = Math.Round(TemperatureSlider.Value);
            _currentProfile.TemperatureOffset = Math.Round(TempOffsetSlider.Value);
            _currentProfile.Tint = Math.Round(TintSlider.Value);
            // RGB gains are fractional (0.5-1.5) - two decimals, not whole numbers.
            _currentProfile.RedGain = Math.Round(RedGainSlider.Value, 2);
            _currentProfile.GreenGain = Math.Round(GreenGainSlider.Value, 2);
            _currentProfile.BlueGain = Math.Round(BlueGainSlider.Value, 2);
            
            Log.Info($"UpdateProfileFromUI: Brightness={_currentProfile.Brightness}, Temp={_currentProfile.Temperature}, Tint={_currentProfile.Tint}");
        }
        
        private void SaveAndClose_Click(object sender, RoutedEventArgs e)
        {
            // Save current monitor to pending
            SaveCurrentToPending();
            
            // Save all pending changes to settings file
            foreach (var kvp in _pendingChanges)
            {
                _settingsManager.SetMonitorProfile(kvp.Key, kvp.Value);
            }
            
            // Night Mode
             // Night Mode (Global) - handled in Dashboard now
             // _currentNightMode is loaded but we don't save it from here unless we add back properties
             // But we do save Per-Monitor Offset which is in Profile.
            
            // Apply all monitors that have pending changes
            foreach (var kvp in _pendingChanges)
            {
                var monitor = _monitors.Find(m => m.MonitorDevicePath == kvp.Key);
                if (monitor != null)
                {
                    _applyCallback?.Invoke(monitor, kvp.Value.GammaMode, kvp.Value.ToCalibrationSettings(), null);
                }
            }
            
            Close();
        }
    }
}
