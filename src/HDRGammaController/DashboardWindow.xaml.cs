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
        private readonly MelanopicMonitorService? _melanopicService;
        private readonly System.Windows.Threading.DispatcherTimer _melanopicThrottle;
        private System.Collections.Generic.Dictionary<string, string> _monitorNamesByPath = new();
        public DashboardWindow(
            MonitorManager monitorManager,
            SettingsManager settingsManager,
            NightModeService nightModeService,
            UpdateService updateService,
            ApplyCalibrationRequest applyCallback,
            MelanopicMonitorService? melanopicService = null,
            GammaApplyService? gamerApplyService = null)
        {
            InitializeComponent();
            WindowBoundsPersistence.Attach(this, settingsManager, "Dashboard");

            _viewModel = new DashboardViewModel(
                monitorManager, settingsManager, nightModeService, updateService, applyCallback, gamerApplyService);
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

        // Melanopic-EDI targets from the Brown et al. 2022 consensus (PLOS Biology) /
        // CIE TN 015:2023: daytime aim for >=250 mel-lx to support alertness; evening (the
        // hours before bed) keep under ~10 mel-lx; during sleep under ~1 mel-lx. These are
        // instantaneous corneal illuminance targets, not a standardized cumulative dose.
        private const double EveningMelEdiCeiling = 10.0;
        private const double DaytimeMelEdiTarget = 250.0;
        private const double NightMelEdiCeiling = 1.0;

        // The chart is redrawn on a SLOW cadence (rebuilding canvases is what flickered); the
        // headline numbers update on the fast throttle from in-memory state (no disk).
        private DateTime _lastDoseRedrawUtc = DateTime.MinValue;
        private double _cachedEveningDose;
        private double _cachedEveningBudget;
        private bool _cachedEveningActive;
        private int _cachedDoseMonitorCount;

        private enum CircadianPhase { Daytime, Evening, Night }

        // Clock-based phase. Simple and location-independent: daytime 06:00-18:00, evening
        // 18:00-23:00, night 23:00-06:00. The evening window is the "hours before bed" the
        // <=10 mel-lx target applies to.
        private static CircadianPhase CurrentPhase()
        {
            int h = DateTime.Now.Hour;
            if (h >= 23 || h < 6) return CircadianPhase.Night;
            if (h >= 18) return CircadianPhase.Evening;
            return CircadianPhase.Daytime;
        }

        // The evening dose accumulates from 18:00 local through the night. Returns null during
        // the daytime, when the low-light target doesn't apply.
        private static DateTime? EveningWindowStart()
        {
            var now = DateTime.Now;
            if (now.Hour >= 18) return now.Date.AddHours(18);
            if (now.Hour < 6) return now.Date.AddDays(-1).AddHours(18);
            return null;
        }

        private void OnMelanopicUpdated(MelanopicMonitorState state)
        {
            // Raised on the service's worker thread; coalesce onto the UI thread.
            Dispatcher.BeginInvoke(() =>
            {
                if (!_melanopicThrottle.IsEnabled) _melanopicThrottle.Start();
            });
        }

        private void MelanopicAdvancedExpander_Expanded(object sender, RoutedEventArgs e) => RefreshMelanopic(forceChart: true);

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
                    MelanopicSummary.Text = "Waiting for the first applied state.";
                    return;
                }

                // Friendly names for device paths (refreshed from the VM's monitor snapshot).
                _monitorNamesByPath = _viewModel.GetMonitorSnapshot()
                    .Where(m => !string.IsNullOrEmpty(m.MonitorDevicePath))
                    .GroupBy(m => m.MonitorDevicePath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().FriendlyName, StringComparer.OrdinalIgnoreCase);

                // Slow path (disk read + dose math): recompute the evening dose on a throttle,
                // and only rebuild the charts when the advanced section is actually open.
                if (forceChart || (DateTime.UtcNow - _lastDoseRedrawUtc).TotalSeconds >= 15)
                {
                    _lastDoseRedrawUtc = DateTime.UtcNow;
                    RefreshDoseData(drawCharts: MelanopicAdvancedExpander.IsExpanded);
                }

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
            // "Melanopic light now" is the combined melanopic EDI across displays (the eye sees
            // the sum). Cheap: straight from in-memory state.
            double combinedEdi = states.Sum(s => Math.Max(0, s.Reading.MelanopicEdiLux));
            ExposureBig.Text = combinedEdi >= 100 ? combinedEdi.ToString("F0") : combinedEdi.ToString("F1");

            string screenPhrase = states.Count > 1 ? "your screens add" : "your screen adds";
            string ediText = combinedEdi >= 10 ? combinedEdi.ToString("F0") : combinedEdi.ToString("F1");

            bool over;
            string message;
            switch (CurrentPhase())
            {
                case CircadianPhase.Daytime:
                    over = false;
                    message = $"Daytime: bright light is good for alertness (aim for {DaytimeMelEdiTarget:F0}+ mel-lx total at your eyes). Right now {screenPhrase} about {ediText}.";
                    break;
                case CircadianPhase.Evening:
                    over = combinedEdi > EveningMelEdiCeiling;
                    message = over
                        ? $"Evening: keep total light under {EveningMelEdiCeiling:F0} mel-lx in the hours before bed. Right now {screenPhrase} about {ediText}, so warm or dim the screen."
                        : $"Evening: on track for the hours before bed. Right now {screenPhrase} about {ediText}, within the under-{EveningMelEdiCeiling:F0} mel-lx target.";
                    break;
                default: // Night
                    over = combinedEdi > NightMelEdiCeiling;
                    message = over
                        ? $"Night: aim for under {NightMelEdiCeiling:F0} mel-lx near sleep. Right now {screenPhrase} about {ediText}, so keep the screen warm and dim."
                        : $"Night: low light, good for sleep. Right now {screenPhrase} about {ediText}.";
                    break;
            }

            ExposureBig.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                over ? "ThemeAmber" : "ThemeText");
            ExposureRecommendation.Text = message;

            // Evening dose vs a budget derived from the <=10 mel-lx evening target (there is no
            // standardized cumulative-dose threshold, so this is a Gloam-derived reference).
            if (_cachedEveningActive)
            {
                DoseBig.Text = _cachedEveningDose >= 100 ? _cachedEveningDose.ToString("F0") : _cachedEveningDose.ToString("F1");
                DoseBig.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                    _cachedEveningDose > _cachedEveningBudget ? "ThemeAmber" : "ThemeText");
                DoseGoal.Text = $"of ~{_cachedEveningBudget:F0} at the 10 mel-lx evening target";
            }
            else
            {
                DoseBig.Text = "0";
                DoseBig.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeText");
                DoseGoal.Text = "accrues from 6pm vs the 10 mel-lx target";
            }
        }

        private static string FormatReduction(double fraction) =>
            double.IsFinite(fraction) ? $"{-fraction:P0}" : "n/a";

        private void RefreshDoseData(bool drawCharts)
        {
            if (_melanopicService == null) return;

            // 12-hour window holds any evening without needing the sun schedule configured.
            DateTime windowStart = DateTime.Now.AddHours(-12);
            var samples = MelanopicDoseStore.LoadSince(windowStart);
            _cachedDoseMonitorCount = samples.Select(s => s.MonitorDevicePath).Distinct().Count();

            // Evening dose = integral of mel-EDI since 18:00 local; budget = the target ceiling
            // held over the same span (10 mel-lx x hours elapsed).
            var eveningStart = EveningWindowStart();
            if (eveningStart is DateTime es)
            {
                DateTime esUtc = es.ToUniversalTime();
                var evening = samples.Where(s => s.TimestampUtc >= esUtc).ToList();
                _cachedEveningDose = MelanopicDoseStore.IntegrateDose(evening);
                double hours = Math.Max(0, (DateTime.Now - es).TotalHours);
                _cachedEveningBudget = EveningMelEdiCeiling * hours;
                _cachedEveningActive = true;
            }
            else
            {
                _cachedEveningActive = false;
                _cachedEveningDose = 0;
                _cachedEveningBudget = 0;
            }

            if (!drawCharts) return;

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
                    Text = $"{name}: now {ordered[^1].MelanopicEdiLux:F1} mel-lx · {monitorDose:F0} mel-lx·h over 12 h",
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

            string doseLine = _cachedEveningActive
                ? $"This evening's screen dose so far: {_cachedEveningDose:F1} mel-lx·h, against a {_cachedEveningBudget:F0} budget if the screen sat right at the 10 mel-lx evening target since 6pm."
                : "The evening dose begins accruing at 6pm, measured against the 10 mel-lx evening target.";
            DoseSummary.Text = monitorCount > 1
                ? doseLine + " Charts below cover the last 12 hours, one per display (summed dose is an upper bound)."
                : doseLine + " The chart below covers the last 12 hours.";

            string MonitorName(string devicePath) =>
                _monitorNamesByPath.TryGetValue(devicePath, out var n)
                    ? n
                    : devicePath.Length > 24 ? "…" + devicePath[^24..] : devicePath;
        }
    }
}
