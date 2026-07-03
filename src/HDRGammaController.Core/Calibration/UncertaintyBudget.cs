using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Running per-luminance-decade noise model built from the observed Y spread of
    /// multi-read patch bursts (roadmap 1.4). The calibration orchestrator feeds it live
    /// during a run so subsequent single-read patches in a proven-noisy luminance regime
    /// are automatically escalated to median-of-3; the uncertainty budget (1.3) reuses it
    /// so single-read patches inherit a repeatability estimate from their decade.
    /// </summary>
    /// <remarks>
    /// Bins are luminance decades in absolute cd/m²: &lt;0.1, 0.1–1, 1–10, &gt;10. Meter
    /// noise on real panels is strongly luminance-dependent (dark VA/OLED readings are
    /// noise-dominated; bright ones are quiet), and a decade is coarse enough that a few
    /// multi-read bursts populate a bin with a usable estimate. Each bin keeps an
    /// exponentially-weighted mean of the RELATIVE spread (max−min over mean) of the
    /// read bursts that landed there, plus a hysteresis noisy/quiet flag so the
    /// escalation decision doesn't chatter around a single threshold.
    /// </remarks>
    public sealed class LuminanceNoiseModel
    {
        /// <summary>Upper edge of the darkest decade bin, cd/m².</summary>
        public const double DecadeBin0MaxY = 0.1;

        /// <summary>Upper edge of the second decade bin, cd/m².</summary>
        public const double DecadeBin1MaxY = 1.0;

        /// <summary>Upper edge of the third decade bin, cd/m²; everything above is the bright bin.</summary>
        public const double DecadeBin2MaxY = 10.0;

        /// <summary>Number of luminance decade bins.</summary>
        public const int BinCount = 4;

        /// <summary>
        /// EWMA weight for new spread observations. 0.3 means the estimate is dominated
        /// by the last ~5 bursts — responsive to warm-up changes without whiplashing on
        /// a single outlier burst.
        /// </summary>
        public const double EwmaWeight = 0.3;

        /// <summary>
        /// A bin whose EWMA relative spread exceeds this is NOISY: subsequent single-read
        /// patches in that decade are escalated to median-of-3 and get lengthened settle.
        /// ~2% relative spread across a 3-read burst is well past normal meter repeatability.
        /// </summary>
        public const double NoisyRelativeSpread = 0.02;

        /// <summary>
        /// A noisy bin whose EWMA drops below this returns to QUIET (single reads). The
        /// gap between 0.5% and 2% is deliberate hysteresis so the decision is stable.
        /// </summary>
        public const double QuietRelativeSpread = 0.005;

        /// <summary>
        /// Floor on the denominator when converting an absolute Y spread to a relative
        /// one. Near black the mean approaches zero and a raw ratio would explode to
        /// nonsense; 0.01 cd/m² caps the relative spread at (spread / 0.01) while still
        /// flagging genuinely noisy dark readings.
        /// </summary>
        public const double RelativeSpreadFloorY = 0.01;

        private readonly double?[] _relativeSpreadEwma = new double?[BinCount];
        private readonly bool[] _noisy = new bool[BinCount];

        /// <summary>Decade bin index for an absolute luminance in cd/m².</summary>
        public static int BinIndex(double y)
        {
            if (!double.IsFinite(y) || y < 0) y = 0;
            if (y < DecadeBin0MaxY) return 0;
            if (y < DecadeBin1MaxY) return 1;
            if (y < DecadeBin2MaxY) return 2;
            return 3;
        }

        /// <summary>
        /// Records the observed spread of one multi-read burst: mean Y of the reads and
        /// the max−min Y spread, both in cd/m². Updates the bin's EWMA and its
        /// noisy/quiet flag (with hysteresis: between the two thresholds the flag holds
        /// its previous state).
        /// </summary>
        public void Record(double meanY, double spreadY)
        {
            if (!double.IsFinite(meanY) || !double.IsFinite(spreadY) || spreadY < 0)
                return;

            double relative = spreadY / Math.Max(meanY, RelativeSpreadFloorY);
            int bin = BinIndex(meanY);

            _relativeSpreadEwma[bin] = _relativeSpreadEwma[bin] is double previous
                ? previous + EwmaWeight * (relative - previous)
                : relative;

            double estimate = _relativeSpreadEwma[bin]!.Value;
            if (estimate > NoisyRelativeSpread)
                _noisy[bin] = true;
            else if (estimate < QuietRelativeSpread)
                _noisy[bin] = false;
        }

        /// <summary>Whether the decade containing <paramref name="y"/> is currently flagged noisy.</summary>
        public bool IsNoisy(double y) => _noisy[BinIndex(y)];

        /// <summary>
        /// EWMA relative-spread estimate for the decade containing <paramref name="y"/>.
        /// Falls back to the average of the populated bins when this bin has no data yet
        /// (a single-read patch may sit in a decade no burst landed in), and null when
        /// the model is completely empty.
        /// </summary>
        public double? RelativeSpreadEstimate(double y)
        {
            if (_relativeSpreadEwma[BinIndex(y)] is double direct)
                return direct;

            var populated = _relativeSpreadEwma.Where(e => e.HasValue).Select(e => e!.Value).ToList();
            return populated.Count > 0 ? populated.Average() : null;
        }

        /// <summary>
        /// Rebuilds a noise model from the per-measurement read metadata recorded by the
        /// orchestrator (<see cref="MeasurementResult.ReadingCount"/> /
        /// <see cref="MeasurementResult.ReadingSpreadY"/>). Lets consumers that only have
        /// the measurement list (report window, tests) recover the run's noise picture
        /// without plumbing the live model through every layer.
        /// </summary>
        public static LuminanceNoiseModel FromMeasurements(IEnumerable<MeasurementResult> measurements)
        {
            var model = new LuminanceNoiseModel();
            foreach (var m in measurements)
            {
                if (m.IsValid && m.ReadingCount > 1 && m.ReadingSpreadY is double spread)
                    model.Record(m.Xyz.Y, spread);
            }
            return model;
        }
    }

    /// <summary>
    /// Measurement-uncertainty budget for calibration metrics (roadmap 1.3), combining
    /// independent standard-uncertainty terms per ISO GUM quadrature (root-sum-square)
    /// and reporting an expanded ± interval at k=2 (~95% coverage).
    /// </summary>
    /// <remarks>
    /// Three terms are combined for average-ΔE-class metrics:
    /// <list type="bullet">
    /// <item><b>Repeatability</b> — from each patch's recorded read spread (standard error
    /// of the median ≈ 1.2533·σ/√n; σ recovered from the max−min range via the standard
    /// d₂ range factors). Single-read patches inherit the relative-spread estimate of
    /// their luminance decade from <see cref="LuminanceNoiseModel"/>. Per-patch Y noise
    /// is propagated to ΔE by a numeric two-point sensitivity (ΔE evaluated at Y±σ), not
    /// calculus. Patch noise is treated as independent, so the term on the AVERAGE ΔE is
    /// √(Σuᵢ²)/N.</item>
    /// <item><b>Instrument/correction</b> — a fixed table by instrument class and spectral
    /// correction state; a systematic term shared by every patch, so it enters the budget
    /// once (not reduced by √N). These are ENGINEERING ESTIMATES, see the constants.</item>
    /// <item><b>Drift residual</b> — what may remain after (or without)
    /// <see cref="DriftCompensator"/>'s multiplicative normalization, derived from the
    /// observed peak white drift fraction.</item>
    /// </list>
    /// Everything here is pure: no I/O, no clocks, no instrument access.
    /// </remarks>
    public static class UncertaintyBudget
    {
        #region Instrument term (engineering estimates, see comments)

        /// <summary>Instrument class + correction state for the fixed instrument term.</summary>
        public enum InstrumentClass
        {
            /// <summary>Filter colorimeter with a panel-matched spectral correction (.ccss/.ccmx) loaded.</summary>
            ColorimeterWithCorrection,

            /// <summary>Filter colorimeter with no spectral correction for the panel.</summary>
            ColorimeterGeneric,

            /// <summary>Spectrometer (i1 Pro / ColorMunki class).</summary>
            Spectrometer,
        }

        // The instrument-term values below are ENGINEERING ESTIMATES, not certified
        // uncertainties: they summarize typical inter-instrument agreement reported for
        // this hardware class (X-Rite i1 Display 3 family agreement studies in the
        // DisplayCAL/ArgyllCMS community and vendor inter-instrument specs), expressed
        // as a ΔE2000-equivalent standard uncertainty toward white.

        /// <summary>
        /// Colorimeter with a matching spectral correction: i1D3-class units corrected
        /// with a panel-matched CCSS typically agree with a reference spectro within
        /// ~0.5–1 ΔE toward white; ≈0.5 std-u. Engineering estimate.
        /// </summary>
        public const double ColorimeterWithCorrectionStdU = 0.5;

        /// <summary>
        /// Colorimeter with no correction: filter/observer mismatch on modern
        /// narrow-primary panels (QD-OLED, WLED-PFS) commonly costs 1–3 ΔE toward white;
        /// ≈1.5 std-u. Engineering estimate.
        /// </summary>
        public const double ColorimeterGenericStdU = 1.5;

        /// <summary>
        /// Spectrometer: i1 Pro 2 class inter-instrument agreement is typically
        /// ~0.2–0.4 ΔE on emissive white; ≈0.3 std-u. Engineering estimate.
        /// </summary>
        public const double SpectrometerStdU = 0.3;

        /// <summary>Fixed instrument/correction standard uncertainty (ΔE2000-equivalent).</summary>
        public static double InstrumentTermStdU(InstrumentClass instrument) => instrument switch
        {
            InstrumentClass.ColorimeterWithCorrection => ColorimeterWithCorrectionStdU,
            InstrumentClass.Spectrometer => SpectrometerStdU,
            _ => ColorimeterGenericStdU,
        };

        /// <summary>
        /// Classifies the instrument from its reported model name plus whether a spectral
        /// correction (.ccss/.ccmx) is loaded. Name matching is a heuristic over the
        /// Argyll-reported model strings; anything not recognizably a spectrometer is
        /// treated as a filter colorimeter (the conservative direction).
        /// </summary>
        public static InstrumentClass ClassifyInstrument(string? model, bool hasSpectralCorrection)
        {
            string m = model?.ToLowerInvariant() ?? string.Empty;
            bool isSpectrometer =
                m.Contains("i1 pro") || m.Contains("i1pro") ||
                m.Contains("munki") || m.Contains("spectro");
            if (isSpectrometer)
                return InstrumentClass.Spectrometer;

            return hasSpectralCorrection
                ? InstrumentClass.ColorimeterWithCorrection
                : InstrumentClass.ColorimeterGeneric;
        }

        #endregion

        #region Statistical constants

        /// <summary>Coverage factor for the expanded uncertainty (k=2 ≈ 95% for a normal error model).</summary>
        public const double CoverageFactorK = 2.0;

        /// <summary>
        /// Statistical efficiency penalty of the median vs the mean for normal noise:
        /// SE(median) ≈ 1.2533·σ/√n (= √(π/2)).
        /// </summary>
        public const double MedianEfficiencyFactor = 1.2533;

        /// <summary>
        /// d₂ range-to-sigma factors: E[max−min] = d₂(n)·σ for n normal samples.
        /// Index by n (2..5); the orchestrator takes at most 4 reads.
        /// </summary>
        private static readonly double[] RangeToSigmaD2 = { 0, 0, 1.128, 1.693, 2.059, 2.326 };

        /// <summary>
        /// Fraction of the observed peak white drift assumed to survive as residual after
        /// <see cref="DriftCompensator"/>'s piecewise-linear normalization (the fit is
        /// exact AT the anchors; between anchors up to roughly a quarter of the swing can
        /// be missed). Engineering estimate.
        /// </summary>
        public const double DriftResidualFraction = 0.25;

        /// <summary>
        /// First-order ΔL* per unit RELATIVE luminance error near mid-gray:
        /// L* = 116·(Y/Yn)^⅓ − 16 gives dL*/d(lnY) = (116/3)·(Y/Yn)^⅓ ≈ 22 at L* = 50.
        /// Used to express a relative-luminance drift residual as a ΔE-equivalent.
        /// </summary>
        public const double LStarPerRelativeLuminance = 22.0;

        private static readonly double SqrtThree = Math.Sqrt(3.0);

        /// <summary>σ recovered from a max−min range of n reads via the d₂ factor.</summary>
        public static double RangeToSigma(double range, int readCount)
        {
            if (!double.IsFinite(range) || range <= 0 || readCount < 2)
                return 0;
            int n = Math.Min(readCount, RangeToSigmaD2.Length - 1);
            return range / RangeToSigmaD2[n];
        }

        #endregion

        #region Per-patch repeatability

        /// <summary>
        /// Standard uncertainty of a patch's reported Y (cd/m²) from measurement
        /// repeatability. Multi-read patches use their own recorded spread: σ from the
        /// range via d₂, then the standard error of the median 1.2533·σ/√n. Single-read
        /// patches inherit their luminance decade's relative-spread estimate from the
        /// noise model (converted to σ with d₂(3), the burst size that fed the model);
        /// with no model data the term is 0 — the budget then honestly collapses to the
        /// instrument floor rather than inventing noise.
        /// </summary>
        public static double RepeatabilityYStdU(
            double measuredY, int readingCount, double? readingSpreadY, LuminanceNoiseModel? noiseModel)
        {
            if (!double.IsFinite(measuredY) || measuredY < 0)
                return 0;

            if (readingCount > 1 && readingSpreadY is double spread)
            {
                double sigma = RangeToSigma(spread, readingCount);
                return MedianEfficiencyFactor * sigma / Math.Sqrt(readingCount);
            }

            double? relativeSpread = noiseModel?.RelativeSpreadEstimate(measuredY);
            if (relativeSpread is not double rel || rel <= 0)
                return 0;

            // The model stores the relative RANGE of 3-read bursts; recover σ_rel with
            // d₂(3) and scale by the patch's own Y (floored like the model's ratios).
            double sigmaRel = rel / RangeToSigmaD2[3];
            return sigmaRel * Math.Max(measuredY, LuminanceNoiseModel.RelativeSpreadFloorY);
        }

        #endregion

        #region Drift residual

        /// <summary>
        /// ΔE-equivalent standard uncertainty left by luminance drift. When compensation
        /// ran, only <see cref="DriftResidualFraction"/> of the observed peak drift is
        /// assumed to survive; when it did not run (drift observed but uncorrected), the
        /// full observed drift stands in the data. Either way the surviving relative
        /// error is treated as a rectangular distribution (÷√3 to a std-u) and expressed
        /// in ΔE via <see cref="LStarPerRelativeLuminance"/>.
        /// </summary>
        public static double DriftResidualStdU(double? peakWhiteDriftFraction, bool driftCompensated)
        {
            if (peakWhiteDriftFraction is not double peak || !double.IsFinite(peak) || peak <= 0)
                return 0;

            double survivingRelative = driftCompensated ? DriftResidualFraction * peak : peak;
            return LStarPerRelativeLuminance * survivingRelative / SqrtThree;
        }

        #endregion

        #region Combination

        /// <summary>Root-sum-square of independent standard-uncertainty terms (GUM quadrature).</summary>
        public static double Rss(params double[] terms)
        {
            double sum = 0;
            foreach (double t in terms)
            {
                if (double.IsFinite(t))
                    sum += t * t;
            }
            return Math.Sqrt(sum);
        }

        /// <summary>
        /// Inputs the metric grader supplies when it wants an uncertainty alongside the
        /// metrics: the instrument class, the run's noise model (for single-read
        /// inheritance) and the drift observation from <see cref="DriftCompensator"/>
        /// (as surfaced on CalibrationResult.PeakWhiteDriftFraction / DriftCompensationApplied).
        /// </summary>
        public sealed record Context(
            InstrumentClass Instrument,
            LuminanceNoiseModel? NoiseModel,
            double? PeakWhiteDriftFraction,
            bool DriftCompensated);

        /// <summary>One patch's contribution: its Y std-u and the ΔE-propagated std-u.</summary>
        public sealed record PatchTerm(string Name, double MeasuredY, double YStdU, double DeltaEStdU);

        /// <summary>Per-patch luminance ± for tone metrics (expanded at the budget's k).</summary>
        public sealed record LuminanceUncertainty(string Name, double MeasuredY, double YStdU, double ExpandedYU);

        /// <summary>The combined budget for an average-ΔE-class metric.</summary>
        public sealed record Result(
            double RepeatabilityStdU,
            double InstrumentStdU,
            double DriftStdU,
            double CombinedStdU,
            double ExpandedU,
            double CoverageFactor,
            int PatchCount,
            IReadOnlyList<LuminanceUncertainty> PerPatchLuminance)
        {
            /// <summary>Human-readable breakdown naming the three terms, for tooltips/logs.</summary>
            public string Describe() =>
                $"± {ExpandedU:F2} ΔE (k={CoverageFactor:F0}, ~95%) — std-u terms combined in quadrature: " +
                $"repeatability {RepeatabilityStdU:F2}, instrument/correction {InstrumentStdU:F2}, drift residual {DriftStdU:F2}";
        }

        /// <summary>
        /// Combines per-patch terms with the instrument and drift terms into the budget
        /// for an average-ΔE metric. Per-patch repeatability is independent noise, so its
        /// effect on the average is √(Σuᵢ²)/N; the instrument and drift terms are
        /// systematic across the run and enter once.
        /// </summary>
        public static Result Combine(
            IReadOnlyList<PatchTerm> patches,
            InstrumentClass instrument,
            double? peakWhiteDriftFraction,
            bool driftCompensated)
        {
            double repeatability = 0;
            if (patches.Count > 0)
            {
                double sumSquares = patches.Sum(p =>
                    double.IsFinite(p.DeltaEStdU) ? p.DeltaEStdU * p.DeltaEStdU : 0);
                repeatability = Math.Sqrt(sumSquares) / patches.Count;
            }

            double instrumentU = InstrumentTermStdU(instrument);
            double driftU = DriftResidualStdU(peakWhiteDriftFraction, driftCompensated);
            double combined = Rss(repeatability, instrumentU, driftU);

            var perPatch = patches
                .Select(p => new LuminanceUncertainty(p.Name, p.MeasuredY, p.YStdU, CoverageFactorK * p.YStdU))
                .ToList();

            return new Result(
                repeatability,
                instrumentU,
                driftU,
                combined,
                CoverageFactorK * combined,
                CoverageFactorK,
                patches.Count,
                perPatch);
        }

        #endregion
    }
}
