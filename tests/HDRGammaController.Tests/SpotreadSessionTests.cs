using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class SpotreadSessionTests
    {
        [Fact]
        public void TryAcceptMeasuredXyz_FiniteNonNegativePhysicalSample_Passes()
        {
            var accepted = SpotreadSession.TryAcceptMeasuredXyz(new CieXyz(95.0, 100.0, 108.0), out var error);

            Assert.True(accepted, error);
        }

        [Fact]
        public void TryAcceptMeasuredXyz_NonFiniteSample_Fails()
        {
            var accepted = SpotreadSession.TryAcceptMeasuredXyz(new CieXyz(95.0, double.NaN, 108.0), out var error);

            Assert.False(accepted);
            Assert.Contains("non-finite", error);
        }

        [Fact]
        public void TryAcceptMeasuredXyz_NegativePhysicalSample_Fails()
        {
            var accepted = SpotreadSession.TryAcceptMeasuredXyz(new CieXyz(95.0, 100.0, -0.01), out var error);

            Assert.False(accepted);
            Assert.Contains("negative XYZ", error);
        }

        [Fact]
        public void TryAcceptMeasuredXyz_TinyNegativeRoundoff_Passes()
        {
            var accepted = SpotreadSession.TryAcceptMeasuredXyz(new CieXyz(-1e-7, 100.0, 108.0), out var error);

            Assert.True(accepted, error);
        }
    }
}
