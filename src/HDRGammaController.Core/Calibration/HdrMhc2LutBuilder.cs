using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Builds the per-channel MHC2 tone LUTs for a display running in HDR.
    ///
    /// In HDR the wire format is BT.2100 PQ, and the MHC2 LUTs operate on PQ SIGNAL
    /// (0..1 ≙ 0..10000 nits via ST.2084) — not on the gamma-encoded SDR signal the SDR
    /// path uses. The goal is PQ tracking: CONTENT signal v should produce ST2084(v) nits.
    /// Because the DWM applies the (uniformly scaled) gamut matrix BEFORE these LUTs, the
    /// builder composes that scale into the inversion (see Build's matrixNeutralScale) so
    /// the matrix+LUT chain — not the LUT in isolation — tracks PQ.
    ///
    /// Two measurement sources, by preference:
    ///  - HDR wire-ladder patches (ColorPatch.Nits, FP16 scRGB rendering): wire positions
    ///    are exact and span the full desktop+HDR range, so the LUT inverts MEASURED
    ///    response everywhere up to the panel's reachable peak, going identity only above.
    ///  - SDR-window grayscale fallback: Windows maps SDR content onto the wire as
    ///    PQ(sdrWhiteNits · srgbEotf(v)) — an assumption, and coverage stops at SDR white,
    ///    so correction is knee-safe-limited and blends to identity inside the SDR range.
    /// </summary>
    public static class HdrMhc2LutBuilder
    {
        public sealed record Result(
            double[] LutR, double[] LutG, double[] LutB,
            double MeasuredBlackNits, double MeasuredPeakNits,
            bool WireExact = false);

        private const int LutSamples = 1024;

        /// <remarks>
        /// The LUT is NEUTRAL (identical per channel): white-point correction lives entirely
        /// in the (uniformly scaled, absolute) gamut matrix. Per-channel gains here would
        /// re-tint saturated colors the matrix already placed.
        /// </remarks>
        /// <param name="matrixNeutralScale">
        /// The uniform scale s (0 &lt; s ≤ 1) baked into the MHC2 gamut matrix that Windows
        /// applies BEFORE these LUTs. The LUT model composes it in (see the MATRIX
        /// COMPOSITION comment in the sample loop); 1.0 (identity/no matrix dimming)
        /// reproduces the uncomposed build bit-for-bit.
        /// </param>
        public static Result Build(
            IEnumerable<MeasurementResult> measurements,
            double sdrWhiteNits,
            double matrixNeutralScale = 1.0)
        {
            if (!double.IsFinite(sdrWhiteNits) || sdrWhiteNits < 40 || sdrWhiteNits > 1000)
                throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits),
                    $"SDR white level {sdrWhiteNits:F0} nits is implausible.");

            if (!double.IsFinite(matrixNeutralScale) || matrixNeutralScale <= 0.0 || matrixNeutralScale > 1.0 + 1e-9)
                throw new ArgumentOutOfRangeException(nameof(matrixNeutralScale),
                    $"Matrix neutral-axis scale {matrixNeutralScale} must be in (0, 1] — the uniform-scale policy only ever dims.");

            // PREFERRED: HDR wire-ladder patches (ColorPatch.Nits set) rendered through the
            // FP16 scRGB path. Their wire position is EXACT - PQ⁻¹(requested nits) - with no
            // SDR-mapping assumption, and they reach far above SDR white, so the LUT can
            // correct with measured data across the whole desktop+HDR range. (Validated on
            // the MAG 271QPX: probe confirmed FP16 wire positions are exact while the OS
            // SDR-white value was ~7% off.)
            var wirePoints = measurements
                .Where(IsFiniteWireMeasurement)
                .Select(m => (
                    P: TransferFunctions.PqInverseEotf(m.Patch.Nits!.Value),
                    Nits: m.Xyz.Y))
                .OrderBy(t => t.P)
                .ToList();
            var points = wirePoints;
            bool wireExact = IsUsableWireLadder(wirePoints);

            // FALLBACK: SDR-window grayscale patches. Windows maps SDR content onto the wire
            // as PQ(sdrWhiteNits · srgbEotf(v)) - an ASSUMPTION (the OS-reported white level
            // can be off), and coverage stops at SDR white.
            if (!wireExact)
                points = measurements
                    .Where(IsFiniteGrayscaleMeasurement)
                    .Select(m => (
                        P: TransferFunctions.PqInverseEotf(sdrWhiteNits * TransferFunctions.SrgbEotf(m.Patch.DisplayRgb.R)),
                        Nits: m.Xyz.Y))
                    .OrderBy(t => t.P)
                    .ToList();

            if (points.Count < 5)
                throw new InvalidOperationException(
                    $"Need at least 5 valid grayscale measurements for an HDR LUT (have {points.Count}).");

            // Enforce a monotonic response (measurement noise can produce tiny inversions).
            for (int i = 1; i < points.Count; i++)
                if (points[i].Nits < points[i - 1].Nits)
                    points[i] = (points[i].P, points[i - 1].Nits);

            double blackNits = points[0].Nits;
            double peakNits = points[^1].Nits;
            if (peakNits <= blackNits * 1.5)
                throw new InvalidOperationException(
                    $"Measured HDR grayscale has almost no range ({blackNits:F2}–{peakNits:F2} nits) - readings look invalid.");

            var lut = new double[LutSamples];
            double blendStartNits, blendEndNits;
            if (wireExact)
            {
                // Wire positions are measured, not assumed, so the inversion is valid across
                // the entire measured span - including the panel's tone-mapping knee, which
                // is now just part of the measured response. Identity only ABOVE the top
                // measured point: beyond the panel's reachable output the LUT must leave the
                // panel's own rolloff alone (it cannot create luminance that isn't there).
                blendStartNits = peakNits * 0.90;
                blendEndNits = peakNits;
            }
            else
            {
                // Correct fully only in the lower half of the measured range; fade to IDENTITY
                // between 50% and 80% of it. Two hard-won reasons (verified on the M27Q):
                //  1. The upper range sits inside the panel's HDR tone-mapping knee, where the
                //     ASSUMED wire-axis model breaks down - "correcting" the knee overshoots
                //     and just dims highlights (verified white 189 nits instead of ~220).
                //  2. The LUT is applied per channel AFTER the gamut matrix. Strong curvature
                //     near the top distorts the channel ratios of unequal drive values — which
                //     is exactly the matrix's D65-corrected white (it measured x=0.301, blue-
                //     green, with the aggressive top-end correction). Identity near the top
                //     keeps the white point the matrix worked for.
                blendStartNits = blackNits + (peakNits - blackNits) * 0.50;
                blendEndNits = blackNits + (peakNits - blackNits) * 0.80;
            }

            for (int i = 0; i < LutSamples; i++)
            {
                double p = i / (double)(LutSamples - 1);

                // MATRIX COMPOSITION (the M5 fix). Windows evaluates the MHC2 pipeline as
                //     wire → PQ-decode → gamut matrix (uniformly scaled by s) → PQ-encode → LUT → panel
                // but the panel response in `points` was measured with NO matrix installed.
                // At apply time this LUT therefore never sees raw content wire positions:
                // on the NEUTRAL axis the matrix scales linear luminance by exactly s —
                // both the display and target RGB→XYZ matrices are white-normalized to
                // Y=1, so the absolute matrix maps content white to a display drive of
                // relative luminance 1 (chromaticity changes, luminance does not) and the
                // uniform scale is the only neutral-axis luminance change. Content at PQ
                // position v thus reaches this LUT at w'(v) = PQ⁻¹(s·PQ(v)).
                //
                // Building the LUT against raw positions (the old behavior) made the
                // installed matrix+LUT chain output s·d nits instead of the absolute
                // target d for content d, and — because PQ is nonlinear and the LUT is
                // per-channel over unequal post-matrix drives — re-tinted near-neutrals
                // (fallback reason #2 below describes that failure). Composing s into the
                // model: LUT input p corresponds to content luminance PQ(p)/s, so invert
                // the measured response at THAT target. Then LUT(w'(v)) = f⁻¹(PQ(v)):
                // absolute PQ tracking through matrix+LUT up to the panel's reachable
                // peak, identity above it, exactly as designed. With s = 1 the division
                // is exact and the output is bit-identical to the pre-composition build.
                double desired = TransferFunctions.PqEotf(p) / matrixNeutralScale;

                // Analytic passthrough: identity — preserves the panel's own behavior.
                double analytic = p;

                double v;
                if (desired >= blendEndNits)
                {
                    v = analytic;
                }
                else
                {
                    double corrected = InverseResponse(points, Math.Max(desired, blackNits));
                    if (desired <= blendStartNits)
                    {
                        v = corrected;
                    }
                    else
                    {
                        double t = (desired - blendStartNits) / Math.Max(blendEndNits - blendStartNits, 1e-9);
                        t = Math.Clamp(t, 0, 1);
                        double s = t * t * (3 - 2 * t);
                        v = corrected + (analytic - corrected) * s;
                    }
                }
                lut[i] = Math.Clamp(v, 0.0, 1.0);
            }

            // Monotonic cleanup — the regamma LUT must never invert.
            for (int i = 1; i < LutSamples; i++)
                if (lut[i] < lut[i - 1]) lut[i] = lut[i - 1];

            return new Result(lut, (double[])lut.Clone(), (double[])lut.Clone(), blackNits, peakNits, wireExact);
        }

        private static bool IsFiniteWireMeasurement(MeasurementResult m) =>
            m.IsValid &&
            m.Patch.Nits is double nits &&
            double.IsFinite(nits) &&
            nits >= 0.0 &&
            double.IsFinite(m.Xyz.Y) &&
            m.Xyz.Y >= 0.0;

        private static bool IsFiniteGrayscaleMeasurement(MeasurementResult m) =>
            m.IsValid &&
            m.Patch.Category == PatchCategory.Grayscale &&
            double.IsFinite(m.Patch.DisplayRgb.R) &&
            m.Patch.DisplayRgb.R is >= 0.0 and <= 1.0 &&
            double.IsFinite(m.Xyz.Y) &&
            m.Xyz.Y >= 0.0;

        private static bool IsUsableWireLadder(IReadOnlyList<(double P, double Nits)> points)
        {
            int distinctWirePositions = points
                .Select(p => Math.Round(p.P, 12))
                .Distinct()
                .Count();
            if (distinctWirePositions < 5)
                return false;

            double maxWireNits = TransferFunctions.PqEotf(points[^1].P);
            return maxWireNits >= 100.0;
        }

        /// <summary>The wire PQ signal that produced <paramref name="nits"/> on the measured panel.</summary>
        private static double InverseResponse(List<(double P, double Nits)> points, double nits)
        {
            if (nits <= points[0].Nits) return points[0].P;
            for (int i = 1; i < points.Count; i++)
            {
                if (nits <= points[i].Nits)
                {
                    double span = points[i].Nits - points[i - 1].Nits;
                    double t = span <= 1e-9 ? 0 : (nits - points[i - 1].Nits) / span;
                    return points[i - 1].P + (points[i].P - points[i - 1].P) * t;
                }
            }
            return points[^1].P;
        }
    }
}
