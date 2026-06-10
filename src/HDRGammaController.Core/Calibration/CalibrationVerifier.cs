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

            var valid = measurements.Where(m => m.IsValid).ToList();
            double peakY = valid.Count > 0 ? valid.Max(m => m.Xyz.Y) : 1.0;
            if (peakY <= 0) peakY = 1.0;

            // Tone reference for the patches. For PQ (HDR) targets the patches are still SDR
            // content — Windows renders them with the sRGB curve scaled to the SDR white
            // level — so THAT is the curve they should be graded against. PQ-decoding the
            // patch signal would compare against a curve nothing in the pipeline applies.
            double Linearize(double s) => target.TransferFunction == TransferFunctionType.Pq
                ? ColorMath.SrgbEotf(s)
                : target.ApplyEotf(s);

            foreach (var measurement in valid)
            {
                var rgb = measurement.Patch.DisplayRgb;
                var targetXyz = target.LinearRgbToXyz(new LinearRgb(
                    Linearize(rgb.R), Linearize(rgb.G), Linearize(rgb.B)));
                var targetLab = ColorMath.XyzToLab(targetXyz);
                var normalized = new CieXyz(
                    measurement.Xyz.X / peakY, measurement.Xyz.Y / peakY, measurement.Xyz.Z / peakY);
                var measuredLab = ColorMath.XyzToLab(normalized);

                double deltaE = measuredLab.DeltaE2000(targetLab);
                deltaEs.Add(deltaE);

                if (measurement.Patch.Category == PatchCategory.Grayscale)
                    metrics.GrayscaleDeltaEs.Add(deltaE);
                else if (measurement.Patch.Category == PatchCategory.Primary)
                    metrics.PrimaryDeltaEs.Add(deltaE);
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
    }
}
