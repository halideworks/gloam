using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using HDRGammaController.Core;

namespace HDRGammaController.ViewModels
{
    public class MonitorViewModel
    {
        private readonly MonitorInfo _model;
        public MonitorInfo Model => _model;
        
        private readonly ProfileManager _profileManager;
        private readonly DispwinRunner _dispwinRunner;
        private readonly SettingsManager? _settingsManager;
        private readonly int _index;
        
        /// <summary>
        /// Callback to get all monitors for settings window.
        /// </summary>
        public Func<List<MonitorInfo>>? GetAllMonitors { get; set; }
        
        /// <summary>
        /// Callback to notify parent when profile changes (for persistence).
        /// </summary>
        public Action<MonitorInfo, GammaMode>? OnProfileChanged { get; set; }
        
        /// <summary>
        /// Callback to apply gamma with calibration settings.
        /// </summary>
        public Action<MonitorInfo, GammaMode, CalibrationSettings?, int?>? OnApplyWithCalibration { get; set; }

        public string Header => $"{_index}: {_model.FriendlyName} ({(_model.IsHdrActive ? "HDR" : "SDR")})";
        
        // This view model represents a parent menu item, so it has no command but has children.
        public ICommand? Command => null;
        
        public ObservableCollection<ActionViewModel> SubItems { get; } = new ObservableCollection<ActionViewModel>();

        public MonitorViewModel(MonitorInfo model, ProfileManager profileManager, DispwinRunner dispwinRunner, int index, SettingsManager? settingsManager = null)
        {
            _model = model;
            _profileManager = profileManager;
            _dispwinRunner = dispwinRunner;
            _index = index;
            _settingsManager = settingsManager;

            RebuildSubItems();
        }
        
        private void RebuildSubItems()
        {
            SubItems.Clear();
            
            if (_model.IsHdrActive)
            {
                // HDR is active - show gamma options with checkmarks
                string g22Label = (_model.CurrentGamma == GammaMode.Gamma22 ? "✓ " : "   ") + "Gamma 2.2";
                string g24Label = (_model.CurrentGamma == GammaMode.Gamma24 ? "✓ " : "   ") + "Gamma 2.4";
                string defLabel = (_model.CurrentGamma == GammaMode.WindowsDefault ? "✓ " : "   ") + "Windows Default";
                
                SubItems.Add(new ActionViewModel(g22Label, new RelayCommand(_ => ApplyGamma(GammaMode.Gamma22))));
                SubItems.Add(new ActionViewModel(g24Label, new RelayCommand(_ => ApplyGamma(GammaMode.Gamma24))));
                SubItems.Add(new ActionViewModel(defLabel, new RelayCommand(_ => ApplyGamma(GammaMode.WindowsDefault))));
                
                // Add settings option
                SubItems.Add(new ActionViewModel("───────────", null));
                SubItems.Add(new ActionViewModel("Settings...", new RelayCommand(_ => OpenSettings())));
            }
            else
            {
                // HDR not active - show SDR info and settings
                SubItems.Add(new ActionViewModel("(SDR Display)", null));
                 
                // Add settings option
                SubItems.Add(new ActionViewModel("───────────", null));
                SubItems.Add(new ActionViewModel("Settings...", new RelayCommand(_ => OpenSettings())));
            }
        }
        
        private void OpenSettings()
        {
            if (_settingsManager == null) return;
            
            var allMonitors = GetAllMonitors?.Invoke() ?? new List<MonitorInfo> { _model };
            
            var settingsWindow = new SettingsWindow(_model, allMonitors, _settingsManager, 
                (monitor, mode, calibration, nightOverride) =>
                {
                    // Delegate to parent to ensure Night Mode is respected
                    OnApplyWithCalibration?.Invoke(monitor, mode, calibration, nightOverride);
                    
                    if (monitor.MonitorDevicePath == _model.MonitorDevicePath)
                    {
                        RebuildSubItems();
                    }
                });
            settingsWindow.ShowDialog();
        }

        private void ApplyGamma(GammaMode mode)
        {
             try
             {
                // Delegate to parent to ensure Night Mode is respected
                OnApplyWithCalibration?.Invoke(_model, mode, null, null);
                
                // Rebuild sub-items to update checkmarks
                RebuildSubItems();
             }
             catch (Exception ex)
             {
                 System.Windows.MessageBox.Show(
                     $"Failed to apply gamma:\n\n{ex.Message}",
                     "HDR Gamma Controller - Error",
                     System.Windows.MessageBoxButton.OK,
                     System.Windows.MessageBoxImage.Error);
             }
        }
    }
}
