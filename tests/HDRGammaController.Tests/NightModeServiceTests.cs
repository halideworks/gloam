using System;
using System.Linq;
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

        [Fact]
        public void NightModeAlgorithmOptions_DefaultOrder_PutsPerceptualThenUltraNight()
        {
            Assert.Equal(NightModeAlgorithm.Perceptual, NightModeAlgorithmOption.DefaultOptions[0].Value);
            Assert.Equal(NightModeAlgorithm.UltraNight, NightModeAlgorithmOption.DefaultOptions[1].Value);
        }

        [Fact]
        public void ManualOverride_ForcesConfiguredNightTemperature()
        {
            using var service = new NightModeService(new NightModeSettings());
            bool fired = false;
            service.BlendChanged += _ => fired = true;

            service.UpdateSettings(new NightModeSettings
            {
                Enabled = true,
                ManualOverrideEnabled = true,
                TemperatureKelvin = 3000
            });

            Assert.True(fired);
            Assert.Equal(3000, service.CurrentNightKelvin);
            Assert.True(service.IsNightModeActive);
        }

        [Fact]
        public void ManualOverride_UsesWarmestConfiguredSchedulePoint()
        {
            var settings = new NightModeSettings
            {
                TemperatureKelvin = 2700,
                Schedule =
                {
                    new NightModeSchedulePoint { TargetKelvin = 6500 },
                    new NightModeSchedulePoint { TargetKelvin = 3400 },
                    new NightModeSchedulePoint { TargetKelvin = 4200 }
                }
            };

            Assert.Equal(3400, settings.GetManualOverrideKelvin());
        }

        [Fact]
        public void AutoDetectConversion_AssignsDaylightToSunriseAndWarmthToSunset()
        {
            var settings = new NightModeSettings { TemperatureKelvin = 2700 };
            settings.EnsureSchedule(null, null);

            bool converted = settings.ConvertSimpleScheduleToSunTriggers();

            Assert.True(converted);
            Assert.Equal(ScheduleTriggerType.Sunrise, settings.Schedule.Single(p => p.TargetKelvin == 6500).TriggerType);
            Assert.Equal(ScheduleTriggerType.Sunset, settings.Schedule.Single(p => p.TargetKelvin == 2700).TriggerType);
            Assert.True(settings.UseAutoSchedule);
        }

        [Fact]
        public void AutoDetectConversion_RepairsPreviouslyReversedTwoPointSchedule()
        {
            var settings = new NightModeSettings
            {
                Schedule =
                {
                    new NightModeSchedulePoint { TriggerType = ScheduleTriggerType.Sunset, TargetKelvin = 6500 },
                    new NightModeSchedulePoint { TriggerType = ScheduleTriggerType.Sunrise, TargetKelvin = 2700 }
                }
            };

            settings.ConvertSimpleScheduleToSunTriggers();

            Assert.Equal(ScheduleTriggerType.Sunrise, settings.Schedule[0].TriggerType);
            Assert.Equal(ScheduleTriggerType.Sunset, settings.Schedule[1].TriggerType);
        }

        [Fact]
        public void PreviewKelvinOverride_UsesConfiguredNightModeAlgorithm()
        {
            var calibration = new CalibrationSettings { Algorithm = NightModeAlgorithm.Standard };
            var nightMode = new NightModeSettings
            {
                Algorithm = NightModeAlgorithm.AccurateCIE1931,
                UseUltraWarmMode = true
            };

            GammaApplyService.ApplyNightModeToCalibration(calibration, 3400, nightMode);

            Assert.Equal((3400 - 6500) / 70.0, calibration.Temperature, 12);
            Assert.Equal(NightModeAlgorithm.AccurateCIE1931, calibration.Algorithm);
            Assert.True(calibration.UseUltraWarmMode);
        }
    }
}
