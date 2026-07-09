using System;
using System.Runtime.CompilerServices;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Per-monitor calibration settings for advanced adjustments.
    /// </summary>
    public class CalibrationSettings
    {
        public const double MinimumTemperatureScale = (1900.0 - 6500.0) / 70.0;
        public const double MaximumTemperatureScale = 50.0;

        /// <summary>
        /// Optional 3D LUT from colorimeter calibration to use as the base correction.
        /// User adjustments (temp, tint, brightness) are applied on top of this.
        /// </summary>
        public Lut3D? MeasuredCorrectionLut { get; set; }

        /// <summary>
        /// Optional reference to the calibration profile that generated the correction LUT.
        /// </summary>
        public Guid? CalibrationProfileId { get; set; }

        /// <summary>
        /// Brightness level (10-100%). Uses perceptual compression to preserve shadows.
        /// </summary>
        public double Brightness { get; set; } = 100.0;
        
        /// <summary>
        /// Color temperature adjustment. The UI slider uses -50..+50 (mapped to 2700K..10000K
        /// around a 6500K neutral, 70 K per unit), but the field itself accepts an extended
        /// range down to about -65.7 so night-mode schedules and per-monitor offsets can stack
        /// past the slider without being clipped mid-pipeline. <see cref="GetTemperatureMultipliers"/>
        /// re-clamps to 1000..10000 K when resolving the RGB multipliers.
        /// Negative = warmer (more red/yellow), Positive = cooler (more blue).
        /// </summary>
        public double Temperature { get; set; } = 0.0;
        
        /// <summary>
        /// Temperature offset specific to this monitor (to match other monitors).
        /// </summary>
        public double TemperatureOffset { get; set; } = 0.0;
        
        /// <summary>
        /// Tint adjustment (-50 to +50).
        /// Negative = more green, Positive = more magenta.
        /// </summary>
        public double Tint { get; set; } = 0.0;
        
        /// <summary>
        /// Red channel gain multiplier (0.5 to 1.5).
        /// </summary>
        public double RedGain { get; set; } = 1.0;
        
        /// <summary>
        /// Green channel gain multiplier (0.5 to 1.5).
        /// </summary>
        public double GreenGain { get; set; } = 1.0;
        
        /// <summary>
        /// Blue channel gain multiplier (0.5 to 1.5).
        /// </summary>
        public double BlueGain { get; set; } = 1.0;
        
        /// <summary>
        /// Red channel offset/lift (-0.1 to +0.1).
        /// </summary>
        public double RedOffset { get; set; } = 0.0;
        
        /// <summary>
        /// Green channel offset/lift (-0.1 to +0.1).
        /// </summary>
        public double GreenOffset { get; set; } = 0.0;
        
        /// <summary>
        /// Blue channel offset/lift (-0.1 to +0.1).
        /// </summary>
        public double BlueOffset { get; set; } = 0.0;
        
        /// <summary>
        /// Algorithm to use for temperature adjustment.
        /// </summary>
        public NightModeAlgorithm Algorithm { get; set; } = NightModeAlgorithm.Perceptual;

        /// <summary>
        /// Optional per-monitor CCSS spectral sample used to estimate RGB melanopic weights
        /// for Ultra Night. A matching display spectrum improves the amber/green tradeoff.
        /// </summary>
        public string? NightModeCcssPath { get; set; }

        /// <summary>
        /// Parsed coefficients for <see cref="NightModeCcssPath"/>. Runtime-only; populated by
        /// the apply service so LUT generation does not do file I/O.
        /// </summary>
        public NightMelanopicCoefficients? NightMelanopicCoefficients { get; set; }

        /// <summary>
        /// Enable enhanced warmth curve below 2800K for more dramatic visual changes.
        /// Only applies when Algorithm is Standard.
        /// </summary>
        public bool UseUltraWarmMode { get; set; } = false;

        /// <summary>
        /// Strength of the <see cref="NightModeAlgorithm.Perceptual"/> shift: fraction of full
        /// chromatic adaptation (0 = neutral/off, 1 = full colorimetric = Accurate). Lower values
        /// preserve more colour and reduce blue less. Only applies when Algorithm is Perceptual.
        /// </summary>
        public double PerceptualStrength { get; set; } = ColorAdjustments.DefaultPerceptualStrength;

        /// <summary>
        /// If true, uses standard linear dimming instead of perceptual (gamma-lift) dimming.
        /// </summary>
        public bool UseLinearBrightness { get; set; } = false;

        /// <summary>
        /// Constant-Y night mode: compensate the luminance the warm shift removes, within
        /// headroom (see <see cref="ColorAdjustments.RescaleToConstantLuminance"/>). Does not
        /// apply to UltraNight, whose dimming is deliberate.
        /// </summary>
        public bool PreserveNightLuminance { get; set; } = false;

        /// <summary>
        /// Luminance ceiling for <see cref="PreserveNightLuminance"/>, relative to SDR white
        /// in the wire signal. Runtime-only; populated by the apply service from the
        /// monitor's HDR headroom (HdrPeakNits / SdrWhiteLevel, capped) — 1.0 on the SDR
        /// path, whose GDI ramp cannot represent boost. Not persisted.
        /// </summary>
        public double NightLuminanceCeiling { get; set; } = 1.0;

        /// <summary>
        /// Returns true if any adjustments are applied (non-default values).
        /// </summary>
        public bool HasAdjustments =>
            MeasuredCorrectionLut != null ||
            IsAdjusted(Brightness, 100.0, 0.01) ||
            IsAdjusted(Temperature, 0.0, 0.01) ||
            IsAdjusted(TemperatureOffset, 0.0, 0.01) ||
            IsAdjusted(Tint, 0.0, 0.01) ||
            IsAdjusted(RedGain, 1.0, 0.001) ||
            IsAdjusted(GreenGain, 1.0, 0.001) ||
            IsAdjusted(BlueGain, 1.0, 0.001) ||
            IsAdjusted(RedOffset, 0.0, 0.001) ||
            IsAdjusted(GreenOffset, 0.0, 0.001) ||
            IsAdjusted(BlueOffset, 0.0, 0.001);

        /// <summary>
        /// Returns true if a measured 3D LUT calibration is applied.
        /// </summary>
        public bool HasMeasuredCalibration => MeasuredCorrectionLut != null;
        
        /// <summary>
        /// Creates a default (no adjustment) calibration.
        /// </summary>
        public static CalibrationSettings Default => new CalibrationSettings();
        
        /// <summary>
        /// Creates a copy of this settings object.
        /// </summary>
        public CalibrationSettings Clone() => new CalibrationSettings
        {
            MeasuredCorrectionLut = this.MeasuredCorrectionLut, // Reference, not deep copy
            CalibrationProfileId = this.CalibrationProfileId,
            Brightness = this.Brightness,
            UseLinearBrightness = this.UseLinearBrightness,
            Temperature = this.Temperature,
            TemperatureOffset = this.TemperatureOffset,
            Tint = this.Tint,
            RedGain = this.RedGain,
            GreenGain = this.GreenGain,
            BlueGain = this.BlueGain,
            RedOffset = this.RedOffset,
            GreenOffset = this.GreenOffset,
            BlueOffset = this.BlueOffset,
            Algorithm = this.Algorithm,
            NightModeCcssPath = this.NightModeCcssPath,
            NightMelanopicCoefficients = this.NightMelanopicCoefficients,
            UseUltraWarmMode = this.UseUltraWarmMode,
            PerceptualStrength = this.PerceptualStrength,
            PreserveNightLuminance = this.PreserveNightLuminance,
            NightLuminanceCeiling = this.NightLuminanceCeiling
        };

        /// <summary>
        /// Returns a copy clamped to the ranges the LUT pipeline can safely evaluate.
        /// </summary>
        public CalibrationSettings Sanitized() => new CalibrationSettings
        {
            MeasuredCorrectionLut = MeasuredCorrectionLut,
            CalibrationProfileId = CalibrationProfileId,
            Brightness = ClampFinite(Brightness, 0.0, 100.0, 100.0),
            UseLinearBrightness = UseLinearBrightness,
            Temperature = ClampFinite(Temperature, MinimumTemperatureScale, MaximumTemperatureScale, 0.0),
            TemperatureOffset = ClampFinite(TemperatureOffset, -50.0, 50.0, 0.0),
            Tint = ClampFinite(Tint, -50.0, 50.0, 0.0),
            RedGain = ClampFinite(RedGain, 0.5, 1.5, 1.0),
            GreenGain = ClampFinite(GreenGain, 0.5, 1.5, 1.0),
            BlueGain = ClampFinite(BlueGain, 0.5, 1.5, 1.0),
            RedOffset = ClampFinite(RedOffset, -0.5, 0.5, 0.0),
            GreenOffset = ClampFinite(GreenOffset, -0.5, 0.5, 0.0),
            BlueOffset = ClampFinite(BlueOffset, -0.5, 0.5, 0.0),
            Algorithm = Enum.IsDefined(typeof(NightModeAlgorithm), Algorithm)
                ? Algorithm
                : NightModeAlgorithm.Perceptual,
            NightModeCcssPath = NightModeCcssPath,
            NightMelanopicCoefficients = NightMelanopicCoefficients,
            UseUltraWarmMode = UseUltraWarmMode,
            PerceptualStrength = ClampFinite(PerceptualStrength, 0.0, 1.0, ColorAdjustments.DefaultPerceptualStrength),
            PreserveNightLuminance = PreserveNightLuminance,
            NightLuminanceCeiling = ClampFinite(NightLuminanceCeiling, 1.0, 4.0, 1.0)
        };

        private static bool IsAdjusted(double value, double neutral, double tolerance) =>
            !double.IsFinite(value) || Math.Abs(value - neutral) > tolerance;

        private static double ClampFinite(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

        /// <summary>
        /// Returns a hash code suitable for use in dictionaries and caches.
        /// Values are rounded to reduce cache fragmentation for nearly-identical settings.
        /// </summary>
        public override int GetHashCode()
        {
            // Round values to reduce cache fragmentation
            int brightnessKey = (int)Math.Round(Brightness);
            // Temperature units are 70 K. Tenths therefore collapse a fade into ~7 K
            // plateaus even when the scheduler updates smoothly. Hundredths retain
            // sub-kelvin resolution without using unstable raw floating-point bits.
            int tempKey = (int)Math.Round(Temperature * 100);
            int tempOffsetKey = (int)Math.Round(TemperatureOffset * 100);
            int tintKey = (int)Math.Round(Tint * 10);
            int rGainKey = (int)Math.Round(RedGain * 100);
            int gGainKey = (int)Math.Round(GreenGain * 100);
            int bGainKey = (int)Math.Round(BlueGain * 100);
            int rOffsetKey = (int)Math.Round(RedOffset * 1000);
            int gOffsetKey = (int)Math.Round(GreenOffset * 1000);
            int bOffsetKey = (int)Math.Round(BlueOffset * 1000);
            int lutKey = CalibrationProfileId?.GetHashCode() ?? 0;
            int nightCcssKey = NightModeCcssPath?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0;
            // Include the measured-correction LUT instance identity. Without this, two
            // settings with identical scalars but a freshly-regenerated Lut3D (same profile
            // id) would collide in LutGenerator's cache and return the stale LUT. Reference
            // identity is sufficient and correct here: a different LUT instance must not be a
            // false hit. A content hash over the 3D LUT is unnecessary (a suboptimal cache
            // miss is harmless; a *wrong* hit is the failure we're closing).
            int lutInstanceKey = RuntimeHelpers.GetHashCode(MeasuredCorrectionLut);
            // Perceptual strength changes the multipliers, so it must invalidate the LUT cache.
            int strengthKey = (int)Math.Round(PerceptualStrength * 100);
            // Constant-Y flag and ceiling change the multipliers/clamp — same cache rule.
            int ceilingKey = (int)Math.Round(NightLuminanceCeiling * 100);

            return HashCode.Combine(
                HashCode.Combine(brightnessKey, tempKey, tempOffsetKey, tintKey),
                HashCode.Combine(rGainKey, gGainKey, bGainKey),
                HashCode.Combine(rOffsetKey, gOffsetKey, bOffsetKey, PreserveNightLuminance, ceilingKey),
                HashCode.Combine((int)Algorithm, UseLinearBrightness, UseUltraWarmMode, strengthKey, lutKey, lutInstanceKey, nightCcssKey)
            );
        }
    }
}
