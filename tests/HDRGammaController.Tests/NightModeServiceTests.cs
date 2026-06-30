using System;
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

        [Fact]
        public void LegacyTemperatureSetter_ClampsNonFiniteAndOutOfRangeValues()
        {
            var settings = new NightModeSettings();

            settings.Temperature = double.NaN;
            Assert.Equal(NightModeSettings.DefaultNightKelvin, settings.TemperatureKelvin);

            settings.Temperature = -200;
            Assert.Equal(NightModeSettings.MinKelvin, settings.TemperatureKelvin);

            settings.Temperature = 200;
            Assert.Equal(NightModeSettings.MaxKelvin, settings.TemperatureKelvin);
        }

        [Fact]
        public void SchedulePoint_SanitizesTimeLocationAndOffset()
        {
            var point = new NightModeSchedulePoint
            {
                TriggerType = ScheduleTriggerType.FixedTime,
                Time = TimeSpan.FromHours(-1)
            };

            Assert.Equal(TimeSpan.FromHours(23), point.GetTimeOfDay(null, null));

            point.TriggerType = ScheduleTriggerType.Sunset;
            point.OffsetMinutes = double.PositiveInfinity;

            var time = point.GetTimeOfDay(double.NaN, double.NegativeInfinity);

            Assert.Equal(TimeSpan.FromHours(19), time);
        }
    }
}
