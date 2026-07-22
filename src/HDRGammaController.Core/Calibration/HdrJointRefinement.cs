using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Coordinated closed-loop HDR refinement for the complete Windows MHC2 transform:
    /// one 3x3 gamut matrix followed by three 1D PQ-domain LUTs. Each pass measures tone
    /// and color through the SAME installed state, separates neutral luminance from the
    /// fitted color residual, re-parameterizes the tone LUT if matrix headroom changes,
    /// installs both pieces atomically, and keeps the best jointly measured result.
    /// </summary>
    /// <remarks>
    /// This is a constrained block solve rather than an unconstrained 3D LUT fit. That is
    /// deliberate: MHC2 cannot represent an arbitrary luminance-dependent gamut rotation.
    /// The matrix owns the linear color residual, the shared neutral tone curve owns white
    /// luminance tracking, and post-install measurement decides whether their composed,
    /// quantized Windows realization actually improved both objectives.
    /// </remarks>
    public static class HdrJointRefinement
    {
        public const double DefaultToneTarget = HdrMhc2LutBuilder.RefineConvergedError;
        public const double DefaultColorTarget = HdrColorMatrixRefiner.ConvergedAvgItp;

        // Regressions smaller than these are below the resolution worth trading in a noisy
        // live measurement loop. Anything larger disqualifies an otherwise lower joint score.
        public const double ToneRegressionTolerance = 0.0025;
        public const double ColorRegressionTolerance = 0.5;

        public sealed record State(
            double[,] XyzCorrection,
            HdrMhc2LutBuilder.Result Luts);

        public sealed record Measurements(
            IReadOnlyList<MeasurementResult> ToneReadings,
            IReadOnlyList<(ColoredHdrStimulus Stimulus, CieXyz Measured)> ColorReadings)
        {
            public int Count => ToneReadings.Count + ColorReadings.Count;
        }

        public sealed record Metrics(
            double ToneAverageAbsError,
            double ColorAverageDeltaEItp,
            double JointScore)
        {
            public bool IsConverged(double toneTarget, double colorTarget) =>
                ToneAverageAbsError < toneTarget && ColorAverageDeltaEItp < colorTarget;
        }

        public sealed record Candidate(
            State? Value,
            bool ChangedColor,
            bool ChangedTone,
            string? ColorRefusal,
            string? ToneRefusal);

        public static Metrics Evaluate(
            Measurements measurements,
            double toneTarget = DefaultToneTarget,
            double colorTarget = DefaultColorTarget)
        {
            ArgumentNullException.ThrowIfNull(measurements);
            if (!double.IsFinite(toneTarget) || toneTarget <= 0)
                throw new ArgumentOutOfRangeException(nameof(toneTarget));
            if (!double.IsFinite(colorTarget) || colorTarget <= 0)
                throw new ArgumentOutOfRangeException(nameof(colorTarget));

            double tone = HdrMhc2LutBuilder.AverageAbsLuminanceError(measurements.ToneReadings);
            double color = HdrColorMatrixRefiner.AverageReachableItp(measurements.ColorReadings);
            if (!double.IsFinite(tone))
                throw new InvalidOperationException("Joint HDR measurement produced no valid PQ ladder readings.");
            if (!double.IsFinite(color))
                throw new InvalidOperationException("Joint HDR measurement produced no reachable colored readings.");

            double normalizedTone = tone / toneTarget;
            double normalizedColor = color / colorTarget;
            return new Metrics(tone, color,
                Math.Sqrt(normalizedTone * normalizedTone + normalizedColor * normalizedColor));
        }

        /// <summary>
        /// Builds one atomic matrix+LUT candidate from residuals measured through the current
        /// state. The color fit is normalized to unit target-white Y before composition, so
        /// the tone error is corrected exactly once by the LUT. The supplied scale resolver
        /// must use the installer's exact matrix planning policy.
        /// </summary>
        public static Candidate BuildCandidate(
            State current,
            Measurements measurements,
            CieXyz targetWhite,
            Func<double[,], double> resolveMatrixNeutralScale,
            double damping = 1.0)
        {
            ArgumentNullException.ThrowIfNull(current);
            ArgumentNullException.ThrowIfNull(measurements);
            ArgumentNullException.ThrowIfNull(resolveMatrixNeutralScale);
            if (!double.IsFinite(damping) || damping <= 0.0 || damping > 1.0)
                throw new ArgumentOutOfRangeException(nameof(damping));

            var currentMetrics = Evaluate(measurements);

            var colorFit = HdrColorMatrixRefiner.Fit(measurements.ColorReadings, damping);
            double[,] nextCorrection = current.XyzCorrection;
            bool changedColor = false;
            string? colorRefusal = colorFit.RefusalReason;
            if (colorFit.XyzCorrection != null)
            {
                var colorOnlyResidual = HdrColorMatrixRefiner.PreserveNeutralLuminance(
                    colorFit.XyzCorrection, targetWhite);
                nextCorrection = ColorMath.MultiplyMatrices(colorOnlyResidual, current.XyzCorrection);
                changedColor = true;
                colorRefusal = null;
            }

            double nextScale = changedColor
                ? resolveMatrixNeutralScale(nextCorrection)
                : current.Luts.MatrixNeutralScale;
            var rebased = HdrMhc2LutBuilder.RebaseNeutralScale(current.Luts, nextScale);

            var toneFit = HdrMhc2LutBuilder.Refine(
                rebased, measurements.ToneReadings, rebased.MeasuredPeakNits, damping);
            HdrMhc2LutBuilder.Result nextLuts = toneFit.Refined ?? rebased;
            bool changedTone = toneFit.Refined != null;
            string? toneRefusal = toneFit.RefusalReason;

            // A scale rebase is not an independent correction: it only preserves the old
            // neutral mapping under the candidate matrix. Therefore a color step still counts
            // as a candidate even when the tone axis was already converged.
            if (!changedColor && !changedTone)
            {
                string colorReason = colorRefusal ??
                    $"already converged (avg DeltaE ITP {currentMetrics.ColorAverageDeltaEItp:F1})";
                string toneReason = toneRefusal ??
                    $"already converged (avg error {currentMetrics.ToneAverageAbsError:P2})";
                return new Candidate(null, false, false, colorReason, toneReason);
            }

            return new Candidate(
                new State(nextCorrection, nextLuts),
                changedColor, changedTone, colorRefusal, toneRefusal);
        }

        public static bool IsBetter(Metrics candidate, Metrics incumbent)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(incumbent);

            if (!(candidate.JointScore < incumbent.JointScore - 1e-9))
                return false;
            if (candidate.ToneAverageAbsError >
                incumbent.ToneAverageAbsError + ToneRegressionTolerance)
                return false;
            if (candidate.ColorAverageDeltaEItp >
                incumbent.ColorAverageDeltaEItp + ColorRegressionTolerance)
                return false;
            return true;
        }

        public sealed class Config
        {
            public required State InitialState { get; init; }
            public required CieXyz TargetWhite { get; init; }
            public required IReadOnlyList<double> ToneRungs { get; init; }
            public required IReadOnlyList<ColoredHdrStimulus> ColorStimuli { get; init; }

            /// <summary>Measures both objectives through the currently installed state.</summary>
            public required Func<IReadOnlyList<double>, IReadOnlyList<ColoredHdrStimulus>, int,
                CancellationToken, Task<Measurements>> MeasureAsync { get; init; }

            /// <summary>Uses the installer's exact matrix policy to predict candidate scale.</summary>
            public required Func<double[,], double> ResolveMatrixNeutralScale { get; init; }

            /// <summary>
            /// Atomically installs a matrix+LUT state and returns what was actually installed
            /// plus its profile name.
            /// </summary>
            public required Func<State, CancellationToken,
                Task<(State Installed, string ProfileName)>> InstallAsync { get; init; }

            public int MaxPasses { get; init; } = 2;
            public double FirstPassDamping { get; init; } = 1.0;
            public double LaterPassDamping { get; init; } = 0.55;
            public double ToneTarget { get; init; } = DefaultToneTarget;
            public double ColorTarget { get; init; } = DefaultColorTarget;
            public IProgress<HdrRefinementLoop.PassProgress>? Progress { get; init; }
        }

        public sealed record PassRecord(
            int Pass,
            Metrics Before,
            Metrics? After,
            bool ChangedColor,
            bool ChangedTone,
            string? ProfileName,
            string? RefusalReason);

        public sealed record Outcome(
            bool AnyInstall,
            bool Converged,
            bool EndedOnBest,
            State FinalState,
            string? FinalProfileName,
            Metrics InitialMetrics,
            Metrics FinalMetrics,
            IReadOnlyList<PassRecord> Passes,
            string StopReason);

        public static async Task<Outcome> RunAsync(Config config, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (config.ToneRungs.Count == 0)
                throw new ArgumentException("At least one PQ ladder rung is required.", nameof(config));
            if (config.ColorStimuli.Count == 0)
                throw new ArgumentException("At least one colored HDR stimulus is required.", nameof(config));
            if (config.MaxPasses < 1)
                throw new ArgumentOutOfRangeException(nameof(config), "MaxPasses must be at least one.");

            var passes = new List<PassRecord>();
            int sequenceOffset = 0;
            config.Progress?.Report(new HdrRefinementLoop.PassProgress(
                0, config.MaxPasses, "measuring tone and color"));
            var measured = await config.MeasureAsync(
                config.ToneRungs, config.ColorStimuli, sequenceOffset, ct);
            sequenceOffset += measured.Count;

            Metrics initial = Evaluate(measured, config.ToneTarget, config.ColorTarget);
            var best = (State: config.InitialState, Metrics: initial, ProfileName: (string?)null);
            var lastInstalled = best;
            State current = config.InitialState;
            Metrics currentMetrics = initial;
            bool anyInstall = false;
            bool converged = initial.IsConverged(config.ToneTarget, config.ColorTarget);
            string stopReason = converged
                ? $"already converged (tone {initial.ToneAverageAbsError:P2}, color {initial.ColorAverageDeltaEItp:F1})"
                : $"reached max passes ({config.MaxPasses})";

            try
            {
                for (int pass = 1; pass <= config.MaxPasses && !converged; pass++)
                {
                    ct.ThrowIfCancellationRequested();
                    double damping = pass == 1 ? config.FirstPassDamping : config.LaterPassDamping;
                    config.Progress?.Report(new HdrRefinementLoop.PassProgress(
                        pass, config.MaxPasses, "solving matrix and tone together"));

                    Candidate candidate = BuildCandidate(
                        current, measured, config.TargetWhite,
                        config.ResolveMatrixNeutralScale, damping);
                    if (candidate.Value == null)
                    {
                        string refusal = $"color: {candidate.ColorRefusal}; tone: {candidate.ToneRefusal}";
                        converged = currentMetrics.IsConverged(config.ToneTarget, config.ColorTarget);
                        stopReason = converged ? "both objectives converged" : $"joint refinement refused ({refusal})";
                        passes.Add(new PassRecord(pass, currentMetrics, null,
                            false, false, null, refusal));
                        break;
                    }

                    config.Progress?.Report(new HdrRefinementLoop.PassProgress(
                        pass, config.MaxPasses, "installing one atomic correction"));
                    var (installed, profileName) = await config.InstallAsync(candidate.Value, ct);
                    anyInstall = true;
                    lastInstalled = (installed, currentMetrics, profileName);

                    config.Progress?.Report(new HdrRefinementLoop.PassProgress(
                        pass, config.MaxPasses, "verifying tone and color"));
                    measured = await config.MeasureAsync(
                        config.ToneRungs, config.ColorStimuli, sequenceOffset, ct);
                    sequenceOffset += measured.Count;
                    Metrics after = Evaluate(measured, config.ToneTarget, config.ColorTarget);

                    bool improved = IsBetter(after, best.Metrics);
                    passes.Add(new PassRecord(pass, currentMetrics, after,
                        candidate.ChangedColor, candidate.ChangedTone, profileName,
                        improved ? null : "joint score did not improve without a material axis regression"));

                    current = installed;
                    currentMetrics = after;
                    lastInstalled = (installed, after, profileName);

                    if (!improved)
                    {
                        stopReason = $"pass {pass} did not improve the constrained joint objective";
                        break;
                    }

                    best = (installed, after, profileName);
                    if (after.IsConverged(config.ToneTarget, config.ColorTarget))
                    {
                        converged = true;
                        stopReason = $"converged (tone {after.ToneAverageAbsError:P2}, color {after.ColorAverageDeltaEItp:F1})";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (anyInstall && !ReferenceEquals(lastInstalled.State, best.State))
                {
                    try
                    {
                        var (restored, name) = await config.InstallAsync(best.State, CancellationToken.None);
                        best = (restored, best.Metrics, name);
                    }
                    catch
                    {
                        // Best effort; the UI reports cancellation and the recovery state.
                    }
                }
                throw;
            }

            bool endedOnBest = false;
            if (anyInstall && !ReferenceEquals(lastInstalled.State, best.State))
            {
                config.Progress?.Report(new HdrRefinementLoop.PassProgress(
                    passes.Count, config.MaxPasses, "reinstalling best joint pass"));
                var (restored, name) = await config.InstallAsync(best.State, ct);
                best = (restored, best.Metrics, name);
                endedOnBest = true;
            }

            return new Outcome(
                anyInstall, converged, endedOnBest,
                best.State, best.ProfileName,
                initial, best.Metrics, passes, stopReason);
        }
    }
}
