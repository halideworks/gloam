using System;
using System.Collections.Generic;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class Mhc2HardwareCompilerTests
    {
        [Fact]
        public void Compile_IdentitySystem_RetainsPerfectBaselineAndMonotonePayload()
        {
            var target = StandardTargets.SrgbGamma22;
            var characterization = MatchingCharacterization(target, 2.2);
            var identityLut = IdentityLut(256);

            var result = Mhc2HardwareCompiler.Compile(
                IdentityMatrix(), identityLut, identityLut, identityLut,
                new Lut3D(17), characterization, target,
                optimizeMatrix: false);

            Assert.False(result.Certificate.OptimizerApplied);
            // The v2 objective simulates the s15Fixed16 values Windows actually receives,
            // exposing the tiny but real quantization floor instead of scoring doubles.
            Assert.InRange(result.Certificate.Compiled.MaxDeltaE, 0, 0.002);
            Assert.Equal(1, result.Certificate.CorrectabilityFraction, 8);
            AssertMonotone(result.LutR);
            AssertMonotone(result.LutG);
            AssertMonotone(result.LutB);
            Assert.Equal(identityLut, result.LutR);
            Assert.Equal(2, result.Certificate.SchemaVersion);
            Assert.True(result.Certificate.ModelSampleCount > 100);
            Assert.True(result.Certificate.ContinuousCellCount > 1);
            Assert.True(result.Certificate.MatrixQuantizationMaxAbs <= 0.5 / 65536.0 + 1e-12);
        }

        [Fact]
        public void Compile_SeparableGammaMismatch_ImprovesHardwarePayload()
        {
            var target = StandardTargets.SrgbGamma22;
            var characterization = MatchingCharacterization(target, 2.4);
            var identityLut = IdentityLut(512);
            var ideal = SeparableGammaCorrection(size: 17, sourceGamma: 2.2, displayGamma: 2.4);

            var result = Mhc2HardwareCompiler.Compile(
                IdentityMatrix(), identityLut, identityLut, identityLut,
                ideal, characterization, target,
                optimizeMatrix: false);

            Assert.True(result.Certificate.OptimizerApplied);
            Assert.True(result.Certificate.Compiled.AverageDeltaE < result.Certificate.Baseline.AverageDeltaE);
            Assert.True(result.Certificate.Compiled.P95DeltaE < result.Certificate.Baseline.P95DeltaE);
            AssertMonotone(result.LutR);
            Assert.Equal(result.LutR, result.LutG);
            Assert.Equal(result.LutR, result.LutB);
        }

        [Fact]
        public void Compile_NonseparableIdeal_NeverRegressesKnownGoodBaseline()
        {
            var target = StandardTargets.SrgbGamma22;
            var characterization = MatchingCharacterization(target, 2.2);
            var identityLut = IdentityLut(256);
            var ideal = new Lut3D(9);
            for (int r = 0; r < ideal.Size; r++)
            for (int g = 0; g < ideal.Size; g++)
            for (int b = 0; b < ideal.Size; b++)
            {
                float rf = r / (float)(ideal.Size - 1);
                float gf = g / (float)(ideal.Size - 1);
                float bf = b / (float)(ideal.Size - 1);
                // Deliberately impossible for independent 1D LUTs: green changes with red.
                float outG = Math.Clamp(gf + 0.12f * rf * (1 - gf), 0, 1);
                ideal.SetEntry(r, g, b, rf, outG, bf);
            }

            var result = Mhc2HardwareCompiler.Compile(
                IdentityMatrix(), identityLut, identityLut, identityLut,
                ideal, characterization, target);

            Assert.False(result.Certificate.OptimizerApplied);
            Assert.Equal(result.Certificate.Baseline.AverageDeltaE,
                result.Certificate.Compiled.AverageDeltaE, 10);
            Assert.Equal(identityLut, result.LutG);
        }

        [Fact]
        public void Compile_RepresentableCrossChannelRidge_LearnsOrderingMatrix()
        {
            var target = StandardTargets.SrgbPiecewise;
            var characterization = MatchingCharacterization(target, 2.2);
            var srgbTone = ToneCurve.CreateFromArray(Enumerable.Range(0, ToneCurve.LutSize)
                .Select(i => ColorMath.SrgbEotf(i / (double)(ToneCurve.LutSize - 1))).ToArray());
            characterization.RedToneCurve = srgbTone;
            characterization.GreenToneCurve = srgbTone;
            characterization.BlueToneCurve = srgbTone;
            characterization.NeutralToneCurve = srgbTone;
            var ridge = new double[,]
            {
                { 0.90, 0.08, 0.00 },
                { 0.05, 0.92, 0.01 },
                { 0.00, 0.04, 0.94 },
            };
            // If display XYZ is T·A⁻¹ and hardware drives A·x, the net is the target T·x.
            characterization.RgbToXyzMatrix = ColorMath.MultiplyMatrices(
                target.RgbToXyzMatrix, ColorMath.Invert3x3(ridge));
            var ideal = new Lut3D(17);
            for (int ri = 0; ri < ideal.Size; ri++)
            for (int gi = 0; gi < ideal.Size; gi++)
            for (int bi = 0; bi < ideal.Size; bi++)
            {
                double r = ri / (double)(ideal.Size - 1);
                double g = gi / (double)(ideal.Size - 1);
                double b = bi / (double)(ideal.Size - 1);
                double lr = CalibrationVerifier.LinearizePatchSignal(target, r);
                double lg = CalibrationVerifier.LinearizePatchSignal(target, g);
                double lb = CalibrationVerifier.LinearizePatchSignal(target, b);
                ideal.SetEntry(ri, gi, bi,
                    (float)ColorMath.SrgbOetf(Math.Clamp(ridge[0, 0] * lr + ridge[0, 1] * lg + ridge[0, 2] * lb, 0, 1)),
                    (float)ColorMath.SrgbOetf(Math.Clamp(ridge[1, 0] * lr + ridge[1, 1] * lg + ridge[1, 2] * lb, 0, 1)),
                    (float)ColorMath.SrgbOetf(Math.Clamp(ridge[2, 0] * lr + ridge[2, 1] * lg + ridge[2, 2] * lb, 0, 1)));
            }

            var identity = IdentityLut(1024);
            var result = Mhc2HardwareCompiler.Compile(IdentityMatrix(), identity, identity, identity,
                ideal, characterization, target, optimizeMatrix: true);

            Assert.True(result.Certificate.OptimizerApplied);
            Assert.True(result.Certificate.Compiled.P95DeltaE < result.Certificate.Baseline.P95DeltaE);
            Assert.True(Math.Abs(result.Matrix[0, 1]) > 0.005 ||
                        Math.Abs(result.Matrix[1, 0]) > 0.005 ||
                        Math.Abs(result.Matrix[2, 1]) > 0.005);
            Assert.InRange(result.Certificate.OrderConflictRed, 0, 0.15);
            Assert.InRange(result.Certificate.OrderConflictGreen, 0, 0.15);
            Assert.InRange(result.Certificate.OrderConflictBlue, 0, 0.15);
        }

        [Fact]
        public void Certificate_BuildsSpacedCounterexamplesAndRecordsPhysicalEvidence()
        {
            var target = StandardTargets.SrgbGamma22;
            var characterization = MatchingCharacterization(target, 2.4);
            var identityLut = IdentityLut(256);
            var residuals = new[]
            {
                new ModelResidual(new SignalPoint(0.5, 0.5, 0.5, SignalManifold.Gray), ResidualKind.Tone, 0.75),
                new ModelResidual(new SignalPoint(0.6, 0.2, 0.8, SignalManifold.Cube), ResidualKind.Color, 1.25),
            };
            var result = Mhc2HardwareCompiler.Compile(
                IdentityMatrix(), identityLut, identityLut, identityLut,
                SeparableGammaCorrection(9, 2.2, 2.4), characterization, target,
                modelResiduals: residuals,
                optimizeMatrix: false);

            var patches = result.Certificate.BuildVerificationPatches(target);
            Assert.Equal(Mhc2HardwareCompiler.DefaultCounterexampleCount, patches.Count);
            Assert.All(result.Certificate.Counterexamples,
                c => Assert.True(c.EmpiricalUpperEstimate >= c.PredictedDeltaE));
            for (int i = 0; i < patches.Count; i++)
            for (int j = i + 1; j < patches.Count; j++)
                Assert.True(Distance(patches[i].DisplayRgb, patches[j].DisplayRgb) >= 0.17);

            const double whiteY = 120;
            var readings = patches.Select(p => new MeasurementResult
            {
                Patch = p,
                Xyz = new CieXyz(
                    p.TargetXyz!.Value.X * whiteY,
                    p.TargetXyz.Value.Y * whiteY,
                    p.TargetXyz.Value.Z * whiteY),
                IsValid = true,
            }).ToList();
            // Standard verification patches have different names; exact RGB reuse must
            // still satisfy a selected counterexample without a duplicate meter read.
            var first = readings[0];
            readings[0] = new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "Standard sweep reuse",
                    DisplayRgb = first.Patch.DisplayRgb,
                    Category = PatchCategory.General,
                },
                Xyz = first.Xyz,
                IsValid = true,
            };
            result.Certificate.RecordMeasurements(readings, target, whiteY);

            Assert.Equal(patches.Count, result.Certificate.MeasuredCounterexampleCount);
            Assert.Equal(0, result.Certificate.MeasuredWorstDeltaE!.Value, 8);
            Assert.True(result.Certificate.EmpiricalEstimatesHeld);
            Assert.Contains("empirical estimates held", result.Certificate.Status);
        }

        [Fact]
        public void Compile_RejectsNonFiniteOrNonMonotoneHardwareInputs()
        {
            var target = StandardTargets.SrgbGamma22;
            var characterization = MatchingCharacterization(target, 2.2);
            var good = IdentityLut(32);
            var badMatrix = IdentityMatrix();
            badMatrix[1, 2] = double.NaN;
            Assert.Throws<ArgumentException>(() => Mhc2HardwareCompiler.Compile(
                badMatrix, good, good, good, new Lut3D(3), characterization, target));

            var badLut = (double[])good.Clone();
            badLut[10] = badLut[9] - 0.1;
            Assert.Throws<ArgumentException>(() => Mhc2HardwareCompiler.Compile(
                IdentityMatrix(), badLut, good, good, new Lut3D(3), characterization, target));
        }

        [Fact]
        public void ContinuousVerifier_RefinesSteepRegionWithoutDenseUniformCube()
        {
            var result = Mhc2ContinuousVerifier.Verify((r, g, b) =>
            {
                double d2 = Square(r - 0.5) + Square(g - 0.5) + Square(b - 0.5);
                return 0.1 * (r + g + b) + 4.0 * Math.Exp(-d2 / 0.006);
            }, maximumDepth: 6, maximumPoints: 1400, targetEnvelopeGapDeltaE: 0.03);

            Assert.True(result.SampledMaximumDeltaE >= 4.0);
            Assert.InRange(result.EvaluatedPointCount, 100, 1400);
            Assert.True(result.VisitedCellCount > 8);
            Assert.True(result.EmpiricalEnvelopeDeltaE >= result.SampledMaximumDeltaE);
            Assert.Equal(result.EmpiricalEnvelopeDeltaE - result.SampledMaximumDeltaE,
                result.RemainingEnvelopeGapDeltaE, 8);
        }

        [Fact]
        public void PhysicalKeepBestGate_IsLexicographicAndProtectsNeutrals()
        {
            var target = StandardTargets.SrgbGamma22;
            var patches = CalibrationVerifier.BuildVerificationPatches();
            var before = PhysicalReadings(patches, target, chromaScale: 1.04, grayScale: 1.04);
            var improved = PhysicalReadings(patches, target, chromaScale: 1.00, grayScale: 1.00);
            var accepted = Mhc2ClosedLoopRefiner.DecidePhysicalAcceptance(
                before, improved, target, beforeCounterexampleWorst: 2.0, afterCounterexampleWorst: 1.0);
            Assert.True(accepted.Accepted, accepted.Reason);

            // Many colors improve, but a neutral cast is an independent veto.
            var neutralRegression = PhysicalReadings(patches, target, chromaScale: 1.00, grayScale: 1.10);
            var rejected = Mhc2ClosedLoopRefiner.DecidePhysicalAcceptance(
                before, neutralRegression, target, beforeCounterexampleWorst: 2.0, afterCounterexampleWorst: 1.0);
            Assert.False(rejected.Accepted);
            Assert.Contains("neutral sentinel", rejected.Reason);
        }

        [Fact]
        public void ClosedLoopProposal_UsesPhysicalResidualsAndAdvancesOnlySafeCandidate()
        {
            var target = StandardTargets.SrgbGamma22;
            var characterization = MatchingCharacterization(target, 2.2);
            var identity = IdentityLut(1024);
            var current = Mhc2HardwareCompiler.Compile(IdentityMatrix(), identity, identity, identity,
                new Lut3D(9), characterization, target, optimizeMatrix: false);
            var readings = CalibrationVerifier.BuildVerificationPatches().Select(p =>
            {
                var rgb = p.DisplayRgb;
                var xyz = target.LinearRgbToXyz(new LinearRgb(
                    Math.Pow(rgb.R, 2.4), Math.Pow(rgb.G, 2.4), Math.Pow(rgb.B, 2.4)));
                return new MeasurementResult
                {
                    Patch = p,
                    // The model says gamma 2.2, while the physical display has drifted to
                    // 2.4. The ideal LUT is still identity, so only the physical residual
                    // observations can produce a proposal.
                    Xyz = new CieXyz(xyz.X * 120, xyz.Y * 120, xyz.Z * 120),
                    IsValid = true,
                };
            }).ToList();

            var proposal = Mhc2ClosedLoopRefiner.Propose(current,
                new Lut3D(17), characterization, target,
                readings, whiteY: 120);

            Assert.Equal(readings.Count, proposal.ObservationCount);
            Assert.True(proposal.ShouldInstall, proposal.Reason);
            Assert.Equal(1, proposal.Payload.Certificate.ClosedLoopRound);
            Assert.True(proposal.Payload.Certificate.Compiled.P95DeltaE <
                        proposal.Payload.Certificate.Baseline.P95DeltaE);
            Assert.Contains("awaiting physical A/B gate", proposal.Reason);
        }

        private static DisplayCharacterization MatchingCharacterization(CalibrationTarget target, double gamma) => new()
        {
            RedPrimary = target.RedPrimary,
            GreenPrimary = target.GreenPrimary,
            BluePrimary = target.BluePrimary,
            WhitePoint = target.WhitePoint,
            BlackXyz = new CieXyz(0, 0, 0),
            WhiteXyz = target.WhitePoint.ToXyz(1),
            BlackLevel = 0,
            PeakLuminance = 120,
            MeasuredGamma = gamma,
            RedToneCurve = ToneCurve.CreateGamma(gamma),
            GreenToneCurve = ToneCurve.CreateGamma(gamma),
            BlueToneCurve = ToneCurve.CreateGamma(gamma),
            NeutralToneCurve = ToneCurve.CreateGamma(gamma),
            RgbToXyzMatrix = target.RgbToXyzMatrix,
        };

        private static Lut3D SeparableGammaCorrection(int size, double sourceGamma, double displayGamma)
        {
            var lut = new Lut3D(size);
            double exponent = sourceGamma / displayGamma;
            for (int r = 0; r < size; r++)
            for (int g = 0; g < size; g++)
            for (int b = 0; b < size; b++)
            {
                float rf = (float)Math.Pow(r / (double)(size - 1), exponent);
                float gf = (float)Math.Pow(g / (double)(size - 1), exponent);
                float bf = (float)Math.Pow(b / (double)(size - 1), exponent);
                lut.SetEntry(r, g, b, rf, gf, bf);
            }
            return lut;
        }

        private static double[] IdentityLut(int length) =>
            Enumerable.Range(0, length).Select(i => i / (double)(length - 1)).ToArray();

        private static double[,] IdentityMatrix() => new double[,]
        {
            { 1, 0, 0 },
            { 0, 1, 0 },
            { 0, 0, 1 },
        };

        private static void AssertMonotone(double[] lut)
        {
            Assert.All(lut, value => Assert.InRange(value, 0, 1));
            for (int i = 1; i < lut.Length; i++)
                Assert.True(lut[i] >= lut[i - 1]);
        }

        private static double Distance(LinearRgb a, LinearRgb b)
        {
            double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        private static List<MeasurementResult> PhysicalReadings(
            IReadOnlyList<ColorPatch> patches, CalibrationTarget target,
            double chromaScale, double grayScale)
        {
            const double whiteY = 120;
            return patches.Select(p =>
            {
                var rgb = p.DisplayRgb;
                var xyz = target.LinearRgbToXyz(new LinearRgb(
                    CalibrationVerifier.LinearizePatchSignal(target, rgb.R),
                    CalibrationVerifier.LinearizePatchSignal(target, rgb.G),
                    CalibrationVerifier.LinearizePatchSignal(target, rgb.B)));
                double scale = p.Category == PatchCategory.Grayscale ? grayScale : chromaScale;
                return new MeasurementResult
                {
                    Patch = p,
                    // Change chromaticity while holding Y normalization stable.
                    Xyz = new CieXyz(xyz.X * whiteY * scale, xyz.Y * whiteY, xyz.Z * whiteY / scale),
                    IsValid = true,
                };
            }).ToList();
        }

        private static double Square(double value) => value * value;
    }
}
