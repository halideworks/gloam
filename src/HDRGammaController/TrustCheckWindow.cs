using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;
using HDRGammaController.ViewModels;

namespace HDRGammaController
{
    /// <summary>
    /// The 6-patch ~20-second "trust check" (roadmap 4.3): measures white / mid-gray /
    /// black / R / G / B through the installed calibration, grades against the recorded
    /// target, appends to the per-monitor trend history, and charts the trend over months —
    /// so recalibration becomes data-driven instead of superstition. Drift alerts honor the
    /// combined measurement uncertainty of the runs being compared.
    /// </summary>
    public sealed class TrustCheckWindow : Window
    {
        private readonly MonitorManager _monitorManager;
        private readonly SettingsManager _settingsManager;
        private readonly DispwinRunner _dispwinRunner;
        private readonly NightModeService _nightModeService;
        private readonly IToastService? _toastService;

        private readonly ComboBox _monitorPicker;
        private readonly TextBlock _status;
        private readonly TextBlock _latest;
        private readonly Canvas _deltaECanvas;
        private readonly Canvas _duvCanvas;
        private readonly Button _runButton;
        private readonly CheckBox _reminderToggle;

        private CancellationTokenSource? _runCts;
        private bool _running;

        private sealed record MonitorRow(string Label, string DevicePath)
        {
            public override string ToString() => Label;
        }

        public TrustCheckWindow(
            MonitorManager monitorManager,
            SettingsManager settingsManager,
            DispwinRunner dispwinRunner,
            NightModeService nightModeService,
            IToastService? toastService)
        {
            _monitorManager = monitorManager;
            _settingsManager = settingsManager;
            _dispwinRunner = dispwinRunner;
            _nightModeService = nightModeService;
            _toastService = toastService;

            Title = "Trust Check";
            Width = 860;
            Height = 620;
            MinWidth = 720;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.SetResourceReference(BackgroundProperty, "ThemeBg");
            this.SetResourceReference(ForegroundProperty, "ThemeText");
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Gloam;component/Themes/DarkControls.xaml", UriKind.Absolute),
            });

            _monitorPicker = new ComboBox { MinWidth = 320, VerticalAlignment = VerticalAlignment.Center };
            _monitorPicker.SelectionChanged += (_, _) => RefreshTrend();

            _status = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            _status.SetResourceReference(ForegroundProperty, "ThemeTextDim");

            _latest = new TextBlock
            {
                FontSize = 13,
                Margin = new Thickness(0, 8, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            };
            _latest.SetResourceReference(ForegroundProperty, "ThemeText");

            _deltaECanvas = MakeCanvas();
            _duvCanvas = MakeCanvas();

            var charts = new Grid { Margin = new Thickness(0, 4, 0, 8) };
            charts.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            charts.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_deltaECanvas, 0);
            Grid.SetRow(_duvCanvas, 1);
            charts.Children.Add(_deltaECanvas);
            charts.Children.Add(_duvCanvas);

            _reminderToggle = new CheckBox
            {
                Content = "Remind me monthly to run a trust check",
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = _settingsManager.TrustCheckReminderDays > 0,
            };
            _reminderToggle.SetResourceReference(ForegroundProperty, "ThemeTextDim");
            _reminderToggle.Checked += (_, _) => _settingsManager.SetTrustCheckReminderDays(30);
            _reminderToggle.Unchecked += (_, _) => _settingsManager.SetTrustCheckReminderDays(0);

            Button Make(string label, RoutedEventHandler onClick, bool accent = false)
            {
                var b = new Button
                {
                    Content = label,
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(8, 0, 0, 0),
                    BorderThickness = new Thickness(1),
                };
                b.SetResourceReference(BackgroundProperty, accent ? "ThemeAccent" : "ThemeSurface");
                b.SetResourceReference(ForegroundProperty, accent ? "ThemeOnAccent" : "ThemeText");
                b.SetResourceReference(Control.BorderBrushProperty, accent ? "ThemeAccent" : "ThemeBorder");
                b.Click += onClick;
                return b;
            }

            _runButton = Make("Run Check", async (_, _) => await RunAsync(), accent: true);
            var export = Make("Export CSV…", (_, _) => ExportCsv());
            var close = Make("Close", (_, _) => Close());

