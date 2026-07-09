using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Night mode settings for automatic temperature/dimming scheduling.
    /// </summary>
    public class NightModeSettings
    {
        public const int MinKelvin = 1900;
        public const int MaxKelvin = 6500;
        public const int DefaultNightKelvin = 2700;
        public const int MaxFadeMinutes = 120;
        public const double MaxSunOffsetMinutes = 120.0;

        /// <summary>
        /// Whether night mode is enabled.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// When enabled, night mode ignores the schedule and applies TemperatureKelvin now.
        /// Enabled=false still wins and forces daylight.
        /// </summary>
        public bool ManualOverrideEnabled { get; set; } = false;

        /// <summary>
        /// Use automatic sunrise/sunset calculation based on location.
        /// </summary>
        public bool UseAutoSchedule { get; set; } = false;

        /// <summary>
        /// Latitude for sunrise/sunset calculation (-90 to 90).
        /// </summary>
        public double? Latitude { get; set; } = null;

        /// <summary>
        /// Longitude for sunrise/sunset calculation (-180 to 180).
        /// </summary>
        public double? Longitude { get; set; } = null;

        /// <summary>
        /// Start time for night mode (e.g., "21:00"). Used when UseAutoSchedule is false.
        /// </summary>
        public TimeSpan StartTime { get; set; } = new TimeSpan(21, 0, 0);

        /// <summary>
        /// End time for night mode (e.g., "07:00"). Used when UseAutoSchedule is false.
        /// </summary>
        public TimeSpan EndTime { get; set; } = new TimeSpan(7, 0, 0);

        /// <summary>
        /// Color temperature in Kelvin during night mode (1900-6500K, lower = warmer).
        /// Default 2700K matches warm incandescent lighting.
        /// </summary>
        public int TemperatureKelvin { get; set; } = 2700;

        /// <summary>
        /// Algorithm to use for color temperature transformation.
        /// </summary>
        public NightModeAlgorithm Algorithm { get; set; } = NightModeAlgorithm.Perceptual;

        /// <summary>
        /// Enable enhanced warmth curve below 2800K for more dramatic visual changes.
        /// When disabled, uses physically accurate color temperatures (subtle at very warm temps).
        /// </summary>
        public bool UseUltraWarmMode { get; set; } = false;

        /// <summary>
        /// Intensity of the Perceptual algorithm: fraction of full chromatic adaptation
        /// (0 = off/neutral, 1 = full colorimetric shift). Lower preserves more colour.
        /// </summary>
        public double PerceptualStrength { get; set; } = ColorAdjustments.DefaultPerceptualStrength;

        /// <summary>
        /// Constant-Y night mode: compensate the luminance the warm shift removes, within
        /// headroom (HDR headroom, or the room dimming created on SDR). Excluded for
        /// UltraNight, whose dimming is deliberate. Default off — normalize-to-max (dimmer
        /// warm white) remains the standard night behavior.
        /// </summary>
        public bool PreserveLuminance { get; set; } = false;

        public static double ClampPerceptualStrength(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : ColorAdjustments.DefaultPerceptualStrength;

        /// <summary>
        /// Legacy temperature as -50 to +50 scale. Converts to Kelvin internally.
        /// </summary>
        public double Temperature
        {
            get => (TemperatureKelvin - 6500) / 70.0;
            set => TemperatureKelvin = double.IsFinite(value)
                ? ClampKelvin((int)Math.Round(6500 + value * 70))
                : DefaultNightKelvin;
        }

        /// <summary>
        /// Fade duration in minutes (0 = instant, 60 = gradual).
        /// </summary>
        public int FadeMinutes { get; set; } = 30;

        public List<NightModeSchedulePoint> Schedule { get; set; } = new List<NightModeSchedulePoint>();

        public void EnsureSchedule(double? lat, double? lon)
        {
            TemperatureKelvin = ClampKelvin(TemperatureKelvin);
            FadeMinutes = ClampFadeMinutes(FadeMinutes);

            if (Schedule != null && Schedule.Count > 0) return;

            // Migrate legacy settings to schedule
            Schedule = new List<NightModeSchedulePoint>();

            // Point 1: At EndTime (or Sunrise), fade to Daylight (6500K) - morning first chronologically
            var sunrisePoint = new NightModeSchedulePoint
            {
                TriggerType = UseAutoSchedule ? ScheduleTriggerType.Sunrise : ScheduleTriggerType.FixedTime,
                Time = EndTime,
                OffsetMinutes = 0,
                TargetKelvin = 6500, // Daylight
                FadeMinutes = FadeMinutes
            };

            // Point 2: At StartTime (or Sunset), fade to Night Temp - evening second
            var sunsetPoint = new NightModeSchedulePoint
            {
                TriggerType = UseAutoSchedule ? ScheduleTriggerType.Sunset : ScheduleTriggerType.FixedTime,
                Time = StartTime,
                OffsetMinutes = 0,
                TargetKelvin = ClampKelvin(TemperatureKelvin),
                FadeMinutes = FadeMinutes
            };

            Schedule.Add(sunrisePoint);
            Schedule.Add(sunsetPoint);
        }

        /// <summary>
        /// Converts a simple two-point fixed schedule to sun triggers without relying on list
        /// order. The daylight/high-K point belongs at sunrise; the warm/low-K point belongs
        /// at sunset. This also repairs schedules produced by the old auto-detect bug that
        /// assigned the first row to Sunset and the second to Sunrise.
        /// </summary>
        public bool ConvertSimpleScheduleToSunTriggers()
        {
            if (Schedule == null || Schedule.Count != 2)
                return false;

            var daylight = Schedule.OrderByDescending(p => ClampKelvin(p.TargetKelvin)).First();
            var warm = Schedule.OrderBy(p => ClampKelvin(p.TargetKelvin)).First();
            if (ReferenceEquals(daylight, warm))
                return false;

            if (ClampKelvin(daylight.TargetKelvin) < 6000 || ClampKelvin(warm.TargetKelvin) >= 6000)
                return false;

            daylight.TriggerType = ScheduleTriggerType.Sunrise;
            daylight.OffsetMinutes = 0;
            warm.TriggerType = ScheduleTriggerType.Sunset;
            warm.OffsetMinutes = 0;
            UseAutoSchedule = true;
            return true;
        }

        /// <summary>
        /// Gets effective start/end times, using sunrise/sunset if auto mode enabled.
        /// Falls back to the manual StartTime/EndTime if we're in polar day/night, where
        /// the NOAA sentinels (0,0 / 0,24h) would otherwise drive a degenerate schedule.
        /// </summary>
        public (TimeSpan start, TimeSpan end) GetEffectiveTimes()
        {
            if (UseAutoSchedule && Latitude.HasValue && Longitude.HasValue)
            {
                var result = SunCalculator.CalculateTodayDetailed(Latitude.Value, Longitude.Value);
                if (result.HasValidTimes)
                {
                    // Night mode starts at sunset, ends at sunrise
                    return (result.Sunset, result.Sunrise);
                }
                // Polar day/night: fall back to the manual fallback times rather than
                // collapsing the schedule to (0,0) or (0,24h).
            }
            return (StartTime, EndTime);
        }

        public static int ClampKelvin(int kelvin) => Math.Clamp(kelvin, MinKelvin, MaxKelvin);

        public static int ClampFadeMinutes(int minutes) => Math.Clamp(minutes, 0, MaxFadeMinutes);

        public static double ClampOffsetMinutes(double minutes) =>
            double.IsFinite(minutes) ? Math.Clamp(minutes, -MaxSunOffsetMinutes, MaxSunOffsetMinutes) : 0.0;

        public static double? ClampLatitude(double? latitude) =>
            latitude.HasValue && double.IsFinite(latitude.Value)
                ? Math.Clamp(latitude.Value, -90.0, 90.0)
                : null;

        public static double? ClampLongitude(double? longitude) =>
            longitude.HasValue && double.IsFinite(longitude.Value)
                ? Math.Clamp(longitude.Value, -180.0, 180.0)
                : null;

        public static TimeSpan NormalizeTimeOfDay(TimeSpan time)
        {
            long ticks = time.Ticks % TimeSpan.TicksPerDay;
            if (ticks < 0) ticks += TimeSpan.TicksPerDay;
            return TimeSpan.FromTicks(ticks);
        }

        public int GetManualOverrideKelvin()
        {
            int scheduledWarmest = Schedule?
                .Where(p => p != null)
                .Select(p => ClampKelvin(p.TargetKelvin))
                .Where(k => k < MaxKelvin)
                .DefaultIfEmpty(0)
                .Min() ?? 0;

            return scheduledWarmest > 0
                ? scheduledWarmest
                : ClampKelvin(TemperatureKelvin);
        }
    }

    /// <summary>
    /// Service that manages automatic night mode scheduling with fade transitions.
    /// </summary>
    public class NightModeService : IDisposable
    {
        private System.Timers.Timer _timer;
        private NightModeSettings _settings;
        private DateTime? _pauseUntil = null;

        // Guards all mutable state below (settings reference + its cloned schedule,
        // pause window, fade bookkeeping, current kelvin, timer start/stop). The timer
        // Elapsed handler runs on a pool thread while the UI thread mutates settings, so
        // every read/write path takes this lock. BlendChanged is always raised OUTSIDE
        // the lock to avoid handler reentrancy/deadlock.
        private readonly object _stateLock = new();
        private bool _disposed;

        /// <summary>
        /// Fired exactly once per effective state change (kelvin moved or settings changed).
        /// Subscribers re-apply gamma and refresh UI. Value is 1.0 while night mode is in
        /// effect, 0 when forced back to day. Firing more than once per change causes
        /// redundant dispwin invocations, which the user sees as flicker.
        /// </summary>
        public event Action<double>? BlendChanged;

        public int CurrentNightKelvin
        {
            get { lock (_stateLock) { return _currentNightKelvin; } }
        }

        public bool IsNightModeActive
        {
            get { lock (_stateLock) { return _currentNightKelvin < 6450; } }
        }

        private int _currentNightKelvin = 6500;

        public NightModeService(NightModeSettings settings)
        {
            _settings = CloneSettings(settings);

            // One-shot timer, we restart it manually with dynamic intervals
            _timer = new System.Timers.Timer(1000);
            _timer.AutoReset = false;
            _timer.Elapsed += OnTimerElapsed;
        }

        public void PauseUntil(DateTime until)
        {
            double? blend;
            lock (_stateLock)
            {
                _pauseUntil = until;
                blend = UpdateStateLocked(); // Immediate apply
            }
            if (blend.HasValue) BlendChanged?.Invoke(blend.Value);
        }

        public void UpdateSettings(NightModeSettings newSettings)
        {
            // Values captured under the lock; events are raised after release.
            double? blend = null;
            bool forceReapply = false;

            lock (_stateLock)
            {
                // If specific settings changed (like toggle/times), force immediate re-eval
                bool wasEnabled = _settings.Enabled;

                // Store a defensive deep copy so the schedule editor cannot mutate the
                // List the timer enumerates out from under us ('Collection was modified').
                _settings = CloneSettings(newSettings);

                if (_settings.Enabled && !wasEnabled)
                {
                    blend = StartLocked();
                }
                else
                {
                    // Force an update to catch new times/durations immediately
                    blend = UpdateStateLocked();
                    ScheduleNextTickLocked();

                    // Re-apply even when the kelvin didn't move — other settings that affect
                    // the output may have changed (e.g. UseUltraWarmMode, Algorithm).
                    if (_settings.Enabled && !blend.HasValue)
                    {
                        forceReapply = true;
                    }
                }
            }

            if (blend.HasValue) BlendChanged?.Invoke(blend.Value);
            else if (forceReapply) BlendChanged?.Invoke(1.0);
        }

        public void Start()
        {
            double? blend;
            lock (_stateLock)
            {
                blend = StartLocked();
            }
            if (blend.HasValue) BlendChanged?.Invoke(blend.Value);
        }

        /// <summary>Caller must hold <see cref="_stateLock"/>. Returns the blend value to fire, or null.</summary>
        private double? StartLocked()
        {
            if (!_settings.Enabled) return null;
            double? blend = UpdateStateLocked();
            ScheduleNextTickLocked();
            return blend;
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                _timer.Stop();
            }
        }

        /// <summary>
        /// Recomputes the current night-mode state immediately and reschedules the tick.
        /// Callers should invoke this on resume-from-sleep and on system clock/date changes
        /// (SystemEvents.TimeChanged): the one-shot timer may have slept through a scheduled
        /// trigger, and cached sun times may belong to the wrong day — both otherwise leave a
        /// stale kelvin applied until the next natural tick.
        /// </summary>
        public void Refresh()
        {
            double? blend;
            lock (_stateLock)
            {
                if (_disposed) return;

                // The clock or calendar day may have moved underneath us; drop the resolved
                // schedule so sun times are recomputed for the new "today".
                _resolvedSchedule = null;

                blend = UpdateStateLocked();
                ScheduleNextTickLocked();
            }
            if (blend.HasValue) BlendChanged?.Invoke(blend.Value);
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            double? blend;
            lock (_stateLock)
            {
                if (_disposed) return;
                blend = UpdateStateLocked();
                ScheduleNextTickLocked();
            }
            if (blend.HasValue) BlendChanged?.Invoke(blend.Value);
        }

        // Tick adaptively while a fade is interpolating and sleep while idle. The cadence
        // targets a constant perceptual step per update (see MaxMiredStepPerTick), so short
        // and long transitions are both smooth.
        private const double MinFadeTickMs = 1000.0 / 60.0;
        private const double MaxFadeTickMs = 500;
        private const double IdleTickMs = 60000;

        // Fades interpolate in mired (reciprocal Kelvin), so tick density must be based on
        // mired distance too: 6500→6000K spans ~13 mired while 2400→1900K spans ~110 — the
        // same Kelvin distance, wildly different perceptual distances. 0.05 mired ≈ 1 K near
        // 4500K, matching the old "≤1 K per update" smoothness target mid-transition.
        // Since 3.5 this is only the FALLBACK: the live step ceiling comes from
        // JndPacedFade.ComputeMaxStepMired (≤ 0.5 ΔE ITP per write at the current operating
        // point), which prices in the active algorithm's luminance behavior as well.
        private const double MaxMiredStepPerTick = JndPacedFade.FallbackStepMired;

        // The apply pipeline coalesces hardware writes to one per 250 ms
        // (GammaApplyService's coalescer floor) — the realized per-write step can never be
        // finer than the fade rate × this floor, whatever the timer cadence asks for.
        private const double HardwareWriteFloorMs = 250.0;

        private bool _inFadeWindow;
        private double _fadeTickMs = MaxFadeTickMs;
        private double _msToNextTrigger = double.MaxValue;
        private bool _supraJndLoggedThisFade;

        // Resolved (time-sorted) schedule cache. Resolving a sun-trigger point runs the full
        // NOAA ephemeris math, and the old code redid it for every point on every tick. The
        // resolved times only change when the settings object is replaced (UpdateSettings
        // clones wholesale, so reference identity is a sufficient key) or the calendar day
        // rolls over. Refresh() also drops it because a clock change moves "today".
        // Guarded by _stateLock.
        private List<(TimeSpan Time, NightModeSchedulePoint Point)>? _resolvedSchedule;
        private NightModeSettings? _resolvedScheduleSettings;
        private DateTime _resolvedScheduleDate;

        /// <summary>Caller must hold <see cref="_stateLock"/>.</summary>
        private void ScheduleNextTickLocked()
        {
            if (_disposed) return;
            if (!_settings.Enabled || _settings.ManualOverrideEnabled)
            {
                _timer.Stop();
                return;
            }

            _timer.Interval = _inFadeWindow
                ? _fadeTickMs
                : Math.Clamp(_msToNextTrigger, MaxFadeTickMs, IdleTickMs);
            _timer.Start();
        }

        /// <summary>
        /// Caller must hold <see cref="_stateLock"/>.
        /// </summary>
        /// <returns>The blend value to fire via BlendChanged after releasing the lock, or null if no change.</returns>
        private double? UpdateStateLocked()
        {
            if (!_settings.Enabled)
            {
                return ForceDayModeLocked();
            }

            if (_settings.ManualOverrideEnabled)
            {
                _inFadeWindow = false;
                _msToNextTrigger = double.MaxValue;
                int manualKelvin = _settings.GetManualOverrideKelvin();
                if (manualKelvin != _currentNightKelvin)
                {
                    _currentNightKelvin = manualKelvin;
                    return 1.0;
                }
                return null;
            }

            if (_pauseUntil.HasValue)
            {
                if (DateTime.Now < _pauseUntil.Value)
                {
                    return ForceDayModeLocked();
                }
                _pauseUntil = null; // Expired
            }

            _settings.EnsureSchedule(_settings.Latitude, _settings.Longitude);

            int targetKelvin = CalculateCurrentKelvinLocked();

            // Kelvin is integral, so equality is the only useful dedupe here. A larger
            // threshold turns a gradual fade into periodic visible steps.
            if (targetKelvin != _currentNightKelvin)
            {
                _currentNightKelvin = targetKelvin;
                return 1.0;
            }
            return null;
        }

        /// <summary>Caller must hold <see cref="_stateLock"/>.</summary>
        private int CalculateCurrentKelvinLocked()
        {
            var now = DateTime.Now;
            var timeOfDay = now.TimeOfDay;

            // Recomputed below; default to "no fade, nothing scheduled".
            _inFadeWindow = false;
            _fadeTickMs = MaxFadeTickMs;
            _msToNextTrigger = double.MaxValue;

            // 1. Resolve all points to absolute TimeSpans for today (cached per settings
            // instance and calendar day — sun-trigger resolution is ephemeris math we must
            // not redo per point per tick).
            var points = _settings.Schedule;
            if (points == null || points.Count == 0) return 6500;

            var resolvedPoints = GetResolvedScheduleLocked(now.Date, points);

            // 2. Find the last point that occurred (Time <= Now)
            int currentIndex = -1;
            for (int i = 0; i < resolvedPoints.Count; i++)
            {
                if (resolvedPoints[i].Time <= timeOfDay)
                {
                    currentIndex = i;
                }
            }

            // 3. Identify Current Point and Previous Point
            // If current is -1, we are in the early morning before the first point of the day.
            // Our "current state" is determined by the LAST point of Yesterday.

            (TimeSpan Time, NightModeSchedulePoint Point) currentContext;
            (TimeSpan Time, NightModeSchedulePoint Point) previousContext;

            if (currentIndex == -1)
            {
                // Current context is the last point of the list (acting as yesterday's end)
                // Its trigger time was yesterday.
                var last = resolvedPoints[resolvedPoints.Count - 1];
                currentContext = (last.Time - TimeSpan.FromHours(24), last.Point);

                // Prev would be the one before that
                var prev = resolvedPoints.Count > 1 ? resolvedPoints[resolvedPoints.Count - 2] : last;
                if (resolvedPoints.Count > 1)
                     previousContext = (prev.Time - TimeSpan.FromHours(24), prev.Point);
                else previousContext = (prev.Time - TimeSpan.FromHours(48), prev.Point); // Edge case 1 point
            }
            else
            {
                currentContext = resolvedPoints[currentIndex];

                // Previous is index - 1. If index is 0, it's the last point of Yesterday.
                if (currentIndex > 0)
                {
                    previousContext = resolvedPoints[currentIndex - 1];
                }
                else
                {
                    var last = resolvedPoints[resolvedPoints.Count - 1];
                    previousContext = (last.Time - TimeSpan.FromHours(24), last.Point);
                }
            }

            // 4. Calculate interpolation. A point defines the transition TO its own target:
            // at currentContext.Time we start fading from the previous point's target to
            // currentContext's target over currentContext's FadeMinutes.

            var targetPoint = currentContext.Point;
            var startKelvin = previousContext.Point.TargetKelvin;
            var endKelvin = targetPoint.TargetKelvin;

            // Tell the timer when the next scheduled trigger fires so the idle tick can
            // sleep right up to it instead of polling.
            var nextTrigger = resolvedPoints[(currentIndex + 1 + resolvedPoints.Count) % resolvedPoints.Count].Time;
            var untilNext = nextTrigger - timeOfDay;
            if (untilNext <= TimeSpan.Zero) untilNext += TimeSpan.FromHours(24);
            _msToNextTrigger = untilNext.TotalMilliseconds;

            // Check if we are inside the fade window (starts at currentContext.Time)
            var timeSinceTrigger = timeOfDay - currentContext.Time;
            if (timeSinceTrigger < TimeSpan.Zero) timeSinceTrigger += TimeSpan.FromHours(24);

            double fadeMinutes = targetPoint.FadeMinutes;
            if (timeSinceTrigger.TotalMinutes < fadeMinutes && fadeMinutes > 0)
            {
                _inFadeWindow = true;
                double progress = timeSinceTrigger.TotalMinutes / fadeMinutes;
                progress = Math.Clamp(progress, 0.0, 1.0);
                int interpolatedKelvin = InterpolateKelvinInMired(startKelvin, endKelvin, progress);

                // JND pacing: the step ceiling is evaluated at the CURRENT operating point,
                // so it adapts along the fade (cost: one CAT16/locus evaluation × a few
                // probes at ≤ 2–4 Hz — negligible, no caching needed).
                double maxStepMired = JndPacedFade.ComputeMaxStepMired(
                    interpolatedKelvin, _settings.Algorithm, _settings.PerceptualStrength,
                    _settings.UseUltraWarmMode, _settings.PreserveLuminance);
                _fadeTickMs = CalculateFadeTickMilliseconds(startKelvin, endKelvin, fadeMinutes, maxStepMired);

                // Duration wins over imperceptibility: when the fade rate at the ~4
                // writes/sec hardware floor must exceed the JND ceiling, say so once per
                // fade window instead of silently stretching the user's schedule.
                double distanceMired = Math.Abs(1e6 / endKelvin - 1e6 / startKelvin);
                double ratePerMs = distanceMired / (fadeMinutes * 60_000.0);
                if (!_supraJndLoggedThisFade && ratePerMs * HardwareWriteFloorMs > maxStepMired)
                {
                    _supraJndLoggedThisFade = true;
                    Log.Info(
                        $"NightModeService: fade {startKelvin}K→{endKelvin}K over {fadeMinutes:F0} min " +
                        $"outpaces JND pacing (needs {ratePerMs * HardwareWriteFloorMs:F3} mired/write, " +
                        $"ceiling {maxStepMired:F3}); the schedule wins and steps may be briefly perceptible.");
                }

                return interpolatedKelvin;
            }

            // Otherwise we have arrived
            _supraJndLoggedThisFade = false;
            return endKelvin;
        }

        /// <summary>Caller must hold <see cref="_stateLock"/>.</summary>
        private List<(TimeSpan Time, NightModeSchedulePoint Point)> GetResolvedScheduleLocked(
            DateTime today, List<NightModeSchedulePoint> points)
        {
            if (_resolvedSchedule != null &&
                ReferenceEquals(_resolvedScheduleSettings, _settings) &&
                _resolvedScheduleDate == today &&
                _resolvedSchedule.Count == points.Count)
            {
                return _resolvedSchedule;
            }

            var resolved = new List<(TimeSpan Time, NightModeSchedulePoint Point)>(points.Count);
            foreach (var p in points)
            {
                resolved.Add((p.GetTimeOfDay(_settings.Latitude, _settings.Longitude), p));
            }
            resolved.Sort((a, b) => a.Time.CompareTo(b.Time));

            _resolvedSchedule = resolved;
            _resolvedScheduleSettings = _settings;
            _resolvedScheduleDate = today;
            return resolved;
        }

        /// <summary>
        /// Interpolates a fade in MIRED space (reciprocal Kelvin). CCT is a reciprocal
        /// quantity — equal Kelvin steps are perceptually much larger at the warm end — so a
        /// linear-Kelvin lerp front-loads the perceived change into the cool half of the
        /// transition. Constant mired rate is the perceptually uniform fade.
        /// </summary>
        public static int InterpolateKelvinInMired(int startKelvin, int endKelvin, double progress)
        {
            progress = double.IsFinite(progress) ? Math.Clamp(progress, 0.0, 1.0) : 1.0;
            if (startKelvin <= 0 || endKelvin <= 0) return endKelvin;

            double startMired = 1e6 / startKelvin;
            double endMired = 1e6 / endKelvin;
            double mired = startMired + (endMired - startMired) * progress;

            return mired > 0.0 ? (int)Math.Round(1e6 / mired) : endKelvin;
        }

        internal static double CalculateFadeTickMilliseconds(int startKelvin, int endKelvin, double fadeMinutes)
            => CalculateFadeTickMilliseconds(startKelvin, endKelvin, fadeMinutes, MaxMiredStepPerTick);

        internal static double CalculateFadeTickMilliseconds(
            int startKelvin, int endKelvin, double fadeMinutes, double maxStepMired)
        {
            if (startKelvin <= 0 || endKelvin <= 0 || fadeMinutes <= 0) return MaxFadeTickMs;
            if (!double.IsFinite(maxStepMired) || maxStepMired <= 0) maxStepMired = MaxMiredStepPerTick;

            // The fade advances at a constant mired rate (see InterpolateKelvinInMired), so
            // the tick interval that yields a fixed perceptual step per update is derived
            // from the mired distance, not the Kelvin distance. The step ceiling comes from
            // JND pacing (JndPacedFade) with the historical 0.05 mired as fallback contract.
            double distanceMired = Math.Abs(1e6 / endKelvin - 1e6 / startKelvin);
            if (distanceMired <= 0) return MaxFadeTickMs;

            double millisecondsPerMired = fadeMinutes * 60_000.0 / distanceMired;
            return Math.Clamp(millisecondsPerMired * maxStepMired, MinFadeTickMs, MaxFadeTickMs);
        }

        /// <summary>
        /// Caller must hold <see cref="_stateLock"/>.
        /// </summary>
        /// <returns>The blend value to fire via BlendChanged after releasing the lock, or null if no change.</returns>
        private double? ForceDayModeLocked()
        {
            _inFadeWindow = false;
            if (_currentNightKelvin != 6500)
            {
                _currentNightKelvin = 6500;
                return 0; // 0 = day
            }
            return null;
        }

        /// <summary>
        /// Defensive deep copy of settings and its schedule so the caller (the editor) cannot
        /// mutate the List/entries the timer enumerates after handing them to us.
        /// </summary>
        internal static NightModeSettings CloneSettings(NightModeSettings source)
        {
            var clone = new NightModeSettings
            {
                Enabled = source.Enabled,
                ManualOverrideEnabled = source.ManualOverrideEnabled,
                UseAutoSchedule = source.UseAutoSchedule,
                Latitude = NightModeSettings.ClampLatitude(source.Latitude),
                Longitude = NightModeSettings.ClampLongitude(source.Longitude),
                StartTime = NightModeSettings.NormalizeTimeOfDay(source.StartTime),
                EndTime = NightModeSettings.NormalizeTimeOfDay(source.EndTime),
                TemperatureKelvin = NightModeSettings.ClampKelvin(source.TemperatureKelvin),
                Algorithm = source.Algorithm,
                UseUltraWarmMode = source.UseUltraWarmMode,
                PerceptualStrength = NightModeSettings.ClampPerceptualStrength(source.PerceptualStrength),
                PreserveLuminance = source.PreserveLuminance,
                FadeMinutes = NightModeSettings.ClampFadeMinutes(source.FadeMinutes),
                Schedule = new List<NightModeSchedulePoint>()
            };

            if (source.Schedule != null)
            {
                foreach (var p in source.Schedule)
                {
                    clone.Schedule.Add(new NightModeSchedulePoint
                    {
                        TriggerType = p.TriggerType,
                        Time = NightModeSettings.NormalizeTimeOfDay(p.Time),
                        OffsetMinutes = NightModeSettings.ClampOffsetMinutes(p.OffsetMinutes),
                        TargetKelvin = NightModeSettings.ClampKelvin(p.TargetKelvin),
                        FadeMinutes = NightModeSettings.ClampFadeMinutes(p.FadeMinutes)
                    });
                }
            }

            return clone;
        }


        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed) return;
                _disposed = true;
                _timer.Stop();
                _timer.Dispose();
            }
        }
    }
}
