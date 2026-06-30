using HDRGammaController.Core;
using Xunit;

namespace HDRGammaController.Tests
{
    public class MonitorInfoTests
    {
        [Theory]
        [InlineData(200.0, 200.0)]
        [InlineData(20.0, MonitorInfo.MinSdrWhiteLevel)]
        [InlineData(5000.0, MonitorInfo.MaxSdrWhiteLevel)]
        [InlineData(double.NaN, MonitorInfo.DefaultSdrWhiteLevel)]
        [InlineData(double.PositiveInfinity, MonitorInfo.DefaultSdrWhiteLevel)]
        public void SanitizeSdrWhiteLevel_ReturnsFinitePlausibleValue(double input, double expected)
        {
            Assert.Equal(expected, MonitorInfo.SanitizeSdrWhiteLevel(input));
        }

        [Theory]
        [InlineData(1000.0, 1000.0)]
        [InlineData(0.0, 0.0)]
        [InlineData(-1.0, 0.0)]
        [InlineData(double.NaN, 0.0)]
        [InlineData(double.NegativeInfinity, 0.0)]
        public void SanitizeNonNegativeNits_ReturnsFiniteNonNegativeValue(double input, double expected)
        {
            Assert.Equal(expected, MonitorInfo.SanitizeNonNegativeNits(input));
        }
    }
}
