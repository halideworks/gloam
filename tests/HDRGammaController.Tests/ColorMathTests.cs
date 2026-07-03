using Xunit;
using HDRGammaController.Core.Calibration;
using System;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Unit tests for ColorMath color science functions.
    /// Tests are based on CIE standards and reference implementations.
    /// </summary>
    public class ColorMathTests
    {
        private const double Tolerance = 0.0001;
        private const double LooseTolerance = 0.001;

        #region XYZ ↔ Lab Conversion Tests

        [Fact]
        public void XyzToLab_D65White_ReturnsCorrectLab()
        {
            // D65 white point XYZ should convert to L*=100, a*=0, b*=0
            var xyz = ColorMath.D65White;
            var lab = ColorMath.XyzToLab(xyz);

            Assert.InRange(lab.L, 99.9, 100.1);
            Assert.InRange(lab.A, -0.5, 0.5);
            Assert.InRange(lab.B, -0.5, 0.5);
        }

        [Fact]
        public void XyzToLab_Black_ReturnsZeroL()
        {
            var xyz = new CieXyz(0, 0, 0);
            var lab = ColorMath.XyzToLab(xyz);

            Assert.Equal(0, lab.L, 2);
        }

        [Fact]
        public void XyzToLab_KnownValues_MatchesReference()
        {
            // Test case: sRGB red (1, 0, 0) in XYZ
            // XYZ for sRGB red ≈ (0.4124, 0.2126, 0.0193)
            var xyzRed = new CieXyz(0.4124564, 0.2126729, 0.0193339);
            var lab = ColorMath.XyzToLab(xyzRed);

            // Expected L*a*b* for sRGB red (D65): approximately L=53.2, a=80.1, b=67.2
            Assert.InRange(lab.L, 52, 55);
            Assert.InRange(lab.A, 78, 82);
            Assert.InRange(lab.B, 65, 70);
        }

        [Fact]
        public void LabToXyz_RoundTrip_PreservesValues()
        {
            var original = new CieXyz(0.5, 0.4, 0.3);
            var lab = ColorMath.XyzToLab(original);
            var roundTrip = ColorMath.LabToXyz(lab);

            Assert.Equal(original.X, roundTrip.X, 4);
            Assert.Equal(original.Y, roundTrip.Y, 4);
            Assert.Equal(original.Z, roundTrip.Z, 4);
        }

        [Fact]
        public void XyzToLab_LinearSegment_HandlesSmallValues()
        {
            // Test values in the linear segment (below epsilon threshold)
            var xyz = new CieXyz(0.001, 0.001, 0.001);
            var lab = ColorMath.XyzToLab(xyz);

            // Should not throw and should produce valid output
            Assert.True(lab.L >= 0);
            Assert.True(!double.IsNaN(lab.A));
            Assert.True(!double.IsNaN(lab.B));
        }

        [Fact]
        public void XyzLabConversions_NonFiniteInputs_ReturnFiniteSafeValues()
        {
            var lab = ColorMath.XyzToLab(
                new CieXyz(double.NaN, double.PositiveInfinity, double.NegativeInfinity),
                new CieXyz(double.NaN, 0, double.PositiveInfinity));

            Assert.True(double.IsFinite(lab.L));
            Assert.True(double.IsFinite(lab.A));
            Assert.True(double.IsFinite(lab.B));

            var xyz = ColorMath.LabToXyz(
                new CieLab(double.NaN, double.PositiveInfinity, double.NegativeInfinity),
                new CieXyz(0, double.NaN, 0));

            Assert.Equal(0.0, xyz.X, 10);
            Assert.Equal(0.0, xyz.Y, 10);
            Assert.Equal(0.0, xyz.Z, 10);
        }

        #endregion

        #region Delta E 2000 Tests

        [Fact]
        public void DeltaE2000_IdenticalColors_ReturnsZero()
        {
            var lab = new CieLab(50, 25, -25);
            double deltaE = lab.DeltaE2000(lab);

            Assert.Equal(0, deltaE, 6);
        }

        [Fact]
        public void DeltaE2000_SmallDifference_ReturnsSmallValue()
        {
            var lab1 = new CieLab(50, 25, -25);
            var lab2 = new CieLab(50.1, 25.1, -25.1);
            double deltaE = lab1.DeltaE2000(lab2);

            // Small difference should produce small Delta E
            Assert.InRange(deltaE, 0, 1);
        }

        [Fact]
        public void DeltaE2000_IsSymmetric()
        {
            var lab1 = new CieLab(50, 25, -25);
            var lab2 = new CieLab(60, 30, -20);

            double deltaE1 = lab1.DeltaE2000(lab2);
            double deltaE2 = lab2.DeltaE2000(lab1);

            Assert.Equal(deltaE1, deltaE2, 6);
        }

        [Fact]
        public void DeltaE2000_GrayscaleColors_HandlesZeroChroma()
        {
            // Grayscale colors have a=0, b=0 (zero chroma)
            var gray1 = new CieLab(50, 0, 0);
            var gray2 = new CieLab(60, 0, 0);

            double deltaE = gray1.DeltaE2000(gray2);

            // Should not throw or return NaN
            Assert.True(!double.IsNaN(deltaE));
            Assert.True(deltaE > 0);
        }

        [Fact]
        public void DeltaE2000_VeryDifferentColors_ReturnsLargeValue()
        {
            // Black vs White
            var black = new CieLab(0, 0, 0);
            var white = new CieLab(100, 0, 0);

            double deltaE = black.DeltaE2000(white);

            // Should be a large value (>50)
            Assert.True(deltaE > 50);
        }

        [Fact]
        public void DeltaE2000_ReferenceValues_MatchesExpected()
        {
            // Test case from CIE 142-2001 worked examples
            // These are standard test cases for verifying Delta E 2000 implementations
            var lab1 = new CieLab(50.0, 2.6772, -79.7751);
            var lab2 = new CieLab(50.0, 0.0, -82.7485);

            double deltaE = lab1.DeltaE2000(lab2);

            // Expected ≈ 2.0425 (from CIE 142-2001)
            Assert.InRange(deltaE, 1.9, 2.2);
        }

        public static TheoryData<CieLab, CieLab, double> DeltaE2000ReferencePairs => new()
        {
            { new CieLab(50.0000,  2.6772, -79.7751), new CieLab(50.0000,   0.0000, -82.7485),  2.0425 },
            { new CieLab(50.0000,  3.1571, -77.2803), new CieLab(50.0000,   0.0000, -82.7485),  2.8615 },
            { new CieLab(50.0000,  2.8361, -74.0200), new CieLab(50.0000,   0.0000, -82.7485),  3.4412 },
            { new CieLab(50.0000, -1.3802, -84.2814), new CieLab(50.0000,   0.0000, -82.7485),  1.0000 },
            { new CieLab(50.0000, -1.1848, -84.8006), new CieLab(50.0000,   0.0000, -82.7485),  1.0000 },
            { new CieLab(50.0000, -0.9009, -85.5211), new CieLab(50.0000,   0.0000, -82.7485),  1.0000 },
            { new CieLab(50.0000,  0.0000,   0.0000), new CieLab(50.0000,  -1.0000,   2.0000),  2.3669 },
            { new CieLab(50.0000, -1.0000,   2.0000), new CieLab(50.0000,   0.0000,   0.0000),  2.3669 },
            { new CieLab(50.0000,  2.4900,  -0.0010), new CieLab(50.0000,  -2.4900,   0.0009),  7.1792 },
            { new CieLab(50.0000,  2.4900,  -0.0010), new CieLab(50.0000,  -2.4900,   0.0010),  7.1792 },
            { new CieLab(50.0000,  2.4900,  -0.0010), new CieLab(50.0000,  -2.4900,   0.0011),  7.2195 },
            { new CieLab(50.0000,  2.4900,  -0.0010), new CieLab(50.0000,  -2.4900,   0.0012),  7.2195 },
            { new CieLab(50.0000, -0.0010,   2.4900), new CieLab(50.0000,   0.0009,  -2.4900),  4.8045 },
            { new CieLab(50.0000, -0.0010,   2.4900), new CieLab(50.0000,   0.0010,  -2.4900),  4.8045 },
            { new CieLab(50.0000, -0.0010,   2.4900), new CieLab(50.0000,   0.0011,  -2.4900),  4.7461 },
            { new CieLab(50.0000,  2.5000,   0.0000), new CieLab(50.0000,   0.0000,  -2.5000),  4.3065 },
            { new CieLab(50.0000,  2.5000,   0.0000), new CieLab(73.0000,  25.0000, -18.0000), 27.1492 },
            { new CieLab(50.0000,  2.5000,   0.0000), new CieLab(61.0000,  -5.0000,  29.0000), 22.8977 },
            { new CieLab(50.0000,  2.5000,   0.0000), new CieLab(56.0000, -27.0000,  -3.0000), 31.9030 },
            { new CieLab(50.0000,  2.5000,   0.0000), new CieLab(58.0000,  24.0000,  15.0000), 19.4535 },
            { new CieLab(50.0000,  2.5000,   0.0000), new CieLab(50.0000,   3.1736,   0.5854),  1.0000 },
            { new CieLab(50.0000,  2.5000,   0.0000), new CieLab(50.0000,   3.2972,   0.0000),  1.0000 },
            { new CieLab(50.0000,  2.5000,   0.0000), new CieLab(50.0000,   1.8634,   0.5757),  1.0000 },
            { new CieLab(50.0000,  2.5000,   0.0000), new CieLab(50.0000,   3.2592,   0.3350),  1.0000 },
            { new CieLab(60.2574, -34.0099,  36.2677), new CieLab(60.4626, -34.1751,  39.4387),  1.2644 },
            { new CieLab(63.0109, -31.0961,  -5.8663), new CieLab(62.8187, -29.7946,  -4.0864),  1.2630 },
            { new CieLab(61.2901,   3.7196,  -5.3901), new CieLab(61.4292,   2.2480,  -4.9620),  1.8731 },
            { new CieLab(35.0831, -44.1164,   3.7933), new CieLab(35.0232, -40.0716,   1.5901),  1.8645 },
            { new CieLab(22.7233,  20.0904, -46.6940), new CieLab(23.0331,  14.9730, -42.5619),  2.0373 },
            { new CieLab(36.4612,  47.8580,  18.3852), new CieLab(36.2715,  50.5065,  21.2231),  1.4146 },
            { new CieLab(90.8027,  -2.0831,   1.4410), new CieLab(91.1528,  -1.6435,   0.0447),  1.4441 },
            { new CieLab(90.9257,  -0.5406,  -0.9208), new CieLab(88.6381,  -0.8985,  -0.7239),  1.5381 },
            { new CieLab( 6.7747,  -0.2908,  -2.4247), new CieLab( 5.8714,  -0.0985,  -2.2286),  0.6377 },
            { new CieLab( 2.0776,   0.0795,  -1.1350), new CieLab( 0.9033,  -0.0636,  -0.5514),  0.9082 },
        };

        [Theory]
        [MemberData(nameof(DeltaE2000ReferencePairs))]
        public void DeltaE2000_ReferencePairs_MatchExpected(CieLab lab1, CieLab lab2, double expected)
        {
            Assert.Equal(expected, lab1.DeltaE2000(lab2), 4);
        }

        [Theory]
        [InlineData(double.NaN, 0, 0)]
        [InlineData(50, double.PositiveInfinity, 0)]
        [InlineData(50, 0, double.NegativeInfinity)]
        [InlineData(50, 1.0e9, 0)]
        public void DeltaE_InvalidOrOverflowClassInputs_ReturnUndefinedSentinel(double l, double a, double b)
        {
            var corrupt = new CieLab(l, a, b);
            var reference = new CieLab(50, 0, 0);

            Assert.Equal(double.PositiveInfinity, corrupt.DeltaE76(reference));
            Assert.Equal(double.PositiveInfinity, corrupt.DeltaE94(reference));
            Assert.Equal(double.PositiveInfinity, corrupt.DeltaE2000(reference));
        }

        [Fact]
        public void DeltaE2000_LargeButUsableValues_DoesNotReturnNaN()
        {
            var lab1 = new CieLab(50, 100_000, -100_000);
            var lab2 = new CieLab(55, 99_000, -99_500);

            Assert.False(double.IsNaN(lab1.DeltaE2000(lab2)));
        }

        #endregion

        #region Chromatic Adaptation Tests

        [Fact]
        public void ChromaticAdaptation_SameWhite_ReturnsOriginal()
        {
            var xyz = new CieXyz(0.5, 0.4, 0.3);
            var result = ColorMath.ChromaticAdaptation(xyz, ColorMath.D65White, ColorMath.D65White);

            Assert.Equal(xyz.X, result.X, 4);
            Assert.Equal(xyz.Y, result.Y, 4);
            Assert.Equal(xyz.Z, result.Z, 4);
        }

        [Fact]
        public void ChromaticAdaptation_D65ToD50_PreservesLuminance()
        {
            var xyz = new CieXyz(0.5, 0.4, 0.3);
            var result = ColorMath.AdaptD65ToD50(xyz);

            // Y (luminance) should be approximately preserved
            Assert.InRange(result.Y, 0.35, 0.45);
        }

        [Fact]
        public void ChromaticAdaptation_D50ToD65_RoundTrip()
        {
            var original = new CieXyz(0.5, 0.4, 0.3);
            var d50 = ColorMath.AdaptD65ToD50(original);
            var roundTrip = ColorMath.AdaptD50ToD65(d50);

            Assert.Equal(original.X, roundTrip.X, 3);
            Assert.Equal(original.Y, roundTrip.Y, 3);
            Assert.Equal(original.Z, roundTrip.Z, 3);
        }

        [Fact]
        public void ChromaticAdaptation_WhitePoint_AdaptsCorrectly()
        {
            // D65 white adapted to D50 should become D50 white
            var result = ColorMath.AdaptD65ToD50(ColorMath.D65White);

            // Result should be close to D50 white point
            Assert.InRange(result.X, 0.94, 0.99);
            Assert.InRange(result.Y, 0.98, 1.02);
            Assert.InRange(result.Z, 0.80, 0.86);
        }

        [Fact]
        public void ChromaticAdaptation_NonFiniteInputs_ReturnsFiniteSafeValues()
        {
            var result = ColorMath.ChromaticAdaptation(
                new CieXyz(double.NaN, 0.4, double.PositiveInfinity),
                new CieXyz(0, double.NaN, 0),
                new CieXyz(double.PositiveInfinity, 0, 0));

            Assert.True(double.IsFinite(result.X));
            Assert.True(double.IsFinite(result.Y));
            Assert.True(double.IsFinite(result.Z));
        }

        #endregion

        #region CAT16 Chromatic Adaptation Tests

        [Fact]
        public void Cat16Matrix_TimesInverse_IsIdentity()
        {
            var product = ColorMath.MultiplyMatrices(ColorMath.Cat16Matrix, ColorMath.Cat16InverseMatrix);

            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.Equal(r == c ? 1.0 : 0.0, product[r, c], 12);
        }

        [Fact]
        public void Cat16Adapt_FullAdaptation_MapsD65WhiteToIlluminantAWhite()
        {
            // Known corresponding-colour check: at D = 1 the source white must land exactly
            // on the destination white (von Kries in CAT16 cone space is exact for whites).
            var d65 = Chromaticity.D65.ToXyz(1.0);
            var illuminantA = Chromaticity.IlluminantA.ToXyz(1.0);

            var adapted = ColorMath.Cat16Adapt(d65, d65, illuminantA, 1.0);

            Assert.True(Math.Abs(adapted.X - illuminantA.X) < 1e-9, $"X off by {adapted.X - illuminantA.X:E2}");
            Assert.True(Math.Abs(adapted.Y - illuminantA.Y) < 1e-9, $"Y off by {adapted.Y - illuminantA.Y:E2}");
            Assert.True(Math.Abs(adapted.Z - illuminantA.Z) < 1e-9, $"Z off by {adapted.Z - illuminantA.Z:E2}");
        }

        [Fact]
        public void Cat16Adapt_ZeroDegree_IsIdentity()
        {
            // D = 0 means no adaptation at all: any colour must pass through unchanged
            // (up to the forward/inverse matrix round trip).
            var xyz = new CieXyz(0.5, 0.4, 0.3);
            var d65 = Chromaticity.D65.ToXyz(1.0);
            var illuminantA = Chromaticity.IlluminantA.ToXyz(1.0);

            var adapted = ColorMath.Cat16Adapt(xyz, d65, illuminantA, 0.0);

            Assert.Equal(xyz.X, adapted.X, 12);
            Assert.Equal(xyz.Y, adapted.Y, 12);
            Assert.Equal(xyz.Z, adapted.Z, 12);
        }

        [Fact]
        public void Cat16Adapt_FullDegree_MatchesManualFullCat16Transform()
        {
            // D = 1 must reproduce the plain (complete) CAT16 corresponding-colour
            // transform: cone-space von Kries scaling by destWhite_c / sourceWhite_c.
            var xyz = new CieXyz(0.5, 0.4, 0.3);
            var d65 = Chromaticity.D65.ToXyz(1.0);
            var illuminantA = Chromaticity.IlluminantA.ToXyz(1.0);

            double[] srcCone = Multiply(ColorMath.Cat16Matrix, d65.X, d65.Y, d65.Z);
            double[] dstCone = Multiply(ColorMath.Cat16Matrix, illuminantA.X, illuminantA.Y, illuminantA.Z);
            double[] inputCone = Multiply(ColorMath.Cat16Matrix, xyz.X, xyz.Y, xyz.Z);
            double[] expected = Multiply(
                ColorMath.Cat16InverseMatrix,
                inputCone[0] * dstCone[0] / srcCone[0],
                inputCone[1] * dstCone[1] / srcCone[1],
                inputCone[2] * dstCone[2] / srcCone[2]);

            var adapted = ColorMath.Cat16Adapt(xyz, d65, illuminantA, 1.0);

            Assert.Equal(expected[0], adapted.X, 12);
            Assert.Equal(expected[1], adapted.Y, 12);
            Assert.Equal(expected[2], adapted.Z, 12);
        }

        [Fact]
        public void Cat16Adapt_IntermediateDegree_AdaptedWhiteIsConeSpaceBlend()
        {
            // The illuminant-blend formulation: the source white adapted at degree D must
            // land exactly on D·destWhite_c + (1−D)·sourceWhite_c in CAT16 cone space.
            var d65 = Chromaticity.D65.ToXyz(1.0);
            var illuminantA = Chromaticity.IlluminantA.ToXyz(1.0);
            const double degree = 0.5;

            var adapted = ColorMath.Cat16Adapt(d65, d65, illuminantA, degree);

            double[] srcCone = Multiply(ColorMath.Cat16Matrix, d65.X, d65.Y, d65.Z);
            double[] dstCone = Multiply(ColorMath.Cat16Matrix, illuminantA.X, illuminantA.Y, illuminantA.Z);
            double[] adaptedCone = Multiply(ColorMath.Cat16Matrix, adapted.X, adapted.Y, adapted.Z);

            for (int i = 0; i < 3; i++)
            {
                double blend = degree * dstCone[i] + (1.0 - degree) * srcCone[i];
                Assert.Equal(blend, adaptedCone[i], 12);
            }
        }

        [Fact]
        public void Cat16Adapt_SameWhites_ReturnsOriginalAtAnyDegree()
        {
            var xyz = new CieXyz(0.5, 0.4, 0.3);
            var d65 = Chromaticity.D65.ToXyz(1.0);

            foreach (double degree in new[] { 0.0, 0.3, 0.8, 1.0 })
            {
                var adapted = ColorMath.Cat16Adapt(xyz, d65, d65, degree);
                Assert.Equal(xyz.X, adapted.X, 12);
                Assert.Equal(xyz.Y, adapted.Y, 12);
                Assert.Equal(xyz.Z, adapted.Z, 12);
            }
        }

        [Fact]
        public void Cat16Adapt_NonFiniteInputs_ReturnsFiniteSafeValues()
        {
            var result = ColorMath.Cat16Adapt(
                new CieXyz(double.NaN, 0.4, double.PositiveInfinity),
                new CieXyz(0, double.NaN, 0),
                new CieXyz(double.PositiveInfinity, 0, 0),
                double.NaN);

            AssertFinite(result);
        }

        private static double[] Multiply(double[,] m, double x, double y, double z) => new[]
        {
            m[0, 0] * x + m[0, 1] * y + m[0, 2] * z,
            m[1, 0] * x + m[1, 1] * y + m[1, 2] * z,
            m[2, 0] * x + m[2, 1] * y + m[2, 2] * z
        };

        #endregion

        #region RGB ↔ XYZ Conversion Tests

        [Fact]
        public void LinearSrgbToXyz_White_ReturnsD65()
        {
            var white = new LinearRgb(1, 1, 1);
            var xyz = ColorMath.LinearSrgbToXyz(white);

            // Should be approximately D65 white point
            Assert.InRange(xyz.X, 0.94, 0.96);
            Assert.InRange(xyz.Y, 0.99, 1.01);
            Assert.InRange(xyz.Z, 1.08, 1.10);
        }

        [Fact]
        public void LinearSrgbToXyz_Black_ReturnsZero()
        {
            var black = new LinearRgb(0, 0, 0);
            var xyz = ColorMath.LinearSrgbToXyz(black);

            Assert.Equal(0, xyz.X, 6);
            Assert.Equal(0, xyz.Y, 6);
            Assert.Equal(0, xyz.Z, 6);
        }

        [Fact]
        public void XyzToLinearSrgb_RoundTrip_PreservesValues()
        {
            var original = new LinearRgb(0.5, 0.3, 0.7);
            var xyz = ColorMath.LinearSrgbToXyz(original);
            var roundTrip = ColorMath.XyzToLinearSrgb(xyz);

            Assert.Equal(original.R, roundTrip.R, 4);
            Assert.Equal(original.G, roundTrip.G, 4);
            Assert.Equal(original.B, roundTrip.B, 4);
        }

        [Fact]
        public void LinearRec2020ToXyz_White_ReturnsD65()
        {
            var white = new LinearRgb(1, 1, 1);
            var xyz = ColorMath.LinearRec2020ToXyz(white);

            // Should be approximately D65 white point
            Assert.InRange(xyz.Y, 0.99, 1.01);
        }

        [Fact]
        public void RgbXyzConversions_NonFiniteInputs_ReturnFiniteValues()
        {
            var corruptRgb = new LinearRgb(double.NaN, double.PositiveInfinity, double.NegativeInfinity);
            var corruptXyz = new CieXyz(double.NaN, double.PositiveInfinity, double.NegativeInfinity);

            AssertFinite(ColorMath.LinearSrgbToXyz(corruptRgb));
            AssertFinite(ColorMath.XyzToLinearSrgb(corruptXyz));
            AssertFinite(ColorMath.LinearRec2020ToXyz(corruptRgb));
            AssertFinite(ColorMath.XyzToLinearRec2020(corruptXyz));
            AssertFinite(ColorMath.LinearP3D65ToXyz(corruptRgb));
            AssertFinite(ColorMath.XyzToLinearP3D65(corruptXyz));
        }

        #endregion

        #region Transfer Function Tests

        [Fact]
        public void SrgbOetf_Linear_ReturnsLinear()
        {
            // Values below 0.0031308 should use linear segment
            double input = 0.001;
            double result = ColorMath.SrgbOetf(input);

            Assert.Equal(12.92 * input, result, 6);
        }

        [Fact]
        public void SrgbOetf_Gamma_ReturnsGamma()
        {
            // Values above 0.0031308 should use gamma segment
            double input = 0.5;
            double result = ColorMath.SrgbOetf(input);
            double expected = 1.055 * Math.Pow(input, 1.0 / 2.4) - 0.055;

            Assert.Equal(expected, result, 6);
        }

        [Fact]
        public void SrgbEotf_RoundTrip_PreservesValues()
        {
            double original = 0.5;
            double encoded = ColorMath.SrgbOetf(original);
            double decoded = ColorMath.SrgbEotf(encoded);

            Assert.Equal(original, decoded, 6);
        }

        [Fact]
        public void SrgbOetf_Zero_ReturnsZero()
        {
            Assert.Equal(0, ColorMath.SrgbOetf(0), 6);
        }

        [Fact]
        public void SrgbOetf_One_ReturnsOne()
        {
            Assert.Equal(1, ColorMath.SrgbOetf(1), 6);
        }

        [Fact]
        public void GammaEncode_22Gamma_MatchesExpected()
        {
            double input = 0.5;
            double result = ColorMath.GammaEncode(input, 2.2);
            double expected = Math.Pow(0.5, 1.0 / 2.2);

            Assert.Equal(expected, result, 6);
        }

        [Fact]
        public void GammaDecode_RoundTrip_PreservesValues()
        {
            double original = 0.5;
            double gamma = 2.4;
            double encoded = ColorMath.GammaEncode(original, gamma);
            double decoded = ColorMath.GammaDecode(encoded, gamma);

            Assert.Equal(original, decoded, 6);
        }

        [Fact]
        public void TransferFunctions_NonFiniteInputs_ReturnFiniteBoundedValues()
        {
            Assert.Equal(0.0, ColorMath.SrgbOetf(double.NaN), 10);
            Assert.Equal(0.0, ColorMath.SrgbEotf(double.PositiveInfinity), 10);
            Assert.Equal(0.0, ColorMath.GammaEncode(double.NaN, 2.2), 10);
            Assert.Equal(Math.Pow(0.5, 1.0 / 2.2), ColorMath.GammaEncode(0.5, double.NaN), 10);
            Assert.Equal(Math.Pow(0.5, 2.2), ColorMath.GammaDecode(0.5, double.PositiveInfinity), 10);
            Assert.Equal(0.0, ColorMath.Rec2020Oetf(double.NaN), 10);
            Assert.Equal(0.0, ColorMath.Rec2020Eotf(double.NegativeInfinity), 10);
        }

        [Fact]
        public void Rec2020Oetf_RoundTrip_PreservesValues()
        {
            double original = 0.5;
            double encoded = ColorMath.Rec2020Oetf(original);
            double decoded = ColorMath.Rec2020Eotf(encoded);

            Assert.Equal(original, decoded, 4);
        }

        #endregion

        #region CCT Calculation Tests

        [Fact]
        public void ChromaticityToCct_D65_Returns6500()
        {
            double cct = ColorMath.ChromaticityToCct(Chromaticity.D65);

            // Should be approximately 6500K (±100K due to approximation)
            Assert.InRange(cct, 6400, 6600);
        }

        [Fact]
        public void CctToChromaticity_6500K_ReturnsNearD65()
        {
            var chromaticity = ColorMath.CctToChromaticity(6500);

            // Should be close to D65 (0.31271, 0.32902)
            Assert.InRange(chromaticity.X, 0.31, 0.32);
            Assert.InRange(chromaticity.Y, 0.32, 0.34);
        }

        [Fact]
        public void CctToChromaticity_ClampsToApproximationRange()
        {
            var belowRange = ColorMath.CctToChromaticity(1000);
            var minSupported = ColorMath.CctToChromaticity(1667);
            var aboveRange = ColorMath.CctToChromaticity(50000);
            var maxSupported = ColorMath.CctToChromaticity(25000);

            Assert.Equal(minSupported.X, belowRange.X, 12);
            Assert.Equal(minSupported.Y, belowRange.Y, 12);
            Assert.Equal(maxSupported.X, aboveRange.X, 12);
            Assert.Equal(maxSupported.Y, aboveRange.Y, 12);
        }

        [Fact]
        public void CctToChromaticity_NonFiniteInput_FallsBackToD65Range()
        {
            var chromaticity = ColorMath.CctToChromaticity(double.NaN);

            Assert.True(double.IsFinite(chromaticity.X));
            Assert.True(double.IsFinite(chromaticity.Y));
            Assert.InRange(chromaticity.X, 0.31, 0.32);
            Assert.InRange(chromaticity.Y, 0.32, 0.34);
        }

        [Theory]
        [InlineData(double.NaN, 0.3)]
        [InlineData(0.3, double.PositiveInfinity)]
        [InlineData(-0.1, 0.3)]
        [InlineData(0.3, 0.0)]
        [InlineData(0.8, 0.4)]
        public void ChromaticityDiagnostics_InvalidChromaticity_ReturnNeutral(double x, double y)
        {
            var xy = new Chromaticity(x, y);

            Assert.Equal(6500.0, ColorMath.ChromaticityToCct(xy), 10);
            Assert.Equal(0.0, ColorMath.CalculateDuv(xy), 10);
        }

        [Fact]
        public void CctToChromaticity_RoundTrip_ApproximatelyPreserves()
        {
            double originalCct = 5500;
            var chromaticity = ColorMath.CctToChromaticity(originalCct);
            double roundTripCct = ColorMath.ChromaticityToCct(chromaticity);

            // Should be within ±50K
            Assert.InRange(roundTripCct, originalCct - 50, originalCct + 50);
        }

        [Theory]
        [InlineData(2700)]
        [InlineData(4000)]
        [InlineData(6500)]
        [InlineData(10000)]
        public void ChromaticityToCct_NearestLocusSearch_RoundTripsDisplayWhiteRange(double cct)
        {
            var chromaticity = ColorMath.CctToChromaticity(cct);
            double roundTrip = ColorMath.ChromaticityToCct(chromaticity);

            Assert.InRange(roundTrip, cct - 2.0, cct + 2.0);
        }

        [Fact]
        public void CalculateDuv_D65_ReturnsNearZero()
        {
            double duv = ColorMath.CalculateDuv(Chromaticity.D65);

            // D65 is on the Planckian locus, so Duv should be very small
            Assert.InRange(Math.Abs(duv), 0, 0.005);
        }

        [Fact]
        public void CalculateDuv_ReportsGreenPositiveAndMagentaNegative()
        {
            var neutral = ColorMath.CctToChromaticity(6500);
            var greenish = new Chromaticity(neutral.X, neutral.Y + 0.01);
            var magenta = new Chromaticity(neutral.X, neutral.Y - 0.01);

            Assert.True(ColorMath.CalculateDuv(greenish) > 0);
            Assert.True(ColorMath.CalculateDuv(magenta) < 0);
        }

        #endregion

        #region Matrix Operations Tests

        [Fact]
        public void Invert3x3_IdentityMatrix_ReturnsIdentity()
        {
            double[,] identity = {
                { 1, 0, 0 },
                { 0, 1, 0 },
                { 0, 0, 1 }
            };

            var result = ColorMath.Invert3x3(identity);

            Assert.Equal(1, result[0, 0], 6);
            Assert.Equal(0, result[0, 1], 6);
            Assert.Equal(0, result[1, 0], 6);
            Assert.Equal(1, result[1, 1], 6);
        }

        [Fact]
        public void Invert3x3_SrgbMatrix_RoundTrip()
        {
            var inverted = ColorMath.Invert3x3(ColorMath.SrgbToXyzMatrix);
            var product = ColorMath.MultiplyMatrices(ColorMath.SrgbToXyzMatrix, inverted);

            // Product should be approximately identity
            Assert.InRange(product[0, 0], 0.999, 1.001);
            Assert.InRange(product[1, 1], 0.999, 1.001);
            Assert.InRange(product[2, 2], 0.999, 1.001);
            Assert.InRange(Math.Abs(product[0, 1]), 0, 0.001);
        }

        [Fact]
        public void Invert3x3_SingularMatrix_ThrowsException()
        {
            double[,] singular = {
                { 1, 2, 3 },
                { 2, 4, 6 },  // Row 2 = 2 * Row 1 (linearly dependent)
                { 1, 1, 1 }
            };

            Assert.Throws<InvalidOperationException>(() => ColorMath.Invert3x3(singular));
        }

        [Fact]
        public void MatrixOperations_NonFiniteInput_Throw()
        {
            double[,] corrupt = {
                { 1, 0, 0 },
                { 0, double.NaN, 0 },
                { 0, 0, 1 }
            };

            Assert.Throws<InvalidOperationException>(() => ColorMath.Invert3x3(corrupt));
            Assert.Throws<InvalidOperationException>(() => ColorMath.MultiplyMatrices(ColorMath.SrgbToXyzMatrix, corrupt));
        }

        [Fact]
        public void CalculateRgbToXyzMatrix_Rec709Primaries_MatchesSrgbMatrix()
        {
            var matrix = ColorMath.CalculateRgbToXyzMatrix(
                Chromaticity.Rec709Red,
                Chromaticity.Rec709Green,
                Chromaticity.Rec709Blue,
                Chromaticity.D65);

            // Should be similar to sRGB matrix (same primaries)
            Assert.InRange(matrix[0, 0], 0.40, 0.42); // Xr
            Assert.InRange(matrix[1, 0], 0.21, 0.22); // Yr
            Assert.InRange(matrix[2, 0], 0.01, 0.03); // Zr
        }

        [Theory]
        [InlineData(double.NaN, 0.33, "red")]
        [InlineData(0.64, double.PositiveInfinity, "red")]
        [InlineData(-0.1, 0.60, "green")]
        [InlineData(0.15, 0.0, "blue")]
        [InlineData(0.8, 0.4, "white")]
        public void CalculateRgbToXyzMatrix_InvalidChromaticity_ThrowsArgumentException(
            double x, double y, string invalidField)
        {
            var red = invalidField == "red" ? new Chromaticity(x, y) : Chromaticity.Rec709Red;
            var green = invalidField == "green" ? new Chromaticity(x, y) : Chromaticity.Rec709Green;
            var blue = invalidField == "blue" ? new Chromaticity(x, y) : Chromaticity.Rec709Blue;
            var white = invalidField == "white" ? new Chromaticity(x, y) : Chromaticity.D65;

            Assert.Throws<ArgumentException>(() =>
                ColorMath.CalculateRgbToXyzMatrix(red, green, blue, white));
        }

        [Fact]
        public void GamutCoverage_NonFinitePrimaries_ReturnsZero()
        {
            double coverage = ColorMath.GamutCoverage(
                new Chromaticity(double.NaN, 0.33),
                Chromaticity.Rec709Green,
                Chromaticity.Rec709Blue);

            Assert.Equal(0.0, coverage, 10);
        }

        [Fact]
        public void GamutCoverage_ImpossiblePrimaries_ReturnsZero()
        {
            double coverage = ColorMath.GamutCoverage(
                new Chromaticity(0.8, 0.4),
                Chromaticity.Rec709Green,
                Chromaticity.Rec709Blue);

            Assert.Equal(0.0, coverage, 10);
        }

        #endregion

        #region Color Type Tests

        [Fact]
        public void LinearRgb_IsInGamut_ValidRange()
        {
            var inGamut = new LinearRgb(0.5, 0.5, 0.5);
            var outOfGamut = new LinearRgb(1.5, -0.1, 0.5);
            var corrupt = new LinearRgb(double.NaN, 0.5, 0.5);

            Assert.True(inGamut.IsInGamut);
            Assert.False(outOfGamut.IsInGamut);
            Assert.False(corrupt.IsInGamut);
        }

        [Fact]
        public void LinearRgb_ClampAndScale_ReturnFiniteValues()
        {
            var corrupt = new LinearRgb(double.NaN, double.PositiveInfinity, -0.5);

            var clamped = corrupt.Clamp();
            Assert.Equal(0.0, clamped.R, 10);
            Assert.Equal(0.0, clamped.G, 10);
            Assert.Equal(0.0, clamped.B, 10);

            var scaled = corrupt.Scale(double.PositiveInfinity);
            Assert.Equal(0.0, scaled.R, 10);
            Assert.Equal(0.0, scaled.G, 10);
            Assert.Equal(0.0, scaled.B, 10);
        }

        [Fact]
        public void Chromaticity_ToXyz_PreservesY()
        {
            double targetY = 0.5;
            var xy = new Chromaticity(0.3, 0.3);
            var xyz = xy.ToXyz(targetY);

            Assert.Equal(targetY, xyz.Y, 6);
        }

        [Fact]
        public void Chromaticity_ToXyz_InvalidInput_UsesFiniteFallback()
        {
            var xyz = new Chromaticity(double.NaN, 0.3).ToXyz(50.0);

            Assert.True(double.IsFinite(xyz.X));
            Assert.Equal(50.0, xyz.Y, 10);
            Assert.True(double.IsFinite(xyz.Z));

            var black = Chromaticity.D65.ToXyz(double.PositiveInfinity);
            Assert.Equal(0.0, black.X, 10);
            Assert.Equal(0.0, black.Y, 10);
            Assert.Equal(0.0, black.Z, 10);
        }

        [Fact]
        public void CieXyz_ToChromaticity_Correct()
        {
            var xyz = new CieXyz(0.3, 0.3, 0.4);
            var xy = xyz.ToChromaticity();

            // x = X / (X + Y + Z), y = Y / (X + Y + Z)
            double sum = 0.3 + 0.3 + 0.4;
            Assert.Equal(0.3 / sum, xy.X, 6);
            Assert.Equal(0.3 / sum, xy.Y, 6);
        }

        [Theory]
        [InlineData(double.NaN, 0.3, 0.4)]
        [InlineData(double.PositiveInfinity, 0.3, 0.4)]
        [InlineData(0.3, 0.3, double.NegativeInfinity)]
        [InlineData(-1.0, 0.5, 0.5)]
        public void CieXyz_ToChromaticity_InvalidInput_ReturnsD65(double x, double y, double z)
        {
            var xy = new CieXyz(x, y, z).ToChromaticity();

            Assert.Equal(Chromaticity.D65.X, xy.X, 6);
            Assert.Equal(Chromaticity.D65.Y, xy.Y, 6);
        }

        [Fact]
        public void Chromaticity_DistanceTo_ReturnsCorrectDistance()
        {
            var c1 = new Chromaticity(0.3, 0.3);
            var c2 = new Chromaticity(0.4, 0.4);

            double distance = c1.DistanceTo(c2);
            double expected = Math.Sqrt(0.1 * 0.1 + 0.1 * 0.1);

            Assert.Equal(expected, distance, 6);
        }

        #endregion

        private static void AssertFinite(CieXyz xyz)
        {
            Assert.True(double.IsFinite(xyz.X));
            Assert.True(double.IsFinite(xyz.Y));
            Assert.True(double.IsFinite(xyz.Z));
        }

        private static void AssertFinite(LinearRgb rgb)
        {
            Assert.True(double.IsFinite(rgb.R));
            Assert.True(double.IsFinite(rgb.G));
            Assert.True(double.IsFinite(rgb.B));
        }
    }
}
