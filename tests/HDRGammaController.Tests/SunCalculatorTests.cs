using Xunit;
using HDRGammaController.Core;
using System;

namespace HDRGammaController.Tests
{
    public class SunCalculatorTests
    {
        // Allow 5 minutes tolerance for sunrise/sunset calculations
        private static readonly TimeSpan TimeTolerance = TimeSpan.FromMinutes(5);

        #region Basic Functionality

        [Fact]
        public void Calculate_ReturnsValidTimes()
        {
            // New York City coordinates
            var (sunrise, sunset) = SunCalculator.Calculate(40.7128, -74.0060, new DateTime(2024, 6, 21), -4);

            Assert.True(sunrise > TimeSpan.Zero, "Sunrise should be positive");
            Assert.True(sunset > sunrise, "Sunset should be after sunrise");
            Assert.True(sunset < TimeSpan.FromHours(24), "Sunset should be before midnight");
        }

        [Fact]
        public void Calculate_SunriseBeforeSunset()
        {
            // Test various locations with appropriate timezone offsets
            var locations = new[]
            {
                (51.5074, -0.1278, 0),    // London (UTC+0)
                (40.7128, -74.0060, -5),  // New York (UTC-5)
                (35.6762, 139.6503, 9),   // Tokyo (UTC+9)
                (-33.8688, 151.2093, 11)  // Sydney (UTC+11)
            };

            foreach (var (lat, lon, tz) in locations)
            {
                var (sunrise, sunset) = SunCalculator.Calculate(lat, lon, new DateTime(2024, 3, 21), tz);
                Assert.True(sunset > sunrise, $"Sunset should be after sunrise at ({lat}, {lon})");
            }
        }

        #endregion

        #region Seasonal Variation Tests

        [Fact]
        public void Calculate_SummerSolstice_LongestDay_NorthernHemisphere()
        {
            // London on summer solstice (June 21) vs winter solstice (Dec 21)
            var summer = SunCalculator.Calculate(51.5074, -0.1278, new DateTime(2024, 6, 21), 0);
            var winter = SunCalculator.Calculate(51.5074, -0.1278, new DateTime(2024, 12, 21), 0);

            TimeSpan summerDayLength = summer.sunset - summer.sunrise;
            TimeSpan winterDayLength = winter.sunset - winter.sunrise;

            Assert.True(summerDayLength > winterDayLength,
                $"Summer day ({summerDayLength}) should be longer than winter day ({winterDayLength})");
        }

        [Fact]
        public void Calculate_WinterSolstice_LongestDay_SouthernHemisphere()
        {
            // Sydney on Dec 21 (summer there) vs June 21 (winter there)
            var decSolstice = SunCalculator.Calculate(-33.8688, 151.2093, new DateTime(2024, 12, 21), 11);
            var junSolstice = SunCalculator.Calculate(-33.8688, 151.2093, new DateTime(2024, 6, 21), 10);

            TimeSpan decDayLength = decSolstice.sunset - decSolstice.sunrise;
            TimeSpan junDayLength = junSolstice.sunset - junSolstice.sunrise;

            Assert.True(decDayLength > junDayLength,
                $"December day ({decDayLength}) should be longer than June day ({junDayLength}) in southern hemisphere");
        }

        [Fact]
        public void Calculate_Equinox_ApproximatelyEqualDayNight()
        {
            // On equinox, day and night should be approximately equal (12 hours each)
            // Use location near equator for cleaner results
            var equinox = SunCalculator.Calculate(0.0, 0.0, new DateTime(2024, 3, 20), 0);

            TimeSpan dayLength = equinox.sunset - equinox.sunrise;

            // Should be close to 12 hours (within 30 minutes for equator)
            Assert.InRange(dayLength.TotalHours, 11.5, 12.5);
        }

        #endregion

        #region Latitude Effects

        [Fact]
        public void Calculate_HighLatitude_LongerSummerDay()
        {
            // Compare Oslo (60°N) with Madrid (40°N) on summer solstice
            var oslo = SunCalculator.Calculate(59.9139, 10.7522, new DateTime(2024, 6, 21), 2);
            var madrid = SunCalculator.Calculate(40.4168, -3.7038, new DateTime(2024, 6, 21), 2);

            TimeSpan osloDayLength = oslo.sunset - oslo.sunrise;
            TimeSpan madridDayLength = madrid.sunset - madrid.sunrise;

            Assert.True(osloDayLength > madridDayLength,
                $"Higher latitude Oslo ({osloDayLength}) should have longer summer day than Madrid ({madridDayLength})");
        }

