using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HDRGammaController.Core;

namespace HDRGammaController.ViewModels
{
    public class MonitorViewModel : ObservableObject
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

        private ActionViewModel? _gamma22Item;
        private ActionViewModel? _gamma24Item;
        private ActionViewModel? _defaultItem;

        public MonitorViewModel(MonitorInfo model, ProfileManager profileManager, DispwinRunner dispwinRunner, int index, SettingsManager? settingsManager = null)
        {
            _model = model;
            _profileManager = profileManager;
            _dispwinRunner = dispwinRunner;
            _index = index;
            _settingsManager = settingsManager;

            RebuildSubItems();
        }
        
        private string GammaLabel(GammaMode mode, string name)
            => (_model.CurrentGamma == mode ? "✓ " : "   ") + name;

        private void RebuildSubItems()
        {
            SubItems.Clear();

            if (_model.IsHdrActive)
            {
                // HDR is active - show gamma options with checkmarks. These stay open
                // on click so modes can be A/B compared without reopening the menu.
                _gamma22Item = new ActionViewModel(GammaLabel(GammaMode.Gamma22, "Gamma 2.2"), new RelayCommand(() => ApplyGamma(GammaMode.Gamma22)), staysOpenOnClick: true);
                _gamma24Item = new ActionViewModel(GammaLabel(GammaMode.Gamma24, "Gamma 2.4"), new RelayCommand(() => ApplyGamma(GammaMode.Gamma24)), staysOpenOnClick: true);
                _defaultItem = new ActionViewModel(GammaLabel(GammaMode.WindowsDefault, "Windows Default"), new RelayCommand(() => ApplyGamma(GammaMode.WindowsDefault)), staysOpenOnClick: true);

                SubItems.Add(_gamma22Item);
                SubItems.Add(_gamma24Item);
                SubItems.Add(_defaultItem);

                // Add settings option
                SubItems.Add(new ActionViewModel("───────────", null));
                SubItems.Add(new ActionViewModel("Settings...", new RelayCommand(OpenSettings)));
            }
            else
            {
                // HDR not active - show SDR info and settings
                _gamma22Item = _gamma24Item = _defaultItem = null;
                SubItems.Add(new ActionViewModel("(SDR Display)", null));

                // Add settings option
                SubItems.Add(new ActionViewModel("───────────", null));
                SubItems.Add(new ActionViewModel("Settings...", new RelayCommand(OpenSettings)));
            }
        }

        /// <summary>
        /// Update the checkmark labels in place. Clearing SubItems would tear the
        /// items out of the still-open menu (gamma items keep it open on click).
        /// </summary>
        private void UpdateCheckmarks()
        {
            if (_gamma22Item == null || _gamma24Item == null || _defaultItem == null) return;
            _gamma22Item.Header = GammaLabel(GammaMode.Gamma22, "Gamma 2.2");
            _gamma24Item.Header = GammaLabel(GammaMode.Gamma24, "Gamma 2.4");
            _defaultItem.Header = GammaLabel(GammaMode.WindowsDefault, "Windows Default");
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

                // Update checkmarks in place; the menu is still open
                UpdateCheckmarks();
             }
             catch (Exception ex)
             {
                 System.Windows.MessageBox.Show(
                     $"Failed to apply gamma:\n\n{ex.Message}",
                     "Gloam - Error",
                     System.Windows.MessageBoxButton.OK,
                     System.Windows.MessageBoxImage.Error);
             }
        }
    }
}
