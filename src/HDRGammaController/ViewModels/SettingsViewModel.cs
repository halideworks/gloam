using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.ViewModels
{
    public class MonitorChoice
    {
        public MonitorInfo Model { get; }
        public string Label { get; }

        public MonitorChoice(MonitorInfo model)
        {
            Model = model;
            Label = $"{model.FriendlyName} ({(model.IsHdrActive ? "HDR" : "SDR")})";
        }

        // The SettingsWindow ComboBox template renders the closed-state selection
        // box via ToString(), not DisplayMemberPath.
        public override string ToString() => Label;
    }

    public class SettingsViewModel : ObservableObject
    {
        private readonly List<MonitorInfo> _monitors;
        private readonly SettingsManager _settingsManager;
        private readonly Action<MonitorInfo, GammaMode, CalibrationSettings?, int?>? _applyCallback;

        private MonitorInfo _currentMonitor;
        private MonitorProfileData _profile = null!; // Set by LoadMonitorProfile
        private MonitorProfileData? _savedProfile;   // Last saved profile for compare
        private readonly Dictionary<string, MonitorProfileData> _pendingChanges = new();
        private bool _isLoading;

        // True once any unsaved edit has been pushed to the GPU ramp (live preview,
        // Apply button, or compare-end re-apply). Drives the revert on close-without-save.
        private bool _livePreviewApplied;

        // Set by SaveAndClose so the Closed handler's Cleanup skips the revert.
        private bool _savedAndClosed;

        // Debounce timer for live preview - reused to avoid GC pressure
        private DispatcherTimer? _previewTimer;
        private const int PreviewDebounceMs = 100; // Faster response for UI

        public ObservableCollection<MonitorChoice> Monitors { get; }

        public ICommand SetBrightnessCommand { get; }
        public ICommand ResetRgbCommand { get; }
        public ICommand ResetAllCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand SaveAndCloseCommand { get; }
        public ICommand ClearNightSpectrumCommand { get; }

        public event Action? CloseRequested;

        public SettingsViewModel(
            MonitorInfo initialMonitor,
            List<MonitorInfo> allMonitors,
            SettingsManager settingsManager,
            Action<MonitorInfo, GammaMode, CalibrationSettings?, int?>? applyCallback)
        {
            _monitors = allMonitors;
            _settingsManager = settingsManager;
            _applyCallback = applyCallback;
            _currentMonitor = initialMonitor;

            Monitors = new ObservableCollection<MonitorChoice>();
            MonitorChoice? initialChoice = null;
            foreach (var m in _monitors)
            {
                var choice = new MonitorChoice(m);
                Monitors.Add(choice);
                if (m.MonitorDevicePath == initialMonitor.MonitorDevicePath)
                {
                    initialChoice = choice;
                }
            }

            SetBrightnessCommand = new RelayCommand<string>(v =>
            {
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)) Brightness = b;
            });
            ResetRgbCommand = new RelayCommand(() =>
            {
                RedGain = 1.0;
                GreenGain = 1.0;
                BlueGain = 1.0;
            });
            ResetAllCommand = new RelayCommand(ResetAll);
            ApplyCommand = new RelayCommand(ApplyCurrent);
            SaveAndCloseCommand = new RelayCommand(SaveAndClose);
            ClearNightSpectrumCommand = new RelayCommand(() => SelectNightSpectrumPath(null));

            _selectedMonitor = initialChoice ?? (Monitors.Count > 0 ? Monitors[0] : null);
            if (_selectedMonitor != null)
            {
                _currentMonitor = _selectedMonitor.Model;
            }
            LoadMonitorProfile(_currentMonitor);
        }

        private MonitorChoice? _selectedMonitor;
        public MonitorChoice? SelectedMonitor
        {
            get => _selectedMonitor;
            set
            {
                if (value == null || ReferenceEquals(value, _selectedMonitor)) return;

                // Save pending changes for current monitor before switching
                SaveCurrentToPending();

                _selectedMonitor = value;
                OnPropertyChanged();
                _currentMonitor = value.Model;
                LoadMonitorProfile(_currentMonitor);
            }
        }

        private bool _isLivePreview = true;
        public bool IsLivePreview
        {
            get => _isLivePreview;
            set => SetProperty(ref _isLivePreview, value);
        }

        public int GammaModeIndex
        {
            get => _profile.GammaMode switch
            {
                GammaMode.Gamma22 => 0,
                GammaMode.Gamma24 => 1,
                GammaMode.WindowsDefault => 2,
                _ => 0
            };
            set
            {
                var mode = value switch
                {
                    0 => GammaMode.Gamma22,
                    1 => GammaMode.Gamma24,
                    2 => GammaMode.WindowsDefault,
                    _ => GammaMode.Gamma22
                };
                if (_profile.GammaMode == mode) return;
                _profile.GammaMode = mode;
                OnPropertyChanged();
                SchedulePreview();
            }
        }

        // Whole-number values: the raw slider doubles (59.71698113207549%) are
        // meaningless precision for these controls and ugly in the UI/logs.
        public double Brightness
        {
            get => _profile.Brightness;
            set
            {
                value = Math.Round(value);
                if (_profile.Brightness.Equals(value)) return;
                _profile.Brightness = value;
                OnPropertyChanged();
                SchedulePreview();
            }
        }

        public bool EnhanceShadows
        {
            get => !_profile.UseLinearBrightness;
            set
            {
                if (EnhanceShadows == value) return;
                _profile.UseLinearBrightness = !value;
                OnPropertyChanged();
                SchedulePreview();
            }
        }

        public double Temperature
        {
            get => _profile.Temperature;
            set
            {
                value = Math.Round(value);
                if (_profile.Temperature.Equals(value)) return;
                _profile.Temperature = value;
                OnPropertyChanged();
                SchedulePreview();
            }
        }

        public double TemperatureOffset
        {
            get => _profile.TemperatureOffset;
            set
            {
                value = Math.Round(value);
                if (_profile.TemperatureOffset.Equals(value)) return;
                _profile.TemperatureOffset = value;
                OnPropertyChanged();
                SchedulePreview();
            }
        }

        public double Tint
        {
            get => _profile.Tint;
            set
            {
                value = Math.Round(value);
                if (_profile.Tint.Equals(value)) return;
                _profile.Tint = value;
                OnPropertyChanged();
                SchedulePreview();
            }
        }

        // RGB gains are fractional (0.5-1.5) - two decimals, not whole numbers.
        public double RedGain
        {
            get => _profile.RedGain;
            set
            {
                value = Math.Round(value, 2);
                if (_profile.RedGain.Equals(value)) return;
                _profile.RedGain = value;
                OnPropertyChanged();
                SchedulePreview();
            }
        }

        public double GreenGain
        {
            get => _profile.GreenGain;
            set
            {
                value = Math.Round(value, 2);
                if (_profile.GreenGain.Equals(value)) return;
                _profile.GreenGain = value;
                OnPropertyChanged();
                SchedulePreview();
            }
        }

        public double BlueGain
        {
            get => _profile.BlueGain;
            set
            {
                value = Math.Round(value, 2);
                if (_profile.BlueGain.Equals(value)) return;
                _profile.BlueGain = value;
                OnPropertyChanged();
                SchedulePreview();
            }
        }

        public string CurrentMonitorFriendlyName => _currentMonitor.FriendlyName ?? "";

        public string NightSpectrumLabel => string.IsNullOrWhiteSpace(_profile.NightModeCcssPath)
            ? "Generic RGB estimate"
            : System.IO.Path.GetFileName(_profile.NightModeCcssPath);

        public bool HasNightSpectrum => !string.IsNullOrWhiteSpace(_profile.NightModeCcssPath);

        public void SelectNightSpectrumPath(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!path.EndsWith(".ccss", StringComparison.OrdinalIgnoreCase) ||
                    !CgatsValidator.IsValidFile(path, "ccss"))
                {
                    Log.Info($"SettingsViewModel: rejected night spectrum path '{path}'");
                    return;
                }
            }

            _profile.NightModeCcssPath = string.IsNullOrWhiteSpace(path) ? null : path;
            OnPropertyChanged(nameof(NightSpectrumLabel));
            OnPropertyChanged(nameof(HasNightSpectrum));
            SchedulePreview();
        }

        private void LoadMonitorProfile(MonitorInfo monitor)
        {
            Log.Info($"SettingsViewModel: Loading profile for {monitor.MonitorDevicePath?.Substring(0, Math.Min(30, monitor.MonitorDevicePath?.Length ?? 0))}...");

            string devicePath = monitor.MonitorDevicePath ?? "";
            var saved = devicePath.Length > 0 ? _settingsManager.GetMonitorProfile(devicePath) : null;

            // Load saved profile from settings (for compare feature)
            _savedProfile = saved?.Clone() ?? new MonitorProfileData { GammaMode = monitor.CurrentGamma };

            // Check pending changes first, then settings file, then monitor's current state
            if (devicePath.Length > 0 && _pendingChanges.TryGetValue(devicePath, out var pending))
            {
                _profile = pending;
            }
            else
            {
                _profile = saved ?? new MonitorProfileData { GammaMode = monitor.CurrentGamma };
            }

            // Refresh every binding from the new profile without firing previews
            _isLoading = true;
            OnPropertyChanged(string.Empty);
            _isLoading = false;
        }

        private void SaveCurrentToPending()
        {
            if (!string.IsNullOrEmpty(_currentMonitor.MonitorDevicePath))
            {
                // Clone to avoid reference issues
                _pendingChanges[_currentMonitor.MonitorDevicePath] = _profile.Clone();
            }
        }

        private void SchedulePreview()
        {
            if (_isLoading) return;
            if (!IsLivePreview) return;

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
                    ApplyCurrent();
                };
            }

            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void ApplyCurrent()
        {
            _livePreviewApplied = true;
            _applyCallback?.Invoke(_currentMonitor, _profile.GammaMode, _profile.ToCalibrationSettings(), null);
        }

        /// <summary>Hold-to-compare: apply the last saved profile while the button is held.</summary>
        public void CompareStart()
        {
            if (_savedProfile != null)
            {
                _applyCallback?.Invoke(_currentMonitor, _savedProfile.GammaMode, _savedProfile.ToCalibrationSettings(), null);
            }
        }

        /// <summary>Return to the current edits when the compare button is released.</summary>
        public void CompareEnd() => ApplyCurrent();

        private void ResetAll()
        {
            _profile = new MonitorProfileData();
            _isLoading = true;
            OnPropertyChanged(string.Empty);
            _isLoading = false;
            SchedulePreview();
        }

        private void SaveAndClose()
        {
            _savedAndClosed = true; // the live state is about to become the saved state

            // Save current monitor to pending
            SaveCurrentToPending();

            // Save all pending changes to settings file
            foreach (var kvp in _pendingChanges)
            {
                _settingsManager.SetMonitorProfile(kvp.Key, kvp.Value);
            }

            // Apply all monitors that have pending changes
            foreach (var kvp in _pendingChanges)
            {
                var monitor = _monitors.Find(m => m.MonitorDevicePath == kvp.Key);
                if (monitor != null)
                {
                    _applyCallback?.Invoke(monitor, kvp.Value.GammaMode, kvp.Value.ToCalibrationSettings(), null);
                }
            }

            CloseRequested?.Invoke();
        }

        /// <summary>
        /// Window Closed hook. Stops the debounce timer so a pending preview can't fire
        /// after the window closes, and - when the window closed WITHOUT Save &amp; Close
        /// (title-bar X / Alt+F4) - re-applies the persisted profile for every monitor
        /// whose unsaved edits were live-previewed. Without this, the last previewed
        /// state stayed on the GPU ramp until something else happened to refresh it.
        /// Mirrors the CompareStart path, which already re-applies the saved profile.
        /// </summary>
        public void Cleanup()
        {
            _previewTimer?.Stop();
            if (_savedAndClosed || !_livePreviewApplied) return;

            // Include the monitor currently being edited alongside any switched-away-from
            // monitors that accumulated pending (previewed but unsaved) changes.
            SaveCurrentToPending();
            foreach (var kvp in _pendingChanges)
            {
                var monitor = _monitors.Find(m => m.MonitorDevicePath == kvp.Key);
                if (monitor == null) continue;

                var saved = _settingsManager.GetMonitorProfile(kvp.Key)
                    ?? new MonitorProfileData { GammaMode = monitor.CurrentGamma };
                _applyCallback?.Invoke(monitor, saved.GammaMode, saved.ToCalibrationSettings(), null);
            }
        }
    }
}
