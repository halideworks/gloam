using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Iterates <see cref="HdrMhc2LutBuilder.Refine"/> with the same keep-best discipline as
    /// the SDR closed loop (<c>CalibrationOrchestrator.RunClosedLoopAsync</c>): measure the
    /// PQ ladder, refine, install, re-measure, keep whichever pass produced the lowest
    /// average absolute luminance error, and always end with the best LUTs installed.
    /// Measurement and installation are injected as delegates so the loop is fully testable
    /// against the simulated DWM chain with no UI or hardware.
    /// </summary>
    public static class HdrRefinementLoop
    {
        public sealed class Config
        {
            public required HdrMhc2LutBuilder.Result InitialLuts { get; init; }

            /// <summary>Requested rung luminances (already filtered to the panel's range).</summary>
            public required IReadOnlyList<double> RungNits { get; init; }

            /// <summary>
            /// Measures the PQ ladder through the currently installed correction.
            /// Arguments: rung nits, sequence-index offset for the measurement records, token.
            /// </summary>
            public required Func<IReadOnlyList<double>, int, CancellationToken,
                Task<IReadOnlyList<MeasurementResult>>> MeasureLadderAsync { get; init; }

            /// <summary>
            /// Installs candidate LUTs and returns the LUTs the installer ACTUALLY used plus
            /// the installed profile name. The installer may reject the override and rebuild
            /// from measurements (matrix-scale mismatch); the loop detects that by reference
            /// inequality and stops rather than iterate on LUTs it did not compute.
            /// </summary>
            public required Func<HdrMhc2LutBuilder.Result, CancellationToken,
                Task<(HdrMhc2LutBuilder.Result Installed, string ProfileName)>> InstallAsync { get; init; }

            public int MaxRefinePasses { get; init; } = 3;

            /// <summary>Full step on pass 1 — identical to the historical single pass.</summary>
            public double FirstPassDamping { get; init; } = 1.0;

            /// <summary>Damped steps on passes 2+ prevent oscillation on non-smooth gains.</summary>
            public double LaterPassDamping { get; init; } = 0.5;

            /// <summary>Average |Y/req − 1| below which the ladder is considered converged.</summary>
            public double ConvergedAvgError { get; init; } = HdrMhc2LutBuilder.RefineConvergedError;

            public IProgress<PassProgress>? Progress { get; init; }
        }

        public sealed record PassProgress(int Pass, int MaxPasses, string Phase);

        public sealed record PassRecord(
            int Pass,
            double AvgAbsErrorBefore,
            double? AvgAbsErrorAfter,
            string? ProfileName,
            string? RefusalReason);

        public sealed record Outcome(
            bool AnyInstall,
            bool Converged,
            bool EndedOnBest,
            HdrMhc2LutBuilder.Result FinalLuts,
            string? FinalProfileName,
            double InitialAvgAbsError,
            double FinalAvgAbsError,
            IReadOnlyList<PassRecord> Passes,
            string StopReason);

        public static async Task<Outcome> RunAsync(Config config, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (config.RungNits.Count == 0)
                throw new ArgumentException("At least one ladder rung is required.", nameof(config));

            var passes = new List<PassRecord>();
            int sequenceOffset = 0;

            config.Progress?.Report(new PassProgress(0, config.MaxRefinePasses, "measuring initial ladder"));
            var measured = await config.MeasureLadderAsync(config.RungNits, sequenceOffset, ct);
            sequenceOffset += measured.Count;

            double initialError = HdrMhc2LutBuilder.AverageAbsLuminanceError(measured);
            if (!double.IsFinite(initialError))
                throw new InvalidOperationException("Initial PQ ladder produced no valid rungs.");

            // Keep-best state. Best starts as the currently-installed LUTs at the initial
            // error; lastInstalled tracks what is physically on the display right now.
            var best = (Luts: config.InitialLuts, Error: initialError, ProfileName: (string?)null);
            var lastInstalled = best;
            var current = config.InitialLuts;
            double currentError = initialError;
            bool anyInstall = false;
            bool converged = currentError < config.ConvergedAvgError;
            string stopReason = converged
                ? $"already converged (avg error {currentError:P2})"
                : $"reached max passes ({config.MaxRefinePasses})";

            try
            {
                for (int pass = 1; pass <= config.MaxRefinePasses && !converged; pass++)
                {
                    ct.ThrowIfCancellationRequested();
                    double damping = pass == 1 ? config.FirstPassDamping : config.LaterPassDamping;

                    var refinement = HdrMhc2LutBuilder.Refine(
                        current, measured, current.MeasuredPeakNits, damping);

                    if (refinement.Refined == null)
                    {
                        // A "converged" refusal is success; anything else means a
                        // multiplicative touch-up is the wrong tool — stop and keep best.
                        // Refine's own threshold is honored even when the loop was configured
                        // with a stricter target than Refine will ever iterate below.
                        converged = refinement.AverageAbsErrorBefore
                            < Math.Max(config.ConvergedAvgError, HdrMhc2LutBuilder.RefineConvergedError);
                        stopReason = converged
                            ? $"converged (avg error {refinement.AverageAbsErrorBefore:P2})"
                            : $"refinement refused: {refinement.RefusalReason}";
                        passes.Add(new PassRecord(pass, refinement.AverageAbsErrorBefore, null, null,
                            refinement.RefusalReason));
                        break;
                    }

                    config.Progress?.Report(new PassProgress(pass, config.MaxRefinePasses, "installing"));
                    var (installed, profileName) = await config.InstallAsync(refinement.Refined, ct)
                        ;
                    anyInstall = true;

                    if (!ReferenceEquals(installed, refinement.Refined))
                    {
                        // Installer rebuilt from measurements instead of using the refined
                        // LUTs (matrix neutral-scale changed). Iterating on those would be
                        // refining something we never measured — stop honestly.
                        lastInstalled = (installed, double.NaN, profileName);
                        stopReason = "installer rebuilt the LUTs (matrix scale changed) — refinement stopped";
                        passes.Add(new PassRecord(pass, refinement.AverageAbsErrorBefore, null, profileName,
                            stopReason));
                        break;
                    }

                    // The display physically has the refined LUTs from this point on — track
                    // that BEFORE measuring so a cancellation mid-ladder still restores best.
                    lastInstalled = (refinement.Refined, double.NaN, profileName);

                    config.Progress?.Report(new PassProgress(pass, config.MaxRefinePasses, "measuring PQ ladder"));
                    measured = await config.MeasureLadderAsync(config.RungNits, sequenceOffset, ct)
                        ;
                    sequenceOffset += measured.Count;

                    double afterError = HdrMhc2LutBuilder.AverageAbsLuminanceError(measured);
                    if (!double.IsFinite(afterError))
                    {
                        stopReason = $"pass {pass} verification produced no valid rungs";
                        passes.Add(new PassRecord(pass, refinement.AverageAbsErrorBefore, null, profileName, null));
                        lastInstalled = (refinement.Refined, double.NaN, profileName);
                        break;
                    }

                    passes.Add(new PassRecord(pass, refinement.AverageAbsErrorBefore, afterError, profileName, null));
                    lastInstalled = (refinement.Refined, afterError, profileName);
                    current = refinement.Refined;
                    currentError = afterError;

                    if (afterError < best.Error)
                        best = (refinement.Refined, afterError, profileName);

                    if (afterError < config.ConvergedAvgError)
                    {
                        converged = true;
                        stopReason = $"converged (avg error {afterError:P2})";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Leave the display on the best-measured state before propagating: an
                // abandoned mid-loop install may be a regressed pass.
                if (anyInstall && !ReferenceEquals(lastInstalled.Luts, best.Luts))
                {
                    try
                    {
                        var (_, name) = await config.InstallAsync(best.Luts, CancellationToken.None)
                            ;
                        best = (best.Luts, best.Error, name);
                    }
                    catch
                    {
                        // Best-effort only — the caller's cancel path reports the state.
                    }
                }
                throw;
            }

            bool endedOnBest = false;
            if (anyInstall && !ReferenceEquals(lastInstalled.Luts, best.Luts))
            {
                // The final pass regressed (or the loop stopped mid-state); put the best
                // LUTs back. No re-measure: best's ladder numbers were already measured.
                config.Progress?.Report(new PassProgress(passes.Count, config.MaxRefinePasses,
                    "reinstalling best pass"));
                var (_, name) = await config.InstallAsync(best.Luts, ct);
                best = (best.Luts, best.Error, name);
                endedOnBest = true;
            }

            return new Outcome(
                anyInstall,
                converged,
                endedOnBest,
                best.Luts,
                best.ProfileName,
                initialError,
                best.Error,
                passes,
                stopReason);
        }
    }
}
