using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;
using HDRGammaController.ViewModels;
using static HDRGammaController.Core.Calibration.PatchSetGenerator;

namespace HDRGammaController
{
    /// <summary>
    /// Setup window for configuring display calibration settings before starting.
    /// View-only concerns live here: window chrome, child dialogs (CCSS browser,
    /// file picker, profile manager, Argyll download), and DialogResult. The setup
    /// logic lives in CalibrationSetupViewModel.
    /// </summary>
    public partial class CalibrationSetupWindow : Window
    {
        private readonly CalibrationSetupViewModel _viewModel;
        private readonly SettingsManager? _settingsManager;

        /// <summary>Gets the selected calibration target after the dialog completes.</summary>
        public CalibrationTarget? SelectedTarget => _viewModel.ResultTarget;

        /// <summary>Gets the selected calibration preset after the dialog completes.</summary>
        public CalibrationPreset SelectedPreset => _viewModel.ResultPreset;

        /// <summary>Gets the selected monitor after the dialog completes.</summary>
        public MonitorInfo? SelectedMonitor => _viewModel.ResultMonitor;

        /// <summary>Gets the selected display type after the dialog completes.</summary>
        public DisplayType SelectedDisplayType => _viewModel.ResultDisplayType;

        /// <summary>Gets the initialized colorimeter service.</summary>
        public ColorimeterService? ColorimeterService => _viewModel.ColorimeterService;

        public CalibrationSetupWindow(
            List<MonitorInfo> monitors,
            SettingsManager? settingsManager = null,
            string? preferredMonitorDevicePath = null,
            ColorimeterService? reusableColorimeterService = null)
        {
            InitializeComponent();

            _settingsManager = settingsManager;
            WindowBoundsPersistence.Attach(this, settingsManager, "CalibrationSetup");
            _viewModel = new CalibrationSetupViewModel(monitors, settingsManager, preferredMonitorDevicePath, reusableColorimeterService);
            _viewModel.CloseRequested += result =>
            {
                DialogResult = result;
                Close();
            };
            _viewModel.OfferArgyllDownload = reason =>
            {
                var dialog = new ArgyllDownloadDialog(reason) { Owner = this };
                dialog.ShowDialog();
                return dialog.DownloadSucceeded;
            };
            DataContext = _viewModel;
            ThemeToggleButton.Content = BrutalistTheme.IsDark ? "◐" : "◑";

            Loaded += async (s, e) => await _viewModel.OnLoadedAsync();
        }

        private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            BrutalistTheme.Toggle();
            ThemeToggleButton.Content = BrutalistTheme.IsDark ? "◐" : "◑";
        }

        private void FindCorrection_Click(object sender, RoutedEventArgs e)
        {
            var browser = new CcssBrowserWindow(
                _viewModel.SelectedMonitor?.Model.FriendlyName ?? "",
                CalibrationSetupViewModel.CorrectionsFolder,
                title: "Find or Create Meter Correction",
                introText: "Search the DisplayCAL community database for a correction matched to your panel, or " +
                           "generate your own: “From spectrometer…” measures this panel’s spectra into a " +
                           ".ccss, and “Correction matrix…” builds a .ccmx from a spectrometer + colorimeter " +
                           "pair. Saved files can be deleted or de-duplicated below.")
            {
                Owner = this,
            };
            if (browser.ShowDialog() == true && browser.SavedPath != null)
                _viewModel.SelectCorrectionPath(browser.SavedPath, addIfMissing: true);
        }

        private void BrowseCorrection_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select meter correction file",
                Filter = "Colorimeter corrections (*.ccss;*.ccmx)|*.ccss;*.ccmx|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true) return;
            _viewModel.SelectCorrectionPath(dialog.FileName, addIfMissing: true);
        }

        private void ManageProfiles_Click(object sender, RoutedEventArgs e)
        {
            var monitor = _viewModel.SelectedMonitor?.Model;
            if (monitor == null) return;
            new ProfileManagerWindow(monitor, _settingsManager) { Owner = this }.ShowDialog();
        }

        private void PastReports_Click(object sender, RoutedEventArgs e)
        {
            new ReportHistoryWindow { Owner = this }.ShowDialog();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
    }
}
