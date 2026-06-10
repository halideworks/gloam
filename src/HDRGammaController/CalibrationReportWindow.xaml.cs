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

        private void PopulateRecommendations(CalibrationGrade grade)
        {
            var recommendations = new List<string>();

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

            if (_characterization != null)
            {
                double cct = ColorMath.ChromaticityToCct(_characterization.WhitePoint);
                if (cct < 6000 || cct > 7000)
                {
                    recommendations.Add($"White point ({cct:F0}K) differs from D65 (6500K). Adjust OSD color temperature if targeting D65.");
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
        private async Task ApplyAndVerifyAsync()
        {
            if (_applyContext == null || _characterization == null)
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
                    ctx.Monitor, _characterization, ctx.Target,
                    ctx.LutR, ctx.LutG, ctx.LutB, ctx.WhiteLevel,
                    hdrMode: ctx.HdrMode, measurements: _measurements);

                if (result.Success)
                {
                    _profileApplied = true;
                    _installedProfileName = result.ProfileName;
                    _profileEnabled = true;
                    ApplyButton.Content = "Disable Profile";
                    ctx.OnInstalled?.Invoke(result.ProfileName);

                    StatusText.Text = "Profile applied - verifying through the correction…";
                    if (ctx.Colorimeter != null)
                        await RunVerificationAsync();
                    else
                        StatusText.Text = "Profile applied.";
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
                patchWindow.Show();

                var patches = CalibrationVerifier.BuildVerificationPatches();
                var results = new List<MeasurementResult>();
                await colorimeter.BeginMeasurementSessionAsync(hdrMode: ctx.HdrMode);
                for (int i = 0; i < patches.Count; i++)
                {
                    var p = patches[i];
                    VerifyButton.Content = $"Verifying {i + 1}/{patches.Count}…";
                    patchWindow.SetProgress(i + 1, patches.Count, p.Name);
                    patchWindow.SetColor(p.DisplayRgb.R, p.DisplayRgb.G, p.DisplayRgb.B);
                    await Task.Delay(i == 0 ? 1200 : 500); // settle (longer for the first patch)
                    results.Add(await colorimeter.MeasureAsync(p, ctx.HdrMode));
                    if (ctx.CaptureSounds)
                        CalibrationSounds.PlayCapture();
                }
                _verifyMeasurements = results;

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
                StatusText.Text = $"Verified through the applied profile: average ΔE {after.AverageDeltaE:F2} " +
                                  $"({results.Count(r => r.IsValid)} of {patches.Count} patches).";

                // Overlay the corrected response on the charts: the native curves alone read
                // as alarming even when the corrected result is good.
                RenderCharts();

                CalibrationSounds.PlayCompletion();
                Log.Info($"CalibrationReportWindow: Verify pass avg dE {after.AverageDeltaE:F2}, " +
                         $"max {after.MaxDeltaE:F2}, gray {after.AverageGrayscaleDeltaE:F2}, primary {after.AveragePrimaryDeltaE:F2}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Verification failed:\n\n{ex.Message}",
                    "Verify Calibration", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
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
