using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Post-apply verification: a quick patch sweep measured THROUGH the active correction
    /// (Windows applies the installed MHC2 profile to everything on screen, including a patch
    /// window), graded with the same ΔE2000 metric as the pre-calibration report. This is the
    /// honest "after" — re-measured reality, not a model prediction.
    /// </summary>
    public static class CalibrationVerifier
    {
        /// <summary>
        /// A small but representative sweep: grayscale ramp (white point + gamma tracking),
        /// the primaries and secondaries (gamut), and one memory-color mix. ~14 patches keeps
        /// the verify pass under a minute.
        /// </summary>
        public static IReadOnlyList<ColorPatch> BuildVerificationPatches()
        {
            var defs = new (string Name, double R, double G, double B, PatchCategory Cat)[]
            {
                ("White",     1.00, 1.00, 1.00, PatchCategory.Grayscale),
                ("Gray 80%",  0.80, 0.80, 0.80, PatchCategory.Grayscale),
                ("Gray 60%",  0.60, 0.60, 0.60, PatchCategory.Grayscale),
                ("Gray 40%",  0.40, 0.40, 0.40, PatchCategory.Grayscale),
                ("Gray 25%",  0.25, 0.25, 0.25, PatchCategory.Grayscale),
                ("Gray 10%",  0.10, 0.10, 0.10, PatchCategory.Grayscale),
                ("Black",     0.00, 0.00, 0.00, PatchCategory.Grayscale),
                ("Red",       1.00, 0.00, 0.00, PatchCategory.Primary),
                ("Green",     0.00, 1.00, 0.00, PatchCategory.Primary),
                ("Blue",      0.00, 0.00, 1.00, PatchCategory.Primary),
                ("Cyan",      0.00, 1.00, 1.00, PatchCategory.Secondary),
                ("Magenta",   1.00, 0.00, 1.00, PatchCategory.Secondary),
                ("Yellow",    1.00, 1.00, 0.00, PatchCategory.Secondary),
                ("Skin tone", 0.76, 0.57, 0.46, PatchCategory.SkinTone),
            };
            return defs.Select((p, i) => new ColorPatch
            {
                Name = p.Name,
                DisplayRgb = new LinearRgb(p.R, p.G, p.B),
                Category = p.Cat,
                Index = i,
                IsCritical = true,
            }).ToList();
        }

        /// <summary>
        /// ΔE2000 metrics for a set of measurements against a target — the same math and
        /// normalization as the calibration report's native-deviation numbers, so before and
        /// after are directly comparable. The colorimeter returns ABSOLUTE luminance; measured
        /// XYZ is normalized so the brightest patch maps to Y = 1, matching the target scale
        /// (white-point error then shows up correctly as a*/b* deviation).
        /// </summary>
        public static CalibrationMetrics ComputeMetrics(
            IEnumerable<MeasurementResult> measurements, CalibrationTarget target)
        {
            var metrics = new CalibrationMetrics();
            var deltaEs = new List<double>();

            // HDR wire-ladder patches (Patch.Nits) characterize the PQ pipeline for the LUT
            // builder; they are not accuracy patches. Including them would both add garbage
            // ΔEs (DisplayRgb is a placeholder) and blow up peakY normalization (the ladder
            // reaches far above SDR white), wrecking every number in the report.
            var valid = measurements.Where(IsAccuracyMeasurement).ToList();
            var whiteMeasurement = valid
                .Where(m => m.Patch.Category == PatchCategory.Grayscale &&
                            m.Patch.DisplayRgb.R >= 0.99 &&
                            m.Patch.DisplayRgb.G >= 0.99 &&
                            m.Patch.DisplayRgb.B >= 0.99)
                .OrderByDescending(m => m.Xyz.Y)
                .FirstOrDefault();
            double peakY = whiteMeasurement != null
                ? whiteMeasurement.Xyz.Y
                : valid.Count > 0 ? valid.Max(m => m.Xyz.Y) : 1.0;
            if (!double.IsFinite(peakY) || peakY <= 0) peakY = 1.0;

            // Lab reference white (m6): the TARGET's white, so for non-D65 targets a neutral
            // gray on the target white reads a* = b* = 0 at every luminance and the
            // tone/chroma decomposition below stays meaningful. All standard targets are D65,
            // where this is exactly the previous behavior (shared D65 constant reused
            // verbatim so D65 numbers are bit-identical).
            var labWhite = target.WhitePoint.Equals(Chromaticity.D65)
                ? ColorMath.D65White
                : target.WhitePoint.ToXyz(1.0);

            foreach (var measurement in valid)
            {
                var rgb = measurement.Patch.DisplayRgb;
                var targetXyz = target.LinearRgbToXyz(new LinearRgb(
                    LinearizePatchSignal(target, rgb.R),
                    LinearizePatchSignal(target, rgb.G),
                    LinearizePatchSignal(target, rgb.B)));
                var targetLab = ColorMath.XyzToLab(targetXyz, labWhite);
                var normalized = new CieXyz(
                    measurement.Xyz.X / peakY, measurement.Xyz.Y / peakY, measurement.Xyz.Z / peakY);
                var measuredLab = ColorMath.XyzToLab(normalized, labWhite);

                double deltaE = measuredLab.DeltaE2000(targetLab);
                if (!double.IsFinite(deltaE))
                    continue;

                deltaEs.Add(deltaE);
                metrics.PatchResults.Add(new PatchDeltaE(
                    measurement.Patch.Name, measurement.Patch.Category, deltaE));

                if (measurement.Patch.Category == PatchCategory.Grayscale)
                {
                    metrics.GrayscaleDeltaEs.Add(deltaE);

                    // Decompose the gray error: tone (lightness only) vs color (chroma only).
                    // Near-black "regressions" are usually tone-axis instrument noise; a
                    // chroma error at high luminance is a real, visible cast. Splitting the
                    // aggregate answers "is this real?" at a glance.
                    var lumOnly = new CieLab(measuredLab.L, targetLab.A, targetLab.B);
                    var chromaOnly = new CieLab(targetLab.L, measuredLab.A, measuredLab.B);
                    metrics.GrayscaleToneDeltaEs.Add(lumOnly.DeltaE2000(targetLab));
                    metrics.GrayscaleColorDeltaEs.Add(chromaOnly.DeltaE2000(targetLab));
                }
                else if (measurement.Patch.Category == PatchCategory.Primary)
                {
                    metrics.PrimaryDeltaEs.Add(deltaE);
                }

                // ΔE ITP (ITU-R BT.2124): the perceptual metric designed for HDR/PQ. It works
                // on ABSOLUTE luminance, so feed the un-normalized measurement and scale the
                // relative target by the measured peak. Reported alongside ΔE2000 — ITP values
                // run roughly 3× larger for equivalent perceptual error (1 JND ≈ 1 ΔE ITP).
                var targetAbs = new CieXyz(targetXyz.X * peakY, targetXyz.Y * peakY, targetXyz.Z * peakY);
                double deltaEItp = DeltaEItp(measurement.Xyz, targetAbs);
                if (double.IsFinite(deltaEItp))
                    metrics.ItpDeltaEs.Add(deltaEItp);
            }

            if (deltaEs.Count > 0)
            {
                metrics.AverageDeltaE = deltaEs.Average();
                metrics.MaxDeltaE = deltaEs.Max();
                metrics.MinDeltaE = deltaEs.Min();
                var sorted = deltaEs.OrderBy(v => v).ToList();
                int mid = sorted.Count / 2;
                metrics.MedianDeltaE = sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
            }

            return metrics;
        }

        /// <summary>
        /// The ONE authoritative signal→linear rule for grading SDR content patches against a
        /// calibration target. For PQ (HDR) targets the patches are still SDR content —
        /// Windows renders them with the sRGB curve scaled to the SDR white level — so THAT
        /// is the curve they are graded against; PQ-decoding the patch signal would compare
        /// against a curve nothing in the pipeline applies. Every producer of expected patch
        /// colors (this grader, <see cref="VerificationPatchSets.Detailed"/>) must call this
        /// instead of restating the rule.
        /// </summary>
        public static double LinearizePatchSignal(CalibrationTarget target, double signal) =>
            target.TransferFunction == TransferFunctionType.Pq
                ? ColorMath.SrgbEotf(signal)
                : target.ApplyEotf(signal);

        // ITU-R BT.2124 XYZ→LMS crosstalk matrix (the ICtCp cone basis).
        private static readonly double[,] XyzToLms =
        {
            {  0.3592, 0.6976, -0.0358 },
            { -0.1922, 1.1004,  0.0755 },
            {  0.0070, 0.0749,  0.8434 },
        };

        /// <summary>
        /// ΔE ITP per ITU-R BT.2124: XYZ (absolute nits) → LMS → PQ-encode → ICtCp, then
        /// 720·√(ΔI² + ΔT² + ΔP²) with T = 0.5·Ct. One unit ≈ one just-noticeable difference.
        /// Non-physical inputs return NaN (m3): a 0.0 sentinel reads as "perfect match" and
        /// survives IsFinite filters, silently dragging AverageItpDeltaE toward zero. NaN is
        /// excluded by the aggregation's IsFinite check in <see cref="ComputeMetrics"/>.
        /// </summary>
        public static double DeltaEItp(CieXyz a, CieXyz b)
        {
            if (!IsPhysicalXyz(a) || !IsPhysicalXyz(b))
                return double.NaN;

            var (i1, t1, p1) = ToItp(a);
            var (i2, t2, p2) = ToItp(b);
            double di = i1 - i2, dt = t1 - t2, dp = p1 - p2;
            return 720.0 * Math.Sqrt(di * di + dt * dt + dp * dp);
        }

        /// <summary>
        /// Grade of one colored HDR stimulus: ΔE ITP (BT.2124, absolute) between the
        /// measured XYZ and the Rec.2020-container reference, plus the luminance error as
        /// a fraction of the rung. Both are NaN for non-physical readings, following the
        /// NaN-not-sentinel convention of <see cref="DeltaEItp"/>.
        /// </summary>
        public sealed record ColoredHdrPatchGrade(
            string Name,
            double RungNits,
            double MeasuredY,
            double LuminanceError,
            double DeltaEItp);

        /// <summary>
        /// Aggregate for the colored HDR verification set. Reported SEPARATELY from the
        /// neutral sweep metrics: these stimuli are container-referred wide-gamut colors,
        /// so their errors describe the panel's HDR color rendering above SDR white and
        /// must not dilute (or be diluted by) the neutral grade and its thresholds.
        /// Averages/max cover finite ΔE ITP values only; non-physical readings are
        /// excluded and counted in <see cref="ExcludedCount"/>.
        /// </summary>
        public sealed record ColoredHdrMetrics(
            IReadOnlyList<ColoredHdrPatchGrade> Patches,
            double AverageItpDeltaE,
            double MaxItpDeltaE,
            string? WorstPatchName,
            double AverageAbsLuminanceError,
            int GradedCount,
            int ExcludedCount);

        /// <summary>
        /// Grades colored HDR stimuli against their container references. Pure function:
        /// pairs of (stimulus, measured absolute XYZ) in, per-patch grades and the
        /// ΔE ITP average/max (plus worst patch) out. When no reading is physical the
        /// aggregates are NaN and <see cref="ColoredHdrMetrics.WorstPatchName"/> is null.
        /// </summary>
        public static ColoredHdrMetrics GradeColoredHdr(
            IEnumerable<(ColoredHdrStimulus Stimulus, CieXyz MeasuredXyz)> readings)
        {
            var patches = new List<ColoredHdrPatchGrade>();
            var finiteItps = new List<(string Name, double Itp)>();
            var finiteLumErrors = new List<double>();
            int excluded = 0;

            foreach (var (stimulus, measured) in readings)
            {
                double itp = DeltaEItp(measured, stimulus.ReferenceXyz);
                double lumError = IsPhysicalXyz(measured)
                    ? (measured.Y - stimulus.RungNits) / stimulus.RungNits
                    : double.NaN;
                patches.Add(new ColoredHdrPatchGrade(
                    stimulus.Name, stimulus.RungNits, measured.Y, lumError, itp));

                if (double.IsFinite(itp))
                {
                    finiteItps.Add((stimulus.Name, itp));
                    if (double.IsFinite(lumError))
                        finiteLumErrors.Add(System.Math.Abs(lumError));
                }
                else
                {
                    excluded++;
                }
            }

            if (finiteItps.Count == 0)
            {
                return new ColoredHdrMetrics(
                    patches, double.NaN, double.NaN, null, double.NaN, 0, excluded);
            }

            var worst = finiteItps.OrderByDescending(p => p.Itp).First();
            return new ColoredHdrMetrics(
                patches,
                finiteItps.Average(p => p.Itp),
                worst.Itp,
                worst.Name,
                finiteLumErrors.Count > 0 ? finiteLumErrors.Average() : double.NaN,
                finiteItps.Count,
                excluded);
        }

        private static bool IsAccuracyMeasurement(MeasurementResult measurement) =>
            measurement.IsValid &&
            measurement.Patch.Nits is null &&
            IsPhysicalXyz(measurement.Xyz);

        private static bool IsPhysicalXyz(CieXyz xyz) =>
            double.IsFinite(xyz.X) && double.IsFinite(xyz.Y) && double.IsFinite(xyz.Z) &&
            xyz.X >= -1e-6 && xyz.Y >= -1e-6 && xyz.Z >= -1e-6;

        private static (double I, double T, double P) ToItp(CieXyz xyz)
        {
            double l = XyzToLms[0, 0] * xyz.X + XyzToLms[0, 1] * xyz.Y + XyzToLms[0, 2] * xyz.Z;
            double m = XyzToLms[1, 0] * xyz.X + XyzToLms[1, 1] * xyz.Y + XyzToLms[1, 2] * xyz.Z;
            double s = XyzToLms[2, 0] * xyz.X + XyzToLms[2, 1] * xyz.Y + XyzToLms[2, 2] * xyz.Z;

            double lp = TransferFunctions.PqInverseEotf(Math.Max(l, 0));
            double mp = TransferFunctions.PqInverseEotf(Math.Max(m, 0));
            double sp = TransferFunctions.PqInverseEotf(Math.Max(s, 0));

            double i = 0.5 * lp + 0.5 * mp;
            double ct = (6610.0 * lp - 13613.0 * mp + 7003.0 * sp) / 4096.0;
            double cp = (17933.0 * lp - 17390.0 * mp - 543.0 * sp) / 4096.0;
            return (i, 0.5 * ct, cp);
        }
    }
}
