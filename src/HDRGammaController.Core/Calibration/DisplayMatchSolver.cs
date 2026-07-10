using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Multi-display matching (roadmap 4.5): instead of holding every panel to an absolute
    /// target it may not reach, solve for the common white (chromaticity AND luminance) that
    /// all connected panels can reach with the LEAST worst-case perceptual disturbance
    /// (CAM16-UCS ΔE′, minimax) — then express each panel's share of the move as per-channel
    /// gains (chromaticity trim, ≤1 so nothing clips) plus a brightness cap (luminance trim).
    /// The dual-monitor color mismatch is the most common real-world complaint calibration
    /// tools ignore.
    /// </summary>
    /// <remarks>
    /// MODEL-BASED: the solve runs on each panel's stored characterization (measured
    /// RGB→XYZ and peak), so the result is only as good as each panel's calibration —
    /// residual per-panel calibration error and instrument metamerism between panels are
    /// invisible to it. That honest limit is surfaced in the UI; a probe-assisted measured
    /// matching pass is the natural follow-up.
    /// </remarks>
    public static class DisplayMatchSolver
    {
        public sealed record DisplayInput(
            string DevicePath,
            string FriendlyName,
            double[,] RgbToXyzMatrix,     // measured, white-normalized ((1,1,1) → Y=1)
            double PeakLuminanceNits,     // measured white luminance at full drive
            Chromaticity CalibratedWhite); // the white its calibration targets

        public sealed record DisplayAdjustment(
            string DevicePath,
            string FriendlyName,
            double GainR,
            double GainG,
            double GainB,
            double BrightnessPercent,
            double AchievableLuminanceNits, // at the common white, full brightness
            double DeltaEPrimeFromCurrent);

        public sealed record MatchSolution(
            Chromaticity CommonWhite,
            double CommonLuminanceNits,
            double MaxDeltaEPrime,
            IReadOnlyList<DisplayAdjustment> Adjustments);

        /// <summary>Chromaticity search half-width around the calibrated whites' bounding
        /// box — matching whites live near each other; a wider search would only find
        /// solutions perceptually worse for every panel.</summary>
        private const double SearchPad = 0.006;
        private const double SearchStep = 0.001;

        public static MatchSolution Solve(IReadOnlyList<DisplayInput> displays)
        {
            ArgumentNullException.ThrowIfNull(displays);
            if (displays.Count < 2)
                throw new ArgumentException("Matching needs at least two displays.", nameof(displays));
            foreach (var d in displays)
            {
                if (d.RgbToXyzMatrix == null || !double.IsFinite(d.PeakLuminanceNits) || d.PeakLuminanceNits <= 0)
                    throw new ArgumentException($"Display '{d.FriendlyName}' has no usable characterization.");
            }

            // Shared appearance conditions: brightest current white as the adaptation
            // anchor, so luminance sacrifices are priced consistently for every panel.
            double maxPeak = displays.Max(d => d.PeakLuminanceNits);
            var d65 = Chromaticity.D65.ToXyz(1.0);
            var vc = Cam16Ucs.DisplayConditions(new CieXyz(
                d65.X * maxPeak, d65.Y * maxPeak, d65.Z * maxPeak));

            // Each display's CURRENT state: its calibrated white at its own full luminance.
            var currentJabs = displays
                .Select(d =>
                {
                    var w = d.CalibratedWhite.ToXyz(1.0);
                    return Cam16Ucs.ToJabPrime(new CieXyz(
                        w.X * d.PeakLuminanceNits, w.Y * d.PeakLuminanceNits, w.Z * d.PeakLuminanceNits), vc);
                })
                .ToList();

            var inverses = displays.Select(d => ColorMath.Invert3x3(d.RgbToXyzMatrix)).ToList();

            double xMin = displays.Min(d => d.CalibratedWhite.X) - SearchPad;
            double xMax = displays.Max(d => d.CalibratedWhite.X) + SearchPad;
            double yMin = displays.Min(d => d.CalibratedWhite.Y) - SearchPad;
            double yMax = displays.Max(d => d.CalibratedWhite.Y) + SearchPad;

            (Chromaticity White, double Lum, double MaxCost, List<DisplayAdjustment> Adj)? best = null;

            for (double x = xMin; x <= xMax + 1e-12; x += SearchStep)
            {
                for (double y = yMin; y <= yMax + 1e-12; y += SearchStep)
                {
                    var candidate = new Chromaticity(x, y);
                    var evaluated = Evaluate(candidate, displays, inverses, currentJabs, vc);
                    if (evaluated == null) continue;

                    if (best == null || evaluated.Value.MaxCost < best.Value.MaxCost)
                        best = (candidate, evaluated.Value.Lum, evaluated.Value.MaxCost, evaluated.Value.Adj);
                }
            }

            if (best == null)
                throw new InvalidOperationException(
                    "No common white is reachable by every display — a characterization is likely invalid.");

            return new MatchSolution(best.Value.White, best.Value.Lum, best.Value.MaxCost, best.Value.Adj);
        }

        private static (double Lum, double MaxCost, List<DisplayAdjustment> Adj)? Evaluate(
            Chromaticity white,
            IReadOnlyList<DisplayInput> displays,
            IReadOnlyList<double[,]> inverses,
            IReadOnlyList<Cam16Ucs.JabPrime> currentJabs,
            Cam16Ucs.ViewingConditions vc)
        {
            var whiteXyz = white.ToXyz(1.0);
            var drives = new List<(double R, double G, double B, double MaxDrive)>(displays.Count);

            // Per display: the linear drive that produces the candidate white, and the
            // luminance cost of normalizing its largest channel to full scale.
            for (int i = 0; i < displays.Count; i++)
            {
                var inv = inverses[i];
                double r = inv[0, 0] * whiteXyz.X + inv[0, 1] * whiteXyz.Y + inv[0, 2] * whiteXyz.Z;
                double g = inv[1, 0] * whiteXyz.X + inv[1, 1] * whiteXyz.Y + inv[1, 2] * whiteXyz.Z;
                double b = inv[2, 0] * whiteXyz.X + inv[2, 1] * whiteXyz.Y + inv[2, 2] * whiteXyz.Z;
                if (r <= 0 || g <= 0 || b <= 0 || !double.IsFinite(r + g + b))
                    return null; // outside this panel's gamut — candidate infeasible
                drives.Add((r, g, b, Math.Max(r, Math.Max(g, b))));
            }

            // Common luminance: the dimmest panel's reachable white at this chromaticity.
            double commonLum = double.MaxValue;
            for (int i = 0; i < displays.Count; i++)
                commonLum = Math.Min(commonLum, displays[i].PeakLuminanceNits / drives[i].MaxDrive);

            double maxCost = 0;
            var adjustments = new List<DisplayAdjustment>(displays.Count);
            var targetXyzAbs = new CieXyz(
                whiteXyz.X * commonLum, whiteXyz.Y * commonLum, whiteXyz.Z * commonLum);
            var targetJab = Cam16Ucs.ToJabPrime(targetXyzAbs, vc);

            for (int i = 0; i < displays.Count; i++)
            {
                var (r, g, b, maxDrive) = drives[i];
                double achievable = displays[i].PeakLuminanceNits / maxDrive;
                double cost = Cam16Ucs.DeltaEPrime(currentJabs[i], targetJab);
                maxCost = Math.Max(maxCost, cost);

                adjustments.Add(new DisplayAdjustment(
                    displays[i].DevicePath,
                    displays[i].FriendlyName,
                    GainR: r / maxDrive,
                    GainG: g / maxDrive,
                    GainB: b / maxDrive,
                    BrightnessPercent: Math.Clamp(100.0 * commonLum / achievable, 10.0, 100.0),
                    AchievableLuminanceNits: achievable,
                    DeltaEPrimeFromCurrent: cost));
            }

            return (commonLum, maxCost, adjustments);
        }
    }
}
