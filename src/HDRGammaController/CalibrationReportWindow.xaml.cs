using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;
using Microsoft.Win32;

namespace HDRGammaController
{
    /// <summary>
    /// Window for displaying calibration results and metrics.
    /// </summary>
    public partial class CalibrationReportWindow : Window
    {
        private readonly CalibrationProfile? _profile;
        private readonly CalibrationMetrics? _metrics;
        private readonly DisplayCharacterization? _characterization;
        private readonly Lut3D? _correctionLut;
        private readonly IReadOnlyList<MeasurementResult>? _measurements;
        private IReadOnlyList<MeasurementResult>? _verifyMeasurements;
        private System.Threading.CancellationTokenSource? _verifyCts;

        // White tools state: re-anchoring replaces the characterization's white (drift fix);
        // the visual trim shifts the TARGET white (metameric fix). Both rebuild the profile.
        private DisplayCharacterization? _activeCharacterization;
        private Chromaticity? _trimmedTargetWhite;
        private bool _trimNameToggle;
        private bool _trimBusy;
        private (double Dx, double Dy)? _trimPendingNudge;

        // Everything needed to install the calibration as a native Windows MHC2 profile and
        // to verify it afterwards. Set by the calibration window; when present, "Apply
        // Profile" does the real install and "Verify" can re-measure through it.
        public sealed record ApplyContext(
            MonitorInfo Monitor, CalibrationTarget Target,
            double[] LutR, double[] LutG, double[] LutB, double WhiteLevel,
            Action<string>? OnInstalled, ColorimeterService? Colorimeter = null,
            bool HdrMode = false,
            CalibrationStateManager? StateManager = null,
            GammaMode PreviousGammaMode = GammaMode.WindowsDefault,
            CalibrationSettings? PreviousSettings = null,
            double PatchSize = 600, double PatchOffsetX = 0, double PatchOffsetY = 0,
            bool CaptureSounds = false);
        private ApplyContext? _applyContext;
        private bool _profileApplied;

        public void SetApplyContext(ApplyContext context) => _applyContext = context;

        /// <summary>
        /// Hands-free mode: apply the profile and run the verification sweep as soon as the
        /// window opens (set by the calibration window so the whole flow needs no clicks).
        /// </summary>
        public bool AutoApplyOnLoad { get; set; }

        /// <summary>
        /// Notes the closed-loop grey-ramp refinement result. This is a much narrower metric
        /// than the accuracy table (1D grey-axis tracking only, measured during calibration
        /// against the GPU-ramp candidate — not through the MHC2 profile), so it's shown as a
        /// footnote rather than as "the" before/after.
        /// </summary>
        public void SetBeforeAfter(double beforeDeltaE, double afterDeltaE, int refinementRounds)
        {
            BeforeAfterNoteText.Text =
                $"Grey-ramp refinement during calibration: grey-axis tracking {beforeDeltaE:F2} → {afterDeltaE:F2} " +
                $"ΔE after {refinementRounds} pass(es). This 1D check excludes the white-point and gamut " +
                "correction - use Verify above for the full after numbers.";
            BeforeAfterNoteText.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Creates a new CalibrationReportWindow for displaying calibration results.
        /// </summary>
        /// <param name="profile">The calibration profile</param>
        /// <param name="metrics">The calibration metrics</param>
        /// <param name="characterization">The display characterization</param>
        /// <param name="correctionLut">The generated correction LUT</param>
        public CalibrationReportWindow(
            CalibrationProfile profile,
            CalibrationMetrics? metrics = null,
            DisplayCharacterization? characterization = null,
            Lut3D? correctionLut = null,
            IReadOnlyList<MeasurementResult>? measurements = null)
        {
            InitializeComponent();
            WindowTheme.UseDarkTitleBar(this);

            _profile = profile;
            _metrics = metrics;
            _characterization = characterization;
            _activeCharacterization = characterization;
            _correctionLut = correctionLut;
            _measurements = measurements;

            PopulateReport();

            // Charts need real canvas sizes, which exist only after layout.
            Loaded += (_, _) => RenderCharts();
            SizeChanged += (_, _) => RenderCharts();

            Loaded += async (_, _) =>
            {
                if (!AutoApplyOnLoad || _applyContext == null) return;
                // Let the tray's window-closed re-apply land first; the verify bypass then
                // clears it cleanly instead of racing it.
                await Task.Delay(600);
                await ApplyAndVerifyAsync();
            };

            // Escape aborts a running verification from this window too, and closing the
            // window mid-sweep cancels it instead of leaving the sweep running headless.
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape && _verifyCts is { IsCancellationRequested: false })
                {
                    e.Handled = true;
                    _verifyCts.Cancel();
                }
            };
            Closing += (_, _) => _verifyCts?.Cancel();
        }

        private void PopulateReport()
        {
            if (_profile == null) return;

            // Header info
            MonitorNameText.Text = _profile.MonitorName;
            CalibrationDateText.Text = $"Calibrated: {_profile.LastCalibratedAt?.ToLocalTime():g}";

            // Grade display
            var grade = _profile.QualityGrade ?? _metrics?.GetGrade() ?? CalibrationGrade.C;
            SetGradeDisplay(grade);

            // Summary metrics
            if (_metrics != null)
            {
                AvgDeltaEText.Text = $"{_metrics.AverageDeltaE:F2}";
                MaxDeltaEText.Text = $"{_metrics.MaxDeltaE:F2}";
                GrayscaleDeltaEText.Text = $"{_metrics.AverageGrayscaleDeltaE:F2}";
                PrimaryDeltaEText.Text = $"{_metrics.AveragePrimaryDeltaE:F2}";

                // Color code the values
                SetDeltaEColor(AvgDeltaEText, _metrics.AverageDeltaE);
                SetDeltaEColor(MaxDeltaEText, _metrics.MaxDeltaE);
                SetDeltaEColor(GrayscaleDeltaEText, _metrics.AverageGrayscaleDeltaE);
                SetDeltaEColor(PrimaryDeltaEText, _metrics.AveragePrimaryDeltaE);
            }
            else if (_profile.PostCalibrationDeltaE.HasValue)
            {
                AvgDeltaEText.Text = $"{_profile.PostCalibrationDeltaE:F2}";
                SetDeltaEColor(AvgDeltaEText, _profile.PostCalibrationDeltaE.Value);
            }

            // Summary text
            SummaryText.Text = GetSummaryText(grade);

            // Display characteristics
            PopulateDisplayCharacteristics();

            // Primaries comparison
            PopulatePrimariesComparison();

            // Calibration details
            PatchCountText.Text = _profile.PatchCount.ToString();
            // TotalTime would be set from CalibrationResult when saving the profile
            MeasurementTimeText.Text = "--";
            ColorimeterText.Text = _profile.ColorimeterModel ?? "Unknown";
            LutSizeText.Text = $"{_profile.LutSize}x{_profile.LutSize}x{_profile.LutSize}";
            TargetText.Text = _profile.Target.Name;
            ProfilePathText.Text = _profile.GetFilePath();

            // Recommendations
            PopulateRecommendations(grade);

            // Status
            UpdateStatus();
        }

