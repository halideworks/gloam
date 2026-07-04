using System;
using HDRGammaController.Core;
using HDRGammaController.ViewModels;
using Xunit;

namespace HDRGammaController.Tests
{
    public class DashboardViewModelTests
    {
        // "Pause until morning" must target the NEXT 7 AM, not unconditionally
        // tomorrow's: clicked at 1 AM it should pause ~6 hours, not ~30.

        [Fact]
        public void NextMorning_AfterMidnightBeforeSeven_TargetsTodaySevenAm()
        {
            var now = new DateTime(2026, 7, 3, 1, 0, 0);
            Assert.Equal(new DateTime(2026, 7, 3, 7, 0, 0), DashboardViewModel.NextMorning(now));
        }

        [Fact]
        public void NextMorning_InTheEvening_TargetsTomorrowSevenAm()
        {
            var now = new DateTime(2026, 7, 3, 22, 30, 0);
            Assert.Equal(new DateTime(2026, 7, 4, 7, 0, 0), DashboardViewModel.NextMorning(now));
        }

        [Fact]
        public void NextMorning_ExactlySevenAm_TargetsTomorrow()
        {
            // At exactly 7 AM "until morning" means the next one, not a zero-length pause.
            var now = new DateTime(2026, 7, 3, 7, 0, 0);
            Assert.Equal(new DateTime(2026, 7, 4, 7, 0, 0), DashboardViewModel.NextMorning(now));
        }

        [Fact]
        public void NextMorning_JustAfterSevenAm_TargetsTomorrow()
        {
            var now = new DateTime(2026, 7, 3, 7, 0, 1);
            Assert.Equal(new DateTime(2026, 7, 4, 7, 0, 0), DashboardViewModel.NextMorning(now));
        }

        [Fact]
        public void FormatEffectiveTemperatureText_ComposesAdjustmentsInMiredSpace()
        {
            // Base temperature, per-monitor offset and night shift all compose in mired
            // space in the apply path. The dashboard card must report that same result
            // rather than the old linear scale sum.
            double baseScale = -10.0;
            double offsetScale = -10.0;
            double nightShiftScale = -20.0;

            double expectedScale = ColorAdjustments.ComposeTemperatureScaleMired(
                ColorAdjustments.ComposeTemperatureScaleMired(baseScale, offsetScale),
                nightShiftScale);
            string expected = $"{ColorAdjustments.TemperatureScaleToKelvin(expectedScale)}K (Night)";

            string actual = DashboardViewModel.FormatEffectiveTemperatureText(
                baseScale,
                offsetScale,
                nightShiftScale,
                nightModeActive: true);

            Assert.Equal(expected, actual);
            Assert.NotEqual("3700K (Night)", actual);
        }
    }
}
