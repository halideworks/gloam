using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;

namespace HDRGammaController
{
    /// <summary>
    /// Multi-display matching (roadmap 4.5): solves the common white every calibrated panel
    /// can reach with minimal worst-case perceptual disturbance and applies it as
    /// per-monitor channel gains + brightness caps on top of the existing calibrations.
    /// Model-based (runs on stored characterizations) — the window says so.
    /// </summary>
    public sealed class DisplayMatchWindow : Window
    {
        private readonly MonitorManager _monitorManager;
        private readonly SettingsManager _settingsManager;
        private readonly Action _applyAll;

        private readonly TextBlock _summary;
        private readonly TextBlock _details;
        private readonly Button _applyButton;
        private DisplayMatchSolver.MatchSolution? _solution;

        public DisplayMatchWindow(
            MonitorManager monitorManager, SettingsManager settingsManager, Action applyAll)
        {
            _monitorManager = monitorManager;
            _settingsManager = settingsManager;
            _applyAll = applyAll;

            Title = "Match Displays";
            Width = 720;
            Height = 460;
            MinWidth = 620;
            MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.SetResourceReference(BackgroundProperty, "ThemeBg");
            this.SetResourceReference(ForegroundProperty, "ThemeText");
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Gloam;component/Themes/DarkControls.xaml", UriKind.Absolute),
            });

            _summary = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
            _summary.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");