        private void SetGradeDisplay(CalibrationGrade grade)
        {
            string gradeText = grade switch
            {
                CalibrationGrade.APLus => "A+",
                CalibrationGrade.A => "A",
                CalibrationGrade.AMinus => "A-",
                CalibrationGrade.BPlus => "B+",
                CalibrationGrade.B => "B",
                CalibrationGrade.BMinus => "B-",
                CalibrationGrade.CPlus => "C+",
                CalibrationGrade.C => "C",
                CalibrationGrade.CMinus => "C-",
                CalibrationGrade.D => "D",
                CalibrationGrade.F => "F",
                _ => "?"
            };

            GradeText.Text = gradeText;

            // Set color based on grade
            Color gradeColor = grade switch
            {
                CalibrationGrade.APLus or CalibrationGrade.A or CalibrationGrade.AMinus => Color.FromRgb(0x22, 0xc5, 0x5e), // Green
                CalibrationGrade.BPlus or CalibrationGrade.B or CalibrationGrade.BMinus => Color.FromRgb(0x3b, 0x82, 0xf6), // Blue
                CalibrationGrade.CPlus or CalibrationGrade.C or CalibrationGrade.CMinus => Color.FromRgb(0xf9, 0x73, 0x16), // Orange
                CalibrationGrade.D => Color.FromRgb(0xf5, 0x9e, 0x0b), // Amber
                _ => Color.FromRgb(0xef, 0x44, 0x44) // Red
            };

            GradeText.Foreground = new SolidColorBrush(gradeColor);

            Color bgColor = Color.FromArgb(0x40, gradeColor.R, gradeColor.G, gradeColor.B);
            GradeBorder.Background = new SolidColorBrush(bgColor);
        }

        private void SetDeltaEColor(System.Windows.Controls.TextBlock textBlock, double deltaE)
        {
            Color color = deltaE switch
            {
                < 1.0 => Color.FromRgb(0x22, 0xc5, 0x5e),  // Green - excellent
                < 2.0 => Color.FromRgb(0x22, 0xd3, 0xee),  // Cyan - good
                < 3.0 => Color.FromRgb(0xf9, 0x73, 0x16),  // Orange - acceptable
                < 5.0 => Color.FromRgb(0xf5, 0x9e, 0x0b),  // Amber - marginal
                _ => Color.FromRgb(0xef, 0x44, 0x44)       // Red - poor
            };

            textBlock.Foreground = new SolidColorBrush(color);
        }

        private string GetSummaryText(CalibrationGrade grade)
        {
            return grade switch
            {
                CalibrationGrade.APLus or CalibrationGrade.A =>
                    "Excellent calibration! Your display is performing at reference-level accuracy. Color reproduction is virtually indistinguishable from the target.",

                CalibrationGrade.AMinus or CalibrationGrade.BPlus =>
                    "Very good calibration. Your display shows excellent color accuracy suitable for color-critical work. Minor variations may exist in edge cases.",

                CalibrationGrade.B or CalibrationGrade.BMinus =>
                    "Good calibration. Your display shows solid color accuracy for most professional and creative work. Some deviation may be noticeable in critical comparisons.",

                CalibrationGrade.CPlus or CalibrationGrade.C =>
                    "Acceptable calibration. Your display shows reasonable color accuracy for general use. Some color shifts may be visible in side-by-side comparisons.",

                CalibrationGrade.CMinus or CalibrationGrade.D =>
                    "Marginal calibration. Your display shows noticeable color inaccuracies. Consider re-calibrating or checking display settings.",

                _ =>
                    "Poor calibration. Significant color errors were detected. Please check your display settings, colorimeter placement, and try calibrating again."
            };
        }

        private void PopulateDisplayCharacteristics()
        {
            if (_characterization != null)
            {
                PeakLuminanceText.Text = $"{_characterization.PeakLuminance:F1} cd/m\u00B2";
                BlackLevelText.Text = $"{_characterization.BlackLevel:F4} cd/m\u00B2";
                ContrastRatioText.Text = _characterization.ContrastRatio > 100000
                    ? "Infinite"
                    : $"{_characterization.ContrastRatio:F0}:1";
                MeasuredGammaText.Text = $"{_characterization.MeasuredGamma:F2}";

                double cct = ColorMath.ChromaticityToCct(_characterization.WhitePoint);
                double duv = ColorMath.CalculateDuv(_characterization.WhitePoint);
                WhitePointCctText.Text = $"{cct:F0} K";
                WhitePointDuvText.Text = $"{duv:F4}";
            }
            else if (_profile?.MeasuredCharacteristics != null)
            {
                var mc = _profile.MeasuredCharacteristics;
                PeakLuminanceText.Text = $"{mc.PeakLuminance:F1} cd/m\u00B2";
                BlackLevelText.Text = $"{mc.BlackLevel:F4} cd/m\u00B2";
                ContrastRatioText.Text = mc.ContrastRatio > 100000
                    ? "Infinite"
                    : $"{mc.ContrastRatio:F0}:1";
                MeasuredGammaText.Text = $"{mc.MeasuredGamma:F2}";
                WhitePointCctText.Text = $"{mc.MeasuredCct:F0} K";
                WhitePointDuvText.Text = $"{mc.MeasuredDuv:F4}";
            }

            SrgbCoverageText.Text = "--"; // Would need gamut volume calculation
        }

        private void PopulatePrimariesComparison()
        {
            var target = _profile?.Target;
            if (target == null) return;

            // Target primaries
            RedTargetText.Text = FormatChromaticity(target.RedPrimary);
            GreenTargetText.Text = FormatChromaticity(target.GreenPrimary);
            BlueTargetText.Text = FormatChromaticity(target.BluePrimary);
            WhiteTargetText.Text = FormatChromaticity(target.WhitePoint);

            // Measured primaries
            if (_characterization != null)
            {
                RedMeasuredText.Text = FormatChromaticity(_characterization.RedPrimary);
                GreenMeasuredText.Text = FormatChromaticity(_characterization.GreenPrimary);
                BlueMeasuredText.Text = FormatChromaticity(_characterization.BluePrimary);
                WhiteMeasuredText.Text = FormatChromaticity(_characterization.WhitePoint);

                RedErrorText.Text = FormatError(_characterization.RedPrimary, target.RedPrimary);
                GreenErrorText.Text = FormatError(_characterization.GreenPrimary, target.GreenPrimary);
                BlueErrorText.Text = FormatError(_characterization.BluePrimary, target.BluePrimary);
                WhiteErrorText.Text = FormatError(_characterization.WhitePoint, target.WhitePoint);
            }
            else if (_profile?.MeasuredCharacteristics != null)
            {
                var mc = _profile.MeasuredCharacteristics;
                RedMeasuredText.Text = FormatChromaticity(mc.MeasuredRed);
                GreenMeasuredText.Text = FormatChromaticity(mc.MeasuredGreen);
                BlueMeasuredText.Text = FormatChromaticity(mc.MeasuredBlue);
                WhiteMeasuredText.Text = FormatChromaticity(mc.MeasuredWhite);

                RedErrorText.Text = FormatError(mc.MeasuredRed, target.RedPrimary);
                GreenErrorText.Text = FormatError(mc.MeasuredGreen, target.GreenPrimary);
                BlueErrorText.Text = FormatError(mc.MeasuredBlue, target.BluePrimary);
                WhiteErrorText.Text = FormatError(mc.MeasuredWhite, target.WhitePoint);
            }
        }

        private static string FormatChromaticity(Chromaticity c)
        {
            return $"({c.X:F3}, {c.Y:F3})";
        }

        private static string FormatError(Chromaticity measured, Chromaticity target)
        {
            double error = measured.DistanceTo(target);
            return $"{error:F4}";
        }

