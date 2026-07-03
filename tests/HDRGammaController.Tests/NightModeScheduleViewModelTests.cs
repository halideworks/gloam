using System;
using System.Globalization;
using HDRGammaController.Core;
using HDRGammaController.ViewModels;
using Xunit;

namespace HDRGammaController.Tests
{
    public class NightModeScheduleViewModelTests
    {
        /// <summary>Runs an action with CurrentCulture temporarily switched.</summary>
        private static void WithCulture(string cultureName, Action action)
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(cultureName);
                action();
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        private static NightModeScheduleViewModel CreateInitializedViewModel()
        {
            var vm = new NightModeScheduleViewModel();
            vm.Initialize(new NightModeSettings());
            return vm;
        }

        [Fact]
        public void CommitLocation_InvariantDecimal_ParsesCorrectly_UnderCommaDecimalCulture()
        {
            // Regression: under fr-FR, current-culture parsing read "40.67" as 4067
            // (dot ignored as a group separator would be) and clamped it to garbage.
            WithCulture("fr-FR", () =>
            {
                var vm = CreateInitializedViewModel();
                vm.LatitudeText = "40.67";
                vm.LongitudeText = "-73.94";

                vm.CommitLocation();

                Assert.NotNull(vm.Latitude);
                Assert.NotNull(vm.Longitude);
                Assert.Equal(40.67, vm.Latitude!.Value, 2);
                Assert.Equal(-73.94, vm.Longitude!.Value, 2);
            });
        }

        [Fact]
        public void CommitLocation_CommaDecimal_ParsesCorrectly_UnderCommaDecimalCulture()
        {
            // A fr-FR user typing their native comma decimal must also work: the
            // invariant pass rejects "40,67" (AllowThousands is excluded), and the
            // current-culture fallback reads it as 40.67.
            WithCulture("fr-FR", () =>
            {
                var vm = CreateInitializedViewModel();
                vm.LatitudeText = "40,67";
                vm.LongitudeText = "-73,94";

                vm.CommitLocation();

                Assert.NotNull(vm.Latitude);
                Assert.NotNull(vm.Longitude);
                Assert.Equal(40.67, vm.Latitude!.Value, 2);
                Assert.Equal(-73.94, vm.Longitude!.Value, 2);
            });
        }

        [Fact]
        public void CommitLocation_CommaGroupSeparator_UnderDotDecimalCulture_DoesNotParseAsThousands()
        {
            // Under en-US, "40,67" must not silently become 4067: both parse passes
            // exclude AllowThousands, so the input is rejected and the location stays unset.
            WithCulture("en-US", () =>
            {
                var vm = CreateInitializedViewModel();
                vm.LatitudeText = "40,67";

                vm.CommitLocation();

                Assert.Null(vm.Latitude);
            });
        }

        [Theory]
        [InlineData("40.67", 40.67)]
        [InlineData("-73.94", -73.94)]
        [InlineData("0", 0.0)]
        public void TryParseCoordinate_InvariantInput_ParsesUnderAnyCulture(string text, double expected)
        {
            WithCulture("fr-FR", () =>
            {
                Assert.True(NightModeScheduleViewModel.TryParseCoordinate(text, out double value));
                Assert.Equal(expected, value, 6);
            });
        }

        [Fact]
        public void TryParseCoordinate_Garbage_ReturnsFalse()
        {
            Assert.False(NightModeScheduleViewModel.TryParseCoordinate("not a number", out _));
            Assert.False(NightModeScheduleViewModel.TryParseCoordinate("", out _));
            Assert.False(NightModeScheduleViewModel.TryParseCoordinate(null, out _));
        }
    }
}
