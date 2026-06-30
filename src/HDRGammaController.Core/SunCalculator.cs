using System;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Calculates sunrise and sunset times using the NOAA solar position algorithm.
    /// Accurate to within 1-2 minutes for most locations.
    /// </summary>
    public static class SunCalculator
    {
        private const double DegToRad = Math.PI / 180.0;
        private const double RadToDeg = 180.0 / Math.PI;

        /// <summary>
        /// Result of a sunrise/sunset calculation, including a flag for polar day/night where
        /// the returned sunrise/sunset TimeSpans are degenerate sentinels rather than real events.
        /// </summary>
        public readonly struct SunResult
        {
            public readonly TimeSpan Sunrise;
            public readonly TimeSpan Sunset;
            public readonly bool IsPolarNight;   // sun never rises above horizon today
            public readonly bool IsMidnightSun;  // sun never sets below horizon today

            public SunResult(TimeSpan sunrise, TimeSpan sunset, bool polarNight = false, bool midnightSun = false)
            {
                Sunrise = sunrise;
                Sunset = sunset;
                IsPolarNight = polarNight;
                IsMidnightSun = midnightSun;
            }

            public bool HasValidTimes => !IsPolarNight && !IsMidnightSun;
        }

        /// <summary>
        /// Calculate sunrise and sunset times for a given location and date.
        /// </summary>
        /// <param name="latitude">Latitude in degrees (-90 to 90)</param>
        /// <param name="longitude">Longitude in degrees (-180 to 180)</param>
        /// <param name="date">Date to calculate for</param>
        /// <param name="utcOffset">UTC offset in hours (e.g., -5 for EST)</param>
        /// <returns>Tuple of (sunrise, sunset) as local TimeSpan. Callers that need to
        /// distinguish polar day/night from real events should use <see cref="CalculateDetailed"/>.</returns>
        public static (TimeSpan sunrise, TimeSpan sunset) Calculate(
            double latitude, double longitude, DateTime date, double utcOffset)
        {
            var result = CalculateDetailed(latitude, longitude, date, utcOffset);
            return (result.Sunrise, result.Sunset);
        }

        /// <summary>
        /// Detailed version of <see cref="Calculate"/> that reports polar day / polar night
        /// explicitly instead of folding them into the (0,0)/(0,24h) sentinels.
        /// </summary>
        public static SunResult CalculateDetailed(
            double latitude, double longitude, DateTime date, double utcOffset)
        {
            latitude = ClampFinite(latitude, -90.0, 90.0, 0.0);
            longitude = ClampFinite(longitude, -180.0, 180.0, 0.0);
            utcOffset = ClampFinite(utcOffset, -14.0, 14.0, 0.0);

            // Julian day
            int year = date.Year;
            int month = date.Month;
            int day = date.Day;
            
            if (month <= 2)
            {
                year -= 1;
                month += 12;
            }
            
            int A = year / 100;
            int B = 2 - A + (A / 4);
            double JD = Math.Floor(365.25 * (year + 4716)) + 
                        Math.Floor(30.6001 * (month + 1)) + 
                        day + B - 1524.5;
            
            // Julian century
            double JC = (JD - 2451545.0) / 36525.0;
            
            // Geometric mean longitude of sun (degrees)
            double L0 = Mod360(280.46646 + JC * (36000.76983 + 0.0003032 * JC));
            
            // Geometric mean anomaly of sun (degrees)
            double M = Mod360(357.52911 + JC * (35999.05029 - 0.0001537 * JC));
            
            // Eccentricity of Earth's orbit
            double e = 0.016708634 - JC * (0.000042037 + 0.0000001267 * JC);
            
            // Sun's equation of center
            double C = Math.Sin(M * DegToRad) * (1.914602 - JC * (0.004817 + 0.000014 * JC)) +
                       Math.Sin(2 * M * DegToRad) * (0.019993 - 0.000101 * JC) +
                       Math.Sin(3 * M * DegToRad) * 0.000289;
            
            // Sun's true longitude
            double sunLon = L0 + C;
            
            // Sun's apparent longitude
            double omega = 125.04 - 1934.136 * JC;
            double lambda = sunLon - 0.00569 - 0.00478 * Math.Sin(omega * DegToRad);
            
            // Mean obliquity of ecliptic
            double obliq = 23.0 + (26.0 + (21.448 - JC * (46.8150 + JC * (0.00059 - JC * 0.001813))) / 60.0) / 60.0;
            
            // Corrected obliquity
            double obliqCorr = obliq + 0.00256 * Math.Cos(omega * DegToRad);
            
            // Sun's declination
            double decl = Math.Asin(Math.Sin(obliqCorr * DegToRad) * Math.Sin(lambda * DegToRad)) * RadToDeg;
            
            // Equation of time (minutes)
            double y = Math.Tan(obliqCorr / 2 * DegToRad);
            y *= y;
            double eqTime = 4 * RadToDeg * (
                y * Math.Sin(2 * L0 * DegToRad) -
                2 * e * Math.Sin(M * DegToRad) +
                4 * e * y * Math.Sin(M * DegToRad) * Math.Cos(2 * L0 * DegToRad) -
                0.5 * y * y * Math.Sin(4 * L0 * DegToRad) -
                1.25 * e * e * Math.Sin(2 * M * DegToRad)
            );
            
            // Hour angle for sunrise/sunset (sun at horizon)
            double zenith = 90.833; // Standard refraction-corrected zenith
            double cosHA = (Math.Cos(zenith * DegToRad) / 
                           (Math.Cos(latitude * DegToRad) * Math.Cos(decl * DegToRad))) -
                           Math.Tan(latitude * DegToRad) * Math.Tan(decl * DegToRad);
            
            // Handle polar day/night. Sentinels are retained for backward compatibility but
            // the flags on SunResult let callers react correctly instead of scheduling a
            // fade-to-zero transition at local midnight, which the old code effectively did.
            if (cosHA > 1)
            {
                return new SunResult(TimeSpan.Zero, TimeSpan.Zero, polarNight: true);
            }
            else if (cosHA < -1)
            {
                return new SunResult(TimeSpan.Zero, new TimeSpan(24, 0, 0), midnightSun: true);
            }
            
            double HA = Math.Acos(cosHA) * RadToDeg;
            
            // Solar noon (minutes from midnight)
            double solarNoon = (720 - 4 * longitude - eqTime + utcOffset * 60);
            
            // Sunrise and sunset (minutes from midnight)
            double sunriseMinutes = solarNoon - 4 * HA;
            double sunsetMinutes = solarNoon + 4 * HA;
            
            // Normalize to 0-1440 range
            sunriseMinutes = Mod1440(sunriseMinutes);
            sunsetMinutes = Mod1440(sunsetMinutes);

            return new SunResult(
                TimeSpan.FromMinutes(sunriseMinutes),
                TimeSpan.FromMinutes(sunsetMinutes));
        }

        /// <summary>
        /// Calculate sunrise/sunset for today at the given location, using local timezone.
        /// </summary>
        public static (TimeSpan sunrise, TimeSpan sunset) CalculateToday(double latitude, double longitude)
        {
            var now = DateTime.Now;
            var utcOffset = TimeZoneInfo.Local.GetUtcOffset(now).TotalHours;
            return Calculate(latitude, longitude, now, utcOffset);
        }

        /// <summary>
        /// Detailed variant of <see cref="CalculateToday"/>, with polar day/night flags.
        /// </summary>
        public static SunResult CalculateTodayDetailed(double latitude, double longitude)
        {
            var now = DateTime.Now;
            var utcOffset = TimeZoneInfo.Local.GetUtcOffset(now).TotalHours;
            return CalculateDetailed(latitude, longitude, now, utcOffset);
        }
        
        private static double Mod360(double x)
        {
            if (!double.IsFinite(x)) return 0.0;
            return x - 360 * Math.Floor(x / 360);
        }
        
        private static double Mod1440(double x)
        {
            if (!double.IsFinite(x)) return 0.0;
            while (x < 0) x += 1440;
            while (x >= 1440) x -= 1440;
            return x;
        }

        private static double ClampFinite(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }
}
