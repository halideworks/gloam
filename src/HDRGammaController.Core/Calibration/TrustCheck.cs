using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// The 6-patch ~20-second "trust check" (roadmap 4.3): white / mid-gray / black plus the
    /// three primaries, graded with the same metrics machinery as a full verification and
    /// appended to a per-monitor trend history so display aging becomes visible data instead
    /// of superstition. Pure math — the probe/window driving lives in the app project.
    /// </summary>
    public static class TrustCheck
    {
        /// <summary>
        /// The six patches. Black is measured but graded by LUMINANCE only — ΔE at black is
        /// metrologically meaningless (the colorimeter noise floor dominates and Lab blows up
        /// near zero), and reporting one would violate the honesty bar.
        /// </summary>
        public static IReadOnlyList<ColorPatch> BuildPatches()
        {
            var defs = new (string Name, double R, double G, double B, PatchCategory Cat)[]
            {
                ("White",    1.00, 1.00, 1.00, PatchCategory.Grayscale),
                ("Gray 40%", 0.40, 0.40, 0.40, PatchCategory.Grayscale),
                ("Black",    0.00, 0.00, 0.00, PatchCategory.Grayscale),
                ("Red",      1.00, 0.00, 0.00, PatchCategory.Primary),
                ("Green",    0.00, 1.00, 0.00, PatchCategory.Primary),
                ("Blue",     0.00, 0.00, 1.00, PatchCategory.Primary),
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

        public sealed record PatchGrade(
            string Name, double? DeltaE2000, double? DeltaEItp, double MeasuredYNits);

        public sealed record Grade(
            double AvgDeltaE2000,
            double WhiteDeltaE2000,
            double WhiteCctK,
            double WhiteDuv,
            double WhiteNits,
            double BlackNits,
            double? U95DeltaE,
            IReadOnlyList<PatchGrade> Patches);

        /// <summary>
        /// Grades the six measurements against the calibration target. The black patch is
        /// excluded from the ΔE grading (nits only); everything else goes through the shared
        /// <see cref="CalibrationVerifier.ComputeMetrics(IEnumerable{MeasurementResult}, CalibrationTarget, UncertaintyBudget.Context?, out UncertaintyBudget.Result?)"/>
        /// path so trust-check numbers are directly comparable with full-verification reports.
        /// </summary>
        public static Grade Compute(
            IReadOnlyList<MeasurementResult> measurements,
            CalibrationTarget target,
            UncertaintyBudget.Context? uncertaintyContext = null)
        {
            if (measurements == null) throw new ArgumentNullException(nameof(measurements));
            if (target == null) throw new ArgumentNullException(nameof(target));

            var valid = measurements.Where(m => m.IsValid).ToList();
            var black = valid.FirstOrDefault(IsBlackPatch);
            var graded = valid.Where(m => !IsBlackPatch(m)).ToList();
            if (graded.Count == 0)
                throw new InvalidOperationException("Trust check produced no gradable measurements.");

            var metrics = CalibrationVerifier.ComputeMetrics(
                graded, target, uncertaintyContext, out var uncertainty);

            var white = graded.FirstOrDefault(m =>
                m.Patch.Category == PatchCategory.Grayscale &&
                m.Patch.DisplayRgb.R >= 0.999 && m.Patch.DisplayRgb.G >= 0.999 && m.Patch.DisplayRgb.B >= 0.999);
            if (white == null)
                throw new InvalidOperationException("Trust check needs a valid white measurement.");

            var patchGrades = new List<PatchGrade>(valid.Count);
            foreach (var m in valid)
            {
                if (IsBlackPatch(m))
                {
                    patchGrades.Add(new PatchGrade(m.Patch.Name, null, null, m.Xyz.Y));
                    continue;
                }
                PatchDeltaE? result = metrics.PatchResults
                    .Where(p => p.Name == m.Patch.Name)
                    .Select(p => (PatchDeltaE?)p)
                    .FirstOrDefault();
                int idx = graded.IndexOf(m);
                double? itp = idx >= 0 && idx < metrics.ItpDeltaEs.Count
                    ? metrics.ItpDeltaEs[idx]
                    : (double?)null;
                patchGrades.Add(new PatchGrade(m.Patch.Name, result?.DeltaE, itp, m.Xyz.Y));
            }

            double whiteDeltaE = patchGrades.FirstOrDefault(p => p.Name == white.Patch.Name)?.DeltaE2000 ?? 0.0;

            return new Grade(
                AvgDeltaE2000: metrics.AverageDeltaE,
                WhiteDeltaE2000: whiteDeltaE,
                WhiteCctK: white.Cct,
                WhiteDuv: white.Duv,
                WhiteNits: white.Xyz.Y,
                BlackNits: black?.Xyz.Y ?? double.NaN,
                U95DeltaE: uncertainty?.ExpandedU,
                Patches: patchGrades);
        }

        private static bool IsBlackPatch(MeasurementResult m) =>
            m.Patch.Category == PatchCategory.Grayscale &&
            m.Patch.DisplayRgb.R <= 1e-9 && m.Patch.DisplayRgb.G <= 1e-9 && m.Patch.DisplayRgb.B <= 1e-9;
    }
}
