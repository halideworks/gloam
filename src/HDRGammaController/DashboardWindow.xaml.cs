using System;
using System.Windows;
using System.Windows.Input;
using HDRGammaController.Core;
using HDRGammaController.Services;
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
            UpdateService updateService,
            Action<MonitorInfo, GammaMode, CalibrationSettings?, int?> applyCallback)
        {
            InitializeComponent();
            WindowBoundsPersistence.Attach(this, settingsManager, "Dashboard");

            _viewModel = new DashboardViewModel(monitorManager, settingsManager, nightModeService, updateService, applyCallback);
            _viewModel.ConfigureRequested += OnConfigureRequested;
            DataContext = _viewModel;

            // Init Schedule Editor (Global Settings)
            ScheduleEditor.Initialize(_viewModel.EditingNightMode);
            ScheduleEditor.ScheduleChanged += _viewModel.SaveEditedNightMode;
            ScheduleEditor.PreviewTemperatureRequested += async (kelvin) => await _viewModel.PreviewTemperatureAsync(kelvin);

            // The view model subscribes to NightModeService, which outlives this window.
            Closed += (s, e) => _viewModel.Dispose();

            // Reflect the current app-wide brutalist theme on the toggle glyph.
            ThemeToggleButton.Content = BrutalistTheme.IsDark ? "◐" : "◑";
        }

        // The brutalist light/dark palette is app-wide (App.BrutalistTheme): the toggle
        // swaps the Theme* brushes in Application.Resources, which every {DynamicResource}
        // consumer across the app re-resolves. The half-moon glyph mirrors the site button.
        private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            BrutalistTheme.Toggle();
            ThemeToggleButton.Content = BrutalistTheme.IsDark ? "◐" : "◑";
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
                ConfirmDialog.Info(this, "Error", $"Error opening settings: {ex.Message}\n\n{ex.StackTrace}");
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

    }
}
