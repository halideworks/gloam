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
    /// path uses. The goal is PQ tracking: wire signal p should produce ST2084(p) nits
    /// (scaled per channel by the white-point gains that move the panel's native white to
    /// the target white).
    ///
    /// The characterization patches are SDR content, which Windows maps onto the wire as
    /// PQ(sdrWhiteNits · srgbEotf(v)) — so the measurements only cover the wire range up to
    /// the SDR white level. Within that range the LUT inverts the MEASURED response; above
    /// it we have no data, so the LUT continues analytically (pure PQ re-encode of the
    /// desired nits), leaving the panel's own highlight rolloff untouched. A smoothstep
    /// blends the two regimes to avoid a visible seam.
    /// </summary>
    public static class HdrMhc2LutBuilder
    {
        public sealed record Result(
            double[] LutR, double[] LutG, double[] LutB,
            double MeasuredBlackNits, double MeasuredPeakNits);

        private const int LutSamples = 1024;

        public static Result Build(
            IEnumerable<MeasurementResult> measurements,
            double sdrWhiteNits,
            (double R, double G, double B) whiteGains)
        {
            if (sdrWhiteNits < 40 || sdrWhiteNits > 1000)
                throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits),
                    $"SDR white level {sdrWhiteNits:F0} nits is implausible.");

            // Wire-PQ position and measured nits for each grayscale patch. The patch signal v
            // is SDR content; Windows linearizes it with the piecewise sRGB EOTF and scales to
            // the SDR white level before PQ-encoding for the wire.
            var points = measurements
                .Where(m => m.IsValid && m.Patch.Category == PatchCategory.Grayscale)
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
            double pMeasuredMax = points[^1].P;
            if (peakNits <= blackNits * 1.5)
                throw new InvalidOperationException(
                    $"Measured HDR grayscale has almost no range ({blackNits:F2}–{peakNits:F2} nits) — readings look invalid.");

            double[] BuildChannel(double gain)
            {
                var lut = new double[LutSamples];
                // Blend window: the top 15% of the measured nits range fades from
                // measured-inverse correction to the analytic continuation.
                double blendStartNits = blackNits + (peakNits - blackNits) * 0.85;

                for (int i = 0; i < LutSamples; i++)
                {
                    double p = i / (double)(LutSamples - 1);
                    double desired = gain * TransferFunctions.PqEotf(p);

                    // Analytic passthrough: re-encode desired nits as PQ. Identity when
                    // gain == 1; above the measured range this preserves the panel's own
                    // tone mapping behavior.
                    double analytic = TransferFunctions.PqInverseEotf(desired);

                    double v;
                    if (desired >= peakNits)
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
                            double t = (desired - blendStartNits) / Math.Max(peakNits - blendStartNits, 1e-9);
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
                return lut;
            }

            return new Result(
                BuildChannel(whiteGains.R), BuildChannel(whiteGains.G), BuildChannel(whiteGains.B),
                blackNits, peakNits);
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
