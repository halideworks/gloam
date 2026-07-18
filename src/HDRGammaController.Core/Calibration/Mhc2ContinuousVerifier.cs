using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>One evaluated point retained by the adaptive continuous-cube verifier.</summary>
    public sealed record Mhc2ContinuousSample(double R, double G, double B, double DeltaE);

    /// <summary>
    /// Result of adaptive branch-and-bound over the continuous RGB cube. The envelope is a
    /// numerically conservative engineering bound obtained from the largest resolved local
    /// slope with a safety inflation; it is deliberately not called a mathematical proof of
    /// CIEDE2000 because neither a sparse measured display model nor ΔE2000 has a supplied
    /// global Lipschitz constant.
    /// </summary>
    public sealed record Mhc2ContinuousVerificationResult(
        int EvaluatedPointCount,
        int VisitedCellCount,
        int MaximumDepth,
        double SampledMaximumDeltaE,
        double EmpiricalEnvelopeDeltaE,
        double RemainingEnvelopeGapDeltaE,
        double WorstR,
        double WorstG,
        double WorstB,
        IReadOnlyList<Mhc2ContinuousSample> Samples);

    /// <summary>
    /// Deterministic best-first octree verification. Unlike a fixed N³ lattice, every point
    /// represents a cell of the continuous cube: cells whose error is flat are retired early,
    /// while cells with a large value or steep local variation receive most of the budget.
    /// </summary>
    public static class Mhc2ContinuousVerifier
    {
        private readonly record struct Cell(double R0, double G0, double B0,
            double R1, double G1, double B1, int Depth);

        private sealed record CellEstimate(Cell Cell, double SampledMax, double Upper, double LocalSlope);

        public static Mhc2ContinuousVerificationResult Verify(
            Func<double, double, double, double> errorAt,
            int maximumDepth = 6,
            int maximumPoints = 6000,
            double targetEnvelopeGapDeltaE = 0.05,
            double slopeSafetyFactor = 1.75)
        {
            ArgumentNullException.ThrowIfNull(errorAt);
            if (maximumDepth < 1 || maximumDepth > 10)
                throw new ArgumentOutOfRangeException(nameof(maximumDepth));
            if (maximumPoints < 27)
                throw new ArgumentOutOfRangeException(nameof(maximumPoints));
            if (!double.IsFinite(targetEnvelopeGapDeltaE) || targetEnvelopeGapDeltaE < 0)
                throw new ArgumentOutOfRangeException(nameof(targetEnvelopeGapDeltaE));
            if (!double.IsFinite(slopeSafetyFactor) || slopeSafetyFactor < 1)
                throw new ArgumentOutOfRangeException(nameof(slopeSafetyFactor));

            // Dyadic octree coordinates are exactly reusable when keyed at this resolution.
            int lattice = 1 << maximumDepth;
            var cache = new Dictionary<(int R, int G, int B), Mhc2ContinuousSample>();
            var queue = new PriorityQueue<CellEstimate, (double Upper, int Depth, double R, double G, double B)>(
                Comparer<(double Upper, int Depth, double R, double G, double B)>.Create((a, b) =>
                {
                    int c = -a.Upper.CompareTo(b.Upper); // max-priority using the min-heap API
                    if (c != 0) return c;
                    // At equal bound, breadth first: every region reaches the mandatory
                    // coverage depth before a single flat-looking branch can monopolize it.
                    c = a.Depth.CompareTo(b.Depth);
                    if (c != 0) return c;
                    c = a.R.CompareTo(b.R);
                    if (c != 0) return c;
                    c = a.G.CompareTo(b.G);
                    return c != 0 ? c : a.B.CompareTo(b.B);
                }));

            double sampledWorst = 0;
            double worstR = 0, worstG = 0, worstB = 0;
            double globalSlope = 0;
            int visitedCells = 0;
            int reachedDepth = 0;

            Mhc2ContinuousSample Evaluate(double r, double g, double b)
            {
                int ir = (int)Math.Round(Math.Clamp(r, 0, 1) * lattice);
                int ig = (int)Math.Round(Math.Clamp(g, 0, 1) * lattice);
                int ib = (int)Math.Round(Math.Clamp(b, 0, 1) * lattice);
                var key = (ir, ig, ib);
                if (cache.TryGetValue(key, out var existing)) return existing;
                double e = errorAt(ir / (double)lattice, ig / (double)lattice, ib / (double)lattice);
                if (!double.IsFinite(e) || e < 0) e = 1_000;
                var sample = new Mhc2ContinuousSample(
                    ir / (double)lattice, ig / (double)lattice, ib / (double)lattice, e);
                cache[key] = sample;
                if (e > sampledWorst)
                {
                    sampledWorst = e;
                    (worstR, worstG, worstB) = (sample.R, sample.G, sample.B);
                }
                return sample;
            }

            CellEstimate Estimate(Cell cell)
            {
                visitedCells++;
                reachedDepth = Math.Max(reachedDepth, cell.Depth);
                double rm = (cell.R0 + cell.R1) * 0.5;
                double gm = (cell.G0 + cell.G1) * 0.5;
                double bm = (cell.B0 + cell.B1) * 0.5;
                var center = Evaluate(rm, gm, bm);
                var points = new List<Mhc2ContinuousSample>(9) { center };
                for (int mask = 0; mask < 8; mask++)
                    points.Add(Evaluate(
                        (mask & 1) == 0 ? cell.R0 : cell.R1,
                        (mask & 2) == 0 ? cell.G0 : cell.G1,
                        (mask & 4) == 0 ? cell.B0 : cell.B1));

                double localSlope = 0;
                for (int i = 1; i < points.Count; i++)
                {
                    double distance = Distance(points[0], points[i]);
                    if (distance > 0)
                        localSlope = Math.Max(localSlope,
                            Math.Abs(points[0].DeltaE - points[i].DeltaE) / distance);
                }
                // Edge pairs catch a ridge passing near a face without crossing the center.
                for (int i = 1; i < points.Count; i++)
                for (int j = i + 1; j < points.Count; j++)
                {
                    double distance = Distance(points[i], points[j]);
                    if (distance > 0)
                        localSlope = Math.Max(localSlope,
                            Math.Abs(points[i].DeltaE - points[j].DeltaE) / distance);
                }
                globalSlope = Math.Max(globalSlope, localSlope);
                double radius = 0.5 * Math.Sqrt(
                    Square(cell.R1 - cell.R0) + Square(cell.G1 - cell.G0) + Square(cell.B1 - cell.B0));
                double max = points.Max(p => p.DeltaE);
                // Global slope prevents a newly-flat child of a steep parent from being
                // prematurely pruned. The depth taper avoids an irreducible root-sized bound.
                double inheritedSlope = globalSlope / Math.Sqrt(cell.Depth + 1.0);
                double upper = max + slopeSafetyFactor * Math.Max(localSlope, inheritedSlope) * radius;
                return new CellEstimate(cell, max, upper, localSlope);
            }

            var rootCell = new Cell(0, 0, 0, 1, 1, 1, 0);
            _ = Estimate(rootCell);
            int mandatoryCoverageDepth = 1;
            while (mandatoryCoverageDepth < Math.Min(3, maximumDepth))
            {
                int nextDepth = mandatoryCoverageDepth + 1;
                int axisPoints = (1 << nextDepth) + 1;
                if (axisPoints * axisPoints * axisPoints > maximumPoints) break;
                mandatoryCoverageDepth = nextDepth;
            }
            var scaffold = new List<Cell> { rootCell };
            for (int depth = 0; depth < mandatoryCoverageDepth; depth++)
            {
                var children = scaffold.SelectMany(Children).ToList();
                scaffold = children;
            }
            var scaffoldEstimates = scaffold.Select(Estimate).ToList();
            // Re-envelope every scaffold cell with the largest slope seen anywhere in the
            // completed scaffold; traversal order must not make an early flat cell look safer.
            foreach (var estimate in scaffoldEstimates) Enqueue(RefreshGlobalEnvelope(estimate));

            var terminalCells = new List<CellEstimate>();
            while (queue.Count > 0 && cache.Count + 19 <= maximumPoints)
            {
                queue.TryPeek(out var next, out _);
                if (next == null || next.Upper - sampledWorst <= targetEnvelopeGapDeltaE)
                    break;
                queue.Dequeue();
                if (next.Cell.Depth >= maximumDepth)
                {
                    terminalCells.Add(next);
                    continue;
                }
                foreach (var child in Children(next.Cell)) Enqueue(Estimate(child));
            }

            double envelope = sampledWorst;
            foreach (var item in queue.UnorderedItems)
                envelope = Math.Max(envelope, item.Element.Upper);
            foreach (var terminal in terminalCells)
                envelope = Math.Max(envelope, terminal.Upper);

            var retained = cache.Values
                .OrderByDescending(s => s.DeltaE)
                .ThenBy(s => s.R).ThenBy(s => s.G).ThenBy(s => s.B)
                .ToList();
            return new Mhc2ContinuousVerificationResult(
                cache.Count, visitedCells, reachedDepth, sampledWorst, envelope,
                Math.Max(0, envelope - sampledWorst), worstR, worstG, worstB, retained);

            void Enqueue(CellEstimate estimate)
            {
                var c = estimate.Cell;
                queue.Enqueue(estimate, (estimate.Upper, c.Depth, c.R0, c.G0, c.B0));
            }

            static IEnumerable<Cell> Children(Cell c)
            {
                double rm = (c.R0 + c.R1) * 0.5;
                double gm = (c.G0 + c.G1) * 0.5;
                double bm = (c.B0 + c.B1) * 0.5;
                for (int mask = 0; mask < 8; mask++)
                    yield return new Cell(
                        (mask & 1) == 0 ? c.R0 : rm, (mask & 2) == 0 ? c.G0 : gm,
                        (mask & 4) == 0 ? c.B0 : bm,
                        (mask & 1) == 0 ? rm : c.R1, (mask & 2) == 0 ? gm : c.G1,
                        (mask & 4) == 0 ? bm : c.B1, c.Depth + 1);
            }

            CellEstimate RefreshGlobalEnvelope(CellEstimate estimate)
            {
                var c = estimate.Cell;
                double radius = 0.5 * Math.Sqrt(
                    Square(c.R1 - c.R0) + Square(c.G1 - c.G0) + Square(c.B1 - c.B0));
                double inheritedSlope = globalSlope / Math.Sqrt(c.Depth + 1.0);
                return estimate with
                {
                    Upper = estimate.SampledMax + slopeSafetyFactor *
                        Math.Max(estimate.LocalSlope, inheritedSlope) * radius,
                };
            }
        }

        private static double Distance(Mhc2ContinuousSample a, Mhc2ContinuousSample b) =>
            Math.Sqrt(Square(a.R - b.R) + Square(a.G - b.G) + Square(a.B - b.B));

        private static double Square(double value) => value * value;
    }
}
