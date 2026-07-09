using System;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Perceptually-paced fade steps (roadmap 3.5): replaces the fixed 0.05-mired-per-tick
    /// heuristic with a principled per-step ceiling in ΔE ITP (BT.2124, ~1 unit ≈ 1 JND)
    /// evaluated at the CURRENT operating point of the fade. The step size becomes adaptive
    /// along the fade and — critically — includes the luminance dimension the mired heuristic
    /// ignored (normalize-to-max fades also dim; UltraNight dims deliberately and therefore
    /// correctly gets smaller steps).
    /// </summary>
    /// <remarks>
    /// Honesty notes for the threshold choice: instantaneous ΔE ITP between consecutive
    /// adapted whites is a CONSERVATIVE upper bound on visibility during a slow drift —
    /// chromatic adaptation to the current white makes slow changes less visible than the
    /// static metric implies. The target is half a JND per hardware write, computed at a
    /// fixed 100 cd/m² sRGB reference (pacing is a global scheduling decision driving N
    /// monitors, and JND sensitivity is near-Weber-flat above ~50 nits, so the reference is
    /// representative across SDR white settings). Full von Kries adaptation modeling is
    /// deliberately out of scope. The fade DURATION always wins over the JND ceiling: the
    /// schedule is a user promise, and when a short fade over a long span cannot stay
    /// sub-JND at the ~4 writes/sec hardware floor, steps exceed the threshold and the
    /// caller logs it once per fade window (see NightModeService).
    /// </remarks>
    public static class JndPacedFade
    {
        /// <summary>Per-hardware-write perceptual ceiling: half a JND before adaptation discount.</summary>
        public const double TargetStepItp = 0.5;

        /// <summary>Reference adaptation luminance for the pacing metric (SDR reference white).</summary>
        public const double ReferenceWhiteNits = 100.0;

        /// <summary>Finite-difference probe distance in mired.</summary>
        private const double ProbeMired = 0.5;

        // Step-size guards: the lower bound catches pathological derivatives (NaN/huge g);
        // the upper bound keeps the fade sampled finely enough that a schedule edit mid-fade
        // never lands on a coarse grid.
        private const double MinStepMired = 0.01;
        private const double MaxStepMired = 2.0;

        /// <summary>The historical heuristic, kept as the fallback when the metric degenerates.</summary>
        public const double FallbackStepMired = 0.05;

        /// <summary>
        /// Largest mired step whose perceptual size at the current operating point stays
        /// within <see cref="TargetStepItp"/>. Derived from a central finite difference of
        /// ΔE ITP per mired between consecutive adapted whites under the ACTIVE night-mode
        /// algorithm — so UltraNight's deliberate dimming and constant-Y's flat luminance
        /// are both priced in automatically.
        /// </summary>
        public static double ComputeMaxStepMired(
            int currentKelvin,
            NightModeAlgorithm algorithm,
            double perceptualStrength,
            bool useUltraWarmMode,
            bool preserveLuminance,
            NightMelanopicCoefficients? melanopic = null)
        {
            if (currentKelvin <= 0) return FallbackStepMired;

            double mired = 1e6 / Math.Clamp(currentKelvin, 1000, 10000);
            int kelvinLow = KelvinAtMired(mired + ProbeMired);   // warmer probe
            int kelvinHigh = KelvinAtMired(mired - ProbeMired);  // cooler probe
            if (kelvinLow == kelvinHigh) return FallbackStepMired;

            // Recompute the actual probe span from the rounded kelvins so integer rounding
            // does not bias the derivative.
            double actualSpanMired = Math.Abs(1e6 / kelvinLow - 1e6 / kelvinHigh);
            if (actualSpanMired < 1e-6) return FallbackStepMired;

            // The pacing derivative must be an upper bound across every display path the one
            // global fade drives. Constant-Y makes steps chromatic-only (smaller) on displays
            // with headroom, but the SDR path cannot preserve — so with the flag on we price
            // BOTH variants and pace to the more visible one.
            double g = DeltaEPerMired(kelvinLow, kelvinHigh, actualSpanMired,
                algorithm, perceptualStrength, useUltraWarmMode, preserveLuminance: false, melanopic);
            if (preserveLuminance && algorithm != NightModeAlgorithm.UltraNight)
            {
                double gPreserved = DeltaEPerMired(kelvinLow, kelvinHigh, actualSpanMired,
                    algorithm, perceptualStrength, useUltraWarmMode, preserveLuminance: true, melanopic);
                g = Math.Max(g, gPreserved);
            }

            if (!double.IsFinite(g) || g <= 0.0) return FallbackStepMired;

            return Math.Clamp(TargetStepItp / g, MinStepMired, MaxStepMired);
        }

        private static double DeltaEPerMired(
            int kelvinLow, int kelvinHigh, double spanMired,
            NightModeAlgorithm algorithm, double perceptualStrength, bool useUltraWarmMode,
            bool preserveLuminance, NightMelanopicCoefficients? melanopic)
        {
            var low = AdaptedWhiteXyz(kelvinLow, algorithm, perceptualStrength, useUltraWarmMode,
                preserveLuminance, melanopic);
            var high = AdaptedWhiteXyz(kelvinHigh, algorithm, perceptualStrength, useUltraWarmMode,
                preserveLuminance, melanopic);
            return CalibrationVerifier.DeltaEItp(low, high) / spanMired;
        }

        /// <summary>
        /// The absolute XYZ (Y in nits at the 100 cd/m² reference) of the adapted white the
        /// active night algorithm produces at <paramref name="kelvin"/>. Internal so the
        /// reference-value tests can pin the same whites the pacing derivative uses.
        /// </summary>
        internal static CieXyz AdaptedWhiteXyz(
            int kelvin,
            NightModeAlgorithm algorithm,
            double perceptualStrength,
            bool useUltraWarmMode,
            bool preserveLuminance,
            NightMelanopicCoefficients? melanopic = null)
        {
            double scale = (kelvin - 6500) / 70.0;
            var m = ColorAdjustments.GetTemperatureMultipliers(
                scale, algorithm, useUltraWarmMode, perceptualStrength, melanopic, NightBasis.Srgb);

            if (preserveLuminance && algorithm != NightModeAlgorithm.UltraNight)
            {
                // Nominal HDR headroom for the pacing estimate; the flat-luminance variant
                // is only used as one side of the max() in ComputeMaxStepMired.
                m = ColorAdjustments.RescaleToConstantLuminance(m, NightBasis.Srgb, 2.0, 1.0);
            }

            var xyz = ColorMath.LinearSrgbToXyz(new LinearRgb(m.R, m.G, m.B));
            return new CieXyz(
                xyz.X * ReferenceWhiteNits,
                xyz.Y * ReferenceWhiteNits,
                xyz.Z * ReferenceWhiteNits);
        }

        private static int KelvinAtMired(double mired) =>
            (int)Math.Round(1e6 / Math.Clamp(mired, 100.0, 1000.0));
    }
}
