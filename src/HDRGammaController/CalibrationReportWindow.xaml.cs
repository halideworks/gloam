using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using HDRGammaController.Core.Calibration;
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
            Lut3D? correctionLut = null)
        {
            InitializeComponent();

            _profile = profile;
            _metrics = metrics;
            _characterization = characterization;
            _correctionLut = correctionLut;

            PopulateReport();
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

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            // This would integrate with the main application to apply the profile
            MessageBox.Show("Profile application would be triggered here.\n\n" +
                "This will integrate with the main LUT pipeline to apply the calibration.",
                "Apply Profile", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