            var buttons = new Grid();
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 3; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_runButton, 1);
            Grid.SetColumn(export, 2);
            Grid.SetColumn(close, 3);
            buttons.Children.Add(_reminderToggle);
            buttons.Children.Add(_runButton);
            buttons.Children.Add(export);
            buttons.Children.Add(close);

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
            var pickerLabel = new TextBlock
            {
                Text = "Display",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            pickerLabel.SetResourceReference(ForegroundProperty, "ThemeTextDim");
            headerRow.Children.Add(pickerLabel);
            headerRow.Children.Add(_monitorPicker);

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(headerRow, 0);
            Grid.SetRow(_latest, 1);
            Grid.SetRow(_status, 2);
            Grid.SetRow(charts, 3);
            Grid.SetRow(buttons, 4);
            root.Children.Add(headerRow);
            root.Children.Add(_latest);
            root.Children.Add(_status);
            root.Children.Add(charts);
            root.Children.Add(buttons);
            BrutalistChrome.Apply(this, "Trust Check", root);

            SizeChanged += (_, _) => RefreshTrend();
            BrutalistTheme.Changed += OnThemeChanged;
            Closed += (_, _) =>
            {
                BrutalistTheme.Changed -= OnThemeChanged;
                _runCts?.Cancel();
            };

            PopulateMonitors();
        }

        private void OnThemeChanged() => RefreshTrend();

        private static Canvas MakeCanvas()
        {
            var canvas = new Canvas { Margin = new Thickness(0, 4, 0, 4), MinHeight = 140, ClipToBounds = true };
            canvas.SetResourceReference(Panel.BackgroundProperty, "ThemeSurface");
            return canvas;
        }

        private void PopulateMonitors()
        {
            var rows = new List<MonitorRow>();
            foreach (var monitor in _monitorManager.EnumerateMonitors())
            {
                if (string.IsNullOrEmpty(monitor.MonitorDevicePath)) continue;
                var profile = _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath);
                bool calibrated = !string.IsNullOrEmpty(profile?.Mhc2ProfileName);
                rows.Add(new MonitorRow(
                    calibrated ? monitor.FriendlyName : $"{monitor.FriendlyName} (no calibration)",
                    monitor.MonitorDevicePath));
            }

            _monitorPicker.ItemsSource = rows;
            if (rows.Count > 0) _monitorPicker.SelectedIndex = 0;
            if (rows.Count == 0)
                _status.Text = "No displays found.";
        }

        private string? SelectedDevicePath => (_monitorPicker.SelectedItem as MonitorRow)?.DevicePath;

        // ---- The measurement run -------------------------------------------------------------

        private async Task RunAsync()
        {
            if (_running) return;
            string? devicePath = SelectedDevicePath;
            if (devicePath == null) return;

            var monitor = _monitorManager.EnumerateMonitors()
                .FirstOrDefault(m => string.Equals(m.MonitorDevicePath, devicePath, StringComparison.OrdinalIgnoreCase));
            if (monitor == null)
            {
                _status.Text = "The selected display is no longer connected.";
                return;
            }

            var profile = _settingsManager.GetMonitorProfile(devicePath);
            if (string.IsNullOrEmpty(profile?.Mhc2ProfileName))
            {
                // A trust check without a reference is noise — refuse honestly.
                _status.Text = "This display has no active Gloam calibration to check against. Calibrate it first.";
                return;
            }

            var target = StandardTargets.GetByName(profile.CalibTargetName ?? string.Empty)
                ?? StandardTargets.SrgbGamma22;
            bool hdrMode = profile.PreviousColorProfileHdrMode ?? monitor.IsHdrActive;

            string? argyllBin = ArgyllDownloader.IsInstalled()
                ? ArgyllDownloader.LocalArgyllBinDir
                : ArgyllPathFinder.FindArgyllBinPath();
            if (argyllBin == null)
            {
                _status.Text = "ArgyllCMS is not available yet — run a calibration once so Gloam can set it up.";
                return;
            }

            _running = true;
            _runButton.IsEnabled = false;
            using var cts = new CancellationTokenSource();
            _runCts = cts;

            ColorimeterService? colorimeter = null;
            PatchDisplayWindow? patchWindow = null;
            CalibrationStateManager? stateManager = null;
            bool bypassed = false;
            try
            {
                _status.Text = "Detecting instrument…";
                colorimeter = new ColorimeterService(argyllBin);
                if (!await colorimeter.InitializeAsync(cts.Token) || !colorimeter.IsReady)
                {
                    _status.Text = "No colorimeter detected. Connect the instrument and try again.";
                    return;
                }
                if (Enum.TryParse<DisplayType>(profile.CalibDisplayType, out var displayType))
                    colorimeter.SetDisplayType(displayType);
                colorimeter.SetCorrectionFile(profile.MeterCorrectionPath);

                // Same ramp quiescence as the verify sweep: gamma preference / night mode
                // must not ride the GPU ramp while we measure through the installed profile
                // — a check made through the night ramp would poison the trend.
                stateManager = new CalibrationStateManager(_dispwinRunner, _nightModeService);
                try
                {
                    stateManager.EnterBypassMode(monitor,
                        profile.GammaMode, profile.ToCalibrationSettings());
                    bypassed = true;
                }
                catch (Exception ex)
                {
                    Log.Info($"TrustCheckWindow: bypass failed (continuing): {ex.Message}");
                }

                patchWindow = new PatchDisplayWindow(monitor);
                patchWindow.AbortRequested += () => cts.Cancel();
                patchWindow.EnableSweepControls(() => cts.Cancel());
                patchWindow.Show();

                _status.Text = "Position the probe on the marked target…";
                await patchWindow.WaitForPlacementAsync("Calibration trust check", cts.Token);
                await colorimeter.BeginMeasurementSessionAsync(hdrMode, cts.Token);

                var patches = TrustCheck.BuildPatches();
                var results = new List<MeasurementResult>(patches.Count);
                for (int i = 0; i < patches.Count; i++)
                {
                    var patch = patches[i];
                    var next = i + 1 < patches.Count ? patches[i + 1].Name : null;
                    patchWindow.SetProgress(i + 1, patches.Count, patch.Name, next, "Trust check");
                    patchWindow.SetColor(patch.DisplayRgb.R, patch.DisplayRgb.G, patch.DisplayRgb.B);
                    await Task.Delay(i == 0 ? 1200 : 500, cts.Token);

                    // White and black get the 3-read median treatment (the trend's anchor
                    // points); mid-tones and primaries are single reads — ~20 s total.
                    bool multiRead = i == 0 || patch.Name == "Black";
                    if (multiRead)
                    {
                        var reads = new List<MeasurementResult>(3);
                        for (int r = 0; r < 3; r++)
                            reads.Add(await colorimeter.MeasureAsync(patch, hdrMode, cts.Token));
                        results.Add(CalibrationOrchestrator.MedianMeasurement(
                            patch, reads.Where(m => m.IsValid).DefaultIfEmpty(reads[0]).ToList()));
                    }
                    else
                    {
                        results.Add(await colorimeter.MeasureAsync(patch, hdrMode, cts.Token));
                    }
                }

                bool hasCorrection = !string.IsNullOrEmpty(profile.MeterCorrectionPath);
                var context = new UncertaintyBudget.Context(
                    UncertaintyBudget.ClassifyInstrument(colorimeter.ConnectedColorimeter?.Model, hasCorrection),
                    LuminanceNoiseModel.FromMeasurements(results),
                    PeakWhiteDriftFraction: null,
                    DriftCompensated: false);

                var grade = TrustCheck.Compute(results, target, context);

                var entry = new TrustCheckEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    MonitorDevicePath = devicePath,
                    ProfileId = profile.CalibrationProfileId,
                    ProfileName = profile.Mhc2ProfileName,
                    HdrMode = hdrMode,
                    TargetName = target.Name,
                    InstrumentModel = colorimeter.ConnectedColorimeter?.Model,
                    MeterCorrectionFile = System.IO.Path.GetFileName(profile.MeterCorrectionPath ?? string.Empty),
                    AvgDeltaE2000 = grade.AvgDeltaE2000,
                    WhiteDeltaE2000 = grade.WhiteDeltaE2000,
                    WhiteCctK = grade.WhiteCctK,
                    WhiteDuv = grade.WhiteDuv,
                    WhiteNits = grade.WhiteNits,
                    BlackNits = grade.BlackNits,
                    U95DeltaE = grade.U95DeltaE,
                    Patches = grade.Patches,
                };
                TrustCheckHistory.Append(entry);

                string u95 = grade.U95DeltaE is { } u ? $" ± {u:F2}" : string.Empty;
                _status.Text = $"Check complete: avg ΔE2000 {grade.AvgDeltaE2000:F2}{u95}, " +
                               $"white {grade.WhiteCctK:F0} K (Duv {grade.WhiteDuv:+0.0000;-0.0000}), " +
                               $"{grade.WhiteNits:F1} nits, black {grade.BlackNits:F3} nits.";

                var verdict = TrustCheckHistory.AnalyzeDrift(TrustCheckHistory.Load(devicePath));
                if (verdict is { Alert: true })
                {
                    _toastService?.Show("Display drift detected", verdict.Summary, ToastKind.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                _status.Text = "Trust check cancelled.";
            }
            catch (Exception ex)
            {
                Log.Error($"TrustCheckWindow: check failed: {ex}");
                _status.Text = $"Trust check failed: {ex.Message}";
            }
            finally
            {
                if (colorimeter != null)
                {
                    try { await colorimeter.EndMeasurementSessionAsync(); } catch { }
                    colorimeter.Dispose();
                }
                patchWindow?.Close();
                if (bypassed)
                {
                    try { stateManager!.RestorePreviousState(); }
                    catch (Exception ex) { Log.Info($"TrustCheckWindow: bypass restore failed: {ex.Message}"); }
                }
                _runCts = null;
                _running = false;
                _runButton.IsEnabled = true;
                RefreshTrend();
            }
        }

        // ---- Trend rendering -------------------------------------------------------------------

        private static CalibrationCharts.ChartPalette Palette =>
            BrutalistTheme.IsDark ? CalibrationCharts.ChartPalette.Dark : CalibrationCharts.ChartPalette.Light;

        private void RefreshTrend()
        {
            string? devicePath = SelectedDevicePath;
            _deltaECanvas.Children.Clear();
            _duvCanvas.Children.Clear();
            if (devicePath == null) return;

            var history = TrustCheckHistory.Load(devicePath);
            if (history.Count == 0)
            {
                _latest.Text = "No trust checks recorded for this display yet. Run one to start the trend.";
                return;
            }

            var latest = history[^1];
            string u95 = latest.U95DeltaE is { } u ? $" ± {u:F2}" : string.Empty;
            var verdict = TrustCheckHistory.AnalyzeDrift(history);
            // Roadmap 4.2 (feasible core): trend-fitted drift prediction with honest gates —
            // it says nothing until the history can statistically support a statement.
            var prediction = DriftPredictor.Predict(history, DateTime.UtcNow);
            _latest.Text =
                $"Last check {latest.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm}: avg ΔE2000 {latest.AvgDeltaE2000:F2}{u95}, " +
                $"white {latest.WhiteCctK:F0} K / {latest.WhiteNits:F1} nits, Duv {latest.WhiteDuv:+0.0000;-0.0000}" +
                $" [{latest.TargetName}{(latest.HdrMode ? ", HDR" : string.Empty)}]" +
                (verdict != null ? $"\n{verdict.Summary}" : string.Empty) +
                (prediction != null ? $"\n{prediction.Summary}" : string.Empty);

            if (history.Count < 2 || _deltaECanvas.ActualWidth < 40) return;

            var palette = Palette;
            DateTime first = history[0].TimestampUtc;
            double Days(TrustCheckEntry e) => (e.TimestampUtc - first).TotalDays;
            double xMax = Math.Max(Days(history[^1]), 1.0);

            var deltaEPoints = history.Select(e => (Days(e), e.AvgDeltaE2000)).ToList();
            var deltaESeries = new List<CalibrationCharts.Series>
            {
                new("Avg ΔE2000", palette.Cyan, deltaEPoints),
            };
            // Honest banding: dashed ±U95 envelope around each run's own value.
            if (history.Any(e => e.U95DeltaE is not null))
            {
                deltaESeries.Add(new CalibrationCharts.Series("+U95", palette.Neutral,
                    history.Select(e => (Days(e), e.AvgDeltaE2000 + (e.U95DeltaE ?? 0))).ToList(), Dashed: true));
                deltaESeries.Add(new CalibrationCharts.Series("−U95", palette.Neutral,
                    history.Select(e => (Days(e), Math.Max(0, e.AvgDeltaE2000 - (e.U95DeltaE ?? 0)))).ToList(), Dashed: true));
            }
            double yMax = Math.Max(deltaESeries.SelectMany(s => s.Points).Max(p => p.Y) * 1.15, 1.0);
            CalibrationCharts.DrawLineChart(_deltaECanvas, deltaESeries,
                0, xMax, 0, yMax, "Days since first check", "ΔE2000", palette: palette);

            var duvSeries = new List<CalibrationCharts.Series>
            {
                new("White Duv", palette.Orange, history.Select(e => (Days(e), e.WhiteDuv)).ToList()),
            };
            double duvSpan = Math.Max(history.Max(e => Math.Abs(e.WhiteDuv)) * 1.3, 0.005);
            CalibrationCharts.DrawLineChart(_duvCanvas, duvSeries,
                0, xMax, -duvSpan, duvSpan, "Days since first check", "Duv", palette: palette);
        }

        private void ExportCsv()
        {
            string? devicePath = SelectedDevicePath;
            if (devicePath == null) return;
            var history = TrustCheckHistory.Load(devicePath);
            if (history.Count == 0)
            {
                _status.Text = "Nothing to export yet.";
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                FileName = "gloam-trust-checks.csv",
            };
            if (dialog.ShowDialog(this) != true) return;
            System.IO.File.WriteAllText(dialog.FileName, TrustCheckHistory.BuildCsv(history));
            _status.Text = $"Exported {history.Count} check(s) to {dialog.FileName}.";
        }
    }
}
