using System;
using HDRGammaController.Core;
using HDRGammaController.ViewModels;
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

        [Fact]
        public void SchedulePointViewModel_ClampsEditableColorAndFadeValues()
        {
            var model = new NightModeSchedulePoint
            {
                TriggerType = ScheduleTriggerType.FixedTime,
                TargetKelvin = 6500,
                FadeMinutes = 30
            };
            var vm = new SchedulePointViewModel(model);

            vm.TargetKelvin = 10_000;
            vm.FadeMinutes = 10_000;

            Assert.Equal(NightModeSettings.MaxKelvin, model.TargetKelvin);
            Assert.Equal(NightModeSettings.MaxFadeMinutes, model.FadeMinutes);

            vm.TargetKelvin = 100;
            vm.FadeMinutes = -10;

            Assert.Equal(NightModeSettings.MinKelvin, model.TargetKelvin);
            Assert.Equal(0, model.FadeMinutes);
        }

        [Fact]
        public void SchedulePointViewModel_NormalizesTimeAndSunOffsetEdits()
        {
            var model = new NightModeSchedulePoint
            {
                TriggerType = ScheduleTriggerType.FixedTime,
                Time = TimeSpan.Zero
            };
            var vm = new SchedulePointViewModel(model);

            vm.DisplayTime = "2400";
            Assert.Equal(TimeSpan.Zero, model.Time);
            Assert.Equal("00:00", vm.DisplayTime);

            vm.TriggerType = ScheduleTriggerType.Sunset;
            vm.DisplayTime = "999m";

            Assert.Equal(NightModeSettings.MaxSunOffsetMinutes, model.OffsetMinutes);
            Assert.Equal($"+{NightModeSettings.MaxSunOffsetMinutes}m", vm.DisplayTime);
        }

        [Fact]
        public void NightModeScheduleViewModel_CommitLocation_ClampsTextAndSettings()
        {
            var settings = new NightModeSettings();
            var vm = new NightModeScheduleViewModel();
            vm.Initialize(settings);

            vm.LatitudeText = "999";
            vm.LongitudeText = "-999";
            vm.CommitLocation();

            Assert.Equal(90.0, settings.Latitude);
            Assert.Equal(-180.0, settings.Longitude);
            Assert.Equal("90.00", vm.LatitudeText);
            Assert.Equal("-180.00", vm.LongitudeText);
        }
    }
}
