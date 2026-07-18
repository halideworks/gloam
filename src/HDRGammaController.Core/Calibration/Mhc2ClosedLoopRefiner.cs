using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    public sealed record Mhc2ClosedLoopProposal(
        bool ShouldInstall,
        Mhc2CompileResult Payload,
        string Reason,
        int ObservationCount);

    public sealed record Mhc2PhysicalAcceptance(
        bool Accepted,
        string Reason,
        double BeforeAverageDeltaE,
        double AfterAverageDeltaE,
        double BeforeWorstDeltaE,
        double AfterWorstDeltaE);

    /// <summary>
    /// Counterexample-guided inductive refinement for SDR MHC2. Physical errors measured
    /// through the installed payload are converted back to desired display-channel drives
    /// with the characterized XYZ Jacobian, then compiled as high-weight constraints. The
    /// proposal is only provisional: <see cref="DecidePhysicalAcceptance"/> is the transaction
    /// gate after the candidate has been installed and independently remeasured.
    /// </summary>
    public static class Mhc2ClosedLoopRefiner
    {
        public const int MaximumRounds = 2;
        public const double MinimumModelP95Improvement = 0.03;
        public const double MinimumPhysicalAverageImprovement = 0.05;
        public const double SentinelRegressionTolerance = 0.10;

        public static Mhc2ClosedLoopProposal Propose(
            Mhc2CompileResult current,
            Lut3D idealCorrection,
            DisplayCharacterization characterization,
            CalibrationTarget target,
            IReadOnlyList<MeasurementResult> readings,
            double whiteY,
            IReadOnlyList<MeasurementResult>? originalMeasurements = null,
            IReadOnlyList<ModelResidual>? modelResiduals = null)
        {
            ArgumentNullException.ThrowIfNull(current);
            ArgumentNullException.ThrowIfNull(idealCorrection);
            ArgumentNullException.ThrowIfNull(characterization);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(readings);
            int round = current.Certificate.ClosedLoopRound + 1;
            if (round > MaximumRounds)
                return new Mhc2ClosedLoopProposal(false, current,
                    $"closed-loop cap reached ({MaximumRounds} rounds)", 0);
            if (!double.IsFinite(whiteY) || whiteY <= 0)
                return new Mhc2ClosedLoopProposal(false, current,
                    "no valid simultaneously measured white for XYZ normalization", 0);

            var observations = readings.Where(r => r.IsValid && r.Patch.Nits is null)
                .Select(r => new Mhc2PhysicalObservation(
                    Math.Clamp(r.Patch.DisplayRgb.R, 0, 1),
                    Math.Clamp(r.Patch.DisplayRgb.G, 0, 1),
                    Math.Clamp(r.Patch.DisplayRgb.B, 0, 1),
                    r.Xyz.X / whiteY, r.Xyz.Y / whiteY, r.Xyz.Z / whiteY,
                    ObservationWeight(r.Patch)))
                .Where(IsValid).ToList();
            if (observations.Count < 6)
                return new Mhc2ClosedLoopProposal(false, current,
                    $"only {observations.Count} valid normalized observations (need at least 6)", observations.Count);

            var toneCandidate = Mhc2HardwareCompiler.Compile(current.Matrix, current.LutR, current.LutG,
                current.LutB, idealCorrection, characterization, target, originalMeasurements,
                modelResiduals, optimizeMatrix: false,
                physicalObservations: observations, closedLoopRound: round);
            var candidate = target.WhitePointOnly
                ? toneCandidate
                : new[]
                {
                    toneCandidate,
                    Mhc2HardwareCompiler.Compile(current.Matrix, current.LutR, current.LutG,
                        current.LutB, idealCorrection, characterization, target, originalMeasurements,
                        modelResiduals, optimizeMatrix: true, physicalObservations: observations,
                        closedLoopRound: round),
                }
                .Where(c => c.Certificate.OptimizerApplied)
                .OrderBy(c => CandidateScore(c.Certificate.Compiled))
                .FirstOrDefault() ?? toneCandidate;
            // Compare inside the same residual-augmented physical model. The current
            // certificate predates these observations and is therefore not commensurate.
            double p95Improvement = candidate.Certificate.Baseline.P95DeltaE -
                                    candidate.Certificate.Compiled.P95DeltaE;
            bool safe = candidate.Certificate.OptimizerApplied &&
                        p95Improvement >= MinimumModelP95Improvement &&
                        candidate.Certificate.Compiled.MaxNeutralDeltaE <=
                            candidate.Certificate.Baseline.MaxNeutralDeltaE + SentinelRegressionTolerance &&
                        candidate.Certificate.Compiled.MaxAnchorDeltaE <=
                            candidate.Certificate.Baseline.MaxAnchorDeltaE + SentinelRegressionTolerance &&
                        candidate.Certificate.Compiled.MaxGradientDeltaE <=
                            candidate.Certificate.Baseline.MaxGradientDeltaE * 1.05 + SentinelRegressionTolerance;
            candidate.Certificate.ClosedLoopDecision = safe
                ? $"provisional model gain {p95Improvement:F2} ΔE; awaiting physical A/B gate"
                : $"rejected before install (model P95 gain {p95Improvement:F2} ΔE or structure gate failed)";
            // Return the rejected candidate too: callers never install unless ShouldInstall
            // is true, while diagnostics retain the exact payload and gate metrics examined.
            return new Mhc2ClosedLoopProposal(safe, candidate,
                candidate.Certificate.ClosedLoopDecision, observations.Count);
        }

        /// <summary>
        /// Keep-best physical gate. Average improvement must clear a noise floor, while max,
        /// grayscale, primaries and adversarial worst are independent non-regression sentinels.
        /// This is intentionally lexicographic rather than a weighted score: a skin/neutral
        /// regression cannot be bought with many tiny easy-patch wins.
        /// </summary>
        public static Mhc2PhysicalAcceptance DecidePhysicalAcceptance(
            IReadOnlyList<MeasurementResult> beforeStandard,
            IReadOnlyList<MeasurementResult> afterStandard,
            CalibrationTarget target,
            double? beforeCounterexampleWorst,
            double? afterCounterexampleWorst,
            double expandedAverageUncertainty = 0)
        {
            ArgumentNullException.ThrowIfNull(beforeStandard);
            ArgumentNullException.ThrowIfNull(afterStandard);
            ArgumentNullException.ThrowIfNull(target);
            var before = CalibrationVerifier.ComputeMetrics(beforeStandard, target);
            var after = CalibrationVerifier.ComputeMetrics(afterStandard, target);
            double requiredGain = Math.Max(MinimumPhysicalAverageImprovement,
                double.IsFinite(expandedAverageUncertainty) ? expandedAverageUncertainty : 0);
            var failures = new List<string>();
            if (before.AverageDeltaE - after.AverageDeltaE < requiredGain)
                failures.Add($"average gain {before.AverageDeltaE - after.AverageDeltaE:F2} < {requiredGain:F2}");
            if (after.MaxDeltaE > before.MaxDeltaE + SentinelRegressionTolerance)
                failures.Add($"worst patch regressed {before.MaxDeltaE:F2}→{after.MaxDeltaE:F2}");
            if (after.AverageGrayscaleDeltaE > before.AverageGrayscaleDeltaE + SentinelRegressionTolerance)
                failures.Add($"neutral sentinel regressed {before.AverageGrayscaleDeltaE:F2}→{after.AverageGrayscaleDeltaE:F2}");
            if (after.AveragePrimaryDeltaE > before.AveragePrimaryDeltaE + SentinelRegressionTolerance)
                failures.Add($"primary sentinel regressed {before.AveragePrimaryDeltaE:F2}→{after.AveragePrimaryDeltaE:F2}");
            if (beforeCounterexampleWorst is { } bw && afterCounterexampleWorst is { } aw &&
                aw > bw + SentinelRegressionTolerance)
                failures.Add($"adversarial worst regressed {bw:F2}→{aw:F2}");
            bool accepted = failures.Count == 0;
            return new Mhc2PhysicalAcceptance(accepted,
                accepted
                    ? $"accepted: measured average improved {before.AverageDeltaE:F2}→{after.AverageDeltaE:F2}; all sentinels held"
                    : "restored previous payload: " + string.Join("; ", failures),
                before.AverageDeltaE, after.AverageDeltaE, before.MaxDeltaE, after.MaxDeltaE);
        }

        private static double ObservationWeight(ColorPatch patch) => patch.Category switch
        {
            PatchCategory.Grayscale => 3.0,
            PatchCategory.Primary => 2.5,
            PatchCategory.Secondary => 2.0,
            PatchCategory.SkinTone => 2.0,
            _ => patch.IsCritical ? 1.5 : 1.0,
        };

        private static bool IsValid(Mhc2PhysicalObservation o) =>
            double.IsFinite(o.X) && o.X >= 0 && double.IsFinite(o.Y) && o.Y >= 0 &&
            double.IsFinite(o.Z) && o.Z >= 0;

        private static double CandidateScore(Mhc2ErrorStatistics s) =>
            0.25 * s.AverageDeltaE + 0.50 * s.P95DeltaE + 0.25 * s.MaxDeltaE +
            0.04 * s.MaxNeutralDeltaE + 0.02 * s.GradientRmsDeltaE;
    }
}
