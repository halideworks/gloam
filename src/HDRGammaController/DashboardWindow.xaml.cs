using System;
using System.Linq;
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
        private readonly MelanopicMonitorService? _melanopicService;
        private readonly System.Windows.Threading.DispatcherTimer _melanopicThrottle;
        private System.Collections.Generic.Dictionary<string, string> _monitorNamesByPath = new();

        public DashboardWindow(
            MonitorManager monitorManager,
            SettingsManager settingsManager,
            NightModeService nightModeService,
            UpdateService updateService,
            ApplyCalibrationRequest applyCallback,
            MelanopicMonitorService? melanopicService = null)
        {
            InitializeComponent();
            WindowBoundsPersistence.Attach(this, settingsManager, "Dashboard");

            _viewModel = new DashboardViewModel(monitorManager, settingsManager, nightModeService, updateService, applyCallback);
            _viewModel.ConfigureRequested += OnConfigureRequested;
            DataContext = _viewModel;

            // Melanopic card: state-change driven with a 500 ms coalescing throttle so a
            // night-mode fade doesn't redraw the dose chart per kelvin step.
            _melanopicService = melanopicService;
            _melanopicThrottle = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _melanopicThrottle.Tick += (_, _) =>
            {
                _melanopicThrottle.Stop();
                RefreshMelanopic();
            };
            if (_melanopicService != null)
            {
                _melanopicService.MelanopicUpdated += OnMelanopicUpdated;
                Loaded += (_, _) => RefreshMelanopic();
            }
            else
            {
                MelanopicSummary.Text = "Melanopic monitoring is not running.";
            }

            // Init Schedule Editor (Global Settings)
            ScheduleEditor.Initialize(_viewModel.EditingNightMode);
            ScheduleEditor.ScheduleChanged += () =>
            {
                _viewModel.SaveEditedNightMode();
                _viewModel.RefreshNightRenderingBindings();
            };
            ScheduleEditor.PreviewTemperatureRequested += async (kelvin) => await _viewModel.PreviewTemperatureAsync(kelvin);
            _viewModel.NightRenderingEdited += ScheduleEditor.SyncRenderingSettings;

            // Refresh() may swap EditingNightMode for a freshly read snapshot (header
            // Off/Auto/Manual toggle, tray edits). Re-Initialize the editor with the new
            // instance; otherwise it keeps mutating the orphaned old clone and the next
            // save silently discards those graph edits.
            Action rebindScheduleEditor = () => ScheduleEditor.Initialize(_viewModel.EditingNightMode);
            _viewModel.EditingNightModeReplaced += rebindScheduleEditor;

            // The view model subscribes to NightModeService, which outlives this window.
            Closed += (s, e) =>
            {
                _viewModel.EditingNightModeReplaced -= rebindScheduleEditor;
                _viewModel.NightRenderingEdited -= ScheduleEditor.SyncRenderingSettings;
                if (_melanopicService != null)
                    _melanopicService.MelanopicUpdated -= OnMelanopicUpdated;
                _melanopicThrottle.Stop();
                _viewModel.Dispose();
            };

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

        // ---- Melanopic (circadian) card ----------------------------------------------------

        // Evening melanopic-EDI recommendation (Brown et al. 2022 consensus): keep melanopic
        // exposure under ~10 mel-lx in the hours before bed, under ~1 during sleep.
        private const double EveningMelEdiCeiling = 10.0;

        // The chart is redrawn on a SLOW cadence (rebuilding canvases is what flickered); the
        // headline numbers update on the fast throttle from in-memory state (no disk).
        private DateTime _lastDoseRedrawUtc = DateTime.MinValue;
        private double _cachedTonightDose;
        private int _cachedDoseMonitorCount;

        private void OnMelanopicUpdated(MelanopicMonitorState state)
        {
            // Raised on the service's worker thread; coalesce onto the UI thread.
            Dispatcher.BeginInvoke(() =>
            {
                if (!_melanopicThrottle.IsEnabled) _melanopicThrottle.Start();
            });
        }

        private void MelanopicExpander_Expanded(object sender, RoutedEventArgs e) => RefreshMelanopic(forceChart: true);

        private void DoseCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshMelanopic(forceChart: true);

        private void RefreshMelanopic() => RefreshMelanopic(forceChart: false);

        private void RefreshMelanopic(bool forceChart)
        {
            if (_melanopicService == null || !IsLoaded) return;
            try
            {
                var states = _melanopicService.CurrentStates;
                if (states.Count == 0)
                {
                    MelanopicSummary.Text = "Waiting for the first applied state…";
                    return;
                }

                // Friendly names for device paths (refreshed from the VM's monitor snapshot).
                _monitorNamesByPath = _viewModel.GetMonitorSnapshot()
                    .Where(m => !string.IsNullOrEmpty(m.MonitorDevicePath))
                    .GroupBy(m => m.MonitorDevicePath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().FriendlyName, StringComparer.OrdinalIgnoreCase);

                RefreshHeadline(states);

                var lines = new System.Text.StringBuilder();
                foreach (var state in states.OrderBy(s => Name(s.MonitorDevicePath)))
                {
                    var u = state.Uncertainty;
                    // Headline is the geometry-free % reduction; absolute EDI is secondary
                    // and carries its wide, honest band.
                    lines.AppendLine(
                        $"{Name(state.MonitorDevicePath)}:  {FormatReduction(u.ReductionValue)} ± {u.ReductionExpandedU:P0} melanopic vs 6500K   " +
                        $"·   {u.EdiValue:F1} ± {u.EdiExpandedU:F1} mel-lx (est.)" +
                        (state.IsHdrActive ? "   · HDR (SDR-white anchored)" : string.Empty));
                }
                MelanopicSummary.Text = lines.ToString().TrimEnd();

                bool anyGeneric = states.Any(s => !s.Reading.HasSpectra);
                string sources = string.Join("; ", states.Select(s => s.SpectraSourceName).Distinct());
                MelanopicProvenance.Text =
                    $"Spectra: {sources}. Absolute mel-lx assumes the screen fills " +
                    $"{_melanopicService.ViewingSolidAngleSr:F2} sr at the eye (≈ 27″ at 60 cm); " +
                    "the % reduction is independent of that assumption." +
                    (anyGeneric
                        ? " Load a CCSS for your panel (Settings → Ultra Night spectrum) to tighten these numbers."
                        : string.Empty);

                // Only rebuild the chart (disk read + canvas rebuild) occasionally — doing it
                // per update is what flickered. The dose curve changes slowly.
                if (forceChart || (DateTime.UtcNow - _lastDoseRedrawUtc).TotalSeconds >= 15)
                {
                    _lastDoseRedrawUtc = DateTime.UtcNow;
                    DrawDoseCurve();
                }
            }
            catch (Exception ex)
            {
                Log.Info($"DashboardWindow: melanopic refresh failed: {ex.Message}");
            }

            string Name(string devicePath) =>
                _monitorNamesByPath.TryGetValue(devicePath, out var n)
                    ? n
                    : devicePath.Length > 24 ? "…" + devicePath[^24..] : devicePath;
        }

        private void RefreshHeadline(IReadOnlyList<MelanopicMonitorState> states)
        {
            // "Exposure now" is the combined melanopic EDI across displays (the eye sees the
            // sum; labeled an upper bound when >1). Cheap — straight from in-memory state.
            double combinedEdi = states.Sum(s => Math.Max(0, s.Reading.MelanopicEdiLux));
            ExposureBig.Text = combinedEdi >= 100 ? combinedEdi.ToString("F0") : combinedEdi.ToString("F1");

            bool overCeiling = combinedEdi > EveningMelEdiCeiling;
            ExposureBig.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                overCeiling ? "ThemeAmber" : "ThemeText");
            string suffix = states.Count > 1 ? " (combined, upper bound)" : string.Empty;
            ExposureRecommendation.Text = overCeiling
                ? $"Above the ≈{EveningMelEdiCeiling:F0} mel-lx evening target{suffix} — warm or dim the screen before bed."
                : $"At or below the ≈{EveningMelEdiCeiling:F0} mel-lx evening target{suffix} — good for pre-sleep.";

            DoseBig.Text = _cachedTonightDose >= 100
                ? _cachedTonightDose.ToString("F0")
                : _cachedTonightDose.ToString("F1");
        }

        private static string FormatReduction(double fraction) =>
            double.IsFinite(fraction) ? $"{-fraction:P0}" : "n/a";

        private void DrawDoseCurve()
        {
            if (_melanopicService == null) return;

            // "Tonight" window: the last 12 hours — long enough to hold any evening, without
            // needing the sun schedule to be configured.
            DateTime windowStart = DateTime.Now.AddHours(-12);
            var samples = MelanopicDoseStore.LoadSince(windowStart);
            _cachedTonightDose = MelanopicDoseStore.IntegrateDose(samples);
            _cachedDoseMonitorCount = samples.Select(s => s.MonitorDevicePath).Distinct().Count();
            DoseBig.Text = _cachedTonightDose >= 100
                ? _cachedTonightDose.ToString("F0")
                : _cachedTonightDose.ToString("F1");

            DoseChartsPanel.Children.Clear();

            if (samples.Count < 2)
            {
                DoseSummary.Text = "Dose curve appears once melanopic samples accumulate (a sample a minute while the screen is on).";
                return;
            }

            var palette = BrutalistTheme.IsDark
                ? CalibrationCharts.ChartPalette.Dark
                : CalibrationCharts.ChartPalette.Light;
            var seriesColors = new[] { palette.Cyan, palette.Orange, palette.Green, palette.Amber };

            // One chart per display, each with its own y-scale: overlaying differently-bright
            // monitors on a shared axis flattened the dimmer one into an unreadable line.
            var pending = new System.Collections.Generic.List<(System.Windows.Controls.Canvas Canvas,
                System.Collections.Generic.List<(double X, double Y)> Points, string Name, System.Windows.Media.Color Color)>();
            int colorIndex = 0;
            int monitorCount = 0;
            foreach (var group in samples.GroupBy(s => s.MonitorDevicePath).OrderBy(g => MonitorName(g.Key)))
            {
                monitorCount++;
                var ordered = group.OrderBy(s => s.TimestampUtc).ToList();
                var points = ordered
                    .Select(s => ((s.TimestampUtc.ToLocalTime() - windowStart).TotalHours, Math.Max(0, s.MelanopicEdiLux)))
                    .ToList();
                double monitorDose = MelanopicDoseStore.IntegrateDose(ordered);
                string name = MonitorName(group.Key);

                var header = new System.Windows.Controls.TextBlock
                {
                    Text = $"{name} — now {ordered[^1].MelanopicEdiLux:F1} mel-lx · {monitorDose:F0} mel-lx·h over 12 h",
                    FontSize = 11,
                    Margin = new Thickness(0, monitorCount == 1 ? 0 : 10, 0, 4),
                };
                header.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeTextDim");
                DoseChartsPanel.Children.Add(header);

                var canvas = new System.Windows.Controls.Canvas { Height = 120, ClipToBounds = true };
                canvas.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "ThemeSurface");
                DoseChartsPanel.Children.Add(canvas);

                pending.Add((canvas, points, name, seriesColors[(colorIndex++) % seriesColors.Length]));
            }

            // The canvases were just created — their ActualWidth is 0 until a layout pass.
            // Draw at Loaded priority, after layout has run.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                foreach (var (canvas, points, name, color) in pending)
                {
                    if (canvas.ActualWidth < 60 || points.Count < 2) continue;
                    double yMax = Math.Max(points.Max(p => p.Y) * 1.15, 1.0);
                    CalibrationCharts.DrawLineChart(canvas,
                        new[] { new CalibrationCharts.Series(name, color, points) },
                        0, 12, 0, yMax, "Hours (last 12 h)", "mel-lx", palette: palette);
                }
            });

            DoseSummary.Text = monitorCount > 1
                ? $"Combined eye dose over the last 12 h: {_cachedTonightDose:F0} mel-lx·h (all displays summed — an upper-bound estimate)."
                : $"Cumulative melanopic dose over the last 12 h: {_cachedTonightDose:F0} mel-lx·h.";

            string MonitorName(string devicePath) =>
                _monitorNamesByPath.TryGetValue(devicePath, out var n)
                    ? n
                    : devicePath.Length > 24 ? "…" + devicePath[^24..] : devicePath;
        }
    }
}