        private void PopulateRecommendations(CalibrationGrade grade, CalibrationMetrics? verified = null)
        {
            var recommendations = new List<string>();

            // Verify-aware guidance beats generic advice: once we have measured-after data,
            // lead with what it actually says.
            if (verified != null)
            {
                if (_metrics != null && verified.AverageDeltaE > _metrics.AverageDeltaE + 0.3
                    && _profile?.Target.WhitePointOnly != true)
                {
                    recommendations.Add(
                        $"Verified accuracy ({verified.AverageDeltaE:F2}) came back worse than native ({_metrics.AverageDeltaE:F2}) - " +
                        "this panel is already inside the correction's noise floor. Recalibrate with " +
                        "\"White point correction only\", or run without a profile and use a small visual trim.");
                }

                double tone = verified.AverageGrayscaleToneDeltaE;
                double color = verified.AverageGrayscaleColorDeltaE;
                if (color > 1.0 && color > tone * 1.5)
                {
                    recommendations.Add(
                        $"Remaining grayscale error is mostly CHROMATIC (color {color:F2} vs tone {tone:F2}) - a visible cast. " +
                        "Check the meter spectral correction matches this panel; a small Tint trim can finish the job.");
                }
                else if (tone > 1.0 && tone > color * 1.5)
                {
                    recommendations.Add(
                        $"Remaining grayscale error is mostly TONE-AXIS (tone {tone:F2} vs color {color:F2}), typically " +
                        "concentrated near black where colorimeter accuracy is poorest - much of this is instrument noise, " +
                        "not visible error.");
                }
            }

            if (grade <= CalibrationGrade.A)
            {
                recommendations.Add("Your display is calibrated to professional standards.");
                recommendations.Add("Re-calibrate every 2-4 weeks to maintain accuracy.");
            }
            else if (grade <= CalibrationGrade.B)
            {
                recommendations.Add("Consider recalibrating with more patches for improved accuracy.");
                recommendations.Add("Ensure room lighting is consistent during calibration.");
                recommendations.Add("Re-calibrate every 2-4 weeks to maintain accuracy.");
            }
            else if (grade <= CalibrationGrade.C)
            {
                recommendations.Add("Check that your display has warmed up for at least 30 minutes.");
                recommendations.Add("Verify the colorimeter is properly positioned on the display.");
                recommendations.Add("Try using a larger patch set for more accurate profiling.");
            }
            else
            {
                recommendations.Add("Verify your colorimeter is properly connected and positioned.");
                recommendations.Add("Check display settings (reset to factory defaults if possible).");
                recommendations.Add("Ensure ambient light is minimized during calibration.");
                recommendations.Add("Consider using full-screen mode for better measurement accuracy.");
            }

            // The hard-won rule of this project: once the panel already measures inside the
            // system's noise + nonlinearity floor, full gamut correction has nothing real to
            // fix and verification tends to come back WORSE (seen on both test panels).
            // (_profile.Target carries the WhitePointOnly flag; _applyContext isn't set yet
            // when this runs from the constructor.)
            if (_metrics != null && _metrics.AverageDeltaE < 2.5 && _profile?.Target.WhitePointOnly != true)
            {
                recommendations.Add(
                    $"This panel already measures close to target natively (avg ΔE {_metrics.AverageDeltaE:F2}). " +
                    "Full gamut correction usually can't improve that and may verify worse - try the " +
                    "\"White point correction only\" option in calibration setup instead.");
            }

            if (_characterization != null)
            {
                double cct = ColorMath.ChromaticityToCct(_characterization.WhitePoint);
                if (cct < 6000 || cct > 7000)
                {
                    recommendations.Add(
                        $"Native white point ({cct:F0}K) sits well off D65. The installed profile corrects this " +
                        "digitally; setting the monitor's OSD color temperature closer to 6500K instead preserves " +
                        "more brightness and bit depth.");
                }

                double duv = ColorMath.CalculateDuv(_characterization.WhitePoint);
                if (Math.Abs(duv) > 0.01)
                {
                    string tint = duv > 0 ? "green" : "magenta";
                    recommendations.Add($"White point has a slight {tint} tint (Duv={duv:F3}). Adjust OSD tint if available.");
                }
            }

            RecommendationsList.ItemsSource = recommendations;
        }

