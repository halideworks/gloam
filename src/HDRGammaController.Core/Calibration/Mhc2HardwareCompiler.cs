using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Error distribution for one simulated correction pipeline. Values are ΔE2000 in the
    /// display model's normalized XYZ domain.
    /// </summary>
    public sealed class Mhc2ErrorStatistics
    {
        public double AverageDeltaE { get; set; }
        public double P95DeltaE { get; set; }
        public double MaxDeltaE { get; set; }
    }

    /// <summary>
    /// One model-selected stress point for post-install physical verification.
    /// <see cref="EmpiricalUpperEstimate"/> combines the compiled model's predicted error
    /// with held-out/interpolation residuals and local measurement coverage. It is an
    /// empirical engineering estimate, not a formal statistical confidence bound.
    /// </summary>
    public sealed class Mhc2Counterexample
    {
        public string Name { get; set; } = "";
        public double R { get; set; }
        public double G { get; set; }
        public double B { get; set; }
        public double PredictedDeltaE { get; set; }
        public double EmpiricalUncertainty { get; set; }
        public double EmpiricalUpperEstimate { get; set; }
        public double? MeasuredDeltaE { get; set; }
    }

    /// <summary>
    /// Inspectable proof artifact emitted with an SDR MHC2 compile. The first half records
    /// model predictions over a dense cube; the second half is filled by a post-install
    /// colorimeter sweep at the model's worst, uncertainty-aware counterexamples.
    /// </summary>
    public sealed class Mhc2ProofCertificate
    {
        public int SchemaVersion { get; set; } = 1;
        public string Method { get; set; } = "MHC2 matrix + monotone 1D LUT constrained coordinate search";
        public string Status { get; set; } = "Model only — physical counterexamples not yet measured";
        public bool OptimizerApplied { get; set; }
        public int ModelSampleCount { get; set; }
        public Mhc2ErrorStatistics Native { get; set; } = new();
        public Mhc2ErrorStatistics Baseline { get; set; } = new();
        public Mhc2ErrorStatistics Compiled { get; set; } = new();
        public double CorrectabilityFraction { get; set; }
        public double EmpiricalModelResidualP95 { get; set; }
        public double EmpiricalUpperP95Estimate { get; set; }
        public List<Mhc2Counterexample> Counterexamples { get; set; } = new();
        public int MeasuredCounterexampleCount { get; set; }
        public double? MeasuredWorstDeltaE { get; set; }
        public bool? EmpiricalEstimatesHeld { get; set; }
        public string Limitation { get; set; } =
            "Model-based empirical certificate; it is not a formal metrological guarantee. " +
            "Physical counterexample measurements are authoritative.";

        /// <summary>Builds the targeted patches that turn the model certificate into evidence.</summary>
        public IReadOnlyList<ColorPatch> BuildVerificationPatches(CalibrationTarget target)
        {
            return Counterexamples.Select((c, i) =>
            {
                var xyz = TargetXyz(target, c.R, c.G, c.B);
                return new ColorPatch
                {
                    Name = c.Name,
                    DisplayRgb = new LinearRgb(c.R, c.G, c.B),
                    TargetXyz = xyz,
                    TargetLab = ColorMath.XyzToLab(xyz, TargetLabWhite(target)),
                    Category = PatchCategory.General,
                    Index = i,
                    IsCritical = true,
                };
            }).ToList();
        }

        /// <summary>
        /// Attaches colorimeter readings from the targeted sweep. <paramref name="whiteY"/>
        /// is the simultaneously measured standard verification white and keeps proof-patch
        /// normalization identical to the normal verification grade.
        /// </summary>
        public void RecordMeasurements(
            IReadOnlyList<MeasurementResult> measurements,
            CalibrationTarget target,
            double whiteY)
        {
            foreach (var counterexample in Counterexamples)
                counterexample.MeasuredDeltaE = null;
            MeasuredCounterexampleCount = 0;
            MeasuredWorstDeltaE = null;
            EmpiricalEstimatesHeld = null;
            Status = "Model only — physical counterexamples not yet measured";
            if (!double.IsFinite(whiteY) || whiteY <= 0)
                return;

            var validMeasurements = measurements.Where(m => m.IsValid).ToList();
            var byName = validMeasurements
                .GroupBy(m => m.Patch.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            var labWhite = TargetLabWhite(target);
            int measured = 0;
            bool held = true;
            double worst = 0;
            foreach (var counterexample in Counterexamples)
            {
                if (!byName.TryGetValue(counterexample.Name, out var reading))
                {
                    reading = validMeasurements.LastOrDefault(m =>
                        Math.Abs(m.Patch.DisplayRgb.R - counterexample.R) <= 1e-6 &&
                        Math.Abs(m.Patch.DisplayRgb.G - counterexample.G) <= 1e-6 &&
                        Math.Abs(m.Patch.DisplayRgb.B - counterexample.B) <= 1e-6);
                }
                if (reading == null)
                    continue;

                var targetLab = ColorMath.XyzToLab(
                    TargetXyz(target, counterexample.R, counterexample.G, counterexample.B), labWhite);
                var measuredLab = ColorMath.XyzToLab(new CieXyz(
                    reading.Xyz.X / whiteY,
                    reading.Xyz.Y / whiteY,
                    reading.Xyz.Z / whiteY), labWhite);
                double deltaE = measuredLab.DeltaE2000(targetLab);
                if (!double.IsFinite(deltaE))
                    continue;

                counterexample.MeasuredDeltaE = deltaE;
                measured++;
                worst = Math.Max(worst, deltaE);
                held &= deltaE <= counterexample.EmpiricalUpperEstimate + 1e-9;
            }

            MeasuredCounterexampleCount = measured;
            MeasuredWorstDeltaE = measured > 0 ? worst : null;
            EmpiricalEstimatesHeld = measured > 0 ? held : null;
            Status = measured == 0
                ? "Model only — physical counterexamples not yet measured"
                : held
                    ? $"Measured {measured} adversarial counterexamples; empirical estimates held"
                    : $"Measured {measured} adversarial counterexamples; model estimate exceeded";
        }

        public string Describe()
        {
            string optimizer = OptimizerApplied ? "optimized" : "baseline retained";
            string measured = MeasuredWorstDeltaE is { } worst
                ? $" Physical worst {worst:F2} ΔE across {MeasuredCounterexampleCount} counterexamples; " +
                  (EmpiricalEstimatesHeld == true ? "empirical estimates held." : "an empirical estimate was exceeded.")
                : " Counterexamples await physical verification.";
            return $"Proof-Calibrate {optimizer}: model P95 {Compiled.P95DeltaE:F2} ΔE, " +
                   $"empirical upper estimate {EmpiricalUpperP95Estimate:F2}, " +
                   $"correctability {CorrectabilityFraction:P0}.{measured}";
        }

        internal Mhc2ProofCertificate SanitizedCopy()
        {
            static double F(double value) => double.IsFinite(value) ? value : 0;
            static double? FN(double? value) => value is { } v && double.IsFinite(v) ? v : null;
            static Mhc2ErrorStatistics S(Mhc2ErrorStatistics? value) => new()
            {
                AverageDeltaE = Math.Max(0, F(value?.AverageDeltaE ?? 0)),
                P95DeltaE = Math.Max(0, F(value?.P95DeltaE ?? 0)),
                MaxDeltaE = Math.Max(0, F(value?.MaxDeltaE ?? 0)),
            };

            return new Mhc2ProofCertificate
            {
                SchemaVersion = Math.Max(1, SchemaVersion),
                Method = Method ?? "MHC2 constrained compile",
                Status = Status ?? "Unknown",
                OptimizerApplied = OptimizerApplied,
                ModelSampleCount = Math.Max(0, ModelSampleCount),
                Native = S(Native),
                Baseline = S(Baseline),
                Compiled = S(Compiled),
                CorrectabilityFraction = Math.Clamp(F(CorrectabilityFraction), 0, 1),
                EmpiricalModelResidualP95 = Math.Max(0, F(EmpiricalModelResidualP95)),
                EmpiricalUpperP95Estimate = Math.Max(0, F(EmpiricalUpperP95Estimate)),
                Counterexamples = (Counterexamples ?? new List<Mhc2Counterexample>())
                    .Where(c => c != null)
                    .Take(Mhc2HardwareCompiler.DefaultCounterexampleCount)
                    .Select(c =>
                    {
                        double predicted = Math.Max(0, F(c.PredictedDeltaE));
                        return new Mhc2Counterexample
                        {
                            Name = c.Name ?? "",
                            R = Math.Clamp(F(c.R), 0, 1),
                            G = Math.Clamp(F(c.G), 0, 1),
                            B = Math.Clamp(F(c.B), 0, 1),
                            PredictedDeltaE = predicted,
                            EmpiricalUncertainty = Math.Max(0, F(c.EmpiricalUncertainty)),
                            EmpiricalUpperEstimate = Math.Max(predicted, F(c.EmpiricalUpperEstimate)),
                            MeasuredDeltaE = FN(c.MeasuredDeltaE),
                        };
                    }).ToList(),
                MeasuredCounterexampleCount = Math.Max(0, MeasuredCounterexampleCount),
                MeasuredWorstDeltaE = FN(MeasuredWorstDeltaE),
                EmpiricalEstimatesHeld = EmpiricalEstimatesHeld,
                Limitation = Limitation ?? "Model-based empirical certificate; physical measurements are authoritative.",
            };
        }

        private static CieXyz TargetXyz(CalibrationTarget target, double r, double g, double b) =>
            target.LinearRgbToXyz(new LinearRgb(
                CalibrationVerifier.LinearizePatchSignal(target, r),
                CalibrationVerifier.LinearizePatchSignal(target, g),
                CalibrationVerifier.LinearizePatchSignal(target, b)));

        private static CieXyz TargetLabWhite(CalibrationTarget target) =>
            target.WhitePoint.Equals(Chromaticity.D65)
                ? ColorMath.D65White
                : target.WhitePoint.ToXyz(1.0);
    }

    /// <summary>Result of compiling an ideal 3D correction into Windows' MHC2 primitives.</summary>
    public sealed record Mhc2CompileResult(
        double[,] Matrix,
        double[] LutR,
        double[] LutG,
        double[] LutB,
        Mhc2ProofCertificate Certificate);

    /// <summary>
    /// Distills an ideal 3D correction into the hardware path Windows actually exposes:
    /// one 3×3 linear-light matrix followed by three monotone 1D signal LUTs. The search is
    /// deterministic, bounded and baseline-safe: a fitted payload is returned only when it
    /// improves the dense perceptual objective over the caller's existing payload.
    /// </summary>
    public static class Mhc2HardwareCompiler
    {
        public const int DefaultCounterexampleCount = 6;
        private const int FitGridSize = 9;
        private const int ScoreGridSize = 11;
        private const int CounterexampleGridSize = 15;
        private const int FitBins = 257;
        private const double ImprovementEpsilon = 1e-5;
        private const double MinimumCounterexampleSeparation = 0.18;
        private const double MissingResidualUncertaintyDeltaE = AdaptivePatchPlanner.ColorTargetDeltaE;

        private readonly record struct Sample(double R, double G, double B, double IdealR, double IdealG, double IdealB);
        private readonly record struct ScoredPoint(double R, double G, double B, double DeltaE);
        private sealed record Payload(double[,] Matrix, double[] R, double[] G, double[] B, Mhc2ErrorStatistics Stats, double Objective);

        public static Mhc2CompileResult Compile(
            double[,] baselineMatrix,
            double[] baselineLutR,
            double[] baselineLutG,
            double[] baselineLutB,
            Lut3D idealCorrection,
            DisplayCharacterization characterization,
            CalibrationTarget target,
            IReadOnlyList<MeasurementResult>? measurements = null,
            IReadOnlyList<ModelResidual>? modelResiduals = null,
            bool optimizeMatrix = true)
        {
            ValidateInputs(baselineMatrix, baselineLutR, baselineLutG, baselineLutB,
                idealCorrection, characterization, target);

            var baseline = EvaluatePayload(CloneMatrix(baselineMatrix),
                (double[])baselineLutR.Clone(), (double[])baselineLutG.Clone(), (double[])baselineLutB.Clone(),
                characterization, target, ScoreGridSize, baselineMatrix);
            var fitSamples = BuildFitSamples(idealCorrection);

            var fittedAtBaseline = FitPayload(CloneMatrix(baselineMatrix), fitSamples,
                baselineLutR, baselineLutG, baselineLutB, characterization, target, baselineMatrix);
            var best = fittedAtBaseline.Objective + ImprovementEpsilon < baseline.Objective
                ? fittedAtBaseline
                : baseline;

            // Coordinate descent in the nine matrix coefficients. LUTs are re-solved for
            // every candidate, so each score describes the complete matrix+shaper payload.
            foreach (double step in optimizeMatrix ? new[] { 0.020, 0.010, 0.005 } : Array.Empty<double>())
            {
                bool improved;
                int passes = 0;
                do
                {
                    improved = false;
                    passes++;
                    for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 3; col++)
                    {
                        Payload coordinateBest = best;
                        foreach (double direction in new[] { -1.0, 1.0 })
                        {
                            var candidateMatrix = CloneMatrix(best.Matrix);
                            candidateMatrix[row, col] += direction * step;
                            if (!IsSafeMatrix(candidateMatrix))
                                continue;
                            var candidate = FitPayload(candidateMatrix, fitSamples,
                                baselineLutR, baselineLutG, baselineLutB,
                                characterization, target, baselineMatrix);
                            if (candidate.Objective + ImprovementEpsilon < coordinateBest.Objective)
                                coordinateBest = candidate;
                        }
                        if (!ReferenceEquals(coordinateBest, best))
                        {
                            best = coordinateBest;
                            improved = true;
                        }
                    }
                } while (improved && passes < 2);
            }

            // The pre-existing path is the safety contract, independent of what the local
            // optimizer did along the way.
            bool optimizerApplied = best.Objective + ImprovementEpsilon < baseline.Objective;
            if (!optimizerApplied)
                best = baseline;

            var identity = IdentityLut(baselineLutR.Length);
            var native = EvaluatePayload(IdentityMatrix(), identity, identity, identity,
                characterization, target, ScoreGridSize, baselineMatrix);
            var validResiduals = (modelResiduals ?? Array.Empty<ModelResidual>())
                .Where(r => double.IsFinite(r.Magnitude) && r.Magnitude >= 0)
                .Select(r => r.Magnitude)
                .ToArray();
            // Missing held-out residuals mean uncertainty is unknown, not zero. Use the
            // planner's established color-accuracy target as a conservative floor only in
            // that no-evidence case; genuine zero residual observations remain zero.
            double residualP95 = validResiduals.Length > 0
                ? Percentile(validResiduals, 0.95)
                : MissingResidualUncertaintyDeltaE;
            var counterexamples = FindCounterexamples(best, characterization, target,
                measurements, residualP95, DefaultCounterexampleCount);

            double nativeError = PerceptualObjective(native.Stats);
            double compiledError = PerceptualObjective(best.Stats);
            double correctability = nativeError > 1e-9
                ? Math.Clamp(1.0 - compiledError / nativeError, 0, 1)
                : compiledError <= 1e-9 ? 1 : 0;
            var certificate = new Mhc2ProofCertificate
            {
                OptimizerApplied = optimizerApplied,
                ModelSampleCount = CounterexampleGridSize * CounterexampleGridSize * CounterexampleGridSize,
                Native = native.Stats,
                Baseline = baseline.Stats,
                Compiled = best.Stats,
                CorrectabilityFraction = correctability,
                EmpiricalModelResidualP95 = residualP95,
                EmpiricalUpperP95Estimate = best.Stats.P95DeltaE + residualP95,
                Counterexamples = counterexamples,
            };

            return new Mhc2CompileResult(
                CloneMatrix(best.Matrix), (double[])best.R.Clone(), (double[])best.G.Clone(), (double[])best.B.Clone(),
                certificate);
        }

        private static Payload FitPayload(
            double[,] matrix,
            IReadOnlyList<Sample> samples,
            double[] baselineR,
            double[] baselineG,
            double[] baselineB,
            DisplayCharacterization characterization,
            CalibrationTarget target,
            double[,] baselineMatrix)
        {
            var coordinates = samples.Select(s =>
            {
                var post = ApplyMatrix(matrix, s.R, s.G, s.B);
                return (post, s);
            }).ToList();
            var r = FitMonotoneLut(coordinates.Select(v => (v.post.R, v.s.IdealR)), baselineR);
            var g = FitMonotoneLut(coordinates.Select(v => (v.post.G, v.s.IdealG)), baselineG);
            var b = FitMonotoneLut(coordinates.Select(v => (v.post.B, v.s.IdealB)), baselineB);
            return EvaluatePayload(matrix, r, g, b, characterization, target, ScoreGridSize, baselineMatrix);
        }

        private static Payload EvaluatePayload(
            double[,] matrix,
            double[] r,
            double[] g,
            double[] b,
            DisplayCharacterization characterization,
            CalibrationTarget target,
            int gridSize,
            double[,] baselineMatrix)
        {
            var errors = ScoreCube(matrix, r, g, b, characterization, target, gridSize)
                .Select(p => p.DeltaE).OrderBy(v => v).ToArray();
            var stats = new Mhc2ErrorStatistics
            {
                AverageDeltaE = errors.Average(),
                P95DeltaE = Percentile(errors, 0.95),
                MaxDeltaE = errors[^1],
            };
            double matrixPenalty = 0;
            for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                double d = matrix[row, col] - baselineMatrix[row, col];
                matrixPenalty += d * d;
            }
            matrixPenalty = 0.01 * Math.Sqrt(matrixPenalty / 9.0);
            double objective = PerceptualObjective(stats) + matrixPenalty;
            return new Payload(matrix, r, g, b, stats, objective);
        }

        private static double PerceptualObjective(Mhc2ErrorStatistics stats) =>
            0.20 * stats.AverageDeltaE + 0.50 * stats.P95DeltaE + 0.30 * stats.MaxDeltaE;

        private static List<ScoredPoint> ScoreCube(
            double[,] matrix,
            double[] r,
            double[] g,
            double[] b,
            DisplayCharacterization characterization,
            CalibrationTarget target,
            int gridSize)
        {
            var scored = new List<ScoredPoint>(gridSize * gridSize * gridSize);
            var labWhite = target.WhitePoint.Equals(Chromaticity.D65)
                ? ColorMath.D65White
                : target.WhitePoint.ToXyz(1.0);
            for (int ri = 0; ri < gridSize; ri++)
            for (int gi = 0; gi < gridSize; gi++)
            for (int bi = 0; bi < gridSize; bi++)
            {
                double inputR = ri / (double)(gridSize - 1);
                double inputG = gi / (double)(gridSize - 1);
                double inputB = bi / (double)(gridSize - 1);
                var post = ApplyMatrix(matrix, inputR, inputG, inputB);
                double driveR = Lookup(r, post.R);
                double driveG = Lookup(g, post.G);
                double driveB = Lookup(b, post.B);
                var measuredXyz = DisplayXyz(characterization, driveR, driveG, driveB);
                var targetXyz = target.LinearRgbToXyz(new LinearRgb(
                    CalibrationVerifier.LinearizePatchSignal(target, inputR),
                    CalibrationVerifier.LinearizePatchSignal(target, inputG),
                    CalibrationVerifier.LinearizePatchSignal(target, inputB)));
                double deltaE = ColorMath.XyzToLab(measuredXyz, labWhite)
                    .DeltaE2000(ColorMath.XyzToLab(targetXyz, labWhite));
                scored.Add(new ScoredPoint(inputR, inputG, inputB,
                    double.IsFinite(deltaE) ? deltaE : 1_000));
            }
            return scored;
        }

        private static List<Mhc2Counterexample> FindCounterexamples(
            Payload payload,
            DisplayCharacterization characterization,
            CalibrationTarget target,
            IReadOnlyList<MeasurementResult>? measurements,
            double residualP95,
            int count)
        {
            var measured = (measurements ?? Array.Empty<MeasurementResult>())
                .Where(m => m.IsValid && m.Patch.Nits is null)
                .Select(m => m.Patch.DisplayRgb)
                .ToList();
            var ranked = ScoreCube(payload.Matrix, payload.R, payload.G, payload.B,
                    characterization, target, CounterexampleGridSize)
                .Select(point =>
                {
                    double gap = measured.Count == 0
                        ? 1.0
                        : measured.Min(m => Distance(point.R, point.G, point.B, m.R, m.G, m.B));
                    double coverageMultiplier = 1.0 + Math.Min(1.0, gap / 0.35);
                    double uncertainty = residualP95 * coverageMultiplier;
                    return (point, uncertainty, upper: point.DeltaE + uncertainty);
                })
                .OrderByDescending(v => v.upper)
                .ThenByDescending(v => v.point.DeltaE)
                .ToList();

            var selected = new List<(ScoredPoint point, double uncertainty, double upper)>();
            foreach (var candidate in ranked)
            {
                if (selected.Any(s => Distance(candidate.point.R, candidate.point.G, candidate.point.B,
                                               s.point.R, s.point.G, s.point.B) < MinimumCounterexampleSeparation))
                    continue;
                selected.Add(candidate);
                if (selected.Count == count)
                    break;
            }

            return selected.Select((v, i) => new Mhc2Counterexample
            {
                Name = $"Proof CE {i + 1} ({v.point.R:F2}, {v.point.G:F2}, {v.point.B:F2})",
                R = v.point.R,
                G = v.point.G,
                B = v.point.B,
                PredictedDeltaE = v.point.DeltaE,
                EmpiricalUncertainty = v.uncertainty,
                EmpiricalUpperEstimate = v.upper,
            }).ToList();
        }

        private static IReadOnlyList<Sample> BuildFitSamples(Lut3D ideal)
        {
            var samples = new List<Sample>(FitGridSize * FitGridSize * FitGridSize);
            for (int ri = 0; ri < FitGridSize; ri++)
            for (int gi = 0; gi < FitGridSize; gi++)
            for (int bi = 0; bi < FitGridSize; bi++)
            {
                double r = ri / (double)(FitGridSize - 1);
                double g = gi / (double)(FitGridSize - 1);
                double b = bi / (double)(FitGridSize - 1);
                var desired = ideal.LookupTetrahedral((float)r, (float)g, (float)b);
                samples.Add(new Sample(r, g, b,
                    Clamp01(desired.R), Clamp01(desired.G), Clamp01(desired.B)));
            }
            return samples;
        }

        /// <summary>
        /// Weighted isotonic regression (PAVA) over fixed signal bins. A light baseline
        /// prior stabilizes sparsely populated bins without preventing the ideal 3D LUT's
        /// channel marginals from moving the solution.
        /// </summary>
        private static double[] FitMonotoneLut(IEnumerable<(double X, double Y)> pairs, double[] baseline)
        {
            var sums = new double[FitBins];
            var weights = new double[FitBins];
            foreach (var (xRaw, yRaw) in pairs)
            {
                int bin = (int)Math.Round(Clamp01(xRaw) * (FitBins - 1));
                sums[bin] += Clamp01(yRaw);
                weights[bin] += 1.0;
            }

            for (int i = 0; i < FitBins; i++)
            {
                double x = i / (double)(FitBins - 1);
                double priorWeight = i == 0 || i == FitBins - 1 ? 4.0 : 0.12;
                sums[i] += priorWeight * Lookup(baseline, x);
                weights[i] += priorWeight;
            }

            var value = new double[FitBins];
            var blockWeight = new double[FitBins];
            var blockStart = new int[FitBins];
            int blocks = 0;
            for (int i = 0; i < FitBins; i++)
            {
                value[blocks] = sums[i] / weights[i];
                blockWeight[blocks] = weights[i];
                blockStart[blocks] = i;
                blocks++;
                while (blocks >= 2 && value[blocks - 2] > value[blocks - 1])
                {
                    double combinedWeight = blockWeight[blocks - 2] + blockWeight[blocks - 1];
                    value[blocks - 2] = (value[blocks - 2] * blockWeight[blocks - 2] +
                                         value[blocks - 1] * blockWeight[blocks - 1]) / combinedWeight;
                    blockWeight[blocks - 2] = combinedWeight;
                    blocks--;
                }
            }

            var fittedBins = new double[FitBins];
            for (int block = 0; block < blocks; block++)
            {
                int start = blockStart[block];
                int end = block + 1 < blocks ? blockStart[block + 1] : FitBins;
                for (int i = start; i < end; i++)
                    fittedBins[i] = Clamp01(value[block]);
            }

            var result = new double[baseline.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = Lookup(fittedBins, i / (double)(result.Length - 1));
            for (int i = 1; i < result.Length; i++)
                result[i] = Math.Max(result[i], result[i - 1]);
            return result;
        }

        private static (double R, double G, double B) ApplyMatrix(double[,] matrix, double r, double g, double b)
        {
            double lr = ColorMath.SrgbEotf(Clamp01(r));
            double lg = ColorMath.SrgbEotf(Clamp01(g));
            double lb = ColorMath.SrgbEotf(Clamp01(b));
            double mr = matrix[0, 0] * lr + matrix[0, 1] * lg + matrix[0, 2] * lb;
            double mg = matrix[1, 0] * lr + matrix[1, 1] * lg + matrix[1, 2] * lb;
            double mb = matrix[2, 0] * lr + matrix[2, 1] * lg + matrix[2, 2] * lb;
            return (ColorMath.SrgbOetf(Clamp01(mr)),
                    ColorMath.SrgbOetf(Clamp01(mg)),
                    ColorMath.SrgbOetf(Clamp01(mb)));
        }

        private static CieXyz DisplayXyz(DisplayCharacterization c, double r, double g, double b)
        {
            double lr = c.RedToneCurve.Lookup(r);
            double lg = c.GreenToneCurve.Lookup(g);
            double lb = c.BlueToneCurve.Lookup(b);
            var m = c.RgbToXyzMatrix;
            return new CieXyz(
                m[0, 0] * lr + m[0, 1] * lg + m[0, 2] * lb,
                m[1, 0] * lr + m[1, 1] * lg + m[1, 2] * lb,
                m[2, 0] * lr + m[2, 1] * lg + m[2, 2] * lb);
        }

        private static bool IsSafeMatrix(double[,] matrix)
        {
            for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                if (!double.IsFinite(matrix[row, col]) || matrix[row, col] < -0.25 || matrix[row, col] > 1.3)
                    return false;

            for (int mask = 0; mask < 8; mask++)
            {
                double r = (mask & 1) != 0 ? 1 : 0;
                double g = (mask & 2) != 0 ? 1 : 0;
                double b = (mask & 4) != 0 ? 1 : 0;
                for (int row = 0; row < 3; row++)
                {
                    double value = matrix[row, 0] * r + matrix[row, 1] * g + matrix[row, 2] * b;
                    if (value < GamutReachability.MinReachablePrimaryDrive || value > 1.000001)
                        return false;
                }
            }
            return true;
        }

        private static void ValidateInputs(
            double[,] matrix, double[] r, double[] g, double[] b, Lut3D ideal,
            DisplayCharacterization characterization, CalibrationTarget target)
        {
            ArgumentNullException.ThrowIfNull(matrix);
            ArgumentNullException.ThrowIfNull(r);
            ArgumentNullException.ThrowIfNull(g);
            ArgumentNullException.ThrowIfNull(b);
            ArgumentNullException.ThrowIfNull(ideal);
            ArgumentNullException.ThrowIfNull(characterization);
            ArgumentNullException.ThrowIfNull(target);
            if (matrix.GetLength(0) != 3 || matrix.GetLength(1) != 3)
                throw new ArgumentException("MHC2 matrix must be exactly 3x3.", nameof(matrix));
            if (r.Length < 2 || g.Length != r.Length || b.Length != r.Length)
                throw new ArgumentException("MHC2 tone LUTs must have equal lengths of at least two entries.");
            for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                if (!double.IsFinite(matrix[row, col]))
                    throw new ArgumentException("MHC2 matrix entries must be finite.", nameof(matrix));
            foreach (var lut in new[] { r, g, b })
            {
                double previous = double.NegativeInfinity;
                foreach (double value in lut)
                {
                    if (!double.IsFinite(value) || value < 0 || value > 1 || value + 1e-12 < previous)
                        throw new ArgumentException("MHC2 tone LUTs must be finite, bounded, and monotone.");
                    previous = value;
                }
            }
        }

        private static double Lookup(double[] lut, double input)
        {
            double position = Clamp01(input) * (lut.Length - 1);
            int lower = (int)position;
            int upper = Math.Min(lower + 1, lut.Length - 1);
            double fraction = position - lower;
            return lut[lower] + fraction * (lut[upper] - lut[lower]);
        }

        private static double Percentile(IEnumerable<double> values, double percentile)
        {
            var sorted = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return 0;
            double position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
            int lower = (int)position;
            int upper = Math.Min(lower + 1, sorted.Length - 1);
            return sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
        }

        private static double Distance(double ar, double ag, double ab, double br, double bg, double bb)
        {
            double dr = ar - br, dg = ag - bg, db = ab - bb;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        private static double[,] CloneMatrix(double[,] matrix) => (double[,])matrix.Clone();

        private static double[,] IdentityMatrix() => new double[,]
        {
            { 1, 0, 0 },
            { 0, 1, 0 },
            { 0, 0, 1 },
        };

        private static double[] IdentityLut(int length) =>
            Enumerable.Range(0, length).Select(i => i / (double)(length - 1)).ToArray();

        private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
    }
}
