using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Comprehensive calibration report with detailed metrics, analysis, and graph data.
    /// Designed to support extensive visualization and before/after comparison.
    /// </summary>
    /// <remarks>
    /// This report structure supports:
    /// - Before/after measurement comparison
    /// - Delta E analysis by category
    /// - Grayscale tracking graphs
    /// - Gamma response curves
    /// - Chromaticity diagrams (CIE xy and u'v')
    /// - Luminance response
    /// - Color temperature consistency
    /// - Individual patch analysis
    /// </remarks>
    public class CalibrationReport
    {
        #region Identification

        /// <summary>
        /// Unique identifier for this report.
        /// </summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>
        /// UTC timestamp when calibration was performed.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Monitor device path this calibration applies to.
        /// </summary>
        public required string MonitorDevicePath { get; init; }

        /// <summary>
        /// Human-readable monitor name (from EDID).
        /// </summary>
        public required string MonitorName { get; init; }

        /// <summary>
        /// The calibration target that was used.
        /// </summary>
        public required CalibrationTarget Target { get; init; }

        /// <summary>
        /// Colorimeter model used (e.g., "i1 Display Plus").
        /// </summary>
        public required string ColorimeterModel { get; init; }

        /// <summary>
        /// Software version that generated this report.
        /// </summary>
        public string? SoftwareVersion { get; init; }

        /// <summary>
        /// User-provided notes about this calibration.
        /// </summary>
        public string? UserNotes { get; init; }

        /// <summary>
        /// Profile version number (incremented for each new calibration).
        /// </summary>
        public int ProfileVersion { get; init; } = 1;

        #endregion

        #region Raw Measurements

        /// <summary>
        /// All raw measurement results from the calibration session.
        /// </summary>
        public required IReadOnlyList<MeasurementResult> Measurements { get; init; }

        /// <summary>
        /// Pre-calibration verification measurements (before applying LUT).
        /// Used for before/after comparison.
        /// </summary>
        public IReadOnlyList<MeasurementResult>? PreCalibrationMeasurements { get; init; }

        /// <summary>
        /// Post-calibration verification measurements (after applying LUT).
        /// Used for before/after comparison and validation.
        /// </summary>
        public IReadOnlyList<MeasurementResult>? PostCalibrationMeasurements { get; init; }

        #endregion

        #region Display Characteristics

        /// <summary>
        /// Measured display characteristics (primaries, white point, gamma).
        /// </summary>
        public required DisplayCharacteristics MeasuredCharacteristics { get; init; }

        #endregion

        #region Summary Statistics

        /// <summary>
        /// Overall Delta E statistics for all measured patches.
        /// </summary>
        public DeltaEStatistics OverallDeltaE => CalculateDeltaEStats(Measurements);

        /// <summary>
        /// Delta E statistics for grayscale patches only.
        /// </summary>
        public DeltaEStatistics GrayscaleDeltaE =>
            CalculateDeltaEStats(Measurements.Where(m => m.Patch.Category == PatchCategory.Grayscale).ToList());

        /// <summary>
        /// Delta E statistics for primary colors only.
        /// </summary>
        public DeltaEStatistics PrimaryDeltaE =>
            CalculateDeltaEStats(Measurements.Where(m => m.Patch.Category == PatchCategory.Primary).ToList());

        /// <summary>
        /// Delta E statistics for saturated colors.
        /// </summary>
        public DeltaEStatistics SaturatedDeltaE =>
            CalculateDeltaEStats(Measurements.Where(m => m.Patch.Category == PatchCategory.Saturated).ToList());

        /// <summary>
        /// Pre-calibration Delta E (if verification measurements exist).
        /// </summary>
        public DeltaEStatistics? PreCalibrationDeltaE =>
            PreCalibrationMeasurements != null ? CalculateDeltaEStats(PreCalibrationMeasurements) : null;

        /// <summary>
        /// Post-calibration Delta E (if verification measurements exist).
        /// </summary>
        public DeltaEStatistics? PostCalibrationDeltaE =>
            PostCalibrationMeasurements != null ? CalculateDeltaEStats(PostCalibrationMeasurements) : null;

        /// <summary>
        /// Delta E improvement from pre to post calibration.
        /// </summary>
        public double? DeltaEImprovement =>
            PreCalibrationDeltaE != null && PostCalibrationDeltaE != null
                ? PreCalibrationDeltaE.Average - PostCalibrationDeltaE.Average
                : null;

        /// <summary>
        /// Percentage improvement in Delta E.
        /// </summary>
        public double? DeltaEImprovementPercent =>
            PreCalibrationDeltaE != null && PostCalibrationDeltaE != null && PreCalibrationDeltaE.Average > 0
                ? (PreCalibrationDeltaE.Average - PostCalibrationDeltaE.Average) / PreCalibrationDeltaE.Average * 100
                : null;

        #endregion

        #region Graph Data Points

        /// <summary>
        /// Grayscale tracking data for graphing (input level vs measured luminance).
        /// </summary>
        public IReadOnlyList<GrayscaleTrackingPoint> GrayscaleTracking =>
            ExtractGrayscaleTracking();

        /// <summary>
        /// Gamma response curve data (input level vs measured gamma at that level).
        /// </summary>
        public IReadOnlyList<GammaResponsePoint> GammaResponse =>
            ExtractGammaResponse();

        /// <summary>
        /// RGB channel balance data (input level vs RGB ratio).
        /// </summary>
        public IReadOnlyList<RgbBalancePoint> RgbBalance =>
            ExtractRgbBalance();

        /// <summary>
        /// Chromaticity coordinates for graphing on CIE diagram.
        /// </summary>
        public IReadOnlyList<ChromaticityPoint> ChromaticityPoints =>
            ExtractChromaticityPoints();

        /// <summary>
        /// Color temperature vs input level for grayscale.
        /// </summary>
        public IReadOnlyList<ColorTemperaturePoint> ColorTemperatureTracking =>
            ExtractColorTemperatureTracking();

        #endregion

        #region Quality Assessment

        /// <summary>
        /// Overall calibration quality grade (A+ to F).
        /// </summary>
        public CalibrationGrade QualityGrade => CalculateQualityGrade();

        /// <summary>
        /// Detailed quality assessment with individual metrics.
        /// </summary>
        public QualityAssessment Quality => CalculateQuality();

        /// <summary>
        /// List of issues or warnings found during calibration.
        /// </summary>
        public IReadOnlyList<CalibrationIssue> Issues => DetectIssues();

        #endregion

        #region Helper Methods

        private DeltaEStatistics CalculateDeltaEStats(IReadOnlyList<MeasurementResult> measurements)
        {
            if (measurements.Count == 0)
                return new DeltaEStatistics();

            // The colorimeter returns ABSOLUTE luminance (white Y can be ~100–120 cd/m²), but
            // the patch targets are normalized (white Y = 1). Computing Lab on raw absolute XYZ
            // pushes L* far past 100 and makes Δ E meaningless. Normalize measured XYZ so the
            // measured white maps to Y = 1, matching the target scale — the white-point error
            // then shows up correctly as an a*/b* deviation rather than a luminance blowout.
            double peakY = measurements.Max(m => m.Xyz.Y);
            if (peakY <= 0) peakY = 1;

            var deltaEs = new List<double>();
            foreach (var m in measurements)
            {
                var normalized = new CieXyz(m.Xyz.X / peakY, m.Xyz.Y / peakY, m.Xyz.Z / peakY);
                var measuredLab = ColorMath.XyzToLab(normalized);
                if (m.Patch.TargetXyz != null)
                    deltaEs.Add(measuredLab.DeltaE2000(ColorMath.XyzToLab(m.Patch.TargetXyz.Value)));
                else if (m.Patch.TargetLab != null)
                    deltaEs.Add(measuredLab.DeltaE2000(m.Patch.TargetLab.Value));
            }

            if (deltaEs.Count == 0)
                return new DeltaEStatistics();

            deltaEs.Sort();
            double sum = deltaEs.Sum();
            double avg = sum / deltaEs.Count;
            double variance = deltaEs.Sum(d => (d - avg) * (d - avg)) / deltaEs.Count;

            return new DeltaEStatistics
            {
                Count = deltaEs.Count,
                Minimum = deltaEs[0],
                Maximum = deltaEs[^1],
                Average = avg,
                Median = deltaEs[deltaEs.Count / 2],
                Percentile95 = deltaEs[(int)(deltaEs.Count * 0.95)],
                StandardDeviation = Math.Sqrt(variance)
            };
        }

        private IReadOnlyList<GrayscaleTrackingPoint> ExtractGrayscaleTracking()
        {
            var points = new List<GrayscaleTrackingPoint>();
            var grayscaleMeasurements = Measurements
                .Where(m => m.Patch.Category == PatchCategory.Grayscale)
                .OrderBy(m => m.Patch.DisplayRgb.R);

            foreach (var m in grayscaleMeasurements)
            {
                double inputLevel = m.Patch.DisplayRgb.R; // Assumes R=G=B for grayscale
                double targetLuminance = m.Patch.TargetXyz?.Y ?? (inputLevel * MeasuredCharacteristics.PeakLuminance);

                points.Add(new GrayscaleTrackingPoint
                {
                    InputLevel = inputLevel,
                    MeasuredLuminance = m.Luminance,
                    TargetLuminance = targetLuminance,
                    MeasuredCct = m.Cct,
                    TargetCct = ColorMath.ChromaticityToCct(Target.WhitePoint),
                    Duv = m.Duv
                });
            }

            return points;
        }

        private IReadOnlyList<GammaResponsePoint> ExtractGammaResponse()
        {
            var points = new List<GammaResponsePoint>();
            var grayscaleMeasurements = Measurements
                .Where(m => m.Patch.Category == PatchCategory.Grayscale && m.Patch.DisplayRgb.R > 0.01)
                .OrderBy(m => m.Patch.DisplayRgb.R);

            double maxLuminance = Measurements
                .Where(m => m.Patch.Category == PatchCategory.Grayscale)
                .Max(m => m.Luminance);

            foreach (var m in grayscaleMeasurements)
            {
                double inputLevel = m.Patch.DisplayRgb.R;
                double normalizedOutput = maxLuminance > 0 ? m.Luminance / maxLuminance : 0;

                // Calculate effective gamma at this point: gamma = log(output) / log(input)
                double effectiveGamma = inputLevel > 0.01 && normalizedOutput > 0.001
                    ? Math.Log(normalizedOutput) / Math.Log(inputLevel)
                    : Target.Gamma ?? 2.2;

                points.Add(new GammaResponsePoint
                {
                    InputLevel = inputLevel,
                    NormalizedOutput = normalizedOutput,
                    EffectiveGamma = effectiveGamma,
                    TargetGamma = Target.Gamma ?? 2.2
                });
            }

            return points;
        }

        private IReadOnlyList<RgbBalancePoint> ExtractRgbBalance()
        {
            var points = new List<RgbBalancePoint>();
            var grayscaleMeasurements = Measurements
                .Where(m => m.Patch.Category == PatchCategory.Grayscale)
                .OrderBy(m => m.Patch.DisplayRgb.R);

            // We need to estimate RGB from XYZ - this requires the display's color matrix
            var displayMatrix = MeasuredCharacteristics.XyzToRgbMatrix;

            foreach (var m in grayscaleMeasurements)
            {
                double inputLevel = m.Patch.DisplayRgb.R;

                // Convert measured XYZ to display RGB space
                double r = displayMatrix[0, 0] * m.Xyz.X + displayMatrix[0, 1] * m.Xyz.Y + displayMatrix[0, 2] * m.Xyz.Z;
                double g = displayMatrix[1, 0] * m.Xyz.X + displayMatrix[1, 1] * m.Xyz.Y + displayMatrix[1, 2] * m.Xyz.Z;
                double b = displayMatrix[2, 0] * m.Xyz.X + displayMatrix[2, 1] * m.Xyz.Y + displayMatrix[2, 2] * m.Xyz.Z;

                // Normalize to max = 1
                double max = Math.Max(r, Math.Max(g, b));
                if (max > 0) { r /= max; g /= max; b /= max; }

                points.Add(new RgbBalancePoint
                {
                    InputLevel = inputLevel,
                    RedRatio = r,
                    GreenRatio = g,
                    BlueRatio = b
                });
            }

            return points;
        }

        private IReadOnlyList<ChromaticityPoint> ExtractChromaticityPoints()
        {
            return Measurements.Select(m => new ChromaticityPoint
            {
                Name = m.Patch.Name,
                Category = m.Patch.Category,
                Measured = m.Chromaticity,
                Target = m.Patch.TargetXyz?.ToChromaticity()
            }).ToList();
        }

        private IReadOnlyList<ColorTemperaturePoint> ExtractColorTemperatureTracking()
        {
            var points = new List<ColorTemperaturePoint>();
            var grayscaleMeasurements = Measurements
                .Where(m => m.Patch.Category == PatchCategory.Grayscale)
                .OrderBy(m => m.Patch.DisplayRgb.R);

            double targetCct = ColorMath.ChromaticityToCct(Target.WhitePoint);

            foreach (var m in grayscaleMeasurements)
            {
                points.Add(new ColorTemperaturePoint
                {
                    InputLevel = m.Patch.DisplayRgb.R,
                    MeasuredCct = m.Cct,
                    TargetCct = targetCct,
                    Duv = m.Duv
                });
            }

            return points;
        }

        private CalibrationGrade CalculateQualityGrade()
        {
            var stats = OverallDeltaE;

            // Grade based on average Delta E 2000
            // Professional reference: <1 is excellent, <2 is very good, <3 is good
            return stats.Average switch
            {
                < 0.5 => CalibrationGrade.APLus,
                < 1.0 => CalibrationGrade.A,
                < 1.5 => CalibrationGrade.AMinus,
                < 2.0 => CalibrationGrade.BPlus,
                < 2.5 => CalibrationGrade.B,
                < 3.0 => CalibrationGrade.BMinus,
                < 4.0 => CalibrationGrade.CPlus,
                < 5.0 => CalibrationGrade.C,
                < 6.0 => CalibrationGrade.CMinus,
                < 8.0 => CalibrationGrade.D,
                _ => CalibrationGrade.F
            };
        }

        private QualityAssessment CalculateQuality()
        {
            var grayscale = GrayscaleDeltaE;
            var primary = PrimaryDeltaE;
            var saturated = SaturatedDeltaE;
            var overall = OverallDeltaE;

            // Calculate gamma tracking quality
            var gammaPoints = GammaResponse;
            double gammaVariance = 0;
            if (gammaPoints.Count > 0)
            {
                double avgGamma = gammaPoints.Average(p => p.EffectiveGamma);
                gammaVariance = gammaPoints.Sum(p => (p.EffectiveGamma - avgGamma) * (p.EffectiveGamma - avgGamma)) / gammaPoints.Count;
            }

            // Calculate white point accuracy
            var whiteMeasurement = Measurements.FirstOrDefault(m =>
                m.Patch.Category == PatchCategory.Grayscale && m.Patch.DisplayRgb.R > 0.99);
            double whitePointError = whiteMeasurement != null
                ? whiteMeasurement.Chromaticity.DistanceTo(Target.WhitePoint) * 1000 // Convert to practical units
                : 0;

            return new QualityAssessment
            {
                OverallScore = Math.Max(0, 100 - overall.Average * 10),
                GrayscaleScore = Math.Max(0, 100 - grayscale.Average * 15),
                ColorAccuracyScore = Math.Max(0, 100 - saturated.Average * 8),
                GammaTrackingScore = Math.Max(0, 100 - Math.Sqrt(gammaVariance) * 50),
                WhitePointScore = Math.Max(0, 100 - whitePointError * 5),
                ContrastRatio = MeasuredCharacteristics.ContrastRatio
            };
        }

        private IReadOnlyList<CalibrationIssue> DetectIssues()
        {
            var issues = new List<CalibrationIssue>();

            // Normalize measured XYZ to white Y = 1 before Lab/ΔE (targets are normalized);
            // comparing absolute measured Lab (white L* ≈ 559) to normalized targets otherwise
            // flags every critical patch as a huge false-positive ΔE. Mirrors CalculateDeltaEStats.
            double peakY = Measurements.Count > 0 ? Measurements.Max(m => m.Xyz.Y) : 1.0;
            if (peakY <= 0) peakY = 1.0;

            // Check for high Delta E in critical patches
            foreach (var m in Measurements.Where(m => m.Patch.IsCritical))
            {
                if (m.Patch.TargetXyz != null)
                {
                    var normalized = new CieXyz(m.Xyz.X / peakY, m.Xyz.Y / peakY, m.Xyz.Z / peakY);
                    var measuredLab = ColorMath.XyzToLab(normalized);
                    double deltaE = measuredLab.DeltaE2000(ColorMath.XyzToLab(m.Patch.TargetXyz.Value));
                    if (deltaE > 3.0)
                    {
                        issues.Add(new CalibrationIssue
                        {
                            Severity = deltaE > 5.0 ? IssueSeverity.Error : IssueSeverity.Warning,
                            Category = IssueCategory.ColorAccuracy,
                            Message = $"High Delta E ({deltaE:F1}) on critical patch: {m.Patch.Name}",
                            Details = $"Measured: {measuredLab}, Target: {ColorMath.XyzToLab(m.Patch.TargetXyz.Value)}"
                        });
                    }
                }
            }

            // Check white point accuracy
            var whiteMeasurement = Measurements.FirstOrDefault(m =>
                m.Patch.Category == PatchCategory.Grayscale && m.Patch.DisplayRgb.R > 0.99);
            if (whiteMeasurement != null)
            {
                double cctError = Math.Abs(whiteMeasurement.Cct - ColorMath.ChromaticityToCct(Target.WhitePoint));
                if (cctError > 500)
                {
                    issues.Add(new CalibrationIssue
                    {
                        Severity = cctError > 1000 ? IssueSeverity.Error : IssueSeverity.Warning,
                        Category = IssueCategory.WhitePoint,
                        Message = $"White point CCT error: {cctError:F0}K",
                        Details = $"Measured: {whiteMeasurement.Cct:F0}K, Target: {ColorMath.ChromaticityToCct(Target.WhitePoint):F0}K"
                    });
                }

                if (Math.Abs(whiteMeasurement.Duv) > 0.005)
                {
                    issues.Add(new CalibrationIssue
                    {
                        Severity = Math.Abs(whiteMeasurement.Duv) > 0.01 ? IssueSeverity.Error : IssueSeverity.Warning,
                        Category = IssueCategory.WhitePoint,
                        Message = $"White point has {(whiteMeasurement.Duv > 0 ? "green" : "magenta")} tint (Duv: {whiteMeasurement.Duv:F4})",
                        Details = "Duv should be within ±0.005 for accurate white point"
                    });
                }
            }

            // Check gamma consistency
            var gammaPoints = GammaResponse;
            if (gammaPoints.Count > 5)
            {
                double avgGamma = gammaPoints.Skip(1).Average(p => p.EffectiveGamma); // Skip near-black
                double maxDeviation = gammaPoints.Skip(1).Max(p => Math.Abs(p.EffectiveGamma - avgGamma));
                if (maxDeviation > 0.3)
                {
                    issues.Add(new CalibrationIssue
                    {
                        Severity = maxDeviation > 0.5 ? IssueSeverity.Error : IssueSeverity.Warning,
                        Category = IssueCategory.GammaTracking,
                        Message = $"Inconsistent gamma tracking (deviation: {maxDeviation:F2})",
                        Details = $"Average gamma: {avgGamma:F2}, max deviation: {maxDeviation:F2}"
                    });
                }
            }

            // Check for low contrast ratio
            if (MeasuredCharacteristics.ContrastRatio < 500)
            {
                issues.Add(new CalibrationIssue
                {
                    Severity = MeasuredCharacteristics.ContrastRatio < 200 ? IssueSeverity.Error : IssueSeverity.Warning,
                    Category = IssueCategory.ContrastRatio,
                    Message = $"Low contrast ratio: {MeasuredCharacteristics.ContrastRatio:F0}:1",
                    Details = "This may indicate measurement issues or a poorly performing display"
                });
            }

            return issues;
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Delta E statistics for a set of measurements.
    /// </summary>
    public class DeltaEStatistics
    {
        public int Count { get; init; }
        public double Minimum { get; init; }
        public double Maximum { get; init; }
        public double Average { get; init; }
        public double Median { get; init; }
        public double Percentile95 { get; init; }
        public double StandardDeviation { get; init; }

        public override string ToString() =>
            $"ΔE2000: avg={Average:F2}, min={Minimum:F2}, max={Maximum:F2}, 95%={Percentile95:F2}";
    }

    /// <summary>
    /// Measured display characteristics.
    /// </summary>
    public class DisplayCharacteristics
    {
        public required Chromaticity MeasuredRed { get; init; }
        public required Chromaticity MeasuredGreen { get; init; }
        public required Chromaticity MeasuredBlue { get; init; }
        public required Chromaticity MeasuredWhite { get; init; }
        public double MeasuredGamma { get; init; }
        public double PeakLuminance { get; init; }
        public double BlackLevel { get; init; }
        public double ContrastRatio => BlackLevel > 0 ? PeakLuminance / BlackLevel : double.PositiveInfinity;
        public double MeasuredCct => ColorMath.ChromaticityToCct(MeasuredWhite);
        public double MeasuredDuv => ColorMath.CalculateDuv(MeasuredWhite);

        /// <summary>
        /// Gets the RGB to XYZ matrix based on measured primaries.
        /// JsonIgnore: derived from the serialized primaries, and System.Text.Json
        /// cannot serialize double[,] (it broke report-snapshot persistence).
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double[,] RgbToXyzMatrix =>
            ColorMath.CalculateRgbToXyzMatrix(MeasuredRed, MeasuredGreen, MeasuredBlue, MeasuredWhite);

        /// <summary>
        /// Gets the XYZ to RGB matrix based on measured primaries.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double[,] XyzToRgbMatrix => ColorMath.Invert3x3(RgbToXyzMatrix);
    }

    /// <summary>
    /// Data point for grayscale tracking graph.
    /// </summary>
    public class GrayscaleTrackingPoint
    {
        public double InputLevel { get; init; }
        public double MeasuredLuminance { get; init; }
        public double TargetLuminance { get; init; }
        public double MeasuredCct { get; init; }
        public double TargetCct { get; init; }
        public double Duv { get; init; }
        public double LuminanceError => TargetLuminance > 0 ? (MeasuredLuminance - TargetLuminance) / TargetLuminance * 100 : 0;
    }

    /// <summary>
    /// Data point for gamma response graph.
    /// </summary>
    public class GammaResponsePoint
    {
        public double InputLevel { get; init; }
        public double NormalizedOutput { get; init; }
        public double EffectiveGamma { get; init; }
        public double TargetGamma { get; init; }
        public double GammaError => EffectiveGamma - TargetGamma;
    }

    /// <summary>
    /// Data point for RGB balance graph.
    /// </summary>
    public class RgbBalancePoint
    {
        public double InputLevel { get; init; }
        public double RedRatio { get; init; }
        public double GreenRatio { get; init; }
        public double BlueRatio { get; init; }
    }

    /// <summary>
    /// Chromaticity point for CIE diagram.
    /// </summary>
    public class ChromaticityPoint
    {
        public required string Name { get; init; }
        public PatchCategory Category { get; init; }
        public required Chromaticity Measured { get; init; }
        public Chromaticity? Target { get; init; }
    }

    /// <summary>
    /// Color temperature tracking point.
    /// </summary>
    public class ColorTemperaturePoint
    {
        public double InputLevel { get; init; }
        public double MeasuredCct { get; init; }
        public double TargetCct { get; init; }
        public double Duv { get; init; }
        public double CctError => MeasuredCct - TargetCct;
    }

    /// <summary>
    /// Quality assessment scores (0-100 scale).
    /// </summary>
    public class QualityAssessment
    {
        public double OverallScore { get; init; }
        public double GrayscaleScore { get; init; }
        public double ColorAccuracyScore { get; init; }
        public double GammaTrackingScore { get; init; }
        public double WhitePointScore { get; init; }
        public double ContrastRatio { get; init; }
    }

    /// <summary>
    /// Calibration quality grade.
    /// </summary>
    public enum CalibrationGrade
    {
        APLus,
        A,
        AMinus,
        BPlus,
        B,
        BMinus,
        CPlus,
        C,
        CMinus,
        D,
        F
    }

    /// <summary>
    /// An issue or warning detected during calibration.
    /// </summary>
    public class CalibrationIssue
    {
        public required IssueSeverity Severity { get; init; }
        public required IssueCategory Category { get; init; }
        public required string Message { get; init; }
        public string? Details { get; init; }
    }

    public enum IssueSeverity
    {
        Info,
        Warning,
        Error
    }

    public enum IssueCategory
    {
        ColorAccuracy,
        WhitePoint,
        GammaTracking,
        ContrastRatio,
        Measurement,
        Hardware
    }

    #endregion
}