        [Fact]
        public void Calculate_ArcticCircle_MidnightSun()
        {
            // Near Arctic circle on summer solstice - extremely long day or midnight sun
            var arctic = SunCalculator.Calculate(66.5, 25.0, new DateTime(2024, 6, 21), 3);

            // Should return indication of midnight sun (sunset = 24:00)
            // or very long day (>22 hours)
            TimeSpan dayLength = arctic.sunset - arctic.sunrise;
            Assert.True(dayLength.TotalHours > 20 || arctic.sunset >= TimeSpan.FromHours(23),
                "Arctic circle should have midnight sun or very long day on summer solstice");
        }

        [Fact]
        public void Calculate_ArcticCircle_PolarNight()
        {
            // Near Arctic circle on winter solstice - polar night or very short day
            var arctic = SunCalculator.Calculate(68.0, 25.0, new DateTime(2024, 12, 21), 2);

            // Should return indication of polar night (both times zero) or very short day
            TimeSpan dayLength = arctic.sunset - arctic.sunrise;
            Assert.True(dayLength.TotalHours < 4 ||
                       (arctic.sunrise == TimeSpan.Zero && arctic.sunset == TimeSpan.Zero),
                "Arctic circle should have polar night or very short day on winter solstice");
        }

        #endregion

        #region Edge Cases

        [Fact]
        public void Calculate_Equator_ConsistentDayLength()
        {
            // At equator, day length should be relatively consistent year-round
            var jan = SunCalculator.Calculate(0.0, 0.0, new DateTime(2024, 1, 15), 0);
            var jul = SunCalculator.Calculate(0.0, 0.0, new DateTime(2024, 7, 15), 0);

            TimeSpan janDayLength = jan.sunset - jan.sunrise;
            TimeSpan julDayLength = jul.sunset - jul.sunrise;

            // Difference should be small (less than 30 minutes)
            Assert.True(Math.Abs((janDayLength - julDayLength).TotalMinutes) < 30,
                $"Equator day length should be consistent: Jan={janDayLength}, Jul={julDayLength}");
        }

        [Fact]
        public void Calculate_DateLineCrossing_HandlesCorrectly()
        {
            // Fiji (near date line, positive longitude)
            var fiji = SunCalculator.Calculate(-18.1416, 178.4419, new DateTime(2024, 6, 21), 12);

            Assert.True(fiji.sunrise > TimeSpan.Zero);
            Assert.True(fiji.sunset > fiji.sunrise);
        }

        [Fact]
        public void Calculate_NegativeLongitude_HandlesCorrectly()
        {
            // Los Angeles (negative longitude)
            var la = SunCalculator.Calculate(34.0522, -118.2437, new DateTime(2024, 6, 21), -7);

            Assert.True(la.sunrise > TimeSpan.Zero);
            Assert.True(la.sunset > la.sunrise);
        }

        [Fact]
        public void Calculate_NonFiniteInputs_FallsBackToFiniteTimes()
        {
            var result = SunCalculator.Calculate(
                double.NaN,
                double.PositiveInfinity,
                new DateTime(2024, 6, 21),
                double.NegativeInfinity);

            Assert.True(result.sunrise >= TimeSpan.Zero);
            Assert.True(result.sunrise < TimeSpan.FromHours(24));
            Assert.True(result.sunset >= TimeSpan.Zero);
            Assert.True(result.sunset < TimeSpan.FromHours(24));
        }

        #endregion

        #region Known Values (approximate)

        [Fact]
        public void Calculate_NewYork_SummerSolstice_ApproximateValues()
        {
            // NYC on summer solstice 2024 (EDT = UTC-4)
            // Expected: sunrise ~5:25 AM, sunset ~8:31 PM
            var nyc = SunCalculator.Calculate(40.7128, -74.0060, new DateTime(2024, 6, 21), -4);

            // Allow 10 minute tolerance
            Assert.InRange(nyc.sunrise.TotalHours, 5.2, 5.7);
            Assert.InRange(nyc.sunset.TotalHours, 20.3, 20.8);
        }

        [Fact]
        public void Calculate_London_WinterSolstice_ApproximateValues()
        {
            // London on winter solstice 2024 (GMT = UTC+0)
            // Expected: sunrise ~8:04 AM, sunset ~3:53 PM
            var london = SunCalculator.Calculate(51.5074, -0.1278, new DateTime(2024, 12, 21), 0);

            Assert.InRange(london.sunrise.TotalHours, 7.8, 8.3);
            Assert.InRange(london.sunset.TotalHours, 15.6, 16.2);
        }

        #endregion
    }
}
