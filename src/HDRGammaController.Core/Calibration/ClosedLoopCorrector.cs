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
    ///
    /// Domain contract: the VCGT arrays are SIGNAL → SIGNAL. The OUTPUT value of the LUT is
    /// the (gamma-encoded) signal fed to the native panel, whose light response is the
    /// characterization's measured tone curve. Anything that means "x% more light" must
    /// therefore go through that curve — multiplying VCGT values directly applies roughly
    /// gain^(1/γ)⁻¹-worth of error (≈2.2× over-correction on a typical panel).
    /// </summary>
    public sealed class ClosedLoopCorrector
    {
        private const int LutSize = 1024;

        private readonly CalibrationTarget _target;
        private readonly double _sdrWhiteLevel;
        private readonly bool _isHdr;
        private readonly double _damping; // 0..1; lower = more cautious steps

        // Target actually used for linearization. Starts as _target; BuildInitialCorrection
        // replaces it with a measured-black-aware clone for BT.1886 targets (m4) so the LUT
        // build, the refinement targets and the residual score all share ONE tone curve.
        private CalibrationTarget _effectiveTarget;

        // The panel's native luminance response, captured in BuildInitialCorrection. Used to
        // apply white-balance gains in LINEAR light (see ApplyGainInLinear).
        private ToneCurve? _nativeTone;

        // Cautious default damping: each refinement only takes ~half the proposed step, so
        // measurement noise can't make the on-screen correction swing wildly between passes.
        public ClosedLoopCorrector(CalibrationTarget target, double sdrWhiteLevel, bool isHdr, double damping = 0.5)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _effectiveTarget = _target;
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
            // Characterization only (m9): the full Generate() also builds a 17³ 3D LUT that
            // this 1D closed loop never consumes — a pure waste of a full grid inversion.
            var characterization = generator.BuildCharacterizationOnly(hdrMode: _isHdr);

            // m4: give BT.1886 targets the measured black so the EOTF's a/b coefficients
            // reflect the panel's real contrast instead of degenerating to pure 2.4.
            _effectiveTarget = MakeEffectiveTarget(_target, characterization);

            // All three characterization tone curves reference the same shared luminance fit
            // (tone correction is luminance-only); keep it for linear-domain gain application.
            _nativeTone = characterization.GreenToneCurve;

            // Tone target routed through the target's ACTUAL EOTF (M3) — the same
            // linearization the residual score and the verifier use, so the first refinement
            // round no longer "corrects" toward a different curve than the initial build.
            var (r, g, b, _) = LutGenerator.GenerateCalibratedLut(
                _effectiveTarget, characterization, CalibrationSettings.Default, _sdrWhiteLevel, _isHdr);
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

            // The achieved response above is BLACK-SUBTRACTED; map the target EOTF into the
            // same domain (matters for BT.1886 targets whose EOTF has a non-zero floor).
            double targetFloor = Clamp01(_effectiveTarget.ApplyEotf(0.0));
            double targetRange = Math.Max(1.0 - targetFloor, 1e-6);

            var newR = new double[LutSize];
            var newG = new double[LutSize];
            var newB = new double[LutSize];
            for (int i = 0; i < LutSize; i++)
            {
                double v = i / (double)(LutSize - 1);
                double targetLin = Clamp01((Clamp01(_effectiveTarget.ApplyEotf(v)) - targetFloor) / targetRange);

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

                // Damp toward the proposed correction for stability, then apply the white
                // gain IN LINEAR LIGHT (M2): the LUT output is a gamma-encoded panel signal,
                // so a linear gain g becomes OETF(g · EOTF(signal)) through the panel's
                // measured tone curve — multiplying the encoded value directly would apply
                // ~g^γ (≈2.2×) of the requested step and rely on damping to hide it.
                newR[i] = ApplyGainInLinear(Lerp(currentR, cR, _damping), gainR);
                newG[i] = ApplyGainInLinear(Lerp(currentG, cG, _damping), gainG);
                newB[i] = ApplyGainInLinear(Lerp(currentB, cB, _damping), gainB);
            }

            // The endpoints are anchored: 0→0, and white maps to the gained channel maxima.
            newR[0] = newG[0] = newB[0] = 0.0;
            EnforceMonotonic(newR); EnforceMonotonic(newG); EnforceMonotonic(newB);
            return (newR, newG, newB);
        }

        /// <summary>
        /// Mean grayscale deltaE2000 of a measurement set against the TARGET — the scalar the
        /// caller minimizes across rounds. Lower is better; this is what "keep best" compares.
        ///
        /// M1: the reference is the target white (at the measured peak), NOT the measured
        /// white. Grading against the measured white made white score ΔE = 0 by construction
        /// and graded gray neutrality against the current (possibly tinted) white — while
        /// <see cref="WhiteBalanceGains"/> actively moves white, so keep-best could revert a
        /// genuine white-point improvement. With the target-anchored reference the score has
        /// two visible components per patch: tone error (L* vs the target EOTF) and
        /// neutrality error (a*/b* vs the target white chromaticity), both folded into one
        /// ΔE2000 so a white-only improvement strictly lowers the residual.
        /// </summary>
        public double GrayscaleResidualDeltaE(IReadOnlyList<MeasurementResult> measurements)
        {
            var grey = ExtractGrayscale(measurements);
            if (grey.Count == 0) return double.MaxValue;

            // Scale to the measured white endpoint so we compare tone/neutrality, not
            // absolute luminance (the calibration cannot add light).
            double whiteY = grey[^1].Y;
            if (whiteY <= 0) return double.MaxValue;

            var refWhite = TargetLabWhite();
            double sum = 0;
            int n = 0;
            foreach (var p in grey)
            {
                double targetLin = Clamp01(_effectiveTarget.ApplyEotf(p.V));
                // Target neutral at this level: target-white chromaticity at relative
                // luminance targetLin (Y = 1 at white).
                var targetXyz = new CieXyz(refWhite.X * targetLin, refWhite.Y * targetLin, refWhite.Z * targetLin);
                var measuredRel = new CieXyz(p.Xyz.X / whiteY, p.Xyz.Y / whiteY, p.Xyz.Z / whiteY);
                var measuredLab = ColorMath.XyzToLab(measuredRel, refWhite);
                var targetLab = ColorMath.XyzToLab(targetXyz, refWhite);
                sum += measuredLab.DeltaE2000(targetLab);
                n++;
            }
            return n > 0 ? sum / n : double.MaxValue;
        }

        /// <summary>
        /// Decomposes a final closed-loop VCGT into a NEUTRAL tone curve plus per-channel
        /// linear white gains, in the native panel's light domain:
        ///
        ///   channel[i] = OETF_native(gain_c · EOTF_native(neutral[i]))
        ///
        /// The neutral component carries the tone (gamma-tracking) correction shared by all
        /// channels; the gains carry the chromatic white-balance part. Install path use (M4):
        /// ship the neutral curve as the MHC2 tone LUT and let the profile MATRIX do the
        /// chromatic job — shipping the gained channels as well would white-balance twice.
        /// Gains are normalized to mean 1 in linear light, so the achromatic part of the
        /// white balance stays in the neutral curve where it belongs.
        /// </summary>
        public static (double[] NeutralTone, double GainR, double GainG, double GainB) DecomposeCorrection(
            (double[] R, double[] G, double[] B) correction, ToneCurve nativeTone)
        {
            if (nativeTone == null) throw new ArgumentNullException(nameof(nativeTone));
            int n = Math.Min(correction.R?.Length ?? 0, Math.Min(correction.G?.Length ?? 0, correction.B?.Length ?? 0));
            if (n < 2)
                return (IdentityLut(), 1.0, 1.0, 1.0);

            // Per-channel linear light at the white end defines the gains.
            double lr = nativeTone.Lookup(Clamp01(correction.R![n - 1]));
            double lg = nativeTone.Lookup(Clamp01(correction.G![n - 1]));
            double lb = nativeTone.Lookup(Clamp01(correction.B![n - 1]));
            double mean = (lr + lg + lb) / 3.0;

            double gainR = 1.0, gainG = 1.0, gainB = 1.0;
            if (mean > 1e-6 && lr > 1e-9 && lg > 1e-9 && lb > 1e-9)
            {
                gainR = lr / mean;
                gainG = lg / mean;
                gainB = lb / mean;
            }

            var neutral = new double[n];
            for (int i = 0; i < n; i++)
            {
                // De-gain each channel in linear light, then average: if the channels differ
                // only by the white gains this recovers the exact shared tone curve.
                double linR = nativeTone.Lookup(Clamp01(correction.R[i])) / gainR;
                double linG = nativeTone.Lookup(Clamp01(correction.G[i])) / gainG;
                double linB = nativeTone.Lookup(Clamp01(correction.B[i])) / gainB;
                neutral[i] = Clamp01(nativeTone.InverseLookup(Clamp01((linR + linG + linB) / 3.0)));
            }
            EnforceMonotonic(neutral);
            return (neutral, gainR, gainG, gainB);
        }

        /// <summary>
        /// Recomposes a channel from the <see cref="DecomposeCorrection"/> parts (test hook /
        /// documentation of the decomposition model).
        /// </summary>
        public static double[] RecomposeChannel(double[] neutralTone, double gain, ToneCurve nativeTone)
        {
            if (neutralTone == null) throw new ArgumentNullException(nameof(neutralTone));
            if (nativeTone == null) throw new ArgumentNullException(nameof(nativeTone));
            var channel = new double[neutralTone.Length];
            for (int i = 0; i < neutralTone.Length; i++)
                channel[i] = Clamp01(nativeTone.InverseLookup(Clamp01(gain * nativeTone.Lookup(Clamp01(neutralTone[i])))));
            return channel;
        }

        /// <summary>
        /// Wires measured display data into the calibration target where the target's EOTF
        /// depends on it. Today that is BT.1886 (m4): its a/b coefficients are defined by the
        /// display's Lw/Lb, so the measured black is scaled into the target's white domain to
        /// preserve the MEASURED CONTRAST RATIO (the EOTF only depends on Lb/Lw). All other
        /// transfer functions pass through unchanged.
        /// </summary>
        public static CalibrationTarget MakeEffectiveTarget(
            CalibrationTarget target, DisplayCharacterization characterization)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (characterization == null) return target;
            if (target.TransferFunction != TransferFunctionType.Bt1886) return target;
            if (!(characterization.PeakLuminance > 0.0) || !(characterization.BlackLevel > 0.0) ||
                !double.IsFinite(characterization.PeakLuminance) || !double.IsFinite(characterization.BlackLevel) ||
                characterization.BlackLevel >= characterization.PeakLuminance)
            {
                return target;
            }

            double targetWhite = target.PeakLuminance ?? target.ReferenceWhite ?? 0.0;
            if (!double.IsFinite(targetWhite) || targetWhite <= 0.0) targetWhite = 100.0;
            double scaledBlack = characterization.BlackLevel / characterization.PeakLuminance * targetWhite;
            return target.WithBlackLevel(scaledBlack);
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

        /// <summary>
        /// Lab reference white for the residual score: the TARGET's white. Uses the shared
        /// D65 constant verbatim for D65 targets so scores stay bit-comparable with the
        /// verifier's default Lab reference.
        /// </summary>
        private CieXyz TargetLabWhite() =>
            _target.WhitePoint.Equals(Chromaticity.D65) ? ColorMath.D65White : _target.WhitePoint.ToXyz(1.0);

        /// <summary>
        /// Applies a LINEAR-light gain to a gamma-encoded VCGT output value:
        /// OETF(gain · EOTF(signal)) through the panel's measured tone curve (M2). Falls back
        /// to the target's transfer pair when no characterization has been captured yet
        /// (refinement invoked standalone) — the panel is then assumed to track the target,
        /// which is exact for Linear test targets and a far better approximation than
        /// multiplying in the encoded domain.
        /// </summary>
        private double ApplyGainInLinear(double signal, double gain)
        {
            signal = Clamp01(signal);
            if (!double.IsFinite(gain) || Math.Abs(gain - 1.0) < 1e-12) return signal;

            if (_nativeTone != null)
                return Clamp01(_nativeTone.InverseLookup(Clamp01(gain * _nativeTone.Lookup(signal))));

            return Clamp01(_effectiveTarget.ApplyOetf(Clamp01(gain * _effectiveTarget.ApplyEotf(signal))));
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

            // Gain needed to move measured ratios toward target ratios. Renormalize so the
            // LARGEST gain is 1.0: signals cannot exceed full scale, so a >1 gain saturates
            // at white and leaves the white point untouched — white balance on a display
            // only ever works by pulling the excessive channels DOWN.
            double gR = mr > 1e-4 ? tr / mr : 1.0;
            double gG = mg > 1e-4 ? tg / mg : 1.0;
            double gB = mb > 1e-4 ? tb / mb : 1.0;
            double gMax = Math.Max(gR, Math.Max(gG, gB));
            if (!double.IsFinite(gMax) || gMax <= 0) return (1, 1, 1);
            gR /= gMax; gG /= gMax; gB /= gMax;

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

        private static double[] IdentityLut()
        {
            var lut = new double[LutSize];
            for (int i = 0; i < LutSize; i++)
                lut[i] = i / (double)(LutSize - 1);
            return lut;
        }

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
