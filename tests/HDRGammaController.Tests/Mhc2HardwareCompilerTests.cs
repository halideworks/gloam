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
            Assert.Equal(0, result.Certificate.Compiled.MaxDeltaE, 8);
            Assert.Equal(1, result.Certificate.CorrectabilityFraction, 8);
            AssertMonotone(result.LutR);
            AssertMonotone(result.LutG);
            AssertMonotone(result.LutB);
            Assert.Equal(identityLut, result.LutR);
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
    }
}
