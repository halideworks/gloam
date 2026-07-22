using System;
using System.Windows;
using System.Windows.Input;
using HDRGammaController.Core;
using HDRGammaController.Services;
using HDRGammaController.ViewModels;

namespace HDRGammaController
{
    /// <summary>
    /// Compact, keyboard-first Game Lab. The window intentionally has no outer
    /// ScrollViewer: large libraries and scan results virtualize inside their own fixed
    /// workspaces, while only the selected profile owns a live editor tree.
    /// </summary>
    public partial class GameLabWindow : Window
    {
        private readonly DashboardViewModel _viewModel;

        public GameLabWindow(
            MonitorManager monitorManager,
            SettingsManager settingsManager,
            NightModeService nightModeService,
            UpdateService updateService,
            ApplyCalibrationRequest applyCallback,
            GammaApplyService? gamerApplyService = null,
            string? suggestedGameApp = null,
            GamerModeCoordinator? gamerModeCoordinator = null)
        {
            InitializeComponent();
            WindowBoundsPersistence.Attach(this, settingsManager, "GameLabCompact");

            _viewModel = new DashboardViewModel(
                monitorManager, settingsManager, nightModeService, updateService,
                applyCallback, gamerApplyService, gamerModeCoordinator);
            _viewModel.SuggestGamerApp(suggestedGameApp);
            DataContext = _viewModel;

            ThemeToggleButton.Content = BrutalistTheme.IsDark ? "◐" : "◑";
            Activated += (_, _) => _viewModel.RefreshGamerChoices();
            Loaded += (_, _) => ShowGamerControls();
            Closed += (_, _) => _viewModel.Dispose();
            PreviewKeyDown += OnPreviewKeyDown;
        }

        internal void ShowGamerControls()
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    if (!string.IsNullOrWhiteSpace(_viewModel.GamerMode.NewAppText))
                        GamerAppPicker.Focus();
                    else
                        GameProfileSearchBox.Focus();
                }));
        }

        internal void SuggestGamerApp(string? appName) => _viewModel.SuggestGamerApp(appName);

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            BrutalistTheme.Toggle();
            ThemeToggleButton.Content = BrutalistTheme.IsDark ? "◐" : "◑";
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
