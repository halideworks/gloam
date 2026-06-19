using HDRGammaController.Core;
using Xunit;

namespace HDRGammaController.Tests
{
    public class NightModeServiceTests
    {
        [Fact]
        public void FadeCadence_DefaultTransition_TargetsOneKelvinSteps()
        {
            double interval = NightModeService.CalculateFadeTickMilliseconds(6500, 2700, 30);

            Assert.InRange(interval, 473.6, 473.8);
        }

        [Fact]
        public void FadeCadence_ShortTransition_UsesRefreshRateBound()
        {
            double interval = NightModeService.CalculateFadeTickMilliseconds(6500, 1900, 1);

            Assert.InRange(interval, 16.6, 16.7);
        }

        [Theory]
        [InlineData(6500, 6500, 30)]
        [InlineData(6500, 2700, 0)]
        public void FadeCadence_NoEffectiveFade_UsesLowFrequencyBound(int start, int end, double minutes)
        {
            Assert.Equal(500, NightModeService.CalculateFadeTickMilliseconds(start, end, minutes));
        }
    }
}