            _details = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            };
            _details.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextDim");

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

            _applyButton = Make("Apply Match", (_, _) => ApplyMatch(), accent: true);
            _applyButton.IsEnabled = false;
            var reset = Make("Reset Trims", (_, _) => ResetTrims());
            var close = Make("Close", (_, _) => Close());

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            buttons.Children.Add(_applyButton);
            buttons.Children.Add(reset);
            buttons.Children.Add(close);

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var scroll = new ScrollViewer
            {
                Content = _details,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Grid.SetRow(_summary, 0);
            Grid.SetRow(scroll, 1);
            Grid.SetRow(buttons, 2);
            root.Children.Add(_summary);
            root.Children.Add(scroll);
            root.Children.Add(buttons);
            BrutalistChrome.Apply(this, "Match Displays", root);

            SolveNow();
        }

        private void SolveNow()
        {
            try
            {
                var reportsByDevice = LoadLatestReports();
                var inputs = new List<DisplayMatchSolver.DisplayInput>();
                foreach (var monitor in _monitorManager.EnumerateMonitors())
                {
                    if (string.IsNullOrEmpty(monitor.MonitorDevicePath)) continue;

                    // A display is "calibrated" when it has an installed MHC2 profile —
                    // NOT when it happens to carry a stored DisplayCalibrationProfile id
                    // (the calibration flow doesn't set that, which is why the old check
                    // found nothing). Its measured data comes from its latest saved report.
                    var monitorProfile = _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath);
                    bool calibrated = !string.IsNullOrEmpty(monitorProfile?.Mhc2ProfileName);
                    reportsByDevice.TryGetValue(monitor.MonitorDevicePath, out var report);
                    if (!calibrated && report == null) continue;

                    // A calibrated panel behaves like its target (that is what calibration
                    // makes it do), so a GPU-ramp gain maps to a white change through the
                    // TARGET matrix. Prefer the report's target, then the saved target name.
                    var target = report?.Target
                        ?? StandardTargets.GetByName(monitorProfile?.CalibTargetName ?? string.Empty)
                        ?? StandardTargets.SrgbGamma22;

                    double peak = report?.MeasuredCharacteristics?.PeakLuminance
                        ?? (monitor.IsHdrActive && monitor.HdrPeakNits > 0
                            ? monitor.HdrPeakNits
                            : MonitorInfo.SanitizeSdrWhiteLevel(monitor.SdrWhiteLevel));
                    if (!(peak > 0)) peak = 200.0;

                    inputs.Add(new DisplayMatchSolver.DisplayInput(
                        monitor.MonitorDevicePath,
                        monitor.FriendlyName,
                        target.RgbToXyzMatrix,
                        peak,
                        target.WhitePoint));
                }

                if (inputs.Count < 2)
                {
                    _summary.Text = inputs.Count == 0
                        ? "No calibrated displays found. Calibrate at least two displays first — matching runs on their stored calibration reports."
                        : "Only one calibrated display found. Calibrate a second display to match them.";
                    return;
                }

                _solution = DisplayMatchSolver.Solve(inputs);

                _summary.Text =
                    $"Common white: x {_solution.CommonWhite.X:F4}, y {_solution.CommonWhite.Y:F4} at " +
                    $"{_solution.CommonLuminanceNits:F0} nits — the closest point every panel can reach " +
                    $"(worst-case appearance change ΔE′ {_solution.MaxDeltaEPrime:F1}).\n" +
                    "Model-based: solved from each panel's stored characterization; residual per-panel " +
                    "calibration error is not visible to it.";

                var lines = new System.Text.StringBuilder();
                foreach (var a in _solution.Adjustments)
                {
                    lines.AppendLine($"{a.FriendlyName}");
                    lines.AppendLine($"    gains R {a.GainR:F3}  G {a.GainG:F3}  B {a.GainB:F3}   " +
                                     $"brightness {a.BrightnessPercent:F0}%");
                    lines.AppendLine($"    reach at common white {a.AchievableLuminanceNits:F0} nits, " +
                                     $"appearance change ΔE′ {a.DeltaEPrimeFromCurrent:F1}");
                }
                lines.AppendLine();
                lines.AppendLine("Apply writes these as the per-monitor channel gains and brightness on top of");
                lines.AppendLine("the existing calibrations (overwriting current manual gain/brightness trims).");
                lines.AppendLine("Reset Trims restores gains 1.0 / brightness 100% on the matched displays.");
                _details.Text = lines.ToString();
                _applyButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Log.Error($"DisplayMatchWindow: solve failed: {ex}");
                _summary.Text = $"Match solve failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Newest saved report per monitor device path — the reliable source of each panel's
        /// measured target and peak (the monitor profile stores neither a characterization
        /// nor, reliably, a target name).
        /// </summary>
        private static Dictionary<string, CalibrationProfile> LoadLatestReports()
        {
            var latest = new Dictionary<string, (DateTime When, CalibrationProfile Profile)>(StringComparer.OrdinalIgnoreCase);
            string dir = CalibrationProfile.GetReportsDirectory();
            foreach (string path in System.IO.Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var profile = CalibrationProfile.LoadFromFile(path);
                    if (string.IsNullOrEmpty(profile.MonitorDevicePath)) continue;
                    var when = System.IO.File.GetLastWriteTimeUtc(path);
                    if (!latest.TryGetValue(profile.MonitorDevicePath, out var existing) || when > existing.When)
                        latest[profile.MonitorDevicePath] = (when, profile);
                }
                catch
                {
                    // A malformed/older-schema report just doesn't contribute.
                }
            }
            return latest.ToDictionary(kv => kv.Key, kv => kv.Value.Profile, StringComparer.OrdinalIgnoreCase);
        }

        private void ApplyMatch()
        {
            if (_solution == null) return;
            if (!ConfirmDialog.Confirm(this, "Match Displays",
                    "Apply the matched white to all displays?\n\nThis overwrites each monitor's manual " +
                    "channel gains and brightness with the solved trim.",
                    confirmLabel: "Apply", cancelLabel: "Cancel"))
                return;

            try
            {
                foreach (var a in _solution.Adjustments)
                {
                    var profile = _settingsManager.GetMonitorProfile(a.DevicePath);
                    if (profile == null) continue;
                    profile.RedGain = Math.Clamp(a.GainR, 0.5, 1.5);
                    profile.GreenGain = Math.Clamp(a.GainG, 0.5, 1.5);
                    profile.BlueGain = Math.Clamp(a.GainB, 0.5, 1.5);
                    profile.Brightness = Math.Clamp(a.BrightnessPercent, 10.0, 100.0);
                    _settingsManager.SetMonitorProfile(a.DevicePath, profile);
                }
                _applyAll();
                _summary.Text += "\nApplied.";
                Log.Info($"DisplayMatchWindow: applied match — white ({_solution.CommonWhite.X:F4}, " +
                         $"{_solution.CommonWhite.Y:F4}) at {_solution.CommonLuminanceNits:F0} nits " +
                         $"across {_solution.Adjustments.Count} displays.");
            }
            catch (Exception ex)
            {
                Log.Error($"DisplayMatchWindow: apply failed: {ex}");
                ConfirmDialog.Info(this, "Match Displays", $"Apply failed: {ex.Message}");
            }
        }

        private void ResetTrims()
        {
            var targets = _solution?.Adjustments.Select(a => a.DevicePath).ToList()
                ?? _monitorManager.EnumerateMonitors()
                    .Select(m => m.MonitorDevicePath)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();
            foreach (string devicePath in targets)
            {
                var profile = _settingsManager.GetMonitorProfile(devicePath);
                if (profile == null) continue;
                profile.RedGain = 1.0;
                profile.GreenGain = 1.0;
                profile.BlueGain = 1.0;
                profile.Brightness = 100.0;
                _settingsManager.SetMonitorProfile(devicePath, profile);
            }
            _applyAll();
            _summary.Text += "\nTrims reset.";
        }
    }
}
