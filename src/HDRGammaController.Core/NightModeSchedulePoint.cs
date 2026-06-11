using System;

namespace HDRGammaController.Core
{
    public enum ScheduleTriggerType
    {
        FixedTime,
        Sunrise,
        Sunset
    }

    public class NightModeSchedulePoint
    {
        public ScheduleTriggerType TriggerType { get; set; } = ScheduleTriggerType.FixedTime;
        
        // For FixedTime
        public TimeSpan Time { get; set; }
        
        // For Sun triggers (e.g., -30 means 30 mins before sunset)
        public double OffsetMinutes { get; set; }
        
        public int TargetKelvin { get; set; } = 6500;
        public int FadeMinutes { get; set; } = 30;
        
        public TimeSpan GetTimeOfDay(double? lat, double? lon)
        {
            if (TriggerType == ScheduleTriggerType.FixedTime)
                return Time;
                
            if (lat.HasValue && lon.HasValue)
            {
                var result = SunCalculator.CalculateTodayDetailed(lat.Value, lon.Value);
                if (result.HasValidTimes)
                {
                    var baseTime = (TriggerType == ScheduleTriggerType.Sunrise) ? result.Sunrise : result.Sunset;
                    return baseTime.Add(TimeSpan.FromMinutes(OffsetMinutes));
                }
                // Polar day/night: fall through to the 7am/7pm fallback rather than
                // scheduling at the (0,0) or (0,24h) sentinels that confuse the fade logic.
            }

            return (TriggerType == ScheduleTriggerType.Sunrise)
                ? new TimeSpan(7, 0, 0)
                : new TimeSpan(19, 0, 0);
        }
    }
}
