using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// The signal-domain manifold a candidate or measurement lives on. Residual
    /// interpolation and gap (coverage) distances are only meaningful ALONG a manifold:
    /// a grayscale tone residual says nothing about the red-only ramp, and vice versa.
    /// </summary>
    public enum SignalManifold
    {
        /// <summary>The neutral axis R = G = B; distance is |Δlevel|.</summary>
        Gray,

        /// <summary>Red-only ramp (G = B = 0); distance is |ΔR|.</summary>
        RedRamp,

        /// <summary>Green-only ramp (R = B = 0); distance is |ΔG|.</summary>
        GreenRamp,

        /// <summary>Blue-only ramp (R = G = 0); distance is |ΔB|.</summary>
        BlueRamp,

        /// <summary>General color-cube location; distance is Euclidean in signal RGB.</summary>
        Cube
    }

    /// <summary>A signal-domain location tagged with the manifold it belongs to.</summary>
    public readonly record struct SignalPoint(double R, double G, double B, SignalManifold Manifold)
    {
        /// <summary>The scalar coordinate along a one-dimensional manifold (level for gray, channel drive for ramps).</summary>
        public double AxisLevel => Manifold switch
        {
            SignalManifold.Gray => R,
            SignalManifold.RedRamp => R,
            SignalManifold.GreenRamp => G,
            _ => B
        };
    }

    /// <summary>Which error family a model residual belongs to (they have different units and targets).</summary>
    public enum ResidualKind
    {
        /// <summary>
        /// Perceptual tone error of a 1D tone-curve fit, expressed as |ΔL*| (CIE 1976
        /// lightness) between the predicted and measured relative luminance. This weights
        /// shadow errors more than highlight errors in proportion to visibility (the eye's
        /// near-constant contrast sensitivity), while L*'s linear toe keeps it bounded near
        /// black. Target: <see cref="AdaptivePatchPlanner.ToneTargetDeltaLstar"/>.
        /// </summary>
        Tone,

        /// <summary>ΔE2000 between the model's prediction and the measurement (color patches, gray cast).</summary>
        Color
    }

    /// <summary>One model residual: where the current display model disagrees with a measurement, and by how much.</summary>
    public readonly record struct ModelResidual(SignalPoint Location, ResidualKind Kind, double Magnitude);

    /// <summary>
    /// Pure acquisition logic for adaptive patch placement (roadmap 1.1). Given the
    /// candidate pool, the signal locations measured so far and the current model's
    /// residuals, it scores every candidate by predicted model uncertainty and picks the
    /// next measurement batch. Deterministic: same inputs, same picks — no RNG.
    /// </summary>
    /// <remarks>
    /// The score is deliberately explainable, two named terms and nothing else:
    /// <list type="bullet">
    /// <item><b>Residual term</b> — inverse-distance-weighted interpolation of the
    /// residuals on the candidate's manifold, each normalized by its accuracy target so
    /// tone (ΔY/Y) and color (ΔE) errors share one scale where 1.0 = "exactly at
    /// target". High where the model demonstrably fails nearby.</item>
    /// <item><b>Gap term</b> — distance to the nearest measured point along the
    /// candidate's manifold, weighted by <see cref="GapTermWeight"/>. High where nothing
    /// has been measured, so early rounds also fill genuine coverage holes instead of
    /// only chasing the current worst residual.</item>
    /// </list>
    /// Winners are then thinned greedily by MINIMUM-SEPARATION suppression: candidates are
    /// visited in score order and one is skipped when it lies closer than the manifold's
    /// minimum pick separation to an already-accepted winner. This is a simple greedy
    /// spacing filter (NOT a true farthest-point / OFPS "max-min spread" optimizer — it
    /// does not maximize the minimum inter-pick distance, only enforce a floor on it), which
    /// is all this stage needs: it prevents a whole batch from piling onto a single residual
    /// spike while still allowing round-over-round densification (separation is enforced
    /// within a batch only; later rounds may interleave finer samples).
    /// </remarks>
    public static class AdaptivePatchPlanner
    {
        #region Named constants (the whole tuning surface)

        /// <summary>Patches requested per adaptive round.</summary>
        public const int DefaultBatchSize = 12;

        /// <summary>Default total ordinary-patch budget for an adaptive run (drift anchors excluded).</summary>
        public const int DefaultPatchBudget = 120;

        /// <summary>
        /// Accuracy target for tone residuals, in CIE L* units: |ΔL*| between the
        /// tone-curve's prediction and the measurement. This replaces the old
        /// photometric-linear |ΔY|/(white−black) ≤ 1% target, which penalized a shadow error
        /// and a highlight error of equal ΔY equally even though the shadow error is far more
        /// visible. Because dL*/dY grows toward black (capped by L*'s linear toe, so no
        /// near-black blow-up), the same |ΔL*| budget now spends shadow samples in proportion
        /// to their visibility. Set to 1.5 (≈1.5 lightness JND on the WORST predicted point)
        /// after re-tuning: tight enough to keep chasing real localized defects, loose enough
        /// that a well-behaved smooth panel converges from the seed in a round or two instead
        /// of over-sampling its (perceptually amplified) deep-shadow LOO residuals. At
        /// mid-gray |ΔY|≈1.5% maps to |ΔL*|≈1.5, so the mid-tone scale is close to the old
        /// target while shadows tighten substantially.
        /// </summary>
        public const double ToneTargetDeltaLstar = 1.5;

        /// <summary>Accuracy target for color/gray-cast residuals: 0.8 ΔE2000.</summary>
        public const double ColorTargetDeltaE = 0.8;

        /// <summary>
        /// Plateau guard: a round must improve the ROBUST normalized residual summary
        /// (<see cref="RobustNormalizedResidual"/>) by more than this fraction to count as
        /// progress. A shortfall increments a plateau streak; the run only stops once the
        /// streak reaches <see cref="PlateauPatienceRounds"/> (a single flat/noisy round no
        /// longer terminates a still-improving run).
        /// </summary>
        public const double PlateauMinImprovementFraction = 0.10;

        /// <summary>
        /// Consecutive sub-threshold-improvement rounds required before the plateau guard
        /// stops the run. Patience ≥ 2 stops the failure mode where resolving the current
        /// worst point merely exposes a next-worst point of similar size (so the summary
        /// barely moves for one round) from being read as convergence.
        /// </summary>
        public const int PlateauPatienceRounds = 2;

        /// <summary>
        /// Percentile of the target-normalized residual distribution used as the ROBUST
        /// stopping/plateau summary, instead of the single worst point. A high percentile
        /// (90th) tracks "almost everything is at target" while ignoring one transient
        /// spike, so adaptive sampling resolving one point and exposing the next does not
        /// read as a plateau.
        /// </summary>
        public const double RobustResidualPercentile = 0.90;

        /// <summary>
        /// Weight of the gap term relative to the normalized residual term. 0.5 means a
        /// completely unmeasured full-scale span (gap 1.0) scores like a residual at
        /// half its accuracy target — enough to fill true coverage holes in early
        /// rounds, small enough that a demonstrated residual always outranks mere
        /// distance from data.
        /// </summary>
        public const double GapTermWeight = 0.5;

        /// <summary>
        /// Softening added to distances in the inverse-distance weighting of residuals.
        /// Sets the locality of the interpolation: a residual dominates candidates
        /// within a few multiples of this distance and fades beyond.
        /// </summary>
        public const double IdwDistanceEpsilon = 0.02;

        /// <summary>
        /// Minimum separation between two picks of one batch on a 1D manifold (gray axis,
        /// channel ramps): one candidate-pool step (4 8-bit codes, ~1.6% signal). This is
        /// the pool's own lattice spacing, so it forbids picking two ADJACENT lattice
        /// nodes in the same batch (which would add almost no shape information) while
        /// still allowing the batch to bracket a feature tightly; finer spacing than the
        /// lattice is reachable only because later rounds interleave new nodes.
        /// </summary>
        public const double OneDimensionalMinPickSeparation = 4.0 / 255.0;

        /// <summary>Minimum Euclidean separation between two cube picks of one batch.</summary>
        public const double CubeMinPickSeparation = 0.05;

        /// <summary>Candidates closer than this to an already-measured point are duplicates and skipped.</summary>
        public const double DuplicateExclusionDistance = 0.5 / 255.0;

        /// <summary>Gap assigned to a candidate whose manifold has no measured points at all (full scale).</summary>
        public const double UnmeasuredManifoldGap = 1.0;

        #endregion

        /// <summary>Minimum in-batch pick separation for a manifold.</summary>
        public static double MinPickSeparation(SignalManifold manifold) =>
            manifold == SignalManifold.Cube ? CubeMinPickSeparation : OneDimensionalMinPickSeparation;

        /// <summary>
        /// Distance between two points on the SAME manifold: |Δlevel| along 1D
        /// manifolds, Euclidean in the cube. Positive infinity across manifolds — a
        /// residual on one manifold carries no information about another.
        /// </summary>
        public static double Distance(SignalPoint a, SignalPoint b)
        {
            if (a.Manifold != b.Manifold)
                return double.PositiveInfinity;
            if (a.Manifold != SignalManifold.Cube)
                return Math.Abs(a.AxisLevel - b.AxisLevel);
            return Euclidean(a, b);
        }

        private static double Euclidean(SignalPoint a, SignalPoint b)
        {
            double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        /// <summary>
        /// Classifies a measured (or candidate) signal triple onto its manifold:
        /// neutral triples → gray axis; exactly one driven channel → that channel's
        /// ramp; everything else → cube.
        /// </summary>
        public static SignalPoint ClassifySignal(LinearRgb rgb)
        {
            const double eps = 1e-6;
            if (Math.Abs(rgb.R - rgb.G) < eps && Math.Abs(rgb.G - rgb.B) < eps)
                return new SignalPoint(rgb.R, rgb.G, rgb.B, SignalManifold.Gray);
            if (rgb.G <= eps && rgb.B <= eps)
                return new SignalPoint(rgb.R, rgb.G, rgb.B, SignalManifold.RedRamp);
            if (rgb.R <= eps && rgb.B <= eps)
                return new SignalPoint(rgb.R, rgb.G, rgb.B, SignalManifold.GreenRamp);
            if (rgb.R <= eps && rgb.G <= eps)
                return new SignalPoint(rgb.R, rgb.G, rgb.B, SignalManifold.BlueRamp);
            return new SignalPoint(rgb.R, rgb.G, rgb.B, SignalManifold.Cube);
        }

        /// <summary>A residual expressed as a multiple of its accuracy target (1.0 = exactly at target).</summary>
        public static double NormalizedResidual(ModelResidual residual) =>
            residual.Magnitude / (residual.Kind == ResidualKind.Tone ? ToneTargetDeltaLstar : ColorTargetDeltaE);

        /// <summary>Worst residual across the set, target-normalized. 0 for an empty set.</summary>
        public static double MaxNormalizedResidual(IReadOnlyList<ModelResidual> residuals)
        {
            double max = 0;
            foreach (var r in residuals)
            {
                double n = NormalizedResidual(r);
                if (double.IsFinite(n) && n > max) max = n;
            }
            return max;
        }

        /// <summary>
        /// A ROBUST summary of the target-normalized residuals: the
        /// <see cref="RobustResidualPercentile"/> (90th) percentile, linearly interpolated.
        /// Unlike the single worst point, this ignores one transient spike, so it is the
        /// stable basis for the stopping and plateau decisions. 0 for an empty set.
        /// </summary>
        public static double RobustNormalizedResidual(
            IReadOnlyList<ModelResidual> residuals, double percentile = RobustResidualPercentile)
        {
            var norms = new List<double>(residuals.Count);
            foreach (var r in residuals)
            {
                double n = NormalizedResidual(r);
                if (double.IsFinite(n)) norms.Add(n);
            }
            if (norms.Count == 0) return 0;
            norms.Sort();
            double rank = Math.Clamp(percentile, 0.0, 1.0) * (norms.Count - 1);
            int lo = (int)Math.Floor(rank);
            int hi = (int)Math.Ceiling(rank);
            double frac = rank - lo;
            return norms[lo] + frac * (norms[hi] - norms[lo]);
        }

        /// <summary>Outcome of the stopping evaluation between rounds.</summary>
        /// <param name="ShouldStop">Whether the adaptive loop should terminate.</param>
        /// <param name="Reason">Human-readable reason for the decision.</param>
        /// <param name="AccuracyTargetsMet">
        /// True only when the run stopped because the robust residual reached target. False
        /// for a budget-exhausted or plateaued stop, which is a DEGRADED outcome the caller
        /// must surface rather than report as clean success.
        /// </param>
        /// <param name="PlateauStreak">
        /// Updated count of consecutive sub-threshold-improvement rounds; the caller feeds it
        /// back into the next call.
        /// </param>
        public readonly record struct StopDecision(
            bool ShouldStop, string Reason, bool AccuracyTargetsMet, int PlateauStreak);

        /// <summary>
        /// Decides whether another adaptive round is worthwhile. Two summaries play distinct
        /// roles, deliberately:
        /// <list type="bullet">
        /// <item><b>Target-met uses the MAX</b> normalized residual (<paramref name="currentMaxResidual"/>).
        /// The whole point of adaptive placement is to chase the WORST point, so success is
        /// only declared once every point — including a narrow defect the planner is crowding —
        /// is at target. This is strict: it never reports clean success while the worst point
        /// is still bad.</item>
        /// <item><b>Plateau uses the ROBUST summary</b> (<paramref name="currentRobustResidual"/>,
        /// a high percentile) with PATIENCE. Resolving the current worst point merely exposes
        /// a next-worst point of similar size, so a max-based plateau check false-triggers; the
        /// robust summary tracks the whole distribution shifting down as the planner fills in,
        /// and <see cref="PlateauPatienceRounds"/> consecutive sub-threshold rounds are required
        /// before stopping. This is what prevents premature stops on a still-improving run.</item>
        /// </list>
        /// Any stop that is NOT "targets met" (budget exhausted, or a genuine plateau above
        /// target) is flagged degraded via <see cref="StopDecision.AccuracyTargetsMet"/> so the
        /// caller surfaces a warning instead of unqualified success.
        /// </summary>
        /// <param name="currentMaxResidual">Worst normalized residual now (drives target-met).</param>
        /// <param name="currentRobustResidual">Robust (percentile) normalized residual now (drives plateau).</param>
        /// <param name="previousRobustResidual">Robust normalized residual before the last round (null before any round).</param>
        /// <param name="measuredPatchCount">Ordinary (non-anchor, non-wire) patches measured so far.</param>
        /// <param name="patchBudget">Total ordinary-patch budget.</param>
        /// <param name="plateauStreak">Consecutive sub-threshold rounds so far (0 before any round).</param>
        public static StopDecision EvaluateStopping(
            double currentMaxResidual,
            double currentRobustResidual,
            double? previousRobustResidual,
            int measuredPatchCount,
            int patchBudget,
            int plateauStreak = 0)
        {
            if (currentMaxResidual <= 1.0)
                return new StopDecision(true,
                    $"accuracy targets met (worst predicted error at {currentMaxResidual:P0} of target)",
                    AccuracyTargetsMet: true, PlateauStreak: 0);

            if (measuredPatchCount >= patchBudget)
                return new StopDecision(true,
                    $"patch budget of {patchBudget} reached with worst predicted error still at " +
                    $"{currentMaxResidual:P0} of target",
                    AccuracyTargetsMet: false, PlateauStreak: plateauStreak);

            if (previousRobustResidual is double previous && previous > 0)
            {
                double improvement = (previous - currentRobustResidual) / previous;
                if (improvement < PlateauMinImprovementFraction)
                {
                    int streak = plateauStreak + 1;
                    if (streak >= PlateauPatienceRounds)
                        return new StopDecision(true,
                            $"improvement plateaued for {streak} consecutive rounds " +
                            $"({improvement:P0} last round on the robust summary, need > " +
                            $"{PlateauMinImprovementFraction:P0}); worst predicted error still " +
                            $"{currentMaxResidual:P0} of target",
                            AccuracyTargetsMet: false, PlateauStreak: streak);

                    return new StopDecision(false,
                        $"flat round {streak}/{PlateauPatienceRounds} before plateau stop", false, streak);
                }
            }

            // Progress (or no prior round): reset the plateau streak.
            return new StopDecision(false, "predicted error still above target", false, 0);
        }

        /// <summary>
        /// Plans the next measurement batch: scores every pool candidate (residual term
        /// + gap term), sorts deterministically, then accepts winners greedily subject
        /// to the per-manifold minimum pick separation. Candidates that duplicate an
        /// already-measured point are excluded.
        /// </summary>
        public static IReadOnlyList<SignalPoint> PlanNextBatch(
            IReadOnlyList<SignalPoint> candidatePool,
            IReadOnlyList<SignalPoint> measuredPoints,
            IReadOnlyList<ModelResidual> residuals,
            int batchSize = DefaultBatchSize)
        {
            if (batchSize <= 0)
                return Array.Empty<SignalPoint>();

            var scored = new List<(SignalPoint Point, double Score)>(candidatePool.Count);
            foreach (var candidate in candidatePool)
            {
                double gap = GapToNearestMeasured(candidate, measuredPoints);
                if (gap < DuplicateExclusionDistance)
                    continue; // effectively already measured

                double score = ResidualTerm(candidate, residuals) + GapTermWeight * gap;
                scored.Add((candidate, score));
            }

            // Deterministic order: score descending, then a stable coordinate tie-break.
            var ordered = scored
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Point.Manifold)
                .ThenBy(s => s.Point.R)
                .ThenBy(s => s.Point.G)
                .ThenBy(s => s.Point.B)
                .ToList();

            var picks = new List<SignalPoint>(batchSize);
            foreach (var (point, _) in ordered)
            {
                if (picks.Count >= batchSize)
                    break;

                bool tooClose = false;
                foreach (var existing in picks)
                {
                    if (existing.Manifold == point.Manifold &&
                        Distance(existing, point) < MinPickSeparation(point.Manifold))
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (!tooClose)
                    picks.Add(point);
            }

            return picks;
        }

        /// <summary>
        /// Inverse-distance-weighted interpolation of the target-normalized residuals on
        /// the candidate's manifold. Returns 0 when the manifold carries no residuals.
        /// </summary>
        private static double ResidualTerm(SignalPoint candidate, IReadOnlyList<ModelResidual> residuals)
        {
            double weightSum = 0, weightedResidualSum = 0;
            foreach (var residual in residuals)
            {
                if (residual.Location.Manifold != candidate.Manifold)
                    continue;

                double d = Distance(candidate, residual.Location);
                if (!double.IsFinite(d))
                    continue;

                double w = 1.0 / (d + IdwDistanceEpsilon);
                weightSum += w;
                weightedResidualSum += w * NormalizedResidual(residual);
            }

            return weightSum > 0 ? weightedResidualSum / weightSum : 0.0;
        }

        /// <summary>
        /// Distance from a candidate to the nearest measured point along its manifold.
        /// Cube candidates measure against ALL measured points (every measurement is a
        /// point in the cube); 1D candidates only against points on the same manifold.
        /// </summary>
        private static double GapToNearestMeasured(SignalPoint candidate, IReadOnlyList<SignalPoint> measuredPoints)
        {
            double best = double.PositiveInfinity;
            foreach (var measured in measuredPoints)
            {
                double d = candidate.Manifold == SignalManifold.Cube
                    ? Euclidean(candidate, measured)
                    : Distance(candidate, measured);
                if (d < best) best = d;
            }
            return double.IsFinite(best) ? best : UnmeasuredManifoldGap;
        }
    }
}
