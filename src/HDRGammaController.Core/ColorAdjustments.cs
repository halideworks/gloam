using System;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Color adjustment functions for temperature, tint, dimming, and RGB corrections.
    /// </summary>
    public static class ColorAdjustments
    {
        /// <summary>
        /// Applies dimming to value. Can use either linear scaling or perceptual (gamma-compressed) scaling.
        /// </summary>
        /// <param name="value">Input value (0-1)</param>
        /// <param name="brightnessPercent">Brightness percentage (0-100)</param>
        /// <param name="linear">If true, uses linear multiplication. If false, uses power-law compression to preserve shadows.</param>
        /// <returns>Dimmed value</returns>
        public static double ApplyDimming(double value, double brightnessPercent, bool linear = false)
        {
            if (brightnessPercent >= 100.0) return value;
            if (brightnessPercent <= 0) return 0;

            // Clamp to valid range (0-100)
            double brightness = Math.Clamp(brightnessPercent, 0.0, 100.0) / 100.0;

            if (linear)
            {
                return value * brightness;
            }

            // Power-law compression: lifts shadow detail relative to a straight linear dim
            // while still landing white at exactly `brightness`. At value=1 the output equals
            // brightness; at value<1 the shadow lift preserves near-black separation.
            // Formula: output = input^(1/gamma_boost) * brightness
            // where gamma_boost grows as brightness falls (1.0 at 100%, 1.27 at 10%).
            double gammaBoost = 1.0 + (1.0 - brightness) * 0.3;

            return Math.Pow(value, 1.0 / gammaBoost) * brightness;
        }

        /// <summary>
        /// Applies the same dimming curve to an absolute-nit value. Used by the HDR headroom
        /// blend so highlights follow the brightness slider instead of snapping back to passthrough.
        /// </summary>
        /// <remarks>
        /// Anchored at the SDR white level so the headroom curve meets the SDR portion of
        /// the LUT exactly: at nits == sdrWhiteLevel this returns
        /// ApplyDimming(1.0) * sdrWhiteLevel. The previous version normalized against the
        /// 10,000-nit PQ ceiling instead, which made the headroom target start ~1.7× brighter
        /// than the dimmed SDR white at 50% brightness — a visible shelf at the boundary.
        /// Above white the same power curve keeps the rolloff monotonic.
        /// </remarks>
        public static double ApplyDimmingNits(double nits, double brightnessPercent, double sdrWhiteLevel, bool linear = false)
        {
            if (brightnessPercent >= 100.0) return nits;
            if (brightnessPercent <= 0) return 0;

            double brightness = Math.Clamp(brightnessPercent, 0.0, 100.0) / 100.0;

            if (linear)
            {
                return nits * brightness;
            }

            double white = Math.Max(sdrWhiteLevel, 1.0);
            double ratio = Math.Max(nits, 0.0) / white;
            double gammaBoost = 1.0 + (1.0 - brightness) * 0.3;
            return Math.Pow(ratio, 1.0 / gammaBoost) * brightness * white;
        }
        
        /// <summary>
        /// Calculates RGB multipliers for a given color temperature based on the selected algorithm.
        /// </summary>
        public static (double R, double G, double B) GetTemperatureMultipliers(double temperature, NightModeAlgorithm algorithm, bool useUltraWarmMode = false)
        {
            // Convert -50 to +50 scale to Kelvin: -50 = 2700K, 0 = 6500K, +50 = 10000K
            int kelvin = (int)(6500 + temperature * 70);
            kelvin = Math.Clamp(kelvin, 1000, 10000);

            return algorithm switch
            {
                NightModeAlgorithm.AccurateCIE1931 => GetAccurateMultipliers(kelvin),
                NightModeAlgorithm.BlueReduction => GetBlueReductionMultipliers(kelvin),
                _ => GetStandardMultipliers(kelvin, useUltraWarmMode)
            };
        }
        
        /// <summary>
        /// Standard approximation (Tanner Helland's algorithm).
        /// Fast, pleasant, and widely used in photo editing.
        /// When useUltraWarmMode is true, applies enhanced curve below 2800K for more dramatic effect.
        /// </summary>
        public static (double R, double G, double B) GetStandardMultipliers(int kelvin, bool useUltraWarmMode = false)
        {
            // Reference point: 6500K should return (1, 1, 1)
            var ref6500 = GetRawKelvinRGB_Helland(6500);
            double refMax = Math.Max(ref6500.r, Math.Max(ref6500.g, ref6500.b));

            var target = GetRawKelvinRGB_Helland(kelvin);

            // Normalize target
            double maxVal = Math.Max(target.r, Math.Max(target.g, target.b));
            if (maxVal > 0)
            {
                target.r /= maxVal;
                target.g /= maxVal;
                target.b /= maxVal;
            }

            // Scale by reference
            double r = refMax > 0 ? target.r / (ref6500.r / refMax) : target.r;
            double g = refMax > 0 ? target.g / (ref6500.g / refMax) : target.g;
            double b = refMax > 0 ? target.b / (ref6500.b / refMax) : target.b;

            // Enhanced curve below 2800K for more dramatic night mode effect (optional)
            // The Helland algorithm plateaus at very warm temps, so we accelerate the reduction
            if (useUltraWarmMode && kelvin < 2800)
            {
                // Apply additional power curve to make low temps more distinct
                // At 2800K: factor = 0, no change
                // At 1900K: factor = 1, maximum additional reduction
                double factor = Math.Clamp((2800.0 - kelvin) / (2800.0 - 1900.0), 0.0, 1.0);

                // Use power curve for smooth transition (factor^2 for quadratic acceleration)
                double accel = factor * factor;

                // Additional reduction for green and blue
                // Green: reduce by up to 25% more (from ~0.54 to ~0.41)
                // Blue: already at 0 by 1900K, but accelerate the drop
                g *= (1.0 - accel * 0.25);
                b *= (1.0 - accel * 0.5);
            }

            return (Math.Clamp(r, 0, 1.5), Math.Clamp(g, 0, 1.5), Math.Clamp(b, 0, 1.5));
        }

        private static (double r, double g, double b) GetRawKelvinRGB_Helland(int kelvin)
        {
            double temp = kelvin / 100.0;
            double r, g, b;
            
            if (temp <= 66) r = 255;
            else r = 329.698727446 * Math.Pow(temp - 60, -0.1332047592);
            
            if (temp <= 66) g = 99.4708025861 * Math.Log(temp) - 161.1195681661;
            else g = 288.1221695283 * Math.Pow(temp - 60, -0.0755148492);
            
            if (temp >= 66) b = 255;
            else if (temp <= 19) b = 0;
            else b = 138.5177312231 * Math.Log(temp - 10) - 305.0447927307;
            
            return (Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
        }

        /// <summary>
        /// Physically accurate conversion using CIE 1931 color space (Kang et al. approximation).
        /// Converts Kelvin -> xy Chromaticity -> XYZ -> sRGB.
        /// </summary>
        public static (double R, double G, double B) GetAccurateMultipliers(int kelvin)
        {
            double T = kelvin;
            double x, y;

            // Calculate x
            if (T <= 4000)
                x = -0.2661239 * Math.Pow(10, 9) / Math.Pow(T, 3) 
                    - 0.2343580 * Math.Pow(10, 6) / Math.Pow(T, 2) 
                    + 0.8776956 * Math.Pow(10, 3) / T + 0.179910;
            else
                x = -3.0258469 * Math.Pow(10, 9) / Math.Pow(T, 3) 
                    + 2.1070379 * Math.Pow(10, 6) / Math.Pow(T, 2) 
                    + 0.2226347 * Math.Pow(10, 3) / T + 0.240390;

            // Calculate y
            if (T <= 2222)
                y = -1.1063814 * Math.Pow(x, 3) - 1.34811020 * Math.Pow(x, 2) + 2.18555032 * x - 0.20219683;
            else if (T <= 4000)
                y = -0.9549476 * Math.Pow(x, 3) - 1.37418593 * Math.Pow(x, 2) + 2.09137015 * x - 0.16748867;
            else
                y = 3.0817580 * Math.Pow(x, 3) - 5.87338670 * Math.Pow(x, 2) + 3.75112997 * x - 0.37001483;

            // xyY to XYZ (Y=1 for max luminance)
            double Y = 1.0;
            double X = (y == 0) ? 0 : (x * Y) / y;
            double Z = (y == 0) ? 0 : ((1 - x - y) * Y) / y;

            // XYZ to Linear RGB (sRGB D65 Matrix)
            double rL = 3.2406 * X - 1.5372 * Y - 0.4986 * Z;
            double gL = -0.9689 * X + 1.8758 * Y + 0.0415 * Z;
            double bL = 0.0557 * X - 0.2040 * Y + 1.0570 * Z;

            // Clip Linear RGB
            rL = Math.Clamp(rL, 0, 1);
            gL = Math.Clamp(gL, 0, 1);
            bL = Math.Clamp(bL, 0, 1);

            // Gamma Correct (Linear -> sRGB) using proper sRGB transfer function
            // IEC 61966-2-1:1999 specifies piecewise function, not simple 1/2.2 power
            double r = TransferFunctions.SrgbOetf(rL);
            double g = TransferFunctions.SrgbOetf(gL);
            double b = TransferFunctions.SrgbOetf(bL);

            // Normalize to Max=1 to preserve brightness
            double max = Math.Max(r, Math.Max(g, b));
            if (max > 0) { r /= max; g /= max; b /= max; }

            // Reference normalization to D65 (6500K)
            // (Similar to standard, ensuring 6500K = 1,1,1 approximately)
            // But since this IS D65-based XYZ-to-RGB, 6500K should naturally be close to white.
            // We'll trust the output but normalize to ensure max brightness.

            return (r, g, b);
        }

        /// <summary>
        /// Simplistic filter that targets blue/green reduction linearly.
        /// Preserves Red channel fully.
        /// </summary>
        public static (double R, double G, double B) GetBlueReductionMultipliers(int kelvin)
        {
            // Map Kelvin 6500 -> 1900 to Factor 0.0 -> 1.0
            double factor = Math.Clamp((6500.0 - kelvin) / (6500.0 - 1900.0), 0.0, 1.0);

            // Linear reduction
            // Blue: 1.0 -> 0.1 (Aggressive cut)
            // Green: 1.0 -> 0.7 (Mild cut to warm up)
            // Red: 1.0 (Preserve)

            double r = 1.0;
            double g = 1.0 - (factor * 0.3);
            double b = 1.0 - (factor * 0.9);

            return (r, g, b);
        }

        /// <summary>
        /// Calculates RGB multipliers for tint adjustment (green/magenta axis).
        /// </summary>
        public static (double R, double G, double B) GetTintMultipliers(double tint)
        {
            if (Math.Abs(tint) < 0.01) return (1.0, 1.0, 1.0);
            
            // Normalize to -1 to +1 range
            double t = Math.Clamp(tint, -50.0, 50.0) / 50.0;
            
            double r, g, b;
            
            if (t < 0) // More green
            {
                r = 1.0 - (-t) * 0.08;
                g = 1.0 + (-t) * 0.10;
                b = 1.0 - (-t) * 0.08;
            }
            else // More magenta
            {
                r = 1.0 + t * 0.08;
                g = 1.0 - t * 0.12;
                b = 1.0 + t * 0.08;
            }
            
            return (r, g, b);
        }
        
        /// <summary>
        /// Applies all calibration adjustments to an RGB triplet.
        /// Order: Measured 3D LUT → Dimming → Temperature → Tint → RGB Gains → RGB Offsets
        /// </summary>
        public static (double R, double G, double B) ApplyCalibration(
            double r, double g, double b,
            CalibrationSettings settings)
        {
            // 0. Apply measured 3D LUT correction (if present)
            // This is the colorimeter-measured base correction
            if (settings.MeasuredCorrectionLut != null)
            {
                var corrected = settings.MeasuredCorrectionLut.Lookup((float)r, (float)g, (float)b);
                r = corrected.R;
                g = corrected.G;
                b = corrected.B;
            }

            // 1. Apply perceptual dimming
            if (settings.Brightness < 100.0)
            {
                r = ApplyDimming(r, settings.Brightness, settings.UseLinearBrightness);
                g = ApplyDimming(g, settings.Brightness, settings.UseLinearBrightness);
                b = ApplyDimming(b, settings.Brightness, settings.UseLinearBrightness);
            }
            
            // 2. Apply temperature. All algorithms return (1,1,1) at 6500K (temperature 0),
            // so skipping when there's no shift is safe regardless of algorithm.
            if (Math.Abs(settings.Temperature) > 0.01)
            {
                var temp = GetTemperatureMultipliers(settings.Temperature, settings.Algorithm, settings.UseUltraWarmMode);
                r *= temp.R;
                g *= temp.G;
                b *= temp.B;
            }
            
            // 3. Apply tint
            if (Math.Abs(settings.Tint) > 0.01)
            {
                var tint = GetTintMultipliers(settings.Tint);
                r *= tint.R;
                g *= tint.G;
                b *= tint.B;
            }
            
            // 4. Apply RGB gains
            r *= settings.RedGain;
            g *= settings.GreenGain;
            b *= settings.BlueGain;
            
            // 5. Apply RGB offsets (lift)
            r += settings.RedOffset;
            g += settings.GreenOffset;
            b += settings.BlueOffset;
            
            // 6. Clamp to valid range
            r = Math.Clamp(r, 0.0, 1.0);
            g = Math.Clamp(g, 0.0, 1.0);
            b = Math.Clamp(b, 0.0, 1.0);
            
            return (r, g, b);
        }
    }
}