        private void UpdateStatus()
        {
            if (_profile?.IsActive == true)
            {
                StatusText.Text = "Profile is active and applied.";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
                ApplyButton.Content = "Re-apply Profile";
            }
            else
            {
                StatusText.Text = "Profile is not currently active.";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xa0, 0xa0, 0xa0));
                ApplyButton.Content = "Apply Profile";
            }
        }

        private static string FormatTimeSpan(TimeSpan time)
        {
            if (time.TotalHours >= 1)
                return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
            return $"{time.Minutes}:{time.Seconds:D2}";
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_correctionLut == null && _profile?.CorrectionLut == null)
            {
                MessageBox.Show("No LUT data available to export.", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Export 3D LUT",
                Filter = "Adobe Cube LUT (*.cube)|*.cube|All Files (*.*)|*.*",
                DefaultExt = ".cube",
                FileName = $"{_profile?.MonitorName}_calibration.cube"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var lut = _correctionLut ?? _profile?.CorrectionLut;
                    lut?.SaveAsCube(dialog.FileName, $"{_profile?.MonitorName} Calibration LUT");

                    MessageBox.Show($"LUT exported successfully to:\n{dialog.FileName}",
                        "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to export LUT: {ex.Message}",
                        "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static readonly Color CR = Color.FromRgb(0xff, 0x5a, 0x5a);
        private static readonly Color CG = Color.FromRgb(0x55, 0xdd, 0x77);
        private static readonly Color CB = Color.FromRgb(0x5a, 0x9c, 0xff);
        private static readonly Color CGrey = Color.FromRgb(0x99, 0x99, 0x99);

        private void RenderCharts()
        {
            if (_characterization == null) return;
            var target = _profile?.Target;
            // For PQ (HDR) targets the measured patches are SDR content rendered by Windows
            // with the sRGB curve — plotting the PQ EOTF against SDR-content signals produced
            // a nonsense S-curve "target". Grade and draw against the sRGB content curve.
            bool pqTarget = target?.TransferFunction == TransferFunctionType.Pq;
            double targetGamma = pqTarget ? 2.2 : (target?.Gamma ?? 2.2);
            string targetLabel = pqTarget ? "Target (sRGB content)" : "Target";
            const int N = 41;

            double MeasuredOut(ToneCurve? curve, double v) => curve?.Lookup(v) ?? v;
            double TargetOut(double v) => pqTarget
                ? ColorMath.SrgbEotf(v)
                : (target?.ApplyEotf(v) ?? Math.Pow(v, targetGamma));

            // The raw measured grayscale steps (when available): the fitted curve plus real
            // data points, instead of a model line pretending to be measurements.
            var grays = _measurements?
                .Where(m => m.IsValid && m.Patch.Category == PatchCategory.Grayscale)
                .OrderBy(m => m.Patch.DisplayRgb.R)
                .ToList();
            double grayMinY = 0, grayRangeY = 1;
            if (grays is { Count: > 1 })
            {
                grayMinY = grays.Min(m => m.Xyz.Y);
                grayRangeY = Math.Max(grays.Max(m => m.Xyz.Y) - grayMinY, 1e-6);
            }
            double NormY(MeasurementResult m) => Math.Clamp((m.Xyz.Y - grayMinY) / grayRangeY, 0, 1);

            // 1. Tone response: target, fitted curve, and the measured points. (The per-channel
            // tone curves are all fitted from the same grayscale luminance, so a single fit
            // line is honest — per-channel behavior lives in the RGB balance chart.)
            var tRef = new List<(double, double)>();
            var tFit = new List<(double, double)>();
            for (int i = 0; i < N; i++)
            {
                double v = i / (N - 1.0);
                tRef.Add((v, TargetOut(v)));
                tFit.Add((v, MeasuredOut(_characterization.RedToneCurve, v)));
            }
            // Corrected (verified) grayscale, when a verify pass has run — the proof that the
            // scary-looking native curves were actually fixed.
            var corrected = _verifyMeasurements?
                .Where(m => m.IsValid && m.Patch.Category == PatchCategory.Grayscale)
                .OrderBy(m => m.Patch.DisplayRgb.R)
                .ToList();
            double corrMinY = 0, corrRangeY = 1;
            if (corrected is { Count: > 1 })
            {
                corrMinY = corrected.Min(m => m.Xyz.Y);
                corrRangeY = Math.Max(corrected.Max(m => m.Xyz.Y) - corrMinY, 1e-6);
            }
            double CorrNormY(MeasurementResult m) => Math.Clamp((m.Xyz.Y - corrMinY) / corrRangeY, 0, 1);
            var corrColor = Color.FromRgb(0x22, 0xc5, 0x5e);

            var toneSeries = new List<CalibrationCharts.Series>
            {
                new(targetLabel, CGrey, tRef, Dashed: true),
                new("Panel (fit)", Color.FromRgb(0x22, 0xd3, 0xee), tFit),
            };
            if (grays is { Count: > 1 })
                toneSeries.Add(new CalibrationCharts.Series("Measured", Color.FromRgb(0xf9, 0x73, 0x16),
                    grays.Select(m => (m.Patch.DisplayRgb.R, NormY(m))).ToList(), Scatter: true));
            if (corrected is { Count: > 1 })
            {
                var pts = corrected.Select(m => (m.Patch.DisplayRgb.R, CorrNormY(m))).ToList();
                toneSeries.Add(new CalibrationCharts.Series("Corrected", corrColor, pts));
                toneSeries.Add(new CalibrationCharts.Series("", corrColor, pts, Scatter: true));
            }
            CalibrationCharts.DrawLineChart(ToneCanvas, toneSeries, 0, 1, 0, 1, "Input signal", "Output");

            // 2. Gamma tracking (fit line + per-point measured gamma) vs the TARGET's
            // effective gamma curve - a flat "2.2" reference is wrong for sRGB-curve targets
            // (piecewise sRGB runs ~1.9–2.1 effective in the shadows), which made honest
            // tracking read as error. log(out)/log(in) is numerically unstable as input → 1
            // (the fit line used to nosedive at the right edge) — evaluate only [0.05, 0.95].
            var gammaMeas = new List<(double, double)>();
            var gammaRef = new List<(double, double)>();
            for (int i = 1; i < N; i++)
            {
                double v = i / (N - 1.0);
                if (v is < 0.05 or > 0.95) continue;
                double refOut = TargetOut(v);
                if (refOut > 0) gammaRef.Add((v, Math.Log(refOut) / Math.Log(v)));
                double outv = MeasuredOut(_characterization.RedToneCurve, v);
                if (outv > 0) gammaMeas.Add((v, Math.Log(outv) / Math.Log(v)));
            }
            var gammaSeries = new List<CalibrationCharts.Series>
            {
                new("Target (effective)", CGrey, gammaRef, Dashed: true),
                new("Fit", Color.FromRgb(0x22, 0xd3, 0xee), gammaMeas),
            };
            var gammaScatter = new List<(double, double)>();
            if (grays is { Count: > 1 })
            {
                gammaScatter = grays
                    .Where(m => m.Patch.DisplayRgb.R is >= 0.05 and <= 0.95 && NormY(m) > 0)
                    .Select(m => (m.Patch.DisplayRgb.R, Math.Log(NormY(m)) / Math.Log(m.Patch.DisplayRgb.R)))
                    .ToList();
                gammaSeries.Add(new CalibrationCharts.Series("Measured", Color.FromRgb(0xf9, 0x73, 0x16),
                    gammaScatter, Scatter: true));
            }
            if (corrected is { Count: > 1 })
            {
                var corrGamma = corrected
                    .Where(m => m.Patch.DisplayRgb.R is >= 0.05 and <= 0.95 && CorrNormY(m) > 0)
                    .Select(m => (m.Patch.DisplayRgb.R, Math.Log(CorrNormY(m)) / Math.Log(m.Patch.DisplayRgb.R)))
                    .ToList();
                gammaSeries.Add(new CalibrationCharts.Series("Corrected", corrColor, corrGamma));
                gammaSeries.Add(new CalibrationCharts.Series("", corrColor, corrGamma, Scatter: true));
                gammaScatter = gammaScatter.Concat(corrGamma).ToList(); // include in auto-range
            }
            // Auto-range so a deep tone-mapping rolloff (HDR knee) shows as data, not as a
            // suspicious flatline pinned to the chart floor.
            double gMin = 2.8, gMax = 1.6;
            foreach (var (_, gy) in gammaRef.Concat(gammaMeas).Concat(gammaScatter))
            {
                gMin = Math.Min(gMin, gy);
                gMax = Math.Max(gMax, gy);
            }
            gMin = Math.Floor(Math.Clamp(gMin - 0.1, 0.8, 2.0) * 10) / 10;
            gMax = Math.Ceiling(Math.Clamp(gMax + 0.1, 2.4, 3.2) * 10) / 10;
            CalibrationCharts.DrawLineChart(GammaCanvas, gammaSeries, 0, 1, gMin, gMax, "Input signal", "Gamma");

            // 3. Grayscale RGB balance from the MEASURED XYZ of each gray step: each channel's
            // linear contribution relative to neutral (1.0 = no cast). The old version plotted
            // the three fitted tone curves, which are identical by construction — they overlap
            // exactly and only the last-drawn series was visible.
            var balanceSeries = new List<CalibrationCharts.Series>
            {
                new("Neutral", CGrey, new List<(double, double)> { (0, 1), (1, 1) }, Dashed: true),
            };
            if (grays is { Count: > 1 } && _characterization.RgbToXyzMatrix != null)
            {
                var inv = ColorMath.Invert3x3(_characterization.RgbToXyzMatrix);
                double[] ChannelLin(MeasurementResult m) => new[]
                {
                    inv[0, 0] * m.Xyz.X + inv[0, 1] * m.Xyz.Y + inv[0, 2] * m.Xyz.Z,
                    inv[1, 0] * m.Xyz.X + inv[1, 1] * m.Xyz.Y + inv[1, 2] * m.Xyz.Z,
                    inv[2, 0] * m.Xyz.X + inv[2, 1] * m.Xyz.Y + inv[2, 2] * m.Xyz.Z,
                };
                var white = grays.OrderByDescending(m => m.Xyz.Y).First();
                double[] whiteLin = ChannelLin(white);
                var balR = new List<(double, double)>(); var balG = new List<(double, double)>(); var balB = new List<(double, double)>();
                foreach (var m in grays.Where(m => m.Patch.DisplayRgb.R >= 0.08))
                {
                    double[] lin = ChannelLin(m);
                    if (whiteLin.Any(w => w <= 1e-6)) continue;
                    double r = lin[0] / whiteLin[0], g = lin[1] / whiteLin[1], b = lin[2] / whiteLin[2];
                    double avg = (r + g + b) / 3.0;
                    if (avg <= 1e-5) continue;
                    double v = m.Patch.DisplayRgb.R;
                    balR.Add((v, r / avg)); balG.Add((v, g / avg)); balB.Add((v, b / avg));
                }
                balanceSeries.Add(new CalibrationCharts.Series("R", CR, balR));
                balanceSeries.Add(new CalibrationCharts.Series("", CR, balR, Scatter: true));
                balanceSeries.Add(new CalibrationCharts.Series("G", CG, balG));
                balanceSeries.Add(new CalibrationCharts.Series("", CG, balG, Scatter: true));
                balanceSeries.Add(new CalibrationCharts.Series("B", CB, balB));
                balanceSeries.Add(new CalibrationCharts.Series("", CB, balB, Scatter: true));
            }
            CalibrationCharts.DrawLineChart(BalanceCanvas, balanceSeries,
                0, 1, 0.85, 1.15, "Input signal", "Balance");

            // 4. Gamut + white point on CIE xy.
            if (target != null)
            {
                CalibrationCharts.DrawGamutDiagram(GamutCanvas,
                    (target.RedPrimary.X, target.RedPrimary.Y), (target.GreenPrimary.X, target.GreenPrimary.Y),
                    (target.BluePrimary.X, target.BluePrimary.Y), (target.WhitePoint.X, target.WhitePoint.Y),
                    (_characterization.RedPrimary.X, _characterization.RedPrimary.Y),
                    (_characterization.GreenPrimary.X, _characterization.GreenPrimary.Y),
                    (_characterization.BluePrimary.X, _characterization.BluePrimary.Y),
                    (_characterization.WhitePoint.X, _characterization.WhitePoint.Y));
            }
        }

        private string? _installedProfileName;
        private bool _profileEnabled;

        /// <summary>
        /// One button, three states: Apply Profile (not yet installed) → Disable Profile
        /// (installed + active) ↔ Enable Profile (installed + toggled off for comparison).
        /// </summary>
        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_installedProfileName is { } profileName && _applyContext is { } ctx)
            {
                if (_profileEnabled)
                {
                    CalibrationProfileInstaller.Disable(ctx.Monitor, profileName);
                    _profileEnabled = false;
                    ApplyButton.Content = "Enable Profile";
                    StatusText.Text = "Profile disabled - showing the uncorrected panel for comparison.";
                }
                else if (CalibrationProfileInstaller.Reenable(ctx.Monitor, profileName, ctx.HdrMode))
                {
                    _profileEnabled = true;
                    ApplyButton.Content = "Disable Profile";
                    StatusText.Text = "Profile re-enabled.";
                }
                else
                {
                    StatusText.Text = "Could not re-enable the profile - close and re-run the calibration.";
                }
                return;
            }

            await ApplyAndVerifyAsync();
        }

        /// <summary>
        /// Installs the measured correction as the monitor's native Windows color profile
        /// (gamut matrix + tone LUTs; in HDR the installer rebuilds the LUTs in PQ wire-signal
        /// domain and associates via the Advanced Color list), then runs the verification
        /// sweep automatically — the probe is still on the display right after a calibration.
        /// </summary>
        /// <summary>The target with the visual white trim applied, when one is set.</summary>
        private CalibrationTarget EffectiveTarget(ApplyContext ctx) =>
            _trimmedTargetWhite is { } w ? ctx.Target.WithWhitePoint(w) : ctx.Target;

        private async Task ApplyAndVerifyAsync(bool runVerify = true)
        {
            if (_applyContext == null || _activeCharacterization == null)
            {
                MessageBox.Show("This calibration can't be applied (missing display characterization).",
                    "Apply Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var ctx = _applyContext;
            ApplyButton.IsEnabled = false;
            try
            {
                StatusText.Text = "Applying profile…";
                var result = CalibrationProfileInstaller.Install(
                    ctx.Monitor, _activeCharacterization, EffectiveTarget(ctx),
                    ctx.LutR, ctx.LutG, ctx.LutB, ctx.WhiteLevel,
                    hdrMode: ctx.HdrMode, measurements: _measurements);

                if (result.Success)
                {
                    _profileApplied = true;
                    _installedProfileName = result.ProfileName;
                    _profileEnabled = true;
                    ApplyButton.Content = "Disable Profile";
                    ctx.OnInstalled?.Invoke(result.ProfileName);

                    if (runVerify && ctx.Colorimeter != null)
                    {
                        StatusText.Text = "Profile applied - verifying through the correction…";
                        await RunVerificationAsync();
                    }
                    else
                    {
                        StatusText.Text = "Profile applied.";
                    }
                }
                else
                {
                    StatusText.Text = "Apply failed.";
                    MessageBox.Show($"Could not apply the calibration:\n\n{result.Error}",
                        "Apply Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                ApplyButton.IsEnabled = true;
            }
        }

        private void WhiteToolsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_applyContext == null || _activeCharacterization == null)
            {
                MessageBox.Show("White tools need the live calibration context (open this report right after a calibration).",
                    "White Tools", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var menu = new System.Windows.Controls.ContextMenu();
            var reanchor = new System.Windows.Controls.MenuItem
            {
                Header = "Re-anchor white (measure)",
                ToolTip = "Quick drift fix: re-measures only white and rebuilds the profile around the fresh " +
                          "reading. Use after the panel has warmed up, or whenever an OLED has drifted.",
                IsEnabled = _applyContext.Colorimeter != null,
            };
            reanchor.Click += async (_, _) => await ReanchorWhiteAsync();

            var trim = new System.Windows.Controls.MenuItem
            {
                Header = "Visual white trim…",
                ToolTip = "Nudge the target white by eye against a reference display; the result is baked " +
                          "into the profile (fixes the metameric gap instruments can't see).",
                IsEnabled = _profileApplied,
            };
            trim.Click += async (_, _) => await RunVisualWhiteTrimAsync();

            menu.Items.Add(reanchor);
            menu.Items.Add(trim);

            if (_applyContext.HdrMode)
            {
                var hdrValidate = new System.Windows.Controls.MenuItem
                {
                    Header = "Validate HDR patch renderer (experimental)",
                    ToolTip = "Certifies the FP16 scRGB patch path with the probe: checks that above-SDR-white " +
                              "emission works and that the installed profile applies to it identically. Required " +
                              "once before HDR-range calibration can be trusted.",
                    IsEnabled = _applyContext.Colorimeter != null,
                };
                hdrValidate.Click += async (_, _) => await ValidateHdrRendererAsync();
                menu.Items.Add(hdrValidate);
            }

            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Probe-certification of the HDR-range patch path (see docs/hdr-patch-renderer-design.md):
        ///  A) the FP16 swapchain at the SDR white level must measure ≈ the SDR window's white
        ///     (same pipeline, same profile application);
        ///  B) at 2× SDR white it must measure well ABOVE the SDR ceiling (the slider must not
        ///     clamp values above 1.0).
        /// Both passing means HDR-range patch sets can be trusted for calibration/verify.
        /// </summary>
        private async Task ValidateHdrRendererAsync()
        {
            if (_applyContext is not { Colorimeter: { } colorimeter } ctx) return;
            if (MessageBox.Show(
                    "Place the probe on the display, then press Yes.\n\n" +
                    "Three short measurements: SDR white, the FP16 renderer at the same level, " +
                    "and the FP16 renderer at double that level (briefly bright).",
                    "Validate HDR Renderer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            ApplyButton.IsEnabled = false;
            VerifyButton.IsEnabled = false;
            PatchDisplayWindow? surround = null;
            HdrPatchRenderer? renderer = null;
            bool bypassed = false;
            var whitePatch = new ColorPatch { Name = "White", DisplayRgb = new LinearRgb(1, 1, 1), Category = PatchCategory.Grayscale };
            try
            {
                if (ctx.StateManager != null)
                {
                    try { ctx.StateManager.EnterBypassMode(ctx.Monitor, ctx.PreviousGammaMode, ctx.PreviousSettings); bypassed = true; }
                    catch { }
                }

                surround = new PatchDisplayWindow(ctx.Monitor, ctx.PatchSize, ctx.PatchOffsetX, ctx.PatchOffsetY);
                surround.Show();
                await colorimeter.BeginMeasurementSessionAsync(hdrMode: true);

                // 1) SDR baseline through the normal window.
                surround.SetProgress(1, 3, "SDR white (baseline)");
                surround.SetColor(1, 1, 1);
                await Task.Delay(1500);
                var sdr = await colorimeter.MeasureAsync(whitePatch, true);

                // Patch rectangle in pixels (same placement as the surround's patch).
                var b = ctx.Monitor.MonitorBounds;
                int size = (int)ctx.PatchSize;
                int px = b.Left + (b.Right - b.Left - size) / 2 + (int)ctx.PatchOffsetX;
                int py = b.Top + (b.Bottom - b.Top - size) / 2 + (int)ctx.PatchOffsetY;

                surround.SetColor(0, 0, 0); // black behind the FP16 window
                renderer = new HdrPatchRenderer(px, py, size, size);
                Log.Info($"HDR renderer created; scRGB colorspace support reported: {renderer.ScRgbSupported}");

                // 2) FP16 at the SDR white level - must match the SDR baseline.
                surround.SetProgress(2, 3, "FP16 at SDR white level");
                renderer.PresentNits(sdr.Xyz.Y, sdr.Xyz.Y, sdr.Xyz.Y);
                await Task.Delay(1500);
                var hdrSame = await colorimeter.MeasureAsync(whitePatch, true);

                // 3) FP16 at 2x - must exceed the SDR ceiling (capped below panel peak).
                double targetHigh = Math.Min(sdr.Xyz.Y * 2.0,
                    ctx.Monitor.HdrPeakNits > 50 ? ctx.Monitor.HdrPeakNits * 0.9 : sdr.Xyz.Y * 2.0);
                surround.SetProgress(3, 3, $"FP16 at {targetHigh:F0} nits");
                renderer.PresentNits(targetHigh, targetHigh, targetHigh);
                await Task.Delay(1500);
                var hdrHigh = await colorimeter.MeasureAsync(whitePatch, true);

                // Verdicts.
                double sameRatio = hdrSame.Xyz.Y / Math.Max(sdr.Xyz.Y, 1e-6);
                double dxSame = Math.Abs(hdrSame.Chromaticity.X - sdr.Chromaticity.X);
                double dySame = Math.Abs(hdrSame.Chromaticity.Y - sdr.Chromaticity.Y);
                bool passPipeline = sameRatio > 0.90 && sameRatio < 1.10 && dxSame < 0.006 && dySame < 0.006;
                bool passRange = hdrHigh.Xyz.Y > sdr.Xyz.Y * 1.4;

                string report =
                    $"SDR white baseline:   {sdr.Xyz.Y:F1} nits  ({sdr.Chromaticity.X:F4}, {sdr.Chromaticity.Y:F4})\n" +
                    $"FP16 at same level:   {hdrSame.Xyz.Y:F1} nits  ({hdrSame.Chromaticity.X:F4}, {hdrSame.Chromaticity.Y:F4})  ratio {sameRatio:F3}\n" +
                    $"FP16 at {targetHigh:F0} nits:    {hdrHigh.Xyz.Y:F1} nits measured\n\n" +
                    $"Pipeline parity (profile applies to FP16): {(passPipeline ? "PASS" : "FAIL")}\n" +
                    $"Above-SDR-white emission:                  {(passRange ? "PASS" : "FAIL")}\n\n" +
                    (passPipeline && passRange
                        ? "The HDR-range patch path is trustworthy on this system. HDR-range calibration can be built on it."
                        : "Do NOT trust HDR-range measurements on this system yet - send the log for diagnosis.");
                Log.Info($"HDR renderer validation:\n{report}");
                MessageBox.Show(report, "HDR Renderer Validation",
                    MessageBoxButton.OK, passPipeline && passRange ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                Log.Info($"HDR renderer validation failed: {ex}");
                MessageBox.Show($"HDR renderer validation failed:\n\n{ex.Message}",
                    "HDR Renderer Validation", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { await colorimeter.EndMeasurementSessionAsync(); } catch { }
                renderer?.Dispose();
                surround?.Close();
                if (bypassed) { try { ctx.StateManager!.RestorePreviousState(); } catch { } }
                ApplyButton.IsEnabled = true;
                VerifyButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// HDR PQ-tracking verify: FP16 wire patches through the applied profile, graded in
        /// ABSOLUTE nits against ST.2084 plus ΔE ITP against D65 gray at each level. Runs
        /// only when the calibration itself measured a wire ladder (so the LUT actually
        /// corrected this range); rungs stay below the LUT's identity blend at the panel's
        /// reachable peak. Returns the report line, or null when not applicable.
        /// </summary>
        private async Task<string?> RunPqTrackingSweepAsync(
            PatchDisplayWindow patchWindow, ColorimeterService colorimeter,
            ApplyContext ctx, System.Threading.CancellationToken token)
        {
            var wireCal = _measurements?
                .Where(m => m.IsValid && m.Patch.Nits is not null && m.Patch.Nits > 0)
                .ToList();
            if (wireCal == null || wireCal.Count == 0)
                return null; // profile predates the wire ladder - nothing above SDR white was corrected

            // Grade only the region the LUT corrects: below the identity blend that starts
            // at 90% of the panel's reachable (measured) peak.
            double reachablePeak = wireCal.Max(m => m.Xyz.Y);
            double top = reachablePeak * 0.85;
            var rungs = new[] { 16.0, 64, 150, 320, 650, 1000 }.Where(n => n <= top).ToList();
            if (rungs.Count == 0)
                return null;

            HdrPatchRenderer? wire = null;
            var readings = new List<(double Requested, MeasurementResult M)>();
            try
            {
                var rect = patchWindow.GetPatchPixelRect();
                wire = new HdrPatchRenderer(rect.X, rect.Y, rect.Width, rect.Height);
                patchWindow.SetColor(0, 0, 0);

                for (int i = 0; i < rungs.Count; i++)
                {
                    double nits = rungs[i];
                    var p = new ColorPatch
                    {
                        Name = $"PQ {nits:F0} nits",
                        DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                        Nits = nits,
                        Category = PatchCategory.General,
                    };
                    VerifyButton.Content = $"PQ tracking {i + 1}/{rungs.Count}…";
                    patchWindow.SetProgress(i + 1, rungs.Count, p.Name);
                    wire.PresentNits(nits, nits, nits);
                    await Task.Delay(i == 0 ? 1200 : 600, token);
                    var m = await colorimeter.MeasureAsync(p, ctx.HdrMode, token);
                    if (ctx.CaptureSounds)
                        CalibrationSounds.PlayCapture();
                    if (m.IsValid)
                        readings.Add((nits, m));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Info($"CalibrationReportWindow: PQ tracking sweep failed: {ex.Message}");
                return $"HDR PQ tracking sweep failed ({ex.Message}).";
            }
            finally
            {
                wire?.Dispose();
            }

            if (readings.Count == 0)
                return "HDR PQ tracking sweep returned no valid readings.";

            double sumAbsErr = 0, worstErr = 0, worstNits = 0, itpSum = 0, itpMax = 0;
            foreach (var (requested, m) in readings)
            {
                double err = (m.Xyz.Y - requested) / requested;
                sumAbsErr += Math.Abs(err);
                if (Math.Abs(err) > Math.Abs(worstErr)) { worstErr = err; worstNits = requested; }

                // D65 gray at the requested absolute luminance - the PQ spec target.
                var target = new CieXyz(
                    requested * 0.3127 / 0.3290, requested, requested * (1 - 0.3127 - 0.3290) / 0.3290);
                double itp = CalibrationVerifier.DeltaEItp(m.Xyz, target);
                itpSum += itp;
                itpMax = Math.Max(itpMax, itp);
                Log.Info($"CalibrationReportWindow: PQ verify {requested,6:F0} nits -> {m.Xyz.Y,7:F1} " +
                         $"({err:+0.0%;-0.0%}), xy ({m.Xyz.ToChromaticity().X:F4},{m.Xyz.ToChromaticity().Y:F4}), ITP {itp:F1}");
            }

            return $"HDR PQ tracking (FP16 through profile, {readings.Count} levels to {readings[^1].Requested:F0} nits): " +
                   $"avg luminance error {sumAbsErr / readings.Count:P1}, worst {worstErr:+0.0%;-0.0%} at {worstNits:F0} nits; " +
                   $"ΔE ITP avg {itpSum / readings.Count:F1}, max {itpMax:F1} vs D65 gray.";
        }

        /// <summary>
        /// Drift fix for OLEDs and warm-up shifts: measure ONLY white (averaged 3x), replace
        /// the characterization's white anchor, rebuild + reinstall + re-verify. ~30 seconds
        /// instead of a full calibration.
        /// </summary>
        private async Task ReanchorWhiteAsync()
        {
            if (_applyContext is not { Colorimeter: { } colorimeter } ctx || _activeCharacterization is not { } ch)
                return;
            if (MessageBox.Show("Place the probe on the display, then press Yes to re-measure white.",
                    "Re-anchor White", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            ApplyButton.IsEnabled = false;
            VerifyButton.IsEnabled = false;
            PatchDisplayWindow? patchWindow = null;
            bool bypassed = false;
            try
            {
                if (ctx.StateManager != null)
                {
                    try { ctx.StateManager.EnterBypassMode(ctx.Monitor, ctx.PreviousGammaMode, ctx.PreviousSettings); bypassed = true; }
                    catch { }
                }

                patchWindow = new PatchDisplayWindow(ctx.Monitor, ctx.PatchSize, ctx.PatchOffsetX, ctx.PatchOffsetY);
                patchWindow.Show();
                patchWindow.SetColor(1, 1, 1);
                patchWindow.SetProgress(1, 1, "White (re-anchor)");
                await colorimeter.BeginMeasurementSessionAsync(hdrMode: ctx.HdrMode);
                await Task.Delay(1500);

                var whitePatch = new ColorPatch
                {
                    Name = "White",
                    DisplayRgb = new LinearRgb(1, 1, 1),
                    Category = PatchCategory.Grayscale,
                };
                double sx = 0, sy = 0, sz = 0;
                const int reads = 3;
                for (int i = 0; i < reads; i++)
                {
                    var m = await colorimeter.MeasureAsync(whitePatch, ctx.HdrMode);
                    sx += m.Xyz.X; sy += m.Xyz.Y; sz += m.Xyz.Z;
                    if (ctx.CaptureSounds) CalibrationSounds.PlayCapture();
                    await Task.Delay(300);
                }
                var avg = new CieXyz(sx / reads, sy / reads, sz / reads);
                var newWhite = avg.ToChromaticity();
                Log.Info($"CalibrationReportWindow: Re-anchored white to ({newWhite.X:F4},{newWhite.Y:F4}), {avg.Y:F1} nits " +
                         $"(was ({ch.WhitePoint.X:F4},{ch.WhitePoint.Y:F4}), {ch.PeakLuminance:F1} nits).");

                _activeCharacterization = new DisplayCharacterization
                {
                    BlackXyz = ch.BlackXyz,
                    WhiteXyz = avg,
                    RedPrimary = ch.RedPrimary,
                    GreenPrimary = ch.GreenPrimary,
                    BluePrimary = ch.BluePrimary,
                    WhitePoint = newWhite,
                    BlackLevel = ch.BlackLevel,
                    PeakLuminance = avg.Y,
                    RedToneCurve = ch.RedToneCurve,
                    GreenToneCurve = ch.GreenToneCurve,
                    BlueToneCurve = ch.BlueToneCurve,
                    RgbToXyzMatrix = ColorMath.CalculateRgbToXyzMatrix(
                        ch.RedPrimary, ch.GreenPrimary, ch.BluePrimary, newWhite),
                    MeasuredGamma = ch.MeasuredGamma,
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"White re-anchor failed:\n\n{ex.Message}", "Re-anchor White",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                try { await colorimeter.EndMeasurementSessionAsync(); } catch { }
                patchWindow?.Close();
                if (bypassed) { try { ctx.StateManager!.RestorePreviousState(); } catch { } }
                ApplyButton.IsEnabled = true;
                VerifyButton.IsEnabled = true;
            }

            await ApplyAndVerifyAsync();
        }

        /// <summary>
        /// Visual white trim: live-preview nudges of the TARGET white, each rebuilding the
        /// profile (alternating between two preview names so the compositor never serves a
        /// cached profile). Done bakes the trim into a final, recorded profile.
        /// </summary>
        private async Task RunVisualWhiteTrimAsync()
        {
            if (_applyContext is not { } ctx || _activeCharacterization == null) return;

            var baseWhite = EffectiveTarget(ctx).WhitePoint;
            var editor = new WhiteTrimWindow { Owner = this };
            editor.TrimChanged += async (dx, dy) => await InstallTrimPreviewAsync(ctx, baseWhite, dx, dy);

            bool? accepted = editor.ShowDialog();

            // Retire the preview profiles either way (throwaway artifacts, fully ours).
            foreach (string previewName in new[] { TrimPreviewName(ctx, true), TrimPreviewName(ctx, false) })
            {
                CalibrationProfileInstaller.Disable(ctx.Monitor, previewName);
                CalibrationProfileInstaller.Uninstall(ctx.Monitor, previewName);
            }

            if (accepted == true && editor.Result is { } trim && (trim.Dx != 0 || trim.Dy != 0))
            {
                _trimmedTargetWhite = new Chromaticity(baseWhite.X + trim.Dx, baseWhite.Y + trim.Dy);
                StatusText.Text = $"White trim baked in (dx {trim.Dx:+0.0000;-0.0000}, dy {trim.Dy:+0.0000;-0.0000}).";
                await ApplyAndVerifyAsync(runVerify: false);
            }
            else
            {
                // Cancelled (or zero trim): restore the untrimmed profile.
                await ApplyAndVerifyAsync(runVerify: false);
            }
        }

        private static string TrimPreviewName(ApplyContext ctx, bool a) =>
            $"{ctx.Monitor.FriendlyName} - white trim preview {(a ? "A" : "B")}.icm";

        private async Task InstallTrimPreviewAsync(ApplyContext ctx, Chromaticity baseWhite, double dx, double dy)
        {
            // Serialize: nudges can arrive faster than installs complete. Keep only the latest.
            if (_trimBusy) { _trimPendingNudge = (dx, dy); return; }
            _trimBusy = true;
            try
            {
                while (true)
                {
                    var target = ctx.Target.WithWhitePoint(new Chromaticity(baseWhite.X + dx, baseWhite.Y + dy));
                    _trimNameToggle = !_trimNameToggle;
                    var result = CalibrationProfileInstaller.Install(
                        ctx.Monitor, _activeCharacterization!, target,
                        ctx.LutR, ctx.LutG, ctx.LutB, ctx.WhiteLevel,
                        hdrMode: ctx.HdrMode, measurements: _measurements,
                        profileNameOverride: TrimPreviewName(ctx, _trimNameToggle));
                    if (!result.Success)
                        Log.Info($"CalibrationReportWindow: trim preview install failed: {result.Error}");

                    await Task.Delay(150); // let the compositor pick it up before the next step
                    if (_trimPendingNudge is { } pending) { (dx, dy) = pending; _trimPendingNudge = null; }
                    else break;
                }
            }
            finally
            {
                _trimBusy = false;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Re-measures a quick patch sweep THROUGH whatever Windows is currently applying
        /// (normally the just-installed profile) and fills in the "after" row of the accuracy
        /// table - the honest, measured counterpart to the native "before" numbers.
        /// </summary>
        private async void VerifyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_applyContext?.Colorimeter is not { } colorimeter || _applyContext is not { } ctx)
            {
                MessageBox.Show(
                    "Verification isn't available for this report (no colorimeter context). " +
                    "Run it from the report that opens right after a calibration.",
                    "Verify Calibration", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string prompt = _profileApplied
                ? "Place the probe on the display, then press Yes to start.\n\n" +
                  "Tip: turn night mode off while verifying - it tints the measurements."
                : "The profile hasn't been applied from this window yet, so Verify will measure " +
                  "whatever is currently active on the display.\n\n" +
                  "Place the probe on the display, then press Yes to start.";
            if (MessageBox.Show(prompt, "Verify Calibration", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            await RunVerificationAsync();
        }

        /// <summary>
        /// The verify sweep itself (no prompts): measures the verification patches through
        /// whatever Windows is currently applying and fills the "after" row + grade.
        /// Runs automatically after Apply Profile, and from the Verify button for re-runs.
        /// </summary>
        private async Task RunVerificationAsync()
        {
            if (_applyContext?.Colorimeter is not { } colorimeter || _applyContext is not { } ctx)
                return;

            VerifyButton.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            PatchDisplayWindow? patchWindow = null;
            using var verifyCts = new System.Threading.CancellationTokenSource();
            _verifyCts = verifyCts;

            // RAMP QUIESCENCE: verification grades the CALIBRATION, so the user's gamma
            // preference and night mode must not ride on the GPU ramp during the sweep.
            // (The 14:28 run measured grayscale at 5.10 because the ramp guard restored the
            // user's Gamma-2.4 ramp mid-verify — full-signal primaries were untouched, every
            // mid-gray took the shift.) EnterBypassMode clears through DispwinRunner, so the
            // guard MAINTAINS identity instead of "restoring" the preference ramp.
            bool bypassed = false;
            if (ctx.StateManager != null)
            {
                try
                {
                    ctx.StateManager.EnterBypassMode(ctx.Monitor, ctx.PreviousGammaMode, ctx.PreviousSettings);
                    bypassed = true;
                }
                catch (Exception ex)
                {
                    Log.Info($"CalibrationReportWindow: verify bypass failed (continuing): {ex.Message}");
                }
            }

            try
            {
                patchWindow = new PatchDisplayWindow(ctx.Monitor, ctx.PatchSize, ctx.PatchOffsetX, ctx.PatchOffsetY);
                patchWindow.AbortRequested += () => verifyCts.Cancel(); // Escape aborts the sweep
                patchWindow.Show();

                var patches = CalibrationVerifier.BuildVerificationPatches();
                var results = new List<MeasurementResult>();
                await colorimeter.BeginMeasurementSessionAsync(hdrMode: ctx.HdrMode, verifyCts.Token);
                for (int i = 0; i < patches.Count; i++)
                {
                    var p = patches[i];
                    VerifyButton.Content = $"Verifying {i + 1}/{patches.Count}…";
                    patchWindow.SetProgress(i + 1, patches.Count, p.Name);
                    patchWindow.SetColor(p.DisplayRgb.R, p.DisplayRgb.G, p.DisplayRgb.B);
                    await Task.Delay(i == 0 ? 1200 : 500, verifyCts.Token); // settle (longer for the first patch)
                    results.Add(await colorimeter.MeasureAsync(p, ctx.HdrMode, verifyCts.Token));
                    if (ctx.CaptureSounds)
                        CalibrationSounds.PlayCapture();
                }
                _verifyMeasurements = results;

                // HDR PQ-TRACKING SWEEP: when the applied profile was built from the FP16
                // wire ladder, verify it the same way - wire-exact FP16 patches THROUGH the
                // profile, graded in absolute nits against the PQ spec. Only rungs inside
                // the corrected region (below the LUT's identity blend near the panel's
                // reachable peak) are graded; above it the LUT intentionally passes the
                // panel's own rolloff through.
                string? pqSummary = ctx.HdrMode
                    ? await RunPqTrackingSweepAsync(patchWindow, colorimeter, ctx, verifyCts.Token)
                    : null;

                var after = CalibrationVerifier.ComputeMetrics(results, ctx.Target);
                AfterAvgText.Text = $"{after.AverageDeltaE:F2}";
                AfterMaxText.Text = $"{after.MaxDeltaE:F2}";
                AfterGrayscaleText.Text = $"{after.AverageGrayscaleDeltaE:F2}";
                AfterPrimaryText.Text = $"{after.AveragePrimaryDeltaE:F2}";
                SetDeltaEColor(AfterAvgText, after.AverageDeltaE);
                SetDeltaEColor(AfterMaxText, after.MaxDeltaE);
                SetDeltaEColor(AfterGrayscaleText, after.AverageGrayscaleDeltaE);
                SetDeltaEColor(AfterPrimaryText, after.AveragePrimaryDeltaE);

                // The grade AND its summary sentence now reflect what the user actually
                // sees: the corrected display, not the uncorrected panel.
                var afterGrade = after.GetGrade();
                SetGradeDisplay(afterGrade);
                GradeScopeText.Text = "after correction";
                SummaryText.Text = GetSummaryText(afterGrade);
                PerceptualNotePanel.Visibility = Visibility.Visible;

                // Diagnostics line: tone/color decomposition answers "is the residual real?",
                // and ΔE ITP is the HDR-native metric for cross-tool comparison.
                VerifyDetailText.Text =
                    $"Grayscale residual split: tone {after.AverageGrayscaleToneDeltaE:F2} / color {after.AverageGrayscaleColorDeltaE:F2} ΔE2000 " +
                    $"(tone near black is mostly instrument noise; color is a visible cast). " +
                    $"ΔE ITP avg {after.AverageItpDeltaE:F1}, max {after.MaxItpDeltaE:F1} (BT.2124; ~3x ΔE2000 scale, 1 unit ≈ 1 JND).";
                if (pqSummary != null)
                    VerifyDetailText.Text += "\n" + pqSummary;
                VerifyDetailText.Visibility = Visibility.Visible;

                // Refresh recommendations with verify-aware guidance.
                PopulateRecommendations(afterGrade, after);
                StatusText.Text = $"Verified through the applied profile: average ΔE {after.AverageDeltaE:F2} " +
                                  $"({results.Count(r => r.IsValid)} of {patches.Count} patches).";

                // Overlay the corrected response on the charts: the native curves alone read
                // as alarming even when the corrected result is good.
                RenderCharts();

                CalibrationSounds.PlayCompletion();
                Log.Info($"CalibrationReportWindow: Verify pass avg dE {after.AverageDeltaE:F2}, " +
                         $"max {after.MaxDeltaE:F2}, gray {after.AverageGrayscaleDeltaE:F2}, primary {after.AveragePrimaryDeltaE:F2}.");
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Verification cancelled. Use Re-verify to run it again.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Verification failed:\n\n{ex.Message}",
                    "Verify Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _verifyCts = null;
                try { await colorimeter.EndMeasurementSessionAsync(); } catch { }
                patchWindow?.Close();
                if (bypassed)
                {
                    try { ctx.StateManager!.RestorePreviousState(); }
                    catch (Exception ex) { Log.Info($"CalibrationReportWindow: verify bypass restore failed: {ex.Message}"); }
                }
                VerifyButton.Content = "Re-verify";
                VerifyButton.IsEnabled = true;
                ApplyButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }
    }
}
