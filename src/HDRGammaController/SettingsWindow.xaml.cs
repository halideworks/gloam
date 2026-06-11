using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using HDRGammaController.Core;
using HDRGammaController.ViewModels;

namespace HDRGammaController
{
    /// <summary>
    /// View-only concerns: window chrome (drag, close) and the hold-to-compare
    /// mouse gesture. Everything else lives in SettingsViewModel.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly SettingsViewModel _viewModel;

        public SettingsWindow(
            MonitorInfo initialMonitor,
            List<MonitorInfo> allMonitors,
            SettingsManager settingsManager,
            Action<MonitorInfo, GammaMode, CalibrationSettings?, int?>? applyCallback = null)
        {
            InitializeComponent();

            _viewModel = new SettingsViewModel(initialMonitor, allMonitors, settingsManager, applyCallback);
            _viewModel.CloseRequested += Close;
            DataContext = _viewModel;

            // A debounced preview must not fire into a dead window.
            Closed += (s, e) => _viewModel.Cleanup();
        }

        // Simplified constructor for single monitor (backwards compatibility)
        public SettingsWindow(
            MonitorInfo monitor,
            SettingsManager settingsManager,
            Action<MonitorInfo, GammaMode, CalibrationSettings?, int?>? applyCallback = null)
            : this(monitor, new List<MonitorInfo> { monitor }, settingsManager, applyCallback)
        {
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

        private void Compare_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _viewModel.CompareStart();
            e.Handled = true;
        }

        private void Compare_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _viewModel.CompareEnd();
            e.Handled = true;
        }
    }
}
