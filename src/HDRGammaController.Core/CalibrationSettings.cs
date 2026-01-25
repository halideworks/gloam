using System;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Per-monitor calibration settings for advanced adjustments.
    /// </summary>
    public class CalibrationSettings
    {
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
        /// Color temperature adjustment (-50 to +50).
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
        public NightModeAlgorithm Algorithm { get; set; } = NightModeAlgorithm.Standard;

        /// <summary>
        /// Enable enhanced warmth curve below 2800K for more dramatic visual changes.
        /// Only applies when Algorithm is Standard.
        /// </summary>
        public bool UseUltraWarmMode { get; set; } = false;

        /// <summary>
        /// If true, uses standard linear dimming instead of perceptual (gamma-lift) dimming.
        /// </summary>
        public bool UseLinearBrightness { get; set; } = false;

        /// <summary>
        /// Returns true if any adjustments are applied (non-default values).
        /// </summary>
        public bool HasAdjustments =>
            MeasuredCorrectionLut != null ||
            Math.Abs(Brightness - 100.0) > 0.01 ||
            Math.Abs(Temperature) > 0.01 ||
            Math.Abs(TemperatureOffset) > 0.01 ||
            Math.Abs(Tint) > 0.01 ||
            Math.Abs(RedGain - 1.0) > 0.001 ||
            Math.Abs(GreenGain - 1.0) > 0.001 ||
            Math.Abs(BlueGain - 1.0) > 0.001 ||
            Math.Abs(RedOffset) > 0.001 ||
            Math.Abs(GreenOffset) > 0.001 ||
            Math.Abs(BlueOffset) > 0.001;

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
            UseUltraWarmMode = this.UseUltraWarmMode
        };

        /// <summary>
        /// Returns a hash code suitable for use in dictionaries and caches.
        /// Values are rounded to reduce cache fragmentation for nearly-identical settings.
        /// </summary>
        public override int GetHashCode()
        {
            // Round values to reduce cache fragmentation
            int brightnessKey = (int)Math.Round(Brightness);
            int tempKey = (int)Math.Round(Temperature * 10);
            int tempOffsetKey = (int)Math.Round(TemperatureOffset * 10);
            int tintKey = (int)Math.Round(Tint * 10);
            int rGainKey = (int)Math.Round(RedGain * 100);
            int gGainKey = (int)Math.Round(GreenGain * 100);
            int bGainKey = (int)Math.Round(BlueGain * 100);
            int rOffsetKey = (int)Math.Round(RedOffset * 1000);
            int gOffsetKey = (int)Math.Round(GreenOffset * 1000);
            int bOffsetKey = (int)Math.Round(BlueOffset * 1000);
            int lutKey = CalibrationProfileId?.GetHashCode() ?? 0;

            return HashCode.Combine(
                HashCode.Combine(brightnessKey, tempKey, tempOffsetKey, tintKey),
                HashCode.Combine(rGainKey, gGainKey, bGainKey),
                HashCode.Combine(rOffsetKey, gOffsetKey, bOffsetKey),
                HashCode.Combine((int)Algorithm, UseLinearBrightness, UseUltraWarmMode, lutKey)
            );
        }
    }
}
