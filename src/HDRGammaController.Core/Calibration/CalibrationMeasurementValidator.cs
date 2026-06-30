using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Coarse physical sanity checks for colorimeter measurement sets. These gates catch
    /// broken sessions before they become installed profiles: covered sensors, stale reads,
    /// non-monotonic grayscale, missing white/black anchors, or impossible primaries.
    /// </summary>
    public static class CalibrationMeasurementValidator
    {
        public readonly record struct Result(bool IsValid, string? Error)
        {
            public static Result Ok() => new(true, null);
            public static Result Fail(string error) => new(false, error);
        }

        public static string BuildRecoveryText(Result result)
        {
            if (result.IsValid)
                return "Measurement set passed integrity checks: patch count, luminance range, anchors, grayscale monotonicity and primary chromaticities are plausible.";

            string error = result.Error ?? "Measurement validation failed.";
            string normalized = error.ToLowerInvariant();
            string recovery;

            if (normalized.Contains("no measurements") ||
                normalized.Contains("too few") ||
                normalized.Contains("missing a valid white") ||
                normalized.Contains("missing a valid black") ||
                normalized.Contains("at least five valid grayscale"))
            {
                recovery = "Re-run calibration from the beginning and let the full patch sequence complete without skipping failed reads.";
            }
            else if (normalized.Contains("wire-ladder"))
            {
                recovery = "Re-run HDR calibration with the HDR patch window visible on the measured display, and confirm Windows HDR stays enabled for the entire PQ sweep.";
            }
            else if (normalized.Contains("near-black") ||
                     normalized.Contains("measured near black") ||
                     normalized.Contains("not meaningfully brighter"))
            {
                recovery = "Check that the meter is flush on the active patch, the patch window is visible on the measured display and the display is not blanked or covered.";
            }
            else if (normalized.Contains("almost no luminance range") ||
                     normalized.Contains("stale") ||
                     normalized.Contains("repeated readings"))
            {
                recovery = "Restart the meter session, keep the patch window focused and disable overlays or power-saving behavior that could freeze the probe readings.";
            }
            else if (normalized.Contains("white") ||
                     normalized.Contains("drifted") ||
                     normalized.Contains("changed chromaticity"))
            {
                recovery = "Let the display warm up, disable dynamic brightness/local dimming while measuring and keep ambient light stable before retrying.";
            }
            else if (normalized.Contains("non-monotonic"))
            {
                recovery = "Re-run after disabling dynamic contrast, adaptive brightness, VRR/black-frame insertion and any vendor enhancement that can change tone response between patches.";
            }
            else if (normalized.Contains("impossible chromaticity"))
            {
                recovery = "Confirm the meter spectral correction matches the panel, remove ICC/GPU corrections before measuring and verify the color patch is not being color-managed by another app.";
            }
            else
            {
                recovery = "Fix the measurement setup, then re-run calibration before installing a profile.";
            }

            return $"{error} {recovery}";
        }

        public static Result ValidateForProfile(
            IEnumerable<MeasurementResult>? measurements,
            CalibrationTarget target,
            bool hdrMode)
        {
            if (measurements == null)
                return Result.Fail("No measurements were provided.");

            var valid = measurements
                .Where(m => m.IsValid && m.Patch.Nits is null)
                .ToList();
            if (valid.Count < 10)
                return Result.Fail(
                    $"Only {valid.Count} valid accuracy measurements were available - too few to build a trustworthy profile.");
            var nonFinite = valid.FirstOrDefault(m => !IsFinite(m.Xyz));
            if (nonFinite != null)
                return Result.Fail($"{nonFinite.Patch.Name} produced non-finite XYZ measurement values.");
            var nonPhysical = valid.FirstOrDefault(m => !IsNonNegativeXyz(m.Xyz));
            if (nonPhysical != null)
                return Result.Fail($"{nonPhysical.Patch.Name} produced non-physical negative XYZ measurement values.");

            double peak = valid.Max(m => m.Xyz.Y);
            double floor = valid.Min(m => m.Xyz.Y);
            if (peak < 1.0)
                return Result.Fail(
                    $"All measurements were near-black (peak {peak:F3} cd/m²). Check that the sensor is flush against the patch.");
            if (peak <= floor * 1.5)
                return Result.Fail(
                    "The measurements show almost no luminance range. The probe likely returned stale or repeated readings.");

            var white = FindPatch(valid, 1, 1, 1);
            var black = FindPatch(valid, 0, 0, 0);
            if (white == null)
                return Result.Fail("The measurement set is missing a valid white patch.");
            if (black == null)
                return Result.Fail("The measurement set is missing a valid black patch.");
            if (white.Xyz.Y <= black.Xyz.Y + Math.Max(1.0, peak * 0.05))
                return Result.Fail(
                    $"White ({white.Xyz.Y:F2} cd/m²) was not meaningfully brighter than black ({black.Xyz.Y:F2} cd/m²).");

            if (hdrMode)
            {
                var hdrWireAttempts = measurements
                    .Where(m => m.Patch.Nits is not null)
                    .OrderBy(m => m.Patch.Nits!.Value)
                    .ToList();
                var hdrWire = hdrWireAttempts
                    .Where(m => m.IsValid)
                    .ToList();

                // No wire-ladder data means the HDR LUT builder will use its documented
                // SDR-mapped fallback. But if a ladder was requested, it must produce enough
                // valid, believable rows: a failed FP16 renderer or probe session should not
                // disappear and be treated as an intentional fallback.
                if (hdrWireAttempts.Count > 0)
                {
                    var invalidRequestedNits = hdrWireAttempts.FirstOrDefault(m => !IsValidRequestedNits(m.Patch.Nits!.Value));
                    if (invalidRequestedNits != null)
                        return Result.Fail($"{invalidRequestedNits.Patch.Name} has invalid HDR wire-ladder requested luminance metadata.");

                    var nonFiniteWire = hdrWire.FirstOrDefault(m => !IsFinite(m.Xyz));
                    if (nonFiniteWire != null)
                        return Result.Fail($"{nonFiniteWire.Patch.Name} produced non-finite HDR wire-ladder XYZ measurement values.");
                    var nonPhysicalWire = hdrWire.FirstOrDefault(m => !IsNonNegativeXyz(m.Xyz));
                    if (nonPhysicalWire != null)
                        return Result.Fail($"{nonPhysicalWire.Patch.Name} produced non-physical negative HDR wire-ladder XYZ measurement values.");

                    int distinctValidWirePatches = hdrWire
                        .Select(m => m.Patch.Nits!.Value)
                        .Distinct()
                        .Count();
                    if (distinctValidWirePatches < 5)
                        return Result.Fail(
                            $"HDR wire-ladder captured only {distinctValidWirePatches} distinct valid patches ({hdrWire.Count} valid patches out of {hdrWireAttempts.Count}); at least five are needed to build a trustworthy PQ-domain LUT.");

                    double maxRequestedNits = hdrWire.Max(m => m.Patch.Nits!.Value);
                    double maxAttemptedNits = hdrWireAttempts.Max(m => m.Patch.Nits!.Value);
                    if (maxRequestedNits < 100)
                        return Result.Fail(
                            $"HDR wire-ladder reached only {maxRequestedNits:F0} nits; highlight patches were not captured.");
                    if (maxAttemptedNits >= 200 && maxRequestedNits < maxAttemptedNits * 0.80)
                        return Result.Fail(
                            $"HDR wire-ladder valid coverage reached only {maxRequestedNits:F0} nits out of {maxAttemptedNits:F0} nits requested; high-luminance PQ patches failed.");

                    double wirePeak = hdrWire.Max(m => m.Xyz.Y);
                    double wireFloor = hdrWire.Min(m => m.Xyz.Y);
                    if (wirePeak < 1.0)
                        return Result.Fail(
                            $"HDR wire-ladder measured near black (peak {wirePeak:F3} cd/m²). The FP16 HDR patch renderer likely failed or was occluded.");
                    if (wirePeak <= wireFloor + Math.Max(1.0, wirePeak * 0.05))
                        return Result.Fail(
                            "HDR wire-ladder has almost no luminance range. The HDR patch renderer or probe readings look stale.");

                    double tolerance = Math.Max(1.0, wirePeak * 0.05);
                    for (int i = 1; i < hdrWire.Count; i++)
                    {
                        if (hdrWire[i].Xyz.Y + tolerance < hdrWire[i - 1].Xyz.Y)
                        {
                            return Result.Fail(
                                $"HDR wire-ladder is non-monotonic around {hdrWire[i].Patch.Name}: " +
                                $"{hdrWire[i - 1].Xyz.Y:F2} -> {hdrWire[i].Xyz.Y:F2} cd/m².");
                        }
                    }
                }
            }

            var whites = valid
                .Where(m => m.Patch.Category == PatchCategory.Grayscale &&
                            m.Patch.DisplayRgb.R >= 0.99 &&
                            m.Patch.DisplayRgb.G >= 0.99 &&
                            m.Patch.DisplayRgb.B >= 0.99)
                .ToList();
            if (whites.Count > 1)
            {
                double minY = whites.Min(m => m.Xyz.Y);
                double maxY = whites.Max(m => m.Xyz.Y);
                if (minY > 0 && (maxY - minY) / minY > 0.08)
                    return Result.Fail($"Repeated white patches drifted by more than 8% ({minY:F1}-{maxY:F1} cd/m²). Let the display warm up and retry.");

                var first = whites[0].Chromaticity;
                foreach (var w in whites.Skip(1))
                {
                    if (first.DistanceTo(w.Chromaticity) > 0.01)
                        return Result.Fail("Repeated white patches changed chromaticity noticeably during the run.");
                }
            }

            var grayscale = valid
                .Where(m => m.Patch.Category == PatchCategory.Grayscale)
                .OrderBy(m => m.Patch.DisplayRgb.R)
                .ToList();
            if (grayscale.Count < 5)
                return Result.Fail("At least five valid grayscale measurements are required.");

            double monotonicTolerance = Math.Max(0.25, peak * 0.02);
            for (int i = 1; i < grayscale.Count; i++)
            {
                if (grayscale[i].Xyz.Y + monotonicTolerance < grayscale[i - 1].Xyz.Y)
                {
                    return Result.Fail(
                        $"Grayscale is non-monotonic around {grayscale[i].Patch.Name}: " +
                        $"{grayscale[i - 1].Xyz.Y:F2} -> {grayscale[i].Xyz.Y:F2} cd/m².");
                }
            }

            foreach (var primary in valid.Where(m => m.Patch.Category == PatchCategory.Primary))
            {
                if (primary.Xyz.Y <= black.Xyz.Y + 0.5)
                    return Result.Fail($"{primary.Patch.Name} measured near black; the displayed patch or probe reading is invalid.");

                var xy = primary.Chromaticity;
                if (!double.IsFinite(xy.X) || !double.IsFinite(xy.Y) ||
                    xy.X <= 0 || xy.Y <= 0 || xy.X > 0.8 || xy.Y > 0.9 || xy.X + xy.Y > 1.1)
                    return Result.Fail($"{primary.Patch.Name} measured an impossible chromaticity {xy}.");
            }

            return Result.Ok();
        }

        private static MeasurementResult? FindPatch(
            IReadOnlyList<MeasurementResult> measurements, double r, double g, double b)
            => measurements.FirstOrDefault(m =>
                Math.Abs(m.Patch.DisplayRgb.R - r) < 0.02 &&
                Math.Abs(m.Patch.DisplayRgb.G - g) < 0.02 &&
                Math.Abs(m.Patch.DisplayRgb.B - b) < 0.02);

        private static bool IsFinite(CieXyz xyz) =>
            double.IsFinite(xyz.X) && double.IsFinite(xyz.Y) && double.IsFinite(xyz.Z);

        private static bool IsNonNegativeXyz(CieXyz xyz) =>
            xyz.X >= -1e-6 && xyz.Y >= -1e-6 && xyz.Z >= -1e-6;

        private static bool IsValidRequestedNits(double nits) =>
            double.IsFinite(nits) && nits >= 0.0;
    }
}
