using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>Perceptual and structure-preservation metrics for one MHC2 payload.</summary>
    public sealed class Mhc2ErrorStatistics
    {
        public double AverageDeltaE { get; set; }
        public double P95DeltaE { get; set; }
        public double MaxDeltaE { get; set; }
        public double MaxNeutralDeltaE { get; set; }
        public double MaxNeutralChroma { get; set; }
        public double MaxAnchorDeltaE { get; set; }
        public double GradientRmsDeltaE { get; set; }
        public double MaxGradientDeltaE { get; set; }
        public double HardwareWorstP95DeltaE { get; set; }
        public int LutPlateauCount { get; set; }
        public double MaxLutSlope { get; set; }
    }

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

    /// <summary>A normalized physical observation made through an installed SDR payload.</summary>
    public sealed record Mhc2PhysicalObservation(
        double R, double G, double B, double X, double Y, double Z, double Weight = 1.0);

    /// <summary>
    /// Inspectable evidence emitted by the compiler. Schema 2 adds ordering diagnostics,
    /// exact s15Fixed16 simulation, adaptive continuous-cube coverage and neutral/gradient
    /// invariants. Physical measurements remain authoritative.
    /// </summary>
    public sealed class Mhc2ProofCertificate
    {
        public int SchemaVersion { get; set; } = 2;
        public string Method { get; set; } =
            "order-optimal monotone ridge synthesis + precision gauge + adaptive cube branch-and-bound";
        public string Status { get; set; } = "Model only — physical counterexamples not yet measured";
        public bool OptimizerApplied { get; set; }
        public int ModelSampleCount { get; set; }
        public Mhc2ErrorStatistics Native { get; set; } = new();
        public Mhc2ErrorStatistics Baseline { get; set; } = new();
        public Mhc2ErrorStatistics Compiled { get; set; } = new();
        public double CorrectabilityFraction { get; set; }
        public double EmpiricalModelResidualP95 { get; set; }
        public double EmpiricalUpperP95Estimate { get; set; }
        public double OrderConflictRed { get; set; }
        public double OrderConflictGreen { get; set; }
        public double OrderConflictBlue { get; set; }
        public double IsotonicIrreducibleRmse { get; set; }
        public double MatrixQuantizationMaxAbs { get; set; }
        public double LutQuantizationMaxAbs { get; set; }
        public int HardwareLutEntries { get; set; } = 1024;
        public double PrecisionUtilizationRed { get; set; }
        public double PrecisionUtilizationGreen { get; set; }
        public double PrecisionUtilizationBlue { get; set; }
        public int ContinuousCellCount { get; set; }
        public int ContinuousMaxDepth { get; set; }
        public double ContinuousSampledMaxDeltaE { get; set; }
        public double ContinuousEmpiricalEnvelopeDeltaE { get; set; }
        public double ContinuousEnvelopeGapDeltaE { get; set; }
        public int ClosedLoopRound { get; set; }
        public string? ClosedLoopDecision { get; set; }
        public List<Mhc2Counterexample> Counterexamples { get; set; } = new();
        public List<Mhc2PhysicalObservation> PhysicalObservations { get; set; } = new();
        public int MeasuredCounterexampleCount { get; set; }
        public double? MeasuredWorstDeltaE { get; set; }
        public bool? EmpiricalEstimatesHeld { get; set; }
        public string Limitation { get; set; } =
            "The octree envelope is a slope-inflated numerical coverage certificate, not a " +
            "formal metrological bound. Sparse display characterization and ΔE2000 do not " +
            "supply a proven global Lipschitz constant; physical counterexamples are authoritative.";

        public IReadOnlyList<ColorPatch> BuildVerificationPatches(CalibrationTarget target) =>
            Counterexamples.Select((c, i) =>
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

        public void RecordMeasurements(
            IReadOnlyList<MeasurementResult> measurements,
            CalibrationTarget target,
            double whiteY)
        {
            foreach (var c in Counterexamples) c.MeasuredDeltaE = null;
            PhysicalObservations = new List<Mhc2PhysicalObservation>();
            MeasuredCounterexampleCount = 0;
            MeasuredWorstDeltaE = null;
            EmpiricalEstimatesHeld = null;
            Status = "Model only — physical counterexamples not yet measured";
            if (!double.IsFinite(whiteY) || whiteY <= 0) return;

            var valid = measurements.Where(m => m.IsValid).ToList();
            var byName = valid.GroupBy(m => m.Patch.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            var labWhite = TargetLabWhite(target);
            int measured = 0;
            bool held = true;
            double worst = 0;
            foreach (var c in Counterexamples)
            {
                if (!byName.TryGetValue(c.Name, out var reading))
                    reading = valid.LastOrDefault(m => SameRgb(m.Patch.DisplayRgb, c.R, c.G, c.B));
                if (reading == null) continue;

                var normalized = new CieXyz(
                    reading.Xyz.X / whiteY, reading.Xyz.Y / whiteY, reading.Xyz.Z / whiteY);
                double deltaE = ColorMath.XyzToLab(normalized, labWhite)
                    .DeltaE2000(ColorMath.XyzToLab(TargetXyz(target, c.R, c.G, c.B), labWhite));
                if (!double.IsFinite(deltaE)) continue;
                c.MeasuredDeltaE = deltaE;
                PhysicalObservations.Add(new Mhc2PhysicalObservation(
                    c.R, c.G, c.B, normalized.X, normalized.Y, normalized.Z));
                measured++;
                worst = Math.Max(worst, deltaE);
                held &= deltaE <= c.EmpiricalUpperEstimate + 1e-9;
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
                  (EmpiricalEstimatesHeld == true ? "envelope held." : "envelope exceeded.")
                : " Counterexamples await physical verification.";
            string loop = ClosedLoopRound > 0 ? $" Closed-loop round {ClosedLoopRound}: {ClosedLoopDecision}." : "";
            return $"Proof-Calibrate {optimizer}: P95 {Compiled.P95DeltaE:F2} ΔE " +
                   $"(serialized worst {Compiled.HardwareWorstP95DeltaE:F2}), continuous max " +
                   $"{ContinuousSampledMaxDeltaE:F2} +{ContinuousEnvelopeGapDeltaE:F2}, " +
                   $"order conflicts R/G/B {OrderConflictRed:P1}/{OrderConflictGreen:P1}/{OrderConflictBlue:P1}, " +
                   $"correctability {CorrectabilityFraction:P0}.{measured}{loop}";
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
                MaxNeutralDeltaE = Math.Max(0, F(value?.MaxNeutralDeltaE ?? 0)),
                MaxNeutralChroma = Math.Max(0, F(value?.MaxNeutralChroma ?? 0)),
                MaxAnchorDeltaE = Math.Max(0, F(value?.MaxAnchorDeltaE ?? 0)),
                GradientRmsDeltaE = Math.Max(0, F(value?.GradientRmsDeltaE ?? 0)),
                MaxGradientDeltaE = Math.Max(0, F(value?.MaxGradientDeltaE ?? 0)),
                HardwareWorstP95DeltaE = Math.Max(0, F(value?.HardwareWorstP95DeltaE ?? 0)),
                LutPlateauCount = Math.Max(0, value?.LutPlateauCount ?? 0),
                MaxLutSlope = Math.Max(0, F(value?.MaxLutSlope ?? 0)),
            };
            return new Mhc2ProofCertificate
            {
                SchemaVersion = Math.Max(2, SchemaVersion), Method = Method ?? "MHC2 constrained compile",
                Status = Status ?? "Unknown", OptimizerApplied = OptimizerApplied,
                ModelSampleCount = Math.Max(0, ModelSampleCount), Native = S(Native), Baseline = S(Baseline),
                Compiled = S(Compiled), CorrectabilityFraction = Math.Clamp(F(CorrectabilityFraction), 0, 1),
                EmpiricalModelResidualP95 = Math.Max(0, F(EmpiricalModelResidualP95)),
                EmpiricalUpperP95Estimate = Math.Max(0, F(EmpiricalUpperP95Estimate)),
                OrderConflictRed = Math.Clamp(F(OrderConflictRed), 0, 1),
                OrderConflictGreen = Math.Clamp(F(OrderConflictGreen), 0, 1),
                OrderConflictBlue = Math.Clamp(F(OrderConflictBlue), 0, 1),
                IsotonicIrreducibleRmse = Math.Max(0, F(IsotonicIrreducibleRmse)),
                MatrixQuantizationMaxAbs = Math.Max(0, F(MatrixQuantizationMaxAbs)),
                LutQuantizationMaxAbs = Math.Max(0, F(LutQuantizationMaxAbs)),
                HardwareLutEntries = Math.Clamp(HardwareLutEntries, 2, 4096),
                PrecisionUtilizationRed = Math.Clamp(F(PrecisionUtilizationRed), 0, 2),
                PrecisionUtilizationGreen = Math.Clamp(F(PrecisionUtilizationGreen), 0, 2),
                PrecisionUtilizationBlue = Math.Clamp(F(PrecisionUtilizationBlue), 0, 2),
                ContinuousCellCount = Math.Max(0, ContinuousCellCount),
                ContinuousMaxDepth = Math.Max(0, ContinuousMaxDepth),
                ContinuousSampledMaxDeltaE = Math.Max(0, F(ContinuousSampledMaxDeltaE)),
                ContinuousEmpiricalEnvelopeDeltaE = Math.Max(0, F(ContinuousEmpiricalEnvelopeDeltaE)),
                ContinuousEnvelopeGapDeltaE = Math.Max(0, F(ContinuousEnvelopeGapDeltaE)),
                ClosedLoopRound = Math.Max(0, ClosedLoopRound), ClosedLoopDecision = ClosedLoopDecision,
                Counterexamples = (Counterexamples ?? new()).Where(c => c != null)
                    .Take(Mhc2HardwareCompiler.DefaultCounterexampleCount).Select(c =>
                    {
                        double p = Math.Max(0, F(c.PredictedDeltaE));
                        return new Mhc2Counterexample
                        {
                            Name = c.Name ?? "", R = Math.Clamp(F(c.R), 0, 1), G = Math.Clamp(F(c.G), 0, 1),
                            B = Math.Clamp(F(c.B), 0, 1), PredictedDeltaE = p,
                            EmpiricalUncertainty = Math.Max(0, F(c.EmpiricalUncertainty)),
                            EmpiricalUpperEstimate = Math.Max(p, F(c.EmpiricalUpperEstimate)),
                            MeasuredDeltaE = FN(c.MeasuredDeltaE),
                        };
                    }).ToList(),
                PhysicalObservations = (PhysicalObservations ?? new()).Where(IsFinite)
                    .Take(Mhc2HardwareCompiler.DefaultCounterexampleCount).Select(o => new Mhc2PhysicalObservation(
                        Math.Clamp(o.R, 0, 1), Math.Clamp(o.G, 0, 1), Math.Clamp(o.B, 0, 1),
                        F(o.X), F(o.Y), F(o.Z), Math.Max(0, F(o.Weight)))).ToList(),
                MeasuredCounterexampleCount = Math.Max(0, MeasuredCounterexampleCount),
                MeasuredWorstDeltaE = FN(MeasuredWorstDeltaE), EmpiricalEstimatesHeld = EmpiricalEstimatesHeld,
                Limitation = Limitation ?? "Numerical model certificate; physical measurements are authoritative.",
            };
        }

        private static bool IsFinite(Mhc2PhysicalObservation o) =>
            o != null && double.IsFinite(o.R) && double.IsFinite(o.G) && double.IsFinite(o.B) &&
            double.IsFinite(o.X) && double.IsFinite(o.Y) && double.IsFinite(o.Z) && double.IsFinite(o.Weight);
        private static bool SameRgb(LinearRgb rgb, double r, double g, double b) =>
            Math.Abs(rgb.R - r) <= 1e-6 && Math.Abs(rgb.G - g) <= 1e-6 && Math.Abs(rgb.B - b) <= 1e-6;
        internal static CieXyz TargetXyz(CalibrationTarget target, double r, double g, double b) =>
            target.LinearRgbToXyz(new LinearRgb(CalibrationVerifier.LinearizePatchSignal(target, r),
                CalibrationVerifier.LinearizePatchSignal(target, g), CalibrationVerifier.LinearizePatchSignal(target, b)));
        internal static CieXyz TargetLabWhite(CalibrationTarget target) =>
            target.WhitePoint.Equals(Chromaticity.D65) ? ColorMath.D65White : target.WhitePoint.ToXyz(1.0);
    }

    public sealed record Mhc2CompileResult(
        double[,] Matrix, double[] LutR, double[] LutG, double[] LutB, Mhc2ProofCertificate Certificate);

    /// <summary>
    /// Compiles an ideal 3D correction into three monotone single-index models
    /// h_c(m_c^T·EOTF(x)). Matrix rows are learned from the desired channel ordering first;
    /// PAVA then gives the exact least-squares monotone link for each projection. Every
    /// candidate is scored after the same s15Fixed16 quantization and 1024/256-entry LUT
    /// degradation that reaches Windows/driver hardware.
    /// </summary>
    public static class Mhc2HardwareCompiler
    {
        public const int DefaultCounterexampleCount = 6;
        private const int FitGridSize = 9;
        private const int ScoreGridSize = 9;
        private const int FitBins = 257;
        private const int NominalHardwareEntries = 1024;
        private const double ImprovementEpsilon = 1e-5;
        private const double MinimumCounterexampleSeparation = 0.18;
        private const double MissingResidualUncertaintyDeltaE = AdaptivePatchPlanner.ColorTargetDeltaE;

        private readonly record struct Sample(double R, double G, double B,
            double IdealR, double IdealG, double IdealB, double Weight);
        private readonly record struct RankPair(double D0, double D1, double D2, double Margin, double Weight);
        private readonly record struct ScoredPoint(double R, double G, double B, double DeltaE,
            CieLab MeasuredLab, CieLab TargetLab);
        private readonly record struct PhysicalResidual(double R, double G, double B,
            double X, double Y, double Z, double Weight);
        private readonly record struct PhysicalDriveCorrection(double R, double G, double B,
            double RDelta, double GDelta, double BDelta, double Weight);
        private sealed record Payload(double[,] Matrix, double[] R, double[] G, double[] B,
            Mhc2ErrorStatistics Stats, double Objective, double MatrixQuantization, double LutQuantization);

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
            bool optimizeMatrix = true,
            IReadOnlyList<Mhc2PhysicalObservation>? physicalObservations = null,
            int closedLoopRound = 0)
        {
            ValidateInputs(baselineMatrix, baselineLutR, baselineLutG, baselineLutB,
                idealCorrection, characterization, target);
            var observations = (physicalObservations ?? Array.Empty<Mhc2PhysicalObservation>())
                .Where(IsPhysicalObservation).ToList();
            var physicalResiduals = BuildPhysicalResiduals(baselineMatrix, baselineLutR, baselineLutG,
                baselineLutB, characterization, observations);

            var baseline = EvaluatePayload(CloneMatrix(baselineMatrix), (double[])baselineLutR.Clone(),
                (double[])baselineLutG.Clone(), (double[])baselineLutB.Clone(), characterization, target,
                baselineMatrix, physicalResiduals);
            var fitSamples = BuildFitSamples(idealCorrection).ToList();
            // The ideal LUT already corrects the characterized model. Only the *physical
            // model discrepancy* should bend it; replaying observations that agree with the
            // model as extra target constraints merely changes bin weights and can overfit
            // sparse grays without adding information.
            var discrepantObservations = observations.Zip(physicalResiduals, (observation, residual) =>
                    (observation, magnitude: Math.Sqrt(Square(residual.X) + Square(residual.Y) + Square(residual.Z))))
                .Where(v => v.magnitude > 2e-4).Select(v => v.observation).ToList();
            ApplyPhysicalCorrectionField(fitSamples, idealCorrection, discrepantObservations,
                baselineMatrix, baselineLutR, baselineLutG, baselineLutB, characterization, target);

            Payload best = baseline;
            var seedMatrices = new List<double[,]> { CloneMatrix(baselineMatrix) };
            if (optimizeMatrix)
            {
                // A ridge-regression direction is a strong scale-bearing initializer when
                // the ideal really is representable. Pairwise rank optimization then removes
                // dependence on that linear-link assumption and targets the invariant that a
                // monotone shaper actually needs: desired ordering.
                var regressionMatrix = SolveProjectionLeastSquares(baselineMatrix, fitSamples);
                var orderFromBaseline = OptimizeOrdering(baselineMatrix, baselineMatrix, fitSamples);
                var orderFromRegression = OptimizeOrdering(regressionMatrix, baselineMatrix, fitSamples);
                foreach (var solved in new[] { regressionMatrix, orderFromBaseline, orderFromRegression })
                foreach (double blend in new[] { 0.35, 0.65, 1.0 })
                {
                    var candidate = BlendMatrices(baselineMatrix, solved, blend);
                    if (IsSafeMatrix(candidate)) seedMatrices.Add(candidate);
                }
            }

            foreach (var seed in seedMatrices)
            {
                Consider(FitPayload(seed, fitSamples, baselineLutR, baselineLutG, baselineLutB,
                    characterization, target, baselineMatrix, physicalResiduals));
                foreach (var gauge in PrecisionGaugeVariants(seed))
                    Consider(FitPayload(gauge, fitSamples, baselineLutR, baselineLutG, baselineLutB,
                        characterization, target, baselineMatrix, physicalResiduals));
            }

            // Small joint polish after the global order solve. The LUTs are re-solved at every
            // coordinate, so this optimizes the actual matrix+three-link payload, not a proxy.
            if (optimizeMatrix && !ReferenceEquals(best, baseline))
            {
                foreach (double step in new[] { 0.008, 0.003 })
                for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                foreach (double sign in new[] { -1.0, 1.0 })
                {
                    var matrix = CloneMatrix(best.Matrix);
                    matrix[row, col] += sign * step;
                    if (!IsSafeMatrix(matrix)) continue;
                    Consider(FitPayload(matrix, fitSamples, baselineLutR, baselineLutG, baselineLutB,
                        characterization, target, baselineMatrix, physicalResiduals));
                }
            }

            bool optimizerApplied = !ReferenceEquals(best, baseline) &&
                                    best.Objective + ImprovementEpsilon < baseline.Objective;
            if (!optimizerApplied) best = baseline;

            var identity = IdentityLut(baselineLutR.Length);
            var native = EvaluatePayload(IdentityMatrix(), identity, identity, identity,
                characterization, target, baselineMatrix, Array.Empty<PhysicalResidual>());
            var validResiduals = (modelResiduals ?? Array.Empty<ModelResidual>())
                .Where(r => double.IsFinite(r.Magnitude) && r.Magnitude >= 0).Select(r => r.Magnitude).ToArray();
            double residualP95 = validResiduals.Length > 0
                ? Percentile(validResiduals, 0.95) : MissingResidualUncertaintyDeltaE;

            var hardware = HardwareVariant(best.Matrix, best.R, best.G, best.B,
                Math.Min(NominalHardwareEntries, best.R.Length));
            var continuous = Mhc2ContinuousVerifier.Verify((r, g, b) => EvaluatePoint(hardware.Matrix,
                hardware.R, hardware.G, hardware.B, characterization, target, r, g, b, physicalResiduals).DeltaE);
            var counterexamples = FindCounterexamples(continuous.Samples, measurements, residualP95,
                DefaultCounterexampleCount);

            var conflicts = new double[3];
            var rmses = new double[3];
            for (int channel = 0; channel < 3; channel++)
            {
                var pairs = BuildRankPairs(fitSamples, channel);
                conflicts[channel] = OrderConflict(best.Matrix, channel, pairs);
                rmses[channel] = IsotonicRmse(best.Matrix, channel, fitSamples,
                    channel == 0 ? best.R : channel == 1 ? best.G : best.B);
            }

            double nativeError = PerceptualObjective(native.Stats);
            double compiledError = PerceptualObjective(best.Stats);
            // Treat a system already inside the serialized precision floor as fully
            // correctable/converged; ratio arithmetic would otherwise call two identical
            // ~0.001 ΔE quantization floors "0% correctable".
            double correctability = nativeError <= 0.005 && compiledError <= 0.005
                ? 1.0
                : nativeError > 1e-9
                    ? Math.Clamp(1.0 - compiledError / nativeError, 0, 1)
                    : compiledError <= 1e-9 ? 1 : 0;
            var certificate = new Mhc2ProofCertificate
            {
                OptimizerApplied = optimizerApplied, ModelSampleCount = continuous.EvaluatedPointCount,
                Native = native.Stats, Baseline = baseline.Stats, Compiled = best.Stats,
                CorrectabilityFraction = correctability, EmpiricalModelResidualP95 = residualP95,
                EmpiricalUpperP95Estimate = best.Stats.P95DeltaE + residualP95,
                OrderConflictRed = conflicts[0], OrderConflictGreen = conflicts[1], OrderConflictBlue = conflicts[2],
                IsotonicIrreducibleRmse = Math.Sqrt(rmses.Select(v => v * v).Average()),
                MatrixQuantizationMaxAbs = best.MatrixQuantization, LutQuantizationMaxAbs = best.LutQuantization,
                HardwareLutEntries = Math.Min(NominalHardwareEntries, best.R.Length),
                PrecisionUtilizationRed = RowUtilization(best.Matrix, 0),
                PrecisionUtilizationGreen = RowUtilization(best.Matrix, 1),
                PrecisionUtilizationBlue = RowUtilization(best.Matrix, 2),
                ContinuousCellCount = continuous.VisitedCellCount,
                ContinuousMaxDepth = continuous.MaximumDepth,
                ContinuousSampledMaxDeltaE = continuous.SampledMaximumDeltaE,
                ContinuousEmpiricalEnvelopeDeltaE = continuous.EmpiricalEnvelopeDeltaE,
                ContinuousEnvelopeGapDeltaE = continuous.RemainingEnvelopeGapDeltaE,
                Counterexamples = counterexamples, ClosedLoopRound = Math.Max(0, closedLoopRound),
                ClosedLoopDecision = closedLoopRound > 0
                    ? optimizerApplied ? "candidate predicted safe improvement" : "baseline retained by model gates"
                    : null,
            };
            return new Mhc2CompileResult(CloneMatrix(best.Matrix), (double[])best.R.Clone(),
                (double[])best.G.Clone(), (double[])best.B.Clone(), certificate);

            void Consider(Payload candidate)
            {
                if (candidate.Objective + ImprovementEpsilon < best.Objective &&
                    PreservesCriticalStructure(candidate.Stats, baseline.Stats))
                    best = candidate;
            }
        }

        private static Payload FitPayload(double[,] matrix, IReadOnlyList<Sample> samples,
            double[] baselineR, double[] baselineG, double[] baselineB,
            DisplayCharacterization characterization, CalibrationTarget target,
            double[,] baselineMatrix, IReadOnlyList<PhysicalResidual> physicalResiduals)
        {
            var effectiveMatrix = SerializedEffectiveMatrix(matrix);
            var coordinates = samples.Select(s =>
                (Post: ApplyMatrix(effectiveMatrix, s.R, s.G, s.B), S: s)).ToList();
            var r = FitMonotoneLut(coordinates.Select(v => (v.Post.R, v.S.IdealR, v.S.Weight)), baselineR);
            var g = FitMonotoneLut(coordinates.Select(v => (v.Post.G, v.S.IdealG, v.S.Weight)), baselineG);
            var b = FitMonotoneLut(coordinates.Select(v => (v.Post.B, v.S.IdealB, v.S.Weight)), baselineB);
            // Optimized payloads are returned exactly as the ICC serializer will write them.
            double matrixQuantization = MatrixSerializationError(matrix);
            double lutQuantization = MaxQuantizationError(r, g, b);
            r = QuantizeLut(r); g = QuantizeLut(g); b = QuantizeLut(b);
            var evaluated = EvaluatePayload(matrix, r, g, b, characterization, target,
                baselineMatrix, physicalResiduals);
            return evaluated with
            {
                MatrixQuantization = matrixQuantization,
                LutQuantization = lutQuantization,
            };
        }

        private static Payload EvaluatePayload(double[,] matrix, double[] r, double[] g, double[] b,
            DisplayCharacterization characterization, CalibrationTarget target, double[,] baselineMatrix,
            IReadOnlyList<PhysicalResidual> physicalResiduals)
        {
            var primary = HardwareVariant(matrix, r, g, b, Math.Min(NominalHardwareEntries, r.Length));
            var scored = ScoreCube(primary.Matrix, primary.R, primary.G, primary.B,
                characterization, target, ScoreGridSize, physicalResiduals);
            var stats = Statistics(scored, primary.R, primary.G, primary.B, characterization, target,
                primary.Matrix, physicalResiduals);

            double worstVariantP95 = stats.P95DeltaE;
            foreach (int entries in new[] { Math.Min(256, r.Length) }.Distinct())
            {
                var degraded = HardwareVariant(matrix, r, g, b, entries);
                var errors = ScoreCube(degraded.Matrix, degraded.R, degraded.G, degraded.B,
                    characterization, target, ScoreGridSize, physicalResiduals).Select(p => p.DeltaE);
                worstVariantP95 = Math.Max(worstVariantP95, Percentile(errors, 0.95));
            }
            stats.HardwareWorstP95DeltaE = worstVariantP95;

            double matrixPenalty = 0;
            for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                matrixPenalty += Square(matrix[row, col] - baselineMatrix[row, col]);
            matrixPenalty = 0.008 * Math.Sqrt(matrixPenalty / 9.0);
            double structure = 0.035 * stats.MaxNeutralDeltaE + 0.020 * stats.GradientRmsDeltaE +
                               0.010 * stats.MaxAnchorDeltaE;
            double robust = 0.18 * Math.Max(0, worstVariantP95 - stats.P95DeltaE);
            double objective = PerceptualObjective(stats) + structure + robust + matrixPenalty;
            return new Payload(matrix, r, g, b, stats, objective,
                MatrixSerializationError(matrix), MaxQuantizationError(r, g, b));
        }

        private static Mhc2ErrorStatistics Statistics(IReadOnlyList<ScoredPoint> scored,
            double[] r, double[] g, double[] b, DisplayCharacterization c, CalibrationTarget target,
            double[,] matrix, IReadOnlyList<PhysicalResidual> residuals)
        {
            var errors = scored.Select(p => p.DeltaE).OrderBy(v => v).ToArray();
            var neutral = new List<(double DeltaE, double Chroma)>();
            for (int i = 0; i <= 32; i++)
            {
                double x = i / 32.0;
                var p = EvaluatePoint(matrix, r, g, b, c, target, x, x, x, residuals);
                neutral.Add((p.DeltaE, Math.Sqrt(Square(p.MeasuredLab.A) + Square(p.MeasuredLab.B))));
            }
            double gradientSum = 0, gradientMax = 0;
            int gradientCount = 0;
            int n = ScoreGridSize;
            var byIndex = scored.ToArray();
            int Index(int ri, int gi, int bi) => (ri * n + gi) * n + bi;
            for (int ri = 0; ri < n; ri++)
            for (int gi = 0; gi < n; gi++)
            for (int bi = 0; bi < n; bi++)
            {
                var p = byIndex[Index(ri, gi, bi)];
                if (ri + 1 < n) Accumulate(p, byIndex[Index(ri + 1, gi, bi)]);
                if (gi + 1 < n) Accumulate(p, byIndex[Index(ri, gi + 1, bi)]);
                if (bi + 1 < n) Accumulate(p, byIndex[Index(ri, gi, bi + 1)]);
            }
            var anchors = scored.Where(p => IsEndpoint(p.R) && IsEndpoint(p.G) && IsEndpoint(p.B))
                .Select(p => p.DeltaE).DefaultIfEmpty(0);
            int plateau = new[] { r, g, b }.Sum(lut => lut.Zip(lut.Skip(1), (a, z) => z - a)
                .Count(d => d <= 0.5 / 65536.0));
            double maxSlope = new[] { r, g, b }.Max(lut => lut.Zip(lut.Skip(1), (a, z) =>
                (z - a) * (lut.Length - 1)).DefaultIfEmpty(0).Max());
            return new Mhc2ErrorStatistics
            {
                AverageDeltaE = errors.Average(), P95DeltaE = Percentile(errors, 0.95), MaxDeltaE = errors[^1],
                MaxNeutralDeltaE = neutral.Max(v => v.DeltaE), MaxNeutralChroma = neutral.Max(v => v.Chroma),
                MaxAnchorDeltaE = anchors.Max(),
                GradientRmsDeltaE = gradientCount > 0 ? Math.Sqrt(gradientSum / gradientCount) : 0,
                MaxGradientDeltaE = gradientMax, LutPlateauCount = plateau, MaxLutSlope = maxSlope,
            };

            void Accumulate(ScoredPoint p, ScoredPoint q)
            {
                // Jacobian fidelity is the change in the Lab residual vector across an edge;
                // it catches banding/kinks without penalizing a naturally steep target gamut.
                double dl = (q.MeasuredLab.L - q.TargetLab.L) - (p.MeasuredLab.L - p.TargetLab.L);
                double da = (q.MeasuredLab.A - q.TargetLab.A) - (p.MeasuredLab.A - p.TargetLab.A);
                double db = (q.MeasuredLab.B - q.TargetLab.B) - (p.MeasuredLab.B - p.TargetLab.B);
                double value = Math.Sqrt(dl * dl + da * da + db * db);
                gradientSum += value * value; gradientMax = Math.Max(gradientMax, value); gradientCount++;
            }
        }

        private static bool PreservesCriticalStructure(Mhc2ErrorStatistics candidate, Mhc2ErrorStatistics baseline) =>
            candidate.MaxNeutralDeltaE <= baseline.MaxNeutralDeltaE + 0.08 &&
            // Lab chroma below 0.5 is well inside one JND and smaller than real meter
            // repeatability on dark grays. Preserve ΔE strictly, but do not reject a large
            // global improvement for a 0.1–0.4 numerical a*/b* residue around exact zero.
            candidate.MaxNeutralChroma <= Math.Max(0.50, baseline.MaxNeutralChroma + 0.20) &&
            candidate.MaxAnchorDeltaE <= baseline.MaxAnchorDeltaE + 0.10 &&
            candidate.MaxGradientDeltaE <= baseline.MaxGradientDeltaE * 1.05 + 0.08 &&
            candidate.HardwareWorstP95DeltaE <= baseline.HardwareWorstP95DeltaE + 0.03;

        private static double PerceptualObjective(Mhc2ErrorStatistics s) =>
            0.20 * s.AverageDeltaE + 0.48 * s.P95DeltaE + 0.32 * s.MaxDeltaE;

        private static List<ScoredPoint> ScoreCube(double[,] matrix, double[] r, double[] g, double[] b,
            DisplayCharacterization c, CalibrationTarget target, int gridSize,
            IReadOnlyList<PhysicalResidual> residuals)
        {
            var result = new List<ScoredPoint>(gridSize * gridSize * gridSize);
            for (int ri = 0; ri < gridSize; ri++)
            for (int gi = 0; gi < gridSize; gi++)
            for (int bi = 0; bi < gridSize; bi++)
                result.Add(EvaluatePoint(matrix, r, g, b, c, target,
                    ri / (double)(gridSize - 1), gi / (double)(gridSize - 1),
                    bi / (double)(gridSize - 1), residuals));
            return result;
        }

        private static ScoredPoint EvaluatePoint(double[,] matrix, double[] r, double[] g, double[] b,
            DisplayCharacterization c, CalibrationTarget target, double inputR, double inputG, double inputB,
            IReadOnlyList<PhysicalResidual> residuals)
        {
            var post = ApplyMatrix(matrix, inputR, inputG, inputB);
            var xyz = DisplayXyz(c, Lookup(r, post.R), Lookup(g, post.G), Lookup(b, post.B));
            xyz = AddPhysicalResidual(xyz, inputR, inputG, inputB, residuals);
            var targetXyz = Mhc2ProofCertificate.TargetXyz(target, inputR, inputG, inputB);
            var white = Mhc2ProofCertificate.TargetLabWhite(target);
            var measuredLab = ColorMath.XyzToLab(xyz, white);
            var targetLab = ColorMath.XyzToLab(targetXyz, white);
            double deltaE = measuredLab.DeltaE2000(targetLab);
            return new ScoredPoint(inputR, inputG, inputB,
                double.IsFinite(deltaE) ? deltaE : 1_000, measuredLab, targetLab);
        }

        private static IReadOnlyList<Sample> BuildFitSamples(Lut3D ideal)
        {
            var samples = new List<Sample>(FitGridSize * FitGridSize * FitGridSize);
            for (int ri = 0; ri < FitGridSize; ri++)
            for (int gi = 0; gi < FitGridSize; gi++)
            for (int bi = 0; bi < FitGridSize; bi++)
            {
                double r = ri / (double)(FitGridSize - 1), g = gi / (double)(FitGridSize - 1),
                       b = bi / (double)(FitGridSize - 1);
                var desired = ideal.LookupTetrahedral((float)r, (float)g, (float)b);
                bool neutral = ri == gi && gi == bi;
                bool anchor = (ri == 0 || ri == FitGridSize - 1) && (gi == 0 || gi == FitGridSize - 1) &&
                              (bi == 0 || bi == FitGridSize - 1);
                double weight = neutral ? 6 : anchor ? 3 : 1;
                samples.Add(new Sample(r, g, b, Clamp01(desired.R), Clamp01(desired.G),
                    Clamp01(desired.B), weight));
            }
            return samples;
        }

        private static double[,] SolveProjectionLeastSquares(double[,] baseline, IReadOnlyList<Sample> samples)
        {
            var result = CloneMatrix(baseline);
            const double ridge = 0.002;
            for (int channel = 0; channel < 3; channel++)
            {
                var xx = new double[3, 3];
                var xy = new double[3];
                foreach (var s in samples)
                {
                    double[] u = { ColorMath.SrgbEotf(s.R), ColorMath.SrgbEotf(s.G), ColorMath.SrgbEotf(s.B) };
                    double desired = channel == 0 ? s.IdealR : channel == 1 ? s.IdealG : s.IdealB;
                    double y = ColorMath.SrgbEotf(desired);
                    for (int i = 0; i < 3; i++)
                    {
                        xy[i] += s.Weight * u[i] * y;
                        for (int j = 0; j < 3; j++) xx[i, j] += s.Weight * u[i] * u[j];
                    }
                }
                for (int i = 0; i < 3; i++)
                {
                    xx[i, i] += ridge;
                    xy[i] += ridge * baseline[channel, i];
                }
                try
                {
                    var inverse = ColorMath.Invert3x3(xx);
                    for (int i = 0; i < 3; i++)
                        result[channel, i] = inverse[i, 0] * xy[0] + inverse[i, 1] * xy[1] + inverse[i, 2] * xy[2];
                }
                catch
                {
                    for (int i = 0; i < 3; i++) result[channel, i] = baseline[channel, i];
                }
            }
            return result;
        }

        private static double[,] OptimizeOrdering(double[,] initial, double[,] baseline,
            IReadOnlyList<Sample> samples)
        {
            var result = CloneMatrix(initial);
            for (int channel = 0; channel < 3; channel++)
            {
                var row = new[] { initial[channel, 0], initial[channel, 1], initial[channel, 2] };
                double baseNorm = Math.Sqrt(row.Sum(Square));
                var pairs = BuildRankPairs(samples, channel);
                if (pairs.Count == 0) continue;
                for (int iteration = 0; iteration < 140; iteration++)
                {
                    var gradient = new double[3];
                    double totalWeight = 0;
                    foreach (var p in pairs)
                    {
                        double projection = row[0] * p.D0 + row[1] * p.D1 + row[2] * p.D2;
                        double violation = p.Margin - projection;
                        if (violation <= 0) continue;
                        double norm2 = p.D0 * p.D0 + p.D1 * p.D1 + p.D2 * p.D2 + 1e-8;
                        double scale = -2 * p.Weight * violation / norm2;
                        gradient[0] += scale * p.D0; gradient[1] += scale * p.D1; gradient[2] += scale * p.D2;
                        totalWeight += p.Weight;
                    }
                    double regularization = 0.015;
                    for (int k = 0; k < 3; k++)
                        gradient[k] = gradient[k] / Math.Max(1, totalWeight) +
                                      regularization * (row[k] - baseline[channel, k]);
                    double step = 0.24 / Math.Sqrt(iteration + 4.0);
                    for (int k = 0; k < 3; k++) row[k] = Math.Clamp(row[k] - step * gradient[k], -0.25, 1.3);
                }
                double norm = Math.Sqrt(row.Sum(Square));
                if (norm > 1e-9 && baseNorm > 1e-9)
                    for (int k = 0; k < 3; k++) row[k] *= baseNorm / norm; // fix positive scale gauge
                for (int k = 0; k < 3; k++) result[channel, k] = row[k];
            }
            return result;
        }

        private static List<RankPair> BuildRankPairs(IReadOnlyList<Sample> samples, int channel)
        {
            double Desired(Sample s) => channel == 0 ? s.IdealR : channel == 1 ? s.IdealG : s.IdealB;
            var sorted = samples.OrderBy(Desired).ThenBy(s => s.R).ThenBy(s => s.G).ThenBy(s => s.B).ToList();
            var result = new List<RankPair>();
            int[] strides = { 1, 3, 9, 27, 81 };
            for (int i = 0; i < sorted.Count; i++)
            foreach (int stride in strides)
            {
                int j = i + stride;
                if (j >= sorted.Count) continue;
                double dy = Desired(sorted[j]) - Desired(sorted[i]);
                if (dy < 0.004) continue;
                double d0 = ColorMath.SrgbEotf(sorted[j].R) - ColorMath.SrgbEotf(sorted[i].R);
                double d1 = ColorMath.SrgbEotf(sorted[j].G) - ColorMath.SrgbEotf(sorted[i].G);
                double d2 = ColorMath.SrgbEotf(sorted[j].B) - ColorMath.SrgbEotf(sorted[i].B);
                double norm = Math.Sqrt(d0 * d0 + d1 * d1 + d2 * d2);
                if (norm < 1e-8) continue;
                result.Add(new RankPair(d0, d1, d2, 0.012 * dy * norm,
                    Math.Sqrt(sorted[i].Weight * sorted[j].Weight) * Math.Sqrt(dy)));
            }
            return result;
        }

        private static double OrderConflict(double[,] matrix, int channel, IReadOnlyList<RankPair> pairs)
        {
            double bad = 0, total = 0;
            foreach (var p in pairs)
            {
                total += p.Weight;
                double projection = matrix[channel, 0] * p.D0 + matrix[channel, 1] * p.D1 + matrix[channel, 2] * p.D2;
                if (projection <= 0) bad += p.Weight;
            }
            return total > 0 ? bad / total : 0;
        }

        private static double IsotonicRmse(double[,] matrix, int channel, IReadOnlyList<Sample> samples, double[] lut)
        {
            double sum = 0, weight = 0;
            foreach (var s in samples)
            {
                var p = ApplyMatrix(matrix, s.R, s.G, s.B);
                double x = channel == 0 ? p.R : channel == 1 ? p.G : p.B;
                double desired = channel == 0 ? s.IdealR : channel == 1 ? s.IdealG : s.IdealB;
                sum += s.Weight * Square(Lookup(lut, x) - desired); weight += s.Weight;
            }
            return weight > 0 ? Math.Sqrt(sum / weight) : 0;
        }

        private static IEnumerable<double[,]> PrecisionGaugeVariants(double[,] matrix)
        {
            var yielded = new HashSet<string>();
            foreach (double occupancy in new[] { 0.82, 0.92, 0.985 })
            {
                var candidate = CloneMatrix(matrix);
                for (int row = 0; row < 3; row++)
                {
                    double utilization = RowUtilization(candidate, row);
                    if (utilization > 1e-8)
                    {
                        double scale = Math.Clamp(occupancy / utilization, 0.72, 1.28);
                        for (int col = 0; col < 3; col++) candidate[row, col] *= scale;
                    }
                }
                if (!IsSafeMatrix(candidate)) continue;
                string key = string.Join(",", candidate.Cast<double>().Select(v => v.ToString("F6")));
                if (yielded.Add(key)) yield return candidate;
            }
        }

        private static double[] FitMonotoneLut(IEnumerable<(double X, double Y, double Weight)> pairs, double[] baseline)
        {
            var sums = new double[FitBins]; var weights = new double[FitBins];
            foreach (var (xRaw, yRaw, wRaw) in pairs)
            {
                int bin = (int)Math.Round(Clamp01(xRaw) * (FitBins - 1));
                double w = double.IsFinite(wRaw) && wRaw > 0 ? wRaw : 1;
                sums[bin] += w * Clamp01(yRaw); weights[bin] += w;
            }
            for (int i = 0; i < FitBins; i++)
            {
                double x = i / (double)(FitBins - 1);
                double prior = i == 0 || i == FitBins - 1 ? 4 : 0.10;
                sums[i] += prior * Lookup(baseline, x); weights[i] += prior;
            }
            var value = new double[FitBins]; var blockWeight = new double[FitBins];
            var blockStart = new int[FitBins]; int blocks = 0;
            for (int i = 0; i < FitBins; i++)
            {
                value[blocks] = sums[i] / weights[i]; blockWeight[blocks] = weights[i];
                blockStart[blocks] = i; blocks++;
                while (blocks >= 2 && value[blocks - 2] > value[blocks - 1])
                {
                    double w = blockWeight[blocks - 2] + blockWeight[blocks - 1];
                    value[blocks - 2] = (value[blocks - 2] * blockWeight[blocks - 2] +
                                         value[blocks - 1] * blockWeight[blocks - 1]) / w;
                    blockWeight[blocks - 2] = w; blocks--;
                }
            }
            var bins = new double[FitBins];
            for (int block = 0; block < blocks; block++)
            {
                int end = block + 1 < blocks ? blockStart[block + 1] : FitBins;
                for (int i = blockStart[block]; i < end; i++) bins[i] = Clamp01(value[block]);
            }
            var result = new double[baseline.Length];
            for (int i = 0; i < result.Length; i++) result[i] = Lookup(bins, i / (double)(result.Length - 1));
            for (int i = 1; i < result.Length; i++) result[i] = Math.Max(result[i], result[i - 1]);
            return result;
        }

        private static void ApplyPhysicalCorrectionField(List<Sample> samples, Lut3D ideal,
            IReadOnlyList<Mhc2PhysicalObservation> observations, double[,] matrix,
            double[] r, double[] g, double[] b, DisplayCharacterization c, CalibrationTarget target)
        {
            if (observations.Count == 0) return;
            double[,] inverse;
            try { inverse = ColorMath.Invert3x3(c.RgbToXyzMatrix); }
            catch { return; }
            var corrections = new List<PhysicalDriveCorrection>();
            foreach (var o in observations)
            {
                var post = ApplyMatrix(matrix, o.R, o.G, o.B);
                double dr = Lookup(r, post.R), dg = Lookup(g, post.G), db = Lookup(b, post.B);
                var targetXyz = Mhc2ProofCertificate.TargetXyz(target, o.R, o.G, o.B);
                double dx = targetXyz.X - o.X, dy = targetXyz.Y - o.Y, dz = targetXyz.Z - o.Z;
                double cr = inverse[0, 0] * dx + inverse[0, 1] * dy + inverse[0, 2] * dz;
                double cg = inverse[1, 0] * dx + inverse[1, 1] * dy + inverse[1, 2] * dz;
                double cb = inverse[2, 0] * dx + inverse[2, 1] * dy + inverse[2, 2] * dz;
                double idealR = c.RedToneCurve.InverseLookup(Clamp01(c.RedToneCurve.Lookup(dr) + cr));
                double idealG = c.GreenToneCurve.InverseLookup(Clamp01(c.GreenToneCurve.Lookup(dg) + cg));
                double idealB = c.BlueToneCurve.InverseLookup(Clamp01(c.BlueToneCurve.Lookup(db) + cb));
                var prior = ideal.LookupTetrahedral((float)o.R, (float)o.G, (float)o.B);
                corrections.Add(new PhysicalDriveCorrection(o.R, o.G, o.B,
                    idealR - prior.R, idealG - prior.G, idealB - prior.B,
                    Math.Max(0.25, o.Weight)));
            }

            // Kernel-regress only the *increment* over the dense ideal. This is the smooth
            // representer form of the physical correction field: it honors observations at
            // their locations, fades toward the prior in unsupported regions, and avoids the
            // bin spikes caused by inserting a few high-weight absolute samples into PAVA.
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                double sr = 0, sg = 0, sb = 0, sw = 0;
                foreach (var correction in corrections)
                {
                    double d2 = Square(s.R - correction.R) + Square(s.G - correction.G) +
                                Square(s.B - correction.B);
                    double w = correction.Weight * Math.Exp(-d2 / 0.10);
                    sr += w * correction.RDelta; sg += w * correction.GDelta;
                    sb += w * correction.BDelta; sw += w;
                }
                if (sw <= 1e-12) continue;
                double confidence = sw / (sw + 0.18);
                samples[i] = s with
                {
                    IdealR = Clamp01(s.IdealR + confidence * sr / sw),
                    IdealG = Clamp01(s.IdealG + confidence * sg / sw),
                    IdealB = Clamp01(s.IdealB + confidence * sb / sw),
                };
            }
        }

        private static List<PhysicalResidual> BuildPhysicalResiduals(double[,] matrix, double[] r, double[] g,
            double[] b, DisplayCharacterization c, IReadOnlyList<Mhc2PhysicalObservation> observations)
        {
            var result = new List<PhysicalResidual>();
            foreach (var o in observations)
            {
                var post = ApplyMatrix(matrix, o.R, o.G, o.B);
                var predicted = DisplayXyz(c, Lookup(r, post.R), Lookup(g, post.G), Lookup(b, post.B));
                result.Add(new PhysicalResidual(o.R, o.G, o.B, o.X - predicted.X, o.Y - predicted.Y,
                    o.Z - predicted.Z, Math.Max(0.25, o.Weight)));
            }
            return result;
        }

        private static CieXyz AddPhysicalResidual(CieXyz xyz, double r, double g, double b,
            IReadOnlyList<PhysicalResidual> residuals)
        {
            if (residuals.Count == 0) return xyz;
            double sx = 0, sy = 0, sz = 0, sw = 0;
            foreach (var o in residuals)
            {
                double d2 = Square(r - o.R) + Square(g - o.G) + Square(b - o.B);
                if (d2 < 1e-12) return new CieXyz(Math.Max(0, xyz.X + o.X), Math.Max(0, xyz.Y + o.Y),
                    Math.Max(0, xyz.Z + o.Z));
                double w = o.Weight * Math.Exp(-d2 / 0.075);
                sx += w * o.X; sy += w * o.Y; sz += w * o.Z; sw += w;
            }
            // Kernel confidence shrinks unsupported extrapolation back to the characterized model.
            double confidence = sw / (sw + 0.08);
            return sw > 0 ? new CieXyz(Math.Max(0, xyz.X + confidence * sx / sw),
                Math.Max(0, xyz.Y + confidence * sy / sw), Math.Max(0, xyz.Z + confidence * sz / sw)) : xyz;
        }

        private static List<Mhc2Counterexample> FindCounterexamples(IReadOnlyList<Mhc2ContinuousSample> samples,
            IReadOnlyList<MeasurementResult>? measurements, double residualP95, int count)
        {
            var measured = (measurements ?? Array.Empty<MeasurementResult>()).Where(m => m.IsValid && m.Patch.Nits is null)
                .Select(m => m.Patch.DisplayRgb).ToList();
            var ranked = samples.Select(point =>
            {
                double gap = measured.Count == 0 ? 1 : measured.Min(m =>
                    Distance(point.R, point.G, point.B, m.R, m.G, m.B));
                double uncertainty = residualP95 * (1 + Math.Min(1.0, gap / 0.35));
                return (point, uncertainty, upper: point.DeltaE + uncertainty);
            }).OrderByDescending(v => v.upper).ThenByDescending(v => v.point.DeltaE).ToList();
            var selected = new List<(Mhc2ContinuousSample point, double uncertainty, double upper)>();
            foreach (var candidate in ranked)
            {
                if (selected.Any(s => Distance(candidate.point.R, candidate.point.G, candidate.point.B,
                    s.point.R, s.point.G, s.point.B) < MinimumCounterexampleSeparation)) continue;
                selected.Add(candidate); if (selected.Count == count) break;
            }
            return selected.Select((v, i) => new Mhc2Counterexample
            {
                Name = $"Proof CE {i + 1} ({v.point.R:F3}, {v.point.G:F3}, {v.point.B:F3})",
                R = v.point.R, G = v.point.G, B = v.point.B, PredictedDeltaE = v.point.DeltaE,
                EmpiricalUncertainty = v.uncertainty, EmpiricalUpperEstimate = v.upper,
            }).ToList();
        }

        private static (double[,] Matrix, double[] R, double[] G, double[] B) HardwareVariant(
            double[,] matrix, double[] r, double[] g, double[] b, int entries) =>
            (SerializedEffectiveMatrix(matrix), QuantizeLut(Resample(r, entries)), QuantizeLut(Resample(g, entries)),
                QuantizeLut(Resample(b, entries)));

        private static double[] Resample(double[] source, int length)
        {
            if (source.Length == length) return (double[])source.Clone();
            var result = new double[length];
            for (int i = 0; i < length; i++) result[i] = Lookup(source, i / (double)(length - 1));
            return result;
        }
        private static double[,] QuantizeMatrix(double[,] matrix)
        {
            var result = CloneMatrix(matrix);
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) result[i, j] = Quantize(result[i, j]);
            return result;
        }
        private static double[,] SerializedEffectiveMatrix(double[,] conceptualMatrix)
        {
            // The file does not contain the conceptual RGB matrix. It contains
            // S·M·S⁻¹ in the undocumented MHC2 XYZ sandwich, and *that* matrix is s15Fixed16.
            // Quantize in the serialized domain, then undo the engine sandwich to obtain the
            // exact effective RGB transform used by the objective.
            var srgbToXyz = ColorMath.CalculateRgbToXyzMatrix(
                Chromaticity.Rec709Red, Chromaticity.Rec709Green,
                Chromaticity.Rec709Blue, Chromaticity.D65);
            var tag = Mhc2ProfileBuilder.ToMhc2MatrixDomain(conceptualMatrix);
            var quantizedTag = QuantizeMatrix(tag);
            return ColorMath.MultiplyMatrices(ColorMath.Invert3x3(srgbToXyz),
                ColorMath.MultiplyMatrices(quantizedTag, srgbToXyz));
        }
        private static double MatrixSerializationError(double[,] conceptualMatrix)
        {
            var tag = Mhc2ProfileBuilder.ToMhc2MatrixDomain(conceptualMatrix);
            return MaxQuantizationError(tag);
        }
        private static double[] QuantizeLut(double[] lut) => lut.Select(v => Clamp01(Quantize(v))).ToArray();
        private static double Quantize(double value) => Math.Round(value * 65536.0) / 65536.0;
        private static double MaxQuantizationError(double[,] matrix) => matrix.Cast<double>()
            .Select(v => Math.Abs(v - Quantize(v))).DefaultIfEmpty(0).Max();
        private static double MaxQuantizationError(params double[][] luts) => luts.SelectMany(v => v)
            .Select(v => Math.Abs(v - Quantize(v))).DefaultIfEmpty(0).Max();

        private static (double R, double G, double B) ApplyMatrix(double[,] matrix, double r, double g, double b)
        {
            double lr = ColorMath.SrgbEotf(Clamp01(r)), lg = ColorMath.SrgbEotf(Clamp01(g)),
                   lb = ColorMath.SrgbEotf(Clamp01(b));
            double mr = matrix[0, 0] * lr + matrix[0, 1] * lg + matrix[0, 2] * lb;
            double mg = matrix[1, 0] * lr + matrix[1, 1] * lg + matrix[1, 2] * lb;
            double mb = matrix[2, 0] * lr + matrix[2, 1] * lg + matrix[2, 2] * lb;
            return (ColorMath.SrgbOetf(Clamp01(mr)), ColorMath.SrgbOetf(Clamp01(mg)), ColorMath.SrgbOetf(Clamp01(mb)));
        }
        private static CieXyz DisplayXyz(DisplayCharacterization c, double r, double g, double b)
        {
            double lr = c.RedToneCurve.Lookup(r), lg = c.GreenToneCurve.Lookup(g), lb = c.BlueToneCurve.Lookup(b);
            var m = c.RgbToXyzMatrix;
            return new CieXyz(m[0, 0] * lr + m[0, 1] * lg + m[0, 2] * lb,
                m[1, 0] * lr + m[1, 1] * lg + m[1, 2] * lb,
                m[2, 0] * lr + m[2, 1] * lg + m[2, 2] * lb);
        }

        private static bool IsSafeMatrix(double[,] matrix)
        {
            for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++)
                if (!double.IsFinite(matrix[row, col]) || matrix[row, col] < -0.25 || matrix[row, col] > 1.3) return false;
            for (int mask = 0; mask < 8; mask++)
            for (int row = 0; row < 3; row++)
            {
                double value = matrix[row, 0] * ((mask & 1) != 0 ? 1 : 0) +
                               matrix[row, 1] * ((mask & 2) != 0 ? 1 : 0) +
                               matrix[row, 2] * ((mask & 4) != 0 ? 1 : 0);
                if (value < GamutReachability.MinReachablePrimaryDrive || value > 1.000001) return false;
            }
            return true;
        }
        private static double RowUtilization(double[,] matrix, int row) =>
            Math.Max(0, matrix[row, 0]) + Math.Max(0, matrix[row, 1]) + Math.Max(0, matrix[row, 2]);
        private static double[,] BlendMatrices(double[,] a, double[,] b, double t)
        {
            var result = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) result[i, j] = a[i, j] + t * (b[i, j] - a[i, j]);
            return result;
        }

        private static void ValidateInputs(double[,] matrix, double[] r, double[] g, double[] b, Lut3D ideal,
            DisplayCharacterization characterization, CalibrationTarget target)
        {
            ArgumentNullException.ThrowIfNull(matrix); ArgumentNullException.ThrowIfNull(r);
            ArgumentNullException.ThrowIfNull(g); ArgumentNullException.ThrowIfNull(b);
            ArgumentNullException.ThrowIfNull(ideal); ArgumentNullException.ThrowIfNull(characterization);
            ArgumentNullException.ThrowIfNull(target);
            if (matrix.GetLength(0) != 3 || matrix.GetLength(1) != 3)
                throw new ArgumentException("MHC2 matrix must be exactly 3x3.", nameof(matrix));
            if (r.Length < 2 || g.Length != r.Length || b.Length != r.Length)
                throw new ArgumentException("MHC2 tone LUTs must have equal lengths of at least two entries.");
            foreach (double value in matrix) if (!double.IsFinite(value))
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

        private static bool IsPhysicalObservation(Mhc2PhysicalObservation o) => o != null &&
            double.IsFinite(o.R) && o.R >= 0 && o.R <= 1 && double.IsFinite(o.G) && o.G >= 0 && o.G <= 1 &&
            double.IsFinite(o.B) && o.B >= 0 && o.B <= 1 && double.IsFinite(o.X) && o.X >= 0 &&
            double.IsFinite(o.Y) && o.Y >= 0 && double.IsFinite(o.Z) && o.Z >= 0 &&
            double.IsFinite(o.Weight) && o.Weight > 0;
        private static double Lookup(double[] lut, double input)
        {
            double position = Clamp01(input) * (lut.Length - 1); int lower = (int)position;
            int upper = Math.Min(lower + 1, lut.Length - 1); double fraction = position - lower;
            return lut[lower] + fraction * (lut[upper] - lut[lower]);
        }
        private static double Percentile(IEnumerable<double> values, double percentile)
        {
            var sorted = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return 0;
            double position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
            int lower = (int)position, upper = Math.Min(lower + 1, sorted.Length - 1);
            return sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
        }
        private static double Distance(double ar, double ag, double ab, double br, double bg, double bb) =>
            Math.Sqrt(Square(ar - br) + Square(ag - bg) + Square(ab - bb));
        private static bool IsEndpoint(double value) => value <= 1e-12 || value >= 1 - 1e-12;
        private static double[,] CloneMatrix(double[,] matrix) => (double[,])matrix.Clone();
        private static double[,] IdentityMatrix() => new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        private static double[] IdentityLut(int length) => Enumerable.Range(0, length)
            .Select(i => i / (double)(length - 1)).ToArray();
        private static double Square(double value) => value * value;
        private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
    }
}
