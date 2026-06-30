using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Builds and iteratively refines a per-channel VCGT correction from colorimeter
    /// measurements. The flow is the classic closed loop used by hardware calibrators:
    ///
    ///   1. Measure the native (uncorrected) grayscale  →  <see cref="BuildInitialCorrection"/>
    ///   2. Apply the correction, re-measure            →  <see cref="RefineCorrection"/>
    ///   3. Repeat until the residual dE is small enough or the round budget runs out.
    ///
    /// The correction is three 1024-point signal→signal LUTs suitable for the GPU gamma ramp.
    /// Refinement works on the grey axis: it corrects luminance/gamma tracking by re-mapping
    /// each channel through the measured response, and white-point drift by a per-channel
    /// gain. The caller is expected to keep the best-scoring correction (see
    /// <see cref="GrayscaleResidualDeltaE"/>) so an imperfect refinement step can never ship
    /// a worse result than the initial build.
    /// </summary>
    public sealed class ClosedLoopCorrector
    {
        private const int LutSize = 1024;

        private readonly CalibrationTarget _target;
        private readonly double _sdrWhiteLevel;
        private readonly bool _isHdr;
        private readonly double _damping; // 0..1; lower = more cautious steps

        // Cautious default damping: each refinement only takes ~half the proposed step, so
        // measurement noise can't make the on-screen correction swing wildly between passes.
        public ClosedLoopCorrector(CalibrationTarget target, double sdrWhiteLevel, bool isHdr, double damping = 0.5)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _sdrWhiteLevel = double.IsFinite(sdrWhiteLevel) && sdrWhiteLevel > 0.0 ? sdrWhiteLevel : 200.0;
            _isHdr = isHdr;
            _damping = ClampFinite(damping, 0.1, 1.0, 0.5);
        }

        /// <summary>
        /// Builds the first correction from native (uncorrected) measurements, by
        /// characterizing the display and inverting its response toward the target.
        /// </summary>
        public (double[] R, double[] G, double[] B) BuildInitialCorrection(IReadOnlyList<MeasurementResult> nativeMeasurements)
        {
            var generator = new Lut3DGenerator(_target, nativeMeasurements);
            generator.Generate(); // populates Characterization
            var characterization = generator.Characterization
                ?? throw new InvalidOperationException("Characterization could not be built from measurements.");

            double targetGamma = _target.Gamma ?? 2.2;
            var (r, g, b, _) = LutGenerator.GenerateCalibratedLut(
                targetGamma, characterization, CalibrationSettings.Default, _sdrWhiteLevel, _isHdr);
            return (r, g, b);
        }

        /// <summary>
        /// Produces a refined correction from the response measured WITH the current
        /// correction applied. Returns a new correction; the caller decides whether to keep
        /// it based on <see cref="GrayscaleResidualDeltaE"/>.
        /// </summary>
        public (double[] R, double[] G, double[] B) RefineCorrection(
            IReadOnlyList<MeasurementResult> achievedMeasurements,
            (double[] R, double[] G, double[] B) current)
        {
            var grey = ExtractGrayscale(achievedMeasurements);
            if (grey.Count < 3) return current; // nothing to learn from

            // Anchor the response range to the displayed black/white endpoints. A noisy
            // mid/high gray can overshoot white during a closed-loop pass; letting that
            // sample redefine white makes the next inverse LUT over-correct the range.
            double whiteY = grey[^1].Y;
            double blackY = grey[0].Y;
            double range = Math.Max(whiteY - blackY, 1e-6);

            // Achieved normalized-luminance response a(v): input signal -> [0,1].
            var inputs = grey.Select(p => p.V).ToArray();
            var achieved = grey.Select(p => Clamp01((p.Y - blackY) / range)).ToArray();
            EnforceMonotonic(achieved);

            // Per-channel white-balance gains: push the white point's channel ratios toward
            // the target white. Computed from the brightest neutral measurement.
            var white = grey[grey.Count - 1];
            var (gainR, gainG, gainB) = WhiteBalanceGains(white.Xyz);

            var newR = new double[LutSize];
            var newG = new double[LutSize];
            var newB = new double[LutSize];
            for (int i = 0; i < LutSize; i++)
            {
                double v = i / (double)(LutSize - 1);
                double targetLin = Clamp01(_target.ApplyEotf(v));

                // Find the input v'' whose achieved luminance equals our target, then send
                // whatever signal the current correction sent for v'' — that's the signal
                // empirically known to produce targetLin.
                double vPrime = InverseInterp(inputs, achieved, targetLin);
                double idx = vPrime * (LutSize - 1);

                double cR = Sample(current.R, idx);
                double cG = Sample(current.G, idx);
                double cB = Sample(current.B, idx);
                double currentR = Sample(current.R, i);
                double currentG = Sample(current.G, i);
                double currentB = Sample(current.B, i);

                // Damp toward the proposed correction for stability, then apply the white gain.
                newR[i] = Clamp01(Lerp(currentR, cR, _damping) * gainR);
                newG[i] = Clamp01(Lerp(currentG, cG, _damping) * gainG);
                newB[i] = Clamp01(Lerp(currentB, cB, _damping) * gainB);
            }

            // The endpoints are anchored: 0→0, and white maps to the gained channel maxima.
            newR[0] = newG[0] = newB[0] = 0.0;
            EnforceMonotonic(newR); EnforceMonotonic(newG); EnforceMonotonic(newB);
            return (newR, newG, newB);
        }

        /// <summary>
        /// Mean grayscale deltaE2000 of a measurement set against the target — the scalar the
        /// caller minimizes across rounds. Lower is better; this is what "keep best" compares.
        /// </summary>
        public double GrayscaleResidualDeltaE(IReadOnlyList<MeasurementResult> measurements)
        {
            var grey = ExtractGrayscale(measurements);
            if (grey.Count == 0) return double.MaxValue;

            double whiteY = grey.Max(p => p.Y);
            if (whiteY <= 0) return double.MaxValue;

            // Reference white for Lab is the measured white scaled to the target peak so we
            // compare tone/neutrality, not absolute luminance.
            var refWhite = grey[grey.Count - 1].Xyz;
            double sum = 0;
            int n = 0;
            foreach (var p in grey)
            {
                double targetLin = Clamp01(_target.ApplyEotf(p.V));
                // Target neutral XYZ at this level: targetLin * measured white.
                var targetXyz = new CieXyz(refWhite.X * targetLin, refWhite.Y * targetLin, refWhite.Z * targetLin);
                var measuredLab = ColorMath.XyzToLab(p.Xyz, refWhite);
                var targetLab = ColorMath.XyzToLab(targetXyz, refWhite);
                sum += measuredLab.DeltaE2000(targetLab);
                n++;
            }
            return n > 0 ? sum / n : double.MaxValue;
        }

        // --- helpers -----------------------------------------------------------------

        private readonly record struct GreyPoint(double V, CieXyz Xyz)
        {
            public double Y => Xyz.Y;
        }

        private static List<GreyPoint> ExtractGrayscale(IReadOnlyList<MeasurementResult> measurements)
        {
            return measurements
                .Where(m => m.IsValid && m.Patch.Category == PatchCategory.Grayscale)
                .Select(m => new GreyPoint(m.Patch.DisplayRgb.R, m.Xyz))
                .Where(p => double.IsFinite(p.V) &&
                            double.IsFinite(p.Xyz.X) && double.IsFinite(p.Xyz.Y) && double.IsFinite(p.Xyz.Z) &&
                            p.Xyz.X >= -1e-6 && p.Xyz.Y >= -1e-6 && p.Xyz.Z >= -1e-6)
                .OrderBy(p => p.V)
                .ToList();
        }

        private (double r, double g, double b) WhiteBalanceGains(CieXyz measuredWhite)
        {
            // Target white in linear display-RGB-ish terms: use the target's chromaticity.
            // We only need the relative channel balance, so normalize both to max=1.
            var targetWhiteLin = _target.XyzToLinearRgb(_target.LinearRgbToXyz(new LinearRgb(1, 1, 1)));
            var measuredLin = _target.XyzToLinearRgb(measuredWhite);

            double tMax = Math.Max(targetWhiteLin.R, Math.Max(targetWhiteLin.G, targetWhiteLin.B));
            double mMax = Math.Max(measuredLin.R, Math.Max(measuredLin.G, measuredLin.B));
            if (!double.IsFinite(tMax) || !double.IsFinite(mMax) || tMax <= 0 || mMax <= 0)
                return (1, 1, 1);

            double tr = targetWhiteLin.R / tMax, tg = targetWhiteLin.G / tMax, tb = targetWhiteLin.B / tMax;
            double mr = measuredLin.R / mMax, mg = measuredLin.G / mMax, mb = measuredLin.B / mMax;
            if (!double.IsFinite(tr) || !double.IsFinite(tg) || !double.IsFinite(tb) ||
                !double.IsFinite(mr) || !double.IsFinite(mg) || !double.IsFinite(mb))
            {
                return (1, 1, 1);
            }

            // Gain needed to move measured ratios toward target ratios, damped and bounded so a
            // bad reading can't blow a channel out.
            double gR = mr > 1e-4 ? tr / mr : 1.0;
            double gG = mg > 1e-4 ? tg / mg : 1.0;
            double gB = mb > 1e-4 ? tb / mb : 1.0;
            // Tight per-step bound: white balance moves at most a few percent per round, so a
            // single noisy white reading can't tint the whole screen red or green.
            gR = ClampFinite(Lerp(1.0, gR, _damping), 0.94, 1.06, 1.0);
            gG = ClampFinite(Lerp(1.0, gG, _damping), 0.94, 1.06, 1.0);
            gB = ClampFinite(Lerp(1.0, gB, _damping), 0.94, 1.06, 1.0);
            return (gR, gG, gB);
        }

        /// <summary>Finds x such that y(x) == target, by linear interpolation over monotonic samples.</summary>
        private static double InverseInterp(double[] xs, double[] ys, double target)
        {
            int n = xs.Length;
            if (target <= ys[0]) return xs[0];
            if (target >= ys[n - 1]) return xs[n - 1];
            for (int i = 1; i < n; i++)
            {
                if (target <= ys[i])
                {
                    double dy = ys[i] - ys[i - 1];
                    double t = dy > 1e-9 ? (target - ys[i - 1]) / dy : 0;
                    return xs[i - 1] + t * (xs[i] - xs[i - 1]);
                }
            }
            return xs[n - 1];
        }

        private static double Sample(double[] lut, double idx)
        {
            if (lut == null || lut.Length == 0 || !double.IsFinite(idx))
                return 0.0;

            int lo = (int)Math.Floor(idx);
            int hi = Math.Min(lo + 1, lut.Length - 1);
            lo = Math.Clamp(lo, 0, lut.Length - 1);
            double frac = idx - lo;
            return Clamp01(lut[lo] + (lut[hi] - lut[lo]) * frac);
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static void EnforceMonotonic(double[] lut)
        {
            if (lut.Length == 0) return;
            lut[0] = Clamp01(lut[0]);
            for (int i = 1; i < lut.Length; i++)
            {
                lut[i] = Clamp01(lut[i]);
                if (lut[i] < lut[i - 1]) lut[i] = lut[i - 1];
            }
        }

        private static double Clamp01(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

        private static double ClampFinite(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }
}
