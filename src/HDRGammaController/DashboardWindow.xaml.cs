using System;
using System.Windows;
using System.Windows.Input;
using HDRGammaController.Core;
using HDRGammaController.ViewModels;

namespace HDRGammaController
{
    /// <summary>
    /// View-only concerns live here: window chrome (drag, close), context menu
    /// popping, navigation to SettingsWindow, and wiring the schedule editor
    /// UserControl (not yet MVVM) to the view model. Everything else is in
    /// DashboardViewModel.
    /// </summary>
    public partial class DashboardWindow : Window
    {
        private readonly DashboardViewModel _viewModel;

        public DashboardWindow(
            MonitorManager monitorManager,
            SettingsManager settingsManager,
            NightModeService nightModeService,
            Action<MonitorInfo, GammaMode, CalibrationSettings?, int?> applyCallback)
        {
            InitializeComponent();

            _viewModel = new DashboardViewModel(monitorManager, settingsManager, nightModeService, applyCallback);
            _viewModel.ConfigureRequested += OnConfigureRequested;
            DataContext = _viewModel;

            // Init Schedule Editor (Global Settings)
            ScheduleEditor.Initialize(_viewModel.EditingNightMode);
            ScheduleEditor.ScheduleChanged += _viewModel.SaveEditedNightMode;
            ScheduleEditor.PreviewTemperatureRequested += async (kelvin) => await _viewModel.PreviewTemperatureAsync(kelvin);

            // The view model subscribes to NightModeService, which outlives this window.
            Closed += (s, e) => _viewModel.Dispose();
        }

        private void OnConfigureRequested(DashboardItem item)
        {
            try
            {
                var settingsWindow = new SettingsWindow(
                    item.Model,
                    _viewModel.GetMonitorSnapshot(),
                    _viewModel.SettingsManager,
                    _viewModel.ApplyFromSettings)
                {
                    Owner = this
                };
                settingsWindow.ShowDialog();
                _viewModel.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening settings: {ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void NightModeToggle_RightClick(object sender, MouseButtonEventArgs e)
        {
            NightModeToggle.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }
}
