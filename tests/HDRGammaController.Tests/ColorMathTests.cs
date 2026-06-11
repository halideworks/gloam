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
        public void CctToChromaticity_RoundTrip_ApproximatelyPreserves()
        {
            double originalCct = 5500;
            var chromaticity = ColorMath.CctToChromaticity(originalCct);
            double roundTripCct = ColorMath.ChromaticityToCct(chromaticity);

            // Should be within ±50K
            Assert.InRange(roundTripCct, originalCct - 50, originalCct + 50);
        }

        [Fact]
        public void CalculateDuv_D65_ReturnsNearZero()
        {
            double duv = ColorMath.CalculateDuv(Chromaticity.D65);

            // D65 is on the Planckian locus, so Duv should be very small
            Assert.InRange(Math.Abs(duv), 0, 0.005);
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

        #endregion

        #region Color Type Tests

        [Fact]
        public void LinearRgb_IsInGamut_ValidRange()
        {
            var inGamut = new LinearRgb(0.5, 0.5, 0.5);
            var outOfGamut = new LinearRgb(1.5, -0.1, 0.5);

            Assert.True(inGamut.IsInGamut);
            Assert.False(outOfGamut.IsInGamut);
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
        public void CieXyz_ToChromaticity_Correct()
        {
            var xyz = new CieXyz(0.3, 0.3, 0.4);
            var xy = xyz.ToChromaticity();

            // x = X / (X + Y + Z), y = Y / (X + Y + Z)
            double sum = 0.3 + 0.3 + 0.4;
            Assert.Equal(0.3 / sum, xy.X, 6);
            Assert.Equal(0.3 / sum, xy.Y, 6);
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
    }
}
