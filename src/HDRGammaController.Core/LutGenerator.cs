using System;
using System.Collections.Concurrent;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Generates 1D lookup tables for HDR/SDR gamma correction with optional calibration.
    /// </summary>
    /// <remarks>
    /// The LUT generator implements a multi-stage pipeline:
    /// 1. PQ EOTF decode (signal to linear nits)
    /// 2. Normalize to SDR range
    /// 3. Apply target gamma curve (2.2 or 2.4)
    /// 4. Apply calibration adjustments in linear space
    /// 5. PQ OETF encode (linear nits to signal)
    /// 6. Blend toward passthrough in HDR headroom region
    ///
    /// LUT results are cached to avoid redundant computation for identical parameters.
    /// </remarks>
    public static class LutGenerator
    {
        // Cache for computed LUTs to avoid redundant computation.
        // Key: (GammaMode, exact WhiteLevel, full calibration, IsHdr)
        //
        // CONTRACT: Cached arrays are READ-ONLY. Callers must not mutate them — the cache
        // hands out shared references directly to avoid 32 KB of per-call allocation at
        // slider-drag frequencies. Any write to a returned array corrupts future lookups.
        private static readonly ConcurrentDictionary<(GammaMode, double, CalibrationCacheKey, bool), (double[] R, double[] G, double[] B, double[] Grey)> _lutCache = new();

        // A hash code alone is not identity: collisions returned a wrong cached LUT. Keep
        // every input in the dictionary key so equality is checked field-by-field. The cache
        // is bounded, so exact values are preferable to order-dependent approximate hits.
        private readonly record struct CalibrationCacheKey(
            double Brightness, double Temperature, double TemperatureOffset, double Tint,
            double RedGain, double GreenGain, double BlueGain,
            double RedOffset, double GreenOffset, double BlueOffset,
            NightModeAlgorithm Algorithm, bool LinearBrightness, bool UltraWarm,
            Guid? ProfileId, Lut3D? MeasuredLut)
        {
            public static CalibrationCacheKey From(CalibrationSettings value) => new(
                value.Brightness, value.Temperature, value.TemperatureOffset, value.Tint,
                value.RedGain, value.GreenGain, value.BlueGain,
                value.RedOffset, value.GreenOffset, value.BlueOffset,
                value.Algorithm, value.UseLinearBrightness, value.UseUltraWarmMode,
                value.CalibrationProfileId, value.MeasuredCorrectionLut);
        }

        // Cache ceiling. When we cross it we clear the entire cache at once rather than try to
        // approximate LRU from ConcurrentDictionary.Keys (which has no defined order, so the
        // prior "evict oldest" loop was effectively random). A full flush is simpler and the
        // next few slider ticks repopulate hot entries.
        private const int MaxCacheSize = 100;

        /// <summary>
        /// Generates a 1024-point 1D LUT for HDR gamma correction (single channel, no calibration).
        /// </summary>
        public static double[] GenerateLut(GammaMode gammaMode, double sdrWhiteLevel, bool isHdr = true)
        {
            return GenerateLut(gammaMode, sdrWhiteLevel, CalibrationSettings.Default, isHdr).Grey;
        }

        /// <summary>
        /// Clears the LUT cache to free memory.
        /// </summary>
        public static void ClearCache()
        {
            _lutCache.Clear();
        }
        
        /// <summary>
        /// Generates per-channel 1024-point 1D LUTs for HDR gamma correction with calibration.
        /// Results are cached for performance when called with identical parameters.
        /// </summary>
        /// <param name="gammaMode">The target gamma curve (2.2 or 2.4).</param>
        /// <param name="sdrWhiteLevel">The SDR white level in nits (e.g. 80, 200, 480).</param>
        /// <param name="calibration">Calibration settings for dimming, temp, tint, RGB.</param>
        /// <param name="isHdr">Whether the target display is in HDR mode.</param>
        /// <returns>Per-channel LUTs (R, G, B) and a Grey reference.</returns>
        public static (double[] R, double[] G, double[] B, double[] Grey) GenerateLut(
            GammaMode gammaMode,
            double sdrWhiteLevel,
            CalibrationSettings calibration,
            bool isHdr = true)
        {
            // Display white is part of the actual transfer function. Coarse bucketing made
            // two monitors order-dependent: whichever generated a LUT first supplied its
            // curve to the other.
            double whiteLevelKey = sdrWhiteLevel;
            var calibrationKey = CalibrationCacheKey.From(calibration);
            var cacheKey = (gammaMode, whiteLevelKey, calibrationKey, isHdr);

            if (_lutCache.TryGetValue(cacheKey, out var cachedLut))
            {
                return cachedLut;
            }

            var result = GenerateLutInternal(gammaMode, sdrWhiteLevel, calibration, isHdr);

            // Flush on overflow rather than pretending to LRU over an unordered dictionary.
            if (_lutCache.Count >= MaxCacheSize)
            {
                _lutCache.Clear();
            }

            _lutCache.TryAdd(cacheKey, result);
            return result;
        }

        /// <summary>
        /// Internal LUT generation without caching.
        /// </summary>
        private static (double[] R, double[] G, double[] B, double[] Grey) GenerateLutInternal(
            GammaMode gammaMode,
            double sdrWhiteLevel,
            CalibrationSettings calibration,
            bool isHdr)
        {
            double[] lutR = new double[1024];
            double[] lutG = new double[1024];
            double[] lutB = new double[1024];
            double[] lutGrey = new double[1024];

            if (gammaMode == GammaMode.WindowsDefault && !calibration.HasAdjustments)
            {
                // Identity LUT
                for (int i = 0; i < 1024; i++)
                {
                    double val = i / 1023.0;
                    lutR[i] = val;
                    lutG[i] = val;
                    lutB[i] = val;
                    lutGrey[i] = val;
                }
                return (lutR, lutG, lutB, lutGrey);
            }

            if (!isHdr)
            {
                // SDR generation: decode the incoming signal using the TARGET gamma, do
                // calibration in linear light, then re-encode for the display's assumed
                // native ~2.2 response. Windows SDR content is mastered on a ~2.2 display,
                // so the display decodes as gamma 2.2 regardless of what the user selects
                // here — the gammaMode therefore controls the DECODE of the (re-)mapped
                // signal, not the display. Gamma22/WindowsDefault net to a passthrough
                // (decode 2.2 / encode 1/2.2); Gamma24 now correctly darkens shadows.
                //
                // This mirrors the SDR branch of GenerateCalibratedLut. Previously this path
                // hardcoded 2.2 for both, silently making Gamma 2.4 selection a no-op on SDR.
                double targetGamma = gammaMode switch
                {
                    GammaMode.Gamma24 => 2.4,
                    _ => 2.2 // Gamma22 and WindowsDefault
                };

                for (int i = 0; i < 1024; i++)
                {
                    double input = i / 1023.0;

                    // 1. Decode the signal with the target gamma into linear light.
                    double linear = Math.Pow(input, targetGamma);

                    // 2. Apply calibration (temp/tint/dimming/gains) in linear space.
                    var (r, g, b) = ColorAdjustments.ApplyCalibration(linear, linear, linear, calibration);

                    // 3. Re-encode for the display's native ~2.2 response.
                    lutR[i] = Math.Pow(r, 1.0 / 2.2);
                    lutG[i] = Math.Pow(g, 1.0 / 2.2);
                    lutB[i] = Math.Pow(b, 1.0 / 2.2);
                    lutGrey[i] = (lutR[i] + lutG[i] + lutB[i]) / 3.0;
                }
                return (lutR, lutG, lutB, lutGrey);
            }

            double gamma = gammaMode switch
            {
                GammaMode.Gamma24 => 2.4,
                GammaMode.Gamma22 => 2.2,
                _ => 1.0 // WindowsDefault with calibration
            };

            double blackLevel = 0.0;

            // Precompute the PQ-signal position of SDR white. The headroom blend runs in
            // PQ-signal space (perceptually uniform) rather than linear-nit space — with the
            // old linear blend a 1000-nit specular was only ~8% toward passthrough, so most
            // real HDR highlights stayed fully calibrated regardless of the user's intent.
            double pqSdrWhite = TransferFunctions.PqInverseEotf(sdrWhiteLevel);

            for (int i = 0; i < 1024; i++)
            {
                double normalized = i / 1023.0;

                // 1. PQ EOTF -> linear nits
                double linear = TransferFunctions.PqEotf(normalized);

                double outputR, outputG, outputB;

                if (gammaMode == GammaMode.WindowsDefault)
                {
                    // No gamma correction, just apply calibration
                    outputR = outputG = outputB = linear;
                }
                else
                {
                    // 2. Input Light -> Simulated Signal (inverse sRGB)
                    double srgbNormalized = TransferFunctions.SrgbInverseEotf(linear, sdrWhiteLevel, blackLevel);

                    // 3. Apply gamma
                    double gammaApplied = Math.Pow(srgbNormalized, gamma);

                    // 4. Scale to output nits
                    double outputLinear = blackLevel + (sdrWhiteLevel - blackLevel) * gammaApplied;

                    outputR = outputG = outputB = outputLinear;
                }

                // 5. Apply calibration adjustments (dimming, temp, tint, RGB)
                if (calibration.HasAdjustments)
                {
                    // Normalize to 0-1 range for calibration
                    double normR = Math.Clamp(outputR / sdrWhiteLevel, 0, 1);
                    double normG = Math.Clamp(outputG / sdrWhiteLevel, 0, 1);
                    double normB = Math.Clamp(outputB / sdrWhiteLevel, 0, 1);

                    var (adjR, adjG, adjB) = ColorAdjustments.ApplyCalibration(normR, normG, normB, calibration);

                    // Scale back to nits
                    outputR = adjR * sdrWhiteLevel;
                    outputG = adjG * sdrWhiteLevel;
                    outputB = adjB * sdrWhiteLevel;
                }

                // 6. Encode fully-calibrated output to PQ
                double pqR = TransferFunctions.PqInverseEotf(outputR);
                double pqG = TransferFunctions.PqInverseEotf(outputG);
                double pqB = TransferFunctions.PqInverseEotf(outputB);
                double pqGrey = TransferFunctions.PqInverseEotf((outputR + outputG + outputB) / 3.0);

                if (linear <= sdrWhiteLevel)
                {
                    lutR[i] = pqR;
                    lutG[i] = pqG;
                    lutB[i] = pqB;
                    lutGrey[i] = pqGrey;
                }
                else
                {
                    // HDR headroom: blend the fully-calibrated output toward a
                    // "dim-only passthrough". This preserves the creative grade of
                    // HDR highlights (no re-gamma, no temperature tint on a 2000-nit
                    // specular) while still honoring the brightness slider — so a
                    // user dimming the screen sees highlights come down too.
                    double headroomSignal = ComputeHeadroomTarget(linear, calibration, sdrWhiteLevel);

                    // Blend in PQ-signal space (perceptually uniform) with a smoothstep
                    // for C¹ continuity at the SDR/HDR boundary, eliminating the visible
                    // slope-kink the old linear blend produced in smooth gradients.
                    double t = (normalized - pqSdrWhite) / Math.Max(1.0 - pqSdrWhite, 1e-9);
                    t = Math.Clamp(t, 0.0, 1.0);
                    double blendFactor = t * t * (3.0 - 2.0 * t);

                    lutR[i] = pqR + (headroomSignal - pqR) * blendFactor;
                    lutG[i] = pqG + (headroomSignal - pqG) * blendFactor;
                    lutB[i] = pqB + (headroomSignal - pqB) * blendFactor;
                    lutGrey[i] = pqGrey + (headroomSignal - pqGrey) * blendFactor;
                }
            }

            return (lutR, lutG, lutB, lutGrey);
        }

        /// <summary>
        /// Target the headroom blend fades toward. We preserve HDR creative intent
        /// (no gamma/temp/tint on highlights) but keep brightness dimming active — otherwise
        /// dimming the screen leaves HDR highlights at full brightness, which the user
        /// experiences as specular bloom punching through a dimmed UI.
        /// </summary>
        private static double ComputeHeadroomTarget(double linearNits, CalibrationSettings calibration, double sdrWhiteLevel)
        {
            if (calibration.Brightness >= 100.0)
            {
                // No dimming — target is pure passthrough. Re-encode original linear nits
                // to PQ. For i at the upper end this equals the input signal, matching the
                // old identity-passthrough behavior where no dimming is requested.
                return TransferFunctions.PqInverseEotf(linearNits);
            }

            double dimmed = ColorAdjustments.ApplyDimmingNits(
                linearNits, calibration.Brightness, sdrWhiteLevel, calibration.UseLinearBrightness);
            return TransferFunctions.PqInverseEotf(dimmed);
        }

        #region Calibrated LUT Generation

        /// <summary>
        /// Generates per-channel 1D LUTs using measured display characteristics.
        /// This provides accurate gamma compensation based on actual colorimeter measurements.
        /// </summary>
        /// <param name="targetGamma">The desired output gamma (2.2, 2.4, etc.).</param>
        /// <param name="profile">The calibration profile with measured tone curves.</param>
        /// <param name="calibration">Additional calibration settings (temperature, tint, etc.).</param>
        /// <param name="sdrWhiteLevel">SDR white level in nits.</param>
        /// <param name="isHdr">Whether the display is in HDR mode.</param>
        /// <returns>Per-channel LUTs that compensate for the display's actual response.</returns>
        public static (double[] R, double[] G, double[] B, double[] Grey) GenerateCalibratedLut(
            double targetGamma,
            DisplayCalibrationProfile profile,
            CalibrationSettings calibration,
            double sdrWhiteLevel,
            bool isHdr = true)
        {
            // Convert profile to characterization
            var characterization = profile.ToCharacterization();
            return GenerateCalibratedLut(targetGamma, characterization, calibration, sdrWhiteLevel, isHdr);
        }

        /// <summary>
        /// Generates per-channel 1D LUTs using measured display characteristics.
        /// </summary>
        public static (double[] R, double[] G, double[] B, double[] Grey) GenerateCalibratedLut(
            double targetGamma,
            DisplayCharacterization characterization,
            CalibrationSettings calibration,
            double sdrWhiteLevel,
            bool isHdr = true)
        {
            double[] lutR = new double[1024];
            double[] lutG = new double[1024];
            double[] lutB = new double[1024];
            double[] lutGrey = new double[1024];

            // Get the measured tone curves (what the display actually does)
            var measuredR = characterization.RedToneCurve ?? ToneCurve.CreateGamma(characterization.MeasuredGamma);
            var measuredG = characterization.GreenToneCurve ?? ToneCurve.CreateGamma(characterization.MeasuredGamma);
            var measuredB = characterization.BlueToneCurve ?? ToneCurve.CreateGamma(characterization.MeasuredGamma);

            if (!isHdr)
            {
                // SDR Mode: Compute compensation curves
                // For each input signal level, find what signal to send to get the target output
                for (int i = 0; i < 1024; i++)
                {
                    double input = i / 1023.0;

                    // What linear light level do we WANT for this input?
                    // (Input represents the encoded signal, target gamma defines the desired decoding)
                    double targetLinear = Math.Pow(input, targetGamma);

                    // Apply calibration adjustments to the target
                    var (adjR, adjG, adjB) = ColorAdjustments.ApplyCalibration(
                        targetLinear, targetLinear, targetLinear, calibration);

                    // What signal must we send to the display to get this output?
                    // Use the INVERSE of the measured response
                    lutR[i] = measuredR.InverseLookup(Math.Clamp(adjR, 0, 1));
                    lutG[i] = measuredG.InverseLookup(Math.Clamp(adjG, 0, 1));
                    lutB[i] = measuredB.InverseLookup(Math.Clamp(adjB, 0, 1));
                    lutGrey[i] = (lutR[i] + lutG[i] + lutB[i]) / 3.0;
                }
                return (lutR, lutG, lutB, lutGrey);
            }

            // HDR Mode with calibration-aware compensation
            double blackLevel = characterization.BlackLevel;
            double pqSdrWhite = TransferFunctions.PqInverseEotf(sdrWhiteLevel);

            for (int i = 0; i < 1024; i++)
            {
                double normalized = i / 1023.0;

                // 1. PQ EOTF -> linear nits
                double linear = TransferFunctions.PqEotf(normalized);

                double outputR, outputG, outputB;

                if (Math.Abs(targetGamma - 1.0) < 0.01)
                {
                    // Linear mode (no gamma correction, just calibration)
                    outputR = outputG = outputB = linear;
                }
                else
                {
                    // 2. Compute what linear output we want based on target gamma
                    double srgbNormalized = TransferFunctions.SrgbInverseEotf(linear, sdrWhiteLevel, blackLevel);
                    double gammaApplied = Math.Pow(srgbNormalized, targetGamma);
                    double targetLinear = blackLevel + (sdrWhiteLevel - blackLevel) * gammaApplied;
                    outputR = outputG = outputB = targetLinear;
                }

                // 3. Apply calibration adjustments
                if (calibration.HasAdjustments)
                {
                    double normR = Math.Clamp(outputR / sdrWhiteLevel, 0, 1);
                    double normG = Math.Clamp(outputG / sdrWhiteLevel, 0, 1);
                    double normB = Math.Clamp(outputB / sdrWhiteLevel, 0, 1);

                    var (adjR, adjG, adjB) = ColorAdjustments.ApplyCalibration(normR, normG, normB, calibration);

                    outputR = adjR * sdrWhiteLevel;
                    outputG = adjG * sdrWhiteLevel;
                    outputB = adjB * sdrWhiteLevel;
                }

                // 4. Compensate for display's actual response
                // Convert target linear to the signal level that produces it on THIS display
                double targetNormR = Math.Clamp(outputR / sdrWhiteLevel, 0, 1);
                double targetNormG = Math.Clamp(outputG / sdrWhiteLevel, 0, 1);
                double targetNormB = Math.Clamp(outputB / sdrWhiteLevel, 0, 1);

                double compensatedR = measuredR.InverseLookup(targetNormR) * sdrWhiteLevel;
                double compensatedG = measuredG.InverseLookup(targetNormG) * sdrWhiteLevel;
                double compensatedB = measuredB.InverseLookup(targetNormB) * sdrWhiteLevel;

                // 5. Encode to PQ
                double pqR = TransferFunctions.PqInverseEotf(compensatedR);
                double pqG = TransferFunctions.PqInverseEotf(compensatedG);
                double pqB = TransferFunctions.PqInverseEotf(compensatedB);
                double pqGrey = TransferFunctions.PqInverseEotf((compensatedR + compensatedG + compensatedB) / 3.0);

                if (linear <= sdrWhiteLevel)
                {
                    lutR[i] = pqR;
                    lutG[i] = pqG;
                    lutB[i] = pqB;
                    lutGrey[i] = pqGrey;
                }
                else
                {
                    // Headroom: blend toward a dim-aware passthrough in PQ-signal space
                    // with a smoothstep. See GenerateLutInternal for rationale.
                    double headroomSignal = ComputeHeadroomTarget(linear, calibration, sdrWhiteLevel);

                    double t = (normalized - pqSdrWhite) / Math.Max(1.0 - pqSdrWhite, 1e-9);
                    t = Math.Clamp(t, 0.0, 1.0);
                    double blendFactor = t * t * (3.0 - 2.0 * t);

                    lutR[i] = pqR + (headroomSignal - pqR) * blendFactor;
                    lutG[i] = pqG + (headroomSignal - pqG) * blendFactor;
                    lutB[i] = pqB + (headroomSignal - pqB) * blendFactor;
                    lutGrey[i] = pqGrey + (headroomSignal - pqGrey) * blendFactor;
                }
            }

            return (lutR, lutG, lutB, lutGrey);
        }

        /// <summary>
        /// Checks if a calibration profile is available and valid for the given gamma mode.
        /// </summary>
        public static bool CanUseCalibratedLut(DisplayCalibrationProfile? profile)
        {
            if (profile == null)
                return false;

            // Check if the profile has valid tone curve data
            bool hasToneCurves = profile.RedToneCurve != null &&
                                 profile.GreenToneCurve != null &&
                                 profile.BlueToneCurve != null &&
                                 profile.RedToneCurve.Length > 0;

            return hasToneCurves || profile.MeasuredGamma > 0;
        }

        #endregion
    }
}
