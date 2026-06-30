using Xunit;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using System;

namespace HDRGammaController.Tests
{
    public class TransferFunctionTests
    {
        [Fact]
        public void PqEotf_Bounds()
        {
            Assert.Equal(0.0, TransferFunctions.PqEotf(0.0), 4);
            Assert.Equal(10000.0, TransferFunctions.PqEotf(1.0), 4);
        }

        [Fact]
        public void PqInverseEotf_Bounds()
        {
            Assert.Equal(0.0, TransferFunctions.PqInverseEotf(0.0), 4);
            Assert.Equal(1.0, TransferFunctions.PqInverseEotf(10000.0), 4);
        }

        [Fact]
        public void TransferFunctions_NonFiniteInputs_ReturnFiniteBoundedValues()
        {
            Assert.Equal(0.0, TransferFunctions.PqEotf(double.NaN), 10);
            Assert.Equal(0.0, TransferFunctions.PqInverseEotf(double.PositiveInfinity), 10);
            Assert.Equal(0.0, TransferFunctions.SrgbOetf(double.NaN), 10);
            Assert.Equal(0.0, TransferFunctions.SrgbEotf(double.NegativeInfinity), 10);
            Assert.Equal(0.0, TransferFunctions.SrgbInverseEotf(double.NaN, double.PositiveInfinity), 10);
            Assert.Equal(0.0, TransferFunctions.Bt1886Eotf(double.NaN, double.PositiveInfinity, double.NaN), 10);
            Assert.Equal(0.0, TransferFunctions.Bt1886InverseEotf(double.NaN, double.PositiveInfinity, double.NaN), 10);
        }

        [Theory]
        [InlineData(0.1)]
        [InlineData(0.5)]
        [InlineData(0.8)]
        public void Pq_RoundTrip(double signal)
        {
            double nits = TransferFunctions.PqEotf(signal);
            double result = TransferFunctions.PqInverseEotf(nits);
            Assert.Equal(signal, result, 6);
        }

        [Theory]
        [InlineData(0.1, 0.0623368657)]
        [InlineData(1.0, 0.1499457321)]
        [InlineData(10.0, 0.2996990924)]
        [InlineData(100.0, 0.5080784215)]
        [InlineData(203.0, 0.5806888810)]
        [InlineData(1000.0, 0.7518270962)]
        [InlineData(4000.0, 0.9025723933)]
        [InlineData(10000.0, 1.0)]
        public void PqInverseEotf_St2084ReferencePoints(double nits, double expectedSignal)
        {
            Assert.Equal(expectedSignal, TransferFunctions.PqInverseEotf(nits), 9);
        }

        [Theory]
        [InlineData(0.0623368657, 0.1)]
        [InlineData(0.1499457321, 1.0)]
        [InlineData(0.2996990924, 10.0)]
        [InlineData(0.5080784215, 100.0)]
        [InlineData(0.5806888810, 203.0)]
        [InlineData(0.7518270962, 1000.0)]
        [InlineData(0.9025723933, 4000.0)]
        [InlineData(1.0, 10000.0)]
        public void PqEotf_St2084ReferencePoints(double signal, double expectedNits)
        {
            Assert.Equal(expectedNits, TransferFunctions.PqEotf(signal), 6);
        }

        [Fact]
        public void SrgbInverseEotf_Bounds()
        {
            // Black level
            Assert.Equal(0.0, TransferFunctions.SrgbInverseEotf(0.0, 200.0, 0.0), 4);
            
            // White level (should map to 1.0 signal)
            Assert.Equal(1.0, TransferFunctions.SrgbInverseEotf(200.0, 200.0, 0.0), 4);
        }

        [Fact]
        public void Bt1886Eotf_ZeroBlack_MatchesPureGamma24()
        {
            double signal = 0.5;
            double white = 100.0;

            double actual = TransferFunctions.Bt1886Eotf(signal, white, blackLevel: 0.0);
            double expected = white * Math.Pow(signal, 2.4);

            Assert.Equal(expected, actual, 10);
        }

        [Fact]
        public void Bt1886Eotf_NonzeroBlack_MapsEndpoints()
        {
            double white = 100.0;
            double black = 0.1;

            Assert.Equal(black, TransferFunctions.Bt1886Eotf(0.0, white, black), 10);
            Assert.Equal(white, TransferFunctions.Bt1886Eotf(1.0, white, black), 10);
        }

        [Theory]
        [InlineData(0.05)]
        [InlineData(0.18)]
        [InlineData(0.5)]
        [InlineData(0.9)]
        public void Bt1886_RoundTrip_PreservesSignal(double signal)
        {
            double white = 100.0;
            double black = 0.1;

            double luminance = TransferFunctions.Bt1886Eotf(signal, white, black);
            double roundTrip = TransferFunctions.Bt1886InverseEotf(luminance, white, black);

            Assert.Equal(signal, roundTrip, 10);
        }

        [Fact]
        public void CalibrationTarget_Bt1886_DefaultBlack_PreservesPureGamma24Behavior()
        {
            double signal = 0.5;

            Assert.Equal(
                Math.Pow(signal, 2.4),
                StandardTargets.Rec709Gamma24.ApplyEotf(signal),
                10);
        }

        [Fact]
        public void CalibrationTarget_Bt1886_NonzeroBlack_UsesBlackLevelAwareCurve()
        {
            var target = new CalibrationTarget
            {
                Name = "BT.1886 black-aware test",
                RedPrimary = Chromaticity.Rec709Red,
                GreenPrimary = Chromaticity.Rec709Green,
                BluePrimary = Chromaticity.Rec709Blue,
                WhitePoint = Chromaticity.D65,
                TransferFunction = TransferFunctionType.Bt1886,
                ReferenceWhite = 100.0,
                BlackLevel = 0.1
            };

            Assert.Equal(0.001, target.ApplyEotf(0.0), 10);
            Assert.Equal(1.0, target.ApplyEotf(1.0), 10);

            double midLinear = target.ApplyEotf(0.5);
            double roundTrip = target.ApplyOetf(midLinear);
            Assert.Equal(0.5, roundTrip, 10);
        }
    }
}
