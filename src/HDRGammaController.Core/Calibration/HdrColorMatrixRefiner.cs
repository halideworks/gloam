using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Closed-loop HDR COLOR correction (roadmap 2.2): fits a 3×3 XYZ correction from
    /// colored + neutral stimuli measured THROUGH the installed matrix+LUT correction, so
    /// panels whose gamut rotates with luminance (most QD-OLEDs) get their measured color
    /// error folded back into the MHC2 gamut matrix. The open-loop matrix is built from
    /// chromaticities measured at ONE drive level; this is the measured multi-level
    /// closed-loop step nothing on the market performs.
    /// </summary>
    /// <remarks>
    /// Model: with correction installed, measured XYZ ≈ F · reference XYZ for a linear
    /// residual F (the panel-vs-characterization mismatch pushed through the chain).
    /// The refit installs M′ = D⁻¹·F⁻¹·T (see CalibrationProfileInstaller's
    /// xyzCorrectionOverride), which cancels F to first order. F is fit by
    /// luminance-weighted least squares over all rungs: each pair is normalized to its
    /// rung's luminance (equal relative weight per stimulus) and weighted by √Y_rung so
    /// brighter, perceptually-dominant rungs count more. Neutral (White) stimuli anchor
    /// the fit — see ColoredHdrVerificationSet.BuildForMatrixRefinement.
    /// </remarks>
    public static class HdrColorMatrixRefiner
    {
        /// <summary>Minimum valid stimuli for a stable 9-parameter fit with headroom.</summary>
        public const int MinValidPatches = 8;

        /// <summary>Avg ΔE ITP beyond which a linear touch-up is the wrong tool (profile
        /// not active, night mode riding the ramp, panel moved).</summary>
        public const double MaxAvgItpToRefine = 25.0;

        /// <summary>Avg ΔE ITP below which a reinstall is churn, not improvement (~2 JND
        /// against colored wide-gamut stimuli is instrument-noise territory).</summary>
        public const double ConvergedAvgItp = 2.0;

        /// <summary>Per-element cap on |F − I|: a sane residual is a small rotation.</summary>
        public const double MaxCorrectionDeviation = 0.25;

        /// <summary>
        /// A stimulus whose measured luminance falls below this fraction of its target is
        /// treated as UNREACHABLE (the panel clipped that saturated primary), not as a
        /// correctable error. A gamut matrix cannot add light the panel can't emit — e.g.
        /// "Blue 203 nits" on a panel whose blue channel maxes near 60 nits — so such
        /// readings are excluded from both the fit and the grade that gates it. This is what
        /// keeps a good (sub-1 ΔE) calibration from being told its colors are "way off": the
        /// off-ness is the panel's saturated-primary luminance ceiling, a hardware limit.
        /// </summary>
        public const double ReachableLumFraction = 0.85;

        public sealed record RefinementResult(
            double[,]? XyzCorrection,
            string? RefusalReason,
            CalibrationVerifier.ColoredHdrMetrics Before,
            int ExcludedUnreachable = 0);

        /// <summary>The readings the panel can physically produce (not clipped) — the only
        /// ones a matrix fit or its grade should consider.</summary>
        public static IReadOnlyList<(ColoredHdrStimulus Stimulus, CieXyz Measured)> Reachable(
            IEnumerable<(ColoredHdrStimulus Stimulus, CieXyz Measured)> readings)
            => readings
                .Where(r => IsPhysical(r.Measured) && r.Stimulus.RungNits > 0 &&
                            r.Measured.Y >= r.Stimulus.RungNits * ReachableLumFraction)
                .ToList();

        /// <summary>Average ΔE ITP over the reachable readings only (NaN if none) — the
        /// figure the loop gates and reports on, so saturated clipping never poisons it.</summary>
        public static double AverageReachableItp(
            IEnumerable<(ColoredHdrStimulus Stimulus, CieXyz Measured)> readings)
        {
            var reachable = Reachable(readings);
            if (reachable.Count == 0) return double.NaN;
            return CalibrationVerifier.GradeColoredHdr(
                reachable.Select(r => (r.Stimulus, r.Measured))).AverageItpDeltaE;
        }

        /// <summary>
        /// Fits the XYZ correction F (measured ≈ F·reference). Returns a refusal instead
        /// of a matrix when refinement cannot help or the fit is unstable.
        /// </summary>
        public static RefinementResult Fit(
            IReadOnlyList<(ColoredHdrStimulus Stimulus, CieXyz Measured)> readings,
            double damping = 1.0)
        {
            ArgumentNullException.ThrowIfNull(readings);
            if (!double.IsFinite(damping) || damping <= 0.0 || damping > 1.0)
                throw new ArgumentOutOfRangeException(nameof(damping));

            // Exclude clipped/unreachable stimuli: a matrix can't correct luminance the panel
            // never emitted (saturated blue/red at high rungs). Grade and fit on what's real.
            var reachable = Reachable(readings);
            int excluded = readings.Count(r => IsPhysical(r.Measured) && r.Stimulus.RungNits > 0) - reachable.Count;

            var before = reachable.Count > 0
                ? CalibrationVerifier.GradeColoredHdr(reachable.Select(r => (r.Stimulus, r.Measured)))
                : CalibrationVerifier.GradeColoredHdr(readings.Select(r => (r.Stimulus, r.Measured)));

            var valid = reachable;
            if (valid.Count < MinValidPatches)
            {
                return new RefinementResult(null,
                    $"only {valid.Count} reachable colored reading(s) after excluding {excluded} that exceed the " +
                    "panel's saturated-primary luminance (need at least " +
                    $"{MinValidPatches}) — the panel can't display these colors that bright, which a matrix can't fix",
                    before, excluded);
            }
            if (!valid.Any(r => r.Stimulus.Hue == "White"))
            {
                return new RefinementResult(null,
                    "no neutral (White) stimuli in the sweep — an unanchored matrix fit can rotate the white point",
                    before);
            }
            if (!double.IsFinite(before.AverageItpDeltaE))
                return new RefinementResult(null, "colored sweep produced no gradable readings", before);
            if (before.AverageItpDeltaE < ConvergedAvgItp)
            {
                return new RefinementResult(null,
                    $"already converged (avg ΔE ITP {before.AverageItpDeltaE:F1}) — a matrix touch-up would not improve it",
                    before);
            }
            if (before.AverageItpDeltaE > MaxAvgItpToRefine)
            {
                return new RefinementResult(null,
                    $"avg ΔE ITP {before.AverageItpDeltaE:F1} is beyond the ±{MaxAvgItpToRefine:F0} refinement window — " +
                    "something else is wrong (profile not active, night mode on, or panel drift); re-run the calibration instead",
                    before);
            }

            // Luminance-weighted normal equations for F: measured ≈ F·reference.
            // Pairs normalized by rung luminance; weight √Y_rung.
            var mrT = new double[3, 3]; // Σ w·m·rᵀ
            var rrT = new double[3, 3]; // Σ w·r·rᵀ
            foreach (var (stimulus, measured) in valid)
            {
                double y = stimulus.RungNits;
                double w = Math.Sqrt(y);
                double[] r = { stimulus.ReferenceXyz.X / y, stimulus.ReferenceXyz.Y / y, stimulus.ReferenceXyz.Z / y };
                double[] m = { measured.X / y, measured.Y / y, measured.Z / y };
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        mrT[i, j] += w * m[i] * r[j];
                        rrT[i, j] += w * r[i] * r[j];
                    }
                }
            }

            double[,] f;
            try
            {
                f = ColorMath.MultiplyMatrices(mrT, ColorMath.Invert3x3(rrT));
            }
            catch (Exception ex)
            {
                return new RefinementResult(null, $"matrix fit is singular ({ex.Message})", before);
            }

            // Damp toward identity, then sanity-check the correction is a small residual.
            var damped = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    double identity = i == j ? 1.0 : 0.0;
                    damped[i, j] = identity + damping * (f[i, j] - identity);
                    if (!double.IsFinite(damped[i, j]))
                        return new RefinementResult(null, "matrix fit produced non-finite coefficients", before);
                    if (Math.Abs(damped[i, j] - identity) > MaxCorrectionDeviation)
                    {
                        return new RefinementResult(null,
                            $"fitted correction deviates {Math.Abs(damped[i, j] - identity):F2} from identity at " +
                            $"[{i},{j}] (cap {MaxCorrectionDeviation:F2}) — the residual is not a small linear error; " +
                            "re-run the calibration instead",
                            before);
                    }
                }
            }

            return new RefinementResult(damped, null, before, excluded);
        }

        /// <summary>
        /// Separates color correction from neutral-axis tone correction for the joint HDR
        /// solver. The fitted residual F may contain a uniform luminance gain because its
        /// white anchors participate in the unconstrained XYZ least-squares fit. Dividing F
        /// by its gain on the target white leaves the white chromaticity rotation intact but
        /// makes Y(F*w) == Y(w), so the PQ LUT alone owns neutral luminance tracking and the
        /// two corrections cannot double-count the same white error.
        /// </summary>
        public static double[,] PreserveNeutralLuminance(double[,] xyzResidual, CieXyz targetWhite)
        {
            ArgumentNullException.ThrowIfNull(xyzResidual);
            if (xyzResidual.GetLength(0) != 3 || xyzResidual.GetLength(1) != 3)
                throw new ArgumentException("XYZ residual must be a 3x3 matrix.", nameof(xyzResidual));
            if (!IsPhysical(targetWhite))
                throw new ArgumentException("Target white must be a finite positive XYZ value.", nameof(targetWhite));

            double mappedY = xyzResidual[1, 0] * targetWhite.X +
                             xyzResidual[1, 1] * targetWhite.Y +
                             xyzResidual[1, 2] * targetWhite.Z;
            double gain = mappedY / targetWhite.Y;
            if (!double.IsFinite(gain) || gain <= 0.0)
                throw new InvalidOperationException("Fitted HDR color residual has no physical neutral luminance gain.");

            var normalized = new double[3, 3];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    normalized[row, col] = xyzResidual[row, col] / gain;
            return normalized;
        }

        private static bool IsPhysical(CieXyz xyz) =>
            double.IsFinite(xyz.X) && double.IsFinite(xyz.Y) && double.IsFinite(xyz.Z) &&
            xyz.Y > 0 && xyz.X >= 0 && xyz.Z >= 0;
    }

    /// <summary>
    /// Keep-best iteration of <see cref="HdrColorMatrixRefiner"/>, mirroring
    /// <see cref="HdrRefinementLoop"/>: measure the colored sweep, fit, install the
    /// CUMULATIVE correction, re-measure, keep whichever pass graded best (avg ΔE ITP),
    /// and end with the best correction installed. Cumulative because pass 2 fits the
    /// residual THROUGH pass 1's correction: F_total = F₂·F₁.
    /// </summary>
    public static class HdrColorMatrixLoop
    {
        public sealed class Config
        {
            public required IReadOnlyList<ColoredHdrStimulus> Stimuli { get; init; }

            /// <summary>Measures the stimuli through the currently installed correction.
            /// Arguments: stimuli, sequence offset, token.</summary>
            public required Func<IReadOnlyList<ColoredHdrStimulus>, int, CancellationToken,
                Task<IReadOnlyList<(ColoredHdrStimulus Stimulus, CieXyz Measured)>>> MeasureSweepAsync { get; init; }

            /// <summary>Installs the given cumulative XYZ correction (identity = baseline
            /// matrix) and returns the installed profile name.</summary>
            public required Func<double[,], CancellationToken, Task<string>> InstallAsync { get; init; }

            /// <summary>Colored sweeps are slow (~a minute each); two refine passes is the
            /// pragmatic cap — pass 1 takes the full fitted step, pass 2 cleans up damped.</summary>
            public int MaxRefinePasses { get; init; } = 2;
            public double FirstPassDamping { get; init; } = 1.0;
            public double LaterPassDamping { get; init; } = 0.6;
            public double ConvergedAvgItp { get; init; } = HdrColorMatrixRefiner.ConvergedAvgItp;
            public IProgress<HdrRefinementLoop.PassProgress>? Progress { get; init; }
        }

        public sealed record PassRecord(
            int Pass, double AvgItpBefore, double? AvgItpAfter, string? ProfileName, string? RefusalReason);

        public sealed record Outcome(
            bool AnyInstall,
            bool Converged,
            bool EndedOnBest,
            double[,] FinalCorrection,
            string? FinalProfileName,
            double InitialAvgItp,
            double FinalAvgItp,
            IReadOnlyList<PassRecord> Passes,
            string StopReason);

        public static double[,] IdentityCorrection() => new double[3, 3]
        {
            { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 },
        };

        public static async Task<Outcome> RunAsync(Config config, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (config.Stimuli.Count == 0)
                throw new ArgumentException("At least one stimulus is required.", nameof(config));

            var passes = new List<PassRecord>();
            int sequenceOffset = 0;

            config.Progress?.Report(new HdrRefinementLoop.PassProgress(
                0, config.MaxRefinePasses, "measuring colored sweep"));
            var measured = await config.MeasureSweepAsync(config.Stimuli, sequenceOffset, ct);
            sequenceOffset += measured.Count;

            // Grade on REACHABLE readings only — saturated primaries the panel clips (e.g.
            // blue at high rungs) are a hardware limit, not a matrix-fixable error, and must
            // not poison the initial figure or the convergence check.
            double initialItp = HdrColorMatrixRefiner.AverageReachableItp(measured);
            if (!double.IsFinite(initialItp))
                throw new InvalidOperationException("Initial colored sweep produced no reachable readings.");

            var identity = IdentityCorrection();
            var best = (Correction: identity, Itp: initialItp, ProfileName: (string?)null);
            var lastInstalled = best;
            var cumulative = identity;
            double currentItp = initialItp;
            bool anyInstall = false;
            bool converged = currentItp < config.ConvergedAvgItp;
            string stopReason = converged
                ? $"already converged (avg ΔE ITP {currentItp:F1})"
                : $"reached max passes ({config.MaxRefinePasses})";

            try
            {
                for (int pass = 1; pass <= config.MaxRefinePasses && !converged; pass++)
                {
                    ct.ThrowIfCancellationRequested();
                    double damping = pass == 1 ? config.FirstPassDamping : config.LaterPassDamping;

                    var fit = HdrColorMatrixRefiner.Fit(measured, damping);
                    if (fit.XyzCorrection == null)
                    {
                        converged = fit.Before.AverageItpDeltaE
                            < Math.Max(config.ConvergedAvgItp, HdrColorMatrixRefiner.ConvergedAvgItp);
                        stopReason = converged
                            ? $"converged (avg ΔE ITP {fit.Before.AverageItpDeltaE:F1})"
                            : $"refinement refused: {fit.RefusalReason}";
                        passes.Add(new PassRecord(pass, fit.Before.AverageItpDeltaE, null, null, fit.RefusalReason));
                        break;
                    }

                    // Pass N's fit is the residual through the current correction.
                    cumulative = ColorMath.MultiplyMatrices(fit.XyzCorrection, cumulative);

                    config.Progress?.Report(new HdrRefinementLoop.PassProgress(
                        pass, config.MaxRefinePasses, "installing"));
                    string profileName = await config.InstallAsync(cumulative, ct);
                    anyInstall = true;
                    lastInstalled = (cumulative, double.NaN, profileName);

                    config.Progress?.Report(new HdrRefinementLoop.PassProgress(
                        pass, config.MaxRefinePasses, "measuring colored sweep"));
                    measured = await config.MeasureSweepAsync(config.Stimuli, sequenceOffset, ct);
                    sequenceOffset += measured.Count;

                    double afterItp = HdrColorMatrixRefiner.AverageReachableItp(measured);
                    if (!double.IsFinite(afterItp))
                    {
                        stopReason = $"pass {pass} verification produced no reachable readings";
                        passes.Add(new PassRecord(pass, fit.Before.AverageItpDeltaE, null, profileName, null));
                        break;
                    }

                    passes.Add(new PassRecord(pass, fit.Before.AverageItpDeltaE, afterItp, profileName, null));
                    lastInstalled = (cumulative, afterItp, profileName);
                    currentItp = afterItp;

                    if (afterItp < best.Itp)
                        best = (cumulative, afterItp, profileName);

                    if (afterItp < config.ConvergedAvgItp)
                    {
                        converged = true;
                        stopReason = $"converged (avg ΔE ITP {afterItp:F1})";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (anyInstall && !ReferenceEquals(lastInstalled.Correction, best.Correction))
                {
                    try
                    {
                        best = (best.Correction, best.Itp,
                            await config.InstallAsync(best.Correction, CancellationToken.None));
                    }
                    catch
                    {
                        // Best effort — the caller's cancel path reports the state.
                    }
                }
                throw;
            }

            bool endedOnBest = false;
            if (anyInstall && !ReferenceEquals(lastInstalled.Correction, best.Correction))
            {
                config.Progress?.Report(new HdrRefinementLoop.PassProgress(
                    passes.Count, config.MaxRefinePasses, "reinstalling best pass"));
                best = (best.Correction, best.Itp,
                    await config.InstallAsync(best.Correction, ct));
                endedOnBest = true;
            }

            return new Outcome(anyInstall, converged, endedOnBest,
                best.Correction, best.ProfileName, initialItp, best.Itp, passes, stopReason);
        }
    }
}
