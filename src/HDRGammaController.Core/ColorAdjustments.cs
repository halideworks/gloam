using System;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// RGB basis in which colorimetric night-mode multipliers are derived. A diagonal
    /// (per-channel) white-point scale is basis-dependent: the channel ratios that reach a
    /// given chromaticity in sRGB primaries land at a different chromaticity when applied
    /// to a Rec.2020-encoded signal. The multipliers must therefore be derived in the
    /// primaries of the wire signal they scale — sRGB for the SDR ramp, Rec.2020 for the
    /// HDR10 (PQ / Rec.2020 container) LUT path.
    /// </summary>
    public enum NightBasis
    {
        /// <summary>sRGB / Rec.709 primaries — the SDR wire signal.</summary>
        Srgb,

        /// <summary>Rec.2020 primaries — the HDR10 wire signal container.</summary>
        Rec2020
    }

    /// <summary>
    /// Color adjustment functions for temperature, tint, dimming, and RGB corrections.
    /// </summary>
    public static class ColorAdjustments
    {
        private const int MinimumNightModeKelvin = 1000;
        private const int MaximumNightModeKelvin = 10000;

        /// <summary>
        /// Applies dimming to value. Can use either linear scaling or perceptual (gamma-compressed) scaling.
        /// </summary>
        /// <param name="value">Input value (0-1)</param>
        /// <param name="brightnessPercent">Brightness percentage (0-100)</param>
        /// <param name="linear">If true, uses linear multiplication. If false, uses power-law compression to preserve shadows.</param>
        /// <returns>Dimmed value</returns>
        public static double ApplyDimming(double value, double brightnessPercent, bool linear = false)
        {
            value = ClampFinite(value, 0.0, 1.0, 0.0);
            brightnessPercent = ClampFinite(brightnessPercent, 0.0, 100.0, 100.0);

            if (brightnessPercent >= 100.0) return value;
            if (brightnessPercent <= 0) return 0;

            // Clamp to valid range (0-100)
            double brightness = brightnessPercent / 100.0;

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
            nits = double.IsFinite(nits) ? Math.Max(nits, 0.0) : 0.0;
            brightnessPercent = ClampFinite(brightnessPercent, 0.0, 100.0, 100.0);
            sdrWhiteLevel = double.IsFinite(sdrWhiteLevel) ? Math.Max(sdrWhiteLevel, 1.0) : 1.0;

            if (brightnessPercent >= 100.0) return nits;
            if (brightnessPercent <= 0) return 0;

            double brightness = brightnessPercent / 100.0;

            if (linear)
            {
                return nits * brightness;
            }

            double white = sdrWhiteLevel;
            double ratio = nits / white;
            double gammaBoost = 1.0 + (1.0 - brightness) * 0.3;
            return Math.Pow(ratio, 1.0 / gammaBoost) * brightness * white;
        }
        
        /// <summary>
        /// Calculates RGB multipliers for a given color temperature based on the selected algorithm.
        /// </summary>
        /// <remarks>
        /// <paramref name="basis"/> selects the wire RGB primaries the colorimetric algorithms
        /// (Accurate/Perceptual, and the Planckian core of UltraNight) derive their diagonal in.
        /// Standard and BlueReduction are signal-space heuristics with no colorimetric target,
        /// so the basis does not apply to their per-channel factors.
        /// </remarks>
        public static (double R, double G, double B) GetTemperatureMultipliers(
            double temperature,
            NightModeAlgorithm algorithm,
            bool useUltraWarmMode = false,
            double perceptualStrength = DefaultPerceptualStrength,
            NightMelanopicCoefficients? melanopicCoefficients = null,
            NightBasis basis = NightBasis.Srgb)
        {
            int kelvin = TemperatureScaleToKelvin(temperature);

            return algorithm switch
            {
                NightModeAlgorithm.AccurateCIE1931 => GetAccurateMultipliers(kelvin, basis),
                NightModeAlgorithm.BlueReduction => GetBlueReductionMultipliers(kelvin),
                NightModeAlgorithm.Perceptual => GetPerceptualMultipliers(kelvin, perceptualStrength, basis),
                NightModeAlgorithm.UltraNight => GetUltraNightMultipliers(kelvin, melanopicCoefficients, basis),
                _ => GetStandardMultipliers(kelvin, useUltraWarmMode)
            };
        }

        /// <summary>
        /// Default fraction of full chromatic adaptation applied by
        /// <see cref="NightModeAlgorithm.Perceptual"/> when the user hasn't set an intensity.
        /// 1.0 == the colorimetric shift (<see cref="GetAccurateMultipliers"/>); lower values ease
        /// the white point back toward neutral, preserving colour. 0.8 keeps a clearly warm night
        /// look while retaining far more blue/green chroma than a full shift.
        /// </summary>
        public const double DefaultPerceptualStrength = 0.8;

        /// <summary>
        /// Converts the -50..+50 UI temperature scale to Kelvin (−50 = 3000K, 0 = 6500K,
        /// +50 = 10000K, 70 K per unit) and clamps to the supported night-mode range.
        /// Night mode reaches below the slider's nominal −50 (down to 1900K ≈ −65.7).
        /// </summary>
        public static int TemperatureScaleToKelvin(double temperature)
        {
            temperature = ClampFinite(
                temperature,
                CalibrationSettings.MinimumTemperatureScale,
                CalibrationSettings.MaximumTemperatureScale,
                0.0);

            return ClampNightModeKelvin((int)Math.Round(6500 + temperature * 70));
        }
        
        /// <summary>
        /// Standard approximation (Tanner Helland's algorithm).
        /// Fast, pleasant, and widely used in photo editing.
        /// When useUltraWarmMode is true, applies enhanced curve below 2800K for more dramatic effect.
        /// Returns linear-light multipliers because the LUT applies temperature after
        /// decoding to linear light.
        /// </summary>
        public static (double R, double G, double B) GetStandardMultipliers(int kelvin, bool useUltraWarmMode = false)
        {
            kelvin = ClampNightModeKelvin(kelvin);

            // Reference point: 6500K should return (1, 1, 1)
            var ref6500 = GetRawKelvinRGB_Helland(6500);
            if (!IsUsableRgb(ref6500)) return (1.0, 1.0, 1.0);
            double refMax = MaxChannel(ref6500);

            var target = GetRawKelvinRGB_Helland(kelvin);
            if (!IsUsableRgb(target)) return (1.0, 1.0, 1.0);

            // Helland's approximation returns gamma-encoded RGB-like code values. Normalize
            // target/reference in that code domain, then convert both to linear light before
            // forming ratios; applying code-space ratios inside the linear LUT pipeline
            // under-corrects warm/cool shifts.
            double maxVal = MaxChannel(target);
            if (maxVal <= 0 || !double.IsFinite(maxVal) || refMax <= 0 || !double.IsFinite(refMax))
                return (1.0, 1.0, 1.0);

            double r = LinearCodeRatio(target.r / maxVal, ref6500.r / refMax);
            double g = LinearCodeRatio(target.g / maxVal, ref6500.g / refMax);
            double b = LinearCodeRatio(target.b / maxVal, ref6500.b / refMax);

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

            return ClampMultipliers(r, g, b);
        }

        private static double LinearCodeRatio(double targetCode, double referenceCode)
        {
            double targetLinear = ColorMath.SrgbEotf(ClampSignal(targetCode));
            double referenceLinear = ColorMath.SrgbEotf(ClampSignal(referenceCode));
            return referenceLinear > 1e-9 ? targetLinear / referenceLinear : 1.0;
        }

        private static (double r, double g, double b) GetRawKelvinRGB_Helland(int kelvin)
        {
            kelvin = ClampNightModeKelvin(kelvin);

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
        /// Converts Kelvin -> xy Chromaticity -> XYZ -> linear RGB in the requested wire basis.
        /// Normalized against a 6500K reference computed through the same path AND the same
        /// basis so that, like <see cref="GetStandardMultipliers"/>, 6500K returns exactly
        /// (1, 1, 1) in every basis — the neutral point doesn't shift when the user switches
        /// temperature algorithm or when the pipeline switches between SDR and HDR.
        /// Returns LINEAR-light multipliers because the LUT applies temperature after decoding
        /// to linear light. Returning gamma-encoded ratios here under-corrects warm/cool
        /// shifts and mixes transfer-function math into the white-point shift.
        /// </summary>
        /// <remarks>
        /// This is a von Kries-style diagonal in DISPLAY (wire) RGB primaries, not an LMS-cone
        /// chromatic adaptation transform: a 1D per-channel gamma ramp can only realize a
        /// diagonal scale in the wire's own RGB basis, so an LMS-space CAT is not expressible
        /// here. Because the diagonal is basis-dependent, <paramref name="basis"/> must match
        /// the primaries of the signal the multipliers will scale (sRGB for the SDR ramp,
        /// Rec.2020 for the HDR10 LUT path).
        ///
        /// Luminance is intentionally uncontrolled: normalizing to the brightest channel
        /// (rather than preserving Y) means output luminance falls as the chromaticity target
        /// warms and green/blue are cut. For night use this is desirable — no channel is ever
        /// pushed above 1.0 (which would clip), and the dimmer warm white further reduces
        /// melanopic dose.
        /// </remarks>
        public static (double R, double G, double B) GetAccurateMultipliers(int kelvin, NightBasis basis = NightBasis.Srgb)
        {
            kelvin = ClampNightModeKelvin(kelvin);

            var target = GetRawKelvinLinearRgb_Accurate(kelvin, basis);
            var ref6500 = GetRawKelvinLinearRgb_Accurate(6500, basis);
            return NormalizedRatioMultipliers(target, ref6500);
        }

        /// <summary>
        /// Normalizes target and reference linear RGB each to its own max (so the brightest
        /// channel = 1), then expresses the target as a per-channel ratio against the
        /// reference. When target and reference are identical the ratio is exactly (1,1,1) —
        /// this same-path normalization is what keeps 6500K an exact identity. Target and
        /// reference must be expressed in the SAME wire basis.
        /// </summary>
        private static (double R, double G, double B) NormalizedRatioMultipliers(
            (double r, double g, double b) target,
            (double r, double g, double b) reference)
        {
            if (!IsUsableRgb(target) || !IsUsableRgb(reference)) return (1.0, 1.0, 1.0);

            double targetMax = MaxChannel(target);
            double refMax = MaxChannel(reference);
            if (targetMax <= 0 || refMax <= 0) return (1.0, 1.0, 1.0);

            double r = (target.r / targetMax) / (reference.r / refMax);
            double g = (target.g / targetMax) / (reference.g / refMax);
            double b = (target.b / targetMax) / (reference.b / refMax);

            return ClampMultipliers(r, g, b);
        }

        /// <summary>
        /// Raw linear RGB values for a Kelvin temperature via the Kang/XYZ path, before
        /// any neutral-point normalization. Factored out so <see cref="GetAccurateMultipliers"/>
        /// can compute a 6500K reference through the identical pipeline (same basis included —
        /// mixing bases between target and reference would break the 6500K identity).
        /// </summary>
        private static (double r, double g, double b) GetRawKelvinLinearRgb_Accurate(int kelvin, NightBasis basis)
        {
            kelvin = ClampNightModeKelvin(kelvin);

            var xy = ColorMath.CctToChromaticity(kelvin);
            return XyzToUsableLinearRgb(xy.ToXyz(1.0), basis);
        }

        /// <summary>
        /// Converts a white-point XYZ to linear RGB in the requested wire basis, clipping only
        /// negative out-of-gamut values. High channels are normalized by
        /// <see cref="NormalizedRatioMultipliers"/>; clipping them here would change
        /// chromaticity before the white-balance ratio is formed.
        /// </summary>
        private static (double r, double g, double b) XyzToUsableLinearRgb(CieXyz xyz, NightBasis basis)
        {
            var rgb = basis == NightBasis.Rec2020
                ? ColorMath.XyzToLinearRec2020(xyz)
                : ColorMath.XyzToLinearSrgb(xyz);
            return (PositiveFiniteOrZero(rgb.R), PositiveFiniteOrZero(rgb.G), PositiveFiniteOrZero(rgb.B));
        }

        /// <summary>
        /// Simplistic filter that targets blue/green reduction linearly.
        /// Preserves Red channel fully.
        /// </summary>
        public static (double R, double G, double B) GetBlueReductionMultipliers(int kelvin)
        {
            kelvin = ClampNightModeKelvin(kelvin);

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
        /// Perceptual night-mode white point: incomplete chromatic adaptation modelled in
        /// CAT16 sharpened cone space (Li et al. 2017; CIE 248:2022) via the illuminant-blend
        /// formulation of <see cref="ColorMath.Cat16Adapt"/> — NOT an RGB-space lerp of the
        /// finished multipliers. Returns linear-light per-channel multipliers.
        /// </summary>
        /// <remarks>
        /// The display correction is a 1D per-channel LUT, so only per-channel transforms reach
        /// real content faithfully — a full 3×3 chromatic adaptation cannot be baked in. Within
        /// that constraint the lever that best preserves colour is the *degree* of adaptation D
        /// (<paramref name="strength"/>): the D-adapted white is computed as the D-blend of the
        /// target-Kelvin white and the 6500K reference white in CAT16 cone space, converted
        /// back to XYZ, and only THEN reduced to per-channel display ratios — through the exact
        /// same path <see cref="GetAccurateMultipliers"/> uses (same-path reference
        /// normalization, same wire <paramref name="basis"/> rule: sRGB for the SDR ramp,
        /// Rec.2020 for the HDR10 LUT path), so 6500K stays an exact identity in every basis.
        /// This replaces the previous SoftenToNeutral display-RGB lerp (1 + D·(m − 1)), which
        /// was incomplete von Kries in the display-RGB basis rather than a sharpened cone
        /// space. At D = 1 this equals <see cref="GetAccurateMultipliers"/>; at D = 0 it is
        /// (1, 1, 1); the brightest channel stays at 1.0 (no clipping / colour cast).
        /// </remarks>
        public static (double R, double G, double B) GetPerceptualMultipliers(
            int kelvin,
            double strength = DefaultPerceptualStrength,
            NightBasis basis = NightBasis.Srgb)
        {
            strength = double.IsFinite(strength) ? Math.Clamp(strength, 0.0, 1.0) : DefaultPerceptualStrength;
            kelvin = ClampNightModeKelvin(kelvin);

            // Source and target whites on the same Kang CCT locus the Accurate algorithm uses,
            // both at Y = 1: night mode only moves chromaticity, and the equal-luminance case
            // is what makes the illuminant-blend formulation of D exact (see Cat16Adapt).
            var sourceWhite = ColorMath.CctToChromaticity(6500).ToXyz(1.0);
            var targetWhite = ColorMath.CctToChromaticity(kelvin).ToXyz(1.0);

            // D-adapted white: blend of target and source whites in CAT16 cone space.
            var adaptedTarget = ColorMath.Cat16Adapt(sourceWhite, sourceWhite, targetWhite, strength);

            // Same-path reference: push the 6500K white through the identical CAT16 round
            // trip so that at 6500K target and reference are bit-identical and the ratio is
            // exactly (1, 1, 1). This invariant is load-bearing: the neutral point must not
            // shift when the user switches temperature algorithm.
            var adaptedReference = ColorMath.Cat16Adapt(sourceWhite, sourceWhite, sourceWhite, strength);

            return NormalizedRatioMultipliers(
                XyzToUsableLinearRgb(adaptedTarget, basis),
                XyzToUsableLinearRgb(adaptedReference, basis));
        }

        /// <summary>
        /// Maximum-protection "Ultra Night" (amber): drives toward a red/amber white point,
        /// killing blue and deeply cutting green as the target warms. Blue carries most of a
        /// display's melanopic (circadian) weight, so this minimises melanopic output at the
        /// cost of colour fidelity — a deliberate mode for the darkest part of the evening, not
        /// a colour-accurate one. Red is preserved for readable amber text.
        /// </summary>
        public static (double R, double G, double B) GetUltraNightMultipliers(
            int kelvin,
            NightMelanopicCoefficients? melanopicCoefficients = null,
            NightBasis basis = NightBasis.Srgb)
        {
            kelvin = ClampNightModeKelvin(kelvin);

            double factor = Math.Clamp((6500.0 - kelvin) / (6500.0 - 2000.0), 0.0, 1.0);

            // Base on the true Planckian amber (GetAccurateMultipliers) so the hue sits ON the
            // blackbody locus. Hand-cutting green below that ratio is what produces a magenta
            // cast; keeping the Planckian green ratio avoids it. Then apply an overall luminance
            // reduction — this is the deepest-evening mode, so a full-brightness amber white is
            // both uncomfortable and needlessly high melanopic dose. The dim/floor/green tuning
            // below is a signal-space heuristic, but the Planckian core it scales is
            // basis-dependent and must be derived in the wire basis.
            var (ar, ag, ab) = GetAccurateMultipliers(kelvin, basis);

            double dim = 1.0 - 0.30 * factor;   // → 0.70 at the warm end
            double r = ar * dim;                // ar == 1.0
            double g = ag * dim;
            double b = ab * dim;

            // A channel driven to 0 makes its gamma ramp map white→black, which Windows'
            // SetDeviceGammaRamp rejects (nothing applies). Floor blue at the warmest level the
            // driver accepts before spectral green tuning so the readability floor is honest.
            b = Math.Max(BlueFloor, b);

            if (melanopicCoefficients != null && factor > 0.0)
            {
                g = ApplySpectralGreenReduction(g, b, factor, melanopicCoefficients);
            }

            // Never let green fall below blue (which would swing magenta).
            g = Math.Max(g, b);

            return (r, g, b);
        }

        private static double ApplySpectralGreenReduction(
            double planckianGreen,
            double blue,
            double factor,
            NightMelanopicCoefficients coefficients)
        {
            double redRatio = coefficients.RedMelanopicPerLuminance;
            double greenRatio = coefficients.GreenMelanopicPerLuminance;
            if (redRatio <= 0.0 || greenRatio <= 0.0 || !double.IsFinite(redRatio) || !double.IsFinite(greenRatio))
                return planckianGreen;

            // If the selected CCSS says green has materially more melanopic dose per unit
            // luminance than red, bias Ultra Night toward a deeper red/amber. Keep a readable
            // green floor so text does not become harsh magenta-red.
            double penalty = Math.Clamp((greenRatio / redRatio - 1.0) / 4.0, 0.0, 1.0);
            if (penalty <= 0.0) return planckianGreen;

            double readableGreenFloor = Math.Max(blue * 1.8, 0.12);
            double targetGreen = Math.Min(planckianGreen, readableGreenFloor);
            double strictness = factor * penalty;
            return planckianGreen + (targetGreen - planckianGreen) * strictness;
        }

        // Lowest blue multiplier Ultra Night will emit. Zero produces a flat white→black ramp
        // the GDI gamma validation refuses; this matches the warmest Accurate setting the driver
        // accepts, while still cutting ~90% of blue.
        private const double BlueFloor = 0.10;

        /// <summary>
        /// Calculates RGB multipliers for tint adjustment (green/magenta axis).
        /// </summary>
        public static (double R, double G, double B) GetTintMultipliers(double tint)
        {
            tint = ClampFinite(tint, -50.0, 50.0, 0.0);
            if (Math.Abs(tint) < 0.01) return (1.0, 1.0, 1.0);
            
            // Normalize to -1 to +1 range
            double t = tint / 50.0;
            
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
        /// Order: measured signal-domain 3D LUT, decode to linear light, user adjustments,
        /// encode back to signal RGB.
        /// </summary>
        /// <remarks>
        /// The measured 3D LUT generated by calibration is a signal-domain correction LUT:
        /// its grid input/output values are display RGB code values. User-facing controls
        /// are linear-light operations, so this helper bridges the domains explicitly. Do
        /// not use it after a LUT pipeline has already decoded to linear light; use
        /// <see cref="ApplyUserAdjustmentsLinear"/> for that path.
        /// </summary>
        public static (double R, double G, double B) ApplyCalibration(
            double r, double g, double b,
            CalibrationSettings settings)
        {
            settings = (settings ?? CalibrationSettings.Default).Sanitized();
            r = ClampSignal(r);
            g = ClampSignal(g);
            b = ClampSignal(b);

            // 0. Apply measured 3D LUT correction (if present)
            // This is the colorimeter-measured base correction
            if (settings.MeasuredCorrectionLut != null)
            {
                var corrected = settings.MeasuredCorrectionLut.Lookup((float)r, (float)g, (float)b);
                r = corrected.R;
                g = corrected.G;
                b = corrected.B;
            }

            var (linearR, linearG, linearB) = ApplyUserAdjustmentsLinear(
                ColorMath.SrgbEotf(r),
                ColorMath.SrgbEotf(g),
                ColorMath.SrgbEotf(b),
                settings);

            return (
                ColorMath.SrgbOetf(linearR),
                ColorMath.SrgbOetf(linearG),
                ColorMath.SrgbOetf(linearB));
        }

        /// <summary>
        /// Applies only user-facing adjustments to linear-light RGB.
        /// Order: Dimming → Temperature → Tint → RGB Gains → RGB Offsets.
        /// </summary>
        /// <param name="temperatureBasis">
        /// Wire RGB basis the temperature multipliers are derived in. Must match the container
        /// of the signal being adjusted: <see cref="NightBasis.Srgb"/> for SDR ramps,
        /// <see cref="NightBasis.Rec2020"/> for the HDR10 LUT path. See <see cref="NightBasis"/>.
        /// </param>
        public static (double R, double G, double B) ApplyUserAdjustmentsLinear(
            double r, double g, double b,
            CalibrationSettings settings,
            NightBasis temperatureBasis = NightBasis.Srgb)
        {
            settings = (settings ?? CalibrationSettings.Default).Sanitized();
            r = double.IsFinite(r) ? Math.Clamp(r, 0.0, 1.0) : 0.0;
            g = double.IsFinite(g) ? Math.Clamp(g, 0.0, 1.0) : 0.0;
            b = double.IsFinite(b) ? Math.Clamp(b, 0.0, 1.0) : 0.0;

            // 1. Apply perceptual dimming
            if (settings.Brightness < 100.0)
            {
                r = ApplyDimming(r, settings.Brightness, settings.UseLinearBrightness);
                g = ApplyDimming(g, settings.Brightness, settings.UseLinearBrightness);
                b = ApplyDimming(b, settings.Brightness, settings.UseLinearBrightness);
            }
            
            // 2. Apply temperature. Manual temperature, per-monitor white trim, and
            // scheduled night-mode shifts all live on the same scale and must compose before
            // converting to Kelvin; otherwise direct LUT-generation paths ignore the offset.
            double effectiveTemperature = EffectiveTemperatureScale(settings);
            if (Math.Abs(effectiveTemperature) > 0.01)
            {
                var temp = GetTemperatureMultipliers(
                    effectiveTemperature,
                    settings.Algorithm,
                    settings.UseUltraWarmMode,
                    settings.PerceptualStrength,
                    settings.NightMelanopicCoefficients,
                    temperatureBasis);

                // Constant-Y option: recover the luminance the warm shift removed, within
                // headroom. UltraNight is excluded — its dimming is deliberate melanopic-dose
                // reduction; boosting it back would invert the mode's purpose.
                if (settings.PreserveNightLuminance &&
                    settings.Algorithm != NightModeAlgorithm.UltraNight)
                {
                    temp = RescaleToConstantLuminance(
                        temp,
                        temperatureBasis,
                        settings.NightLuminanceCeiling,
                        settings.Brightness < 100.0
                            ? ApplyDimming(1.0, settings.Brightness, settings.UseLinearBrightness)
                            : 1.0);
                }

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
            
            // 6. Clamp to valid range. Constant-Y raises the ceiling above 1.0 ONLY when the
            // apply service granted real HDR headroom (NightLuminanceCeiling stays 1.0 on the
            // SDR path, whose 256-entry GDI ramp clips at 1.0 and cannot represent boost).
            double upper = settings.PreserveNightLuminance &&
                           settings.Algorithm != NightModeAlgorithm.UltraNight
                ? Math.Max(1.0, settings.NightLuminanceCeiling)
                : 1.0;
            r = Math.Clamp(r, 0.0, upper);
            g = Math.Clamp(g, 0.0, upper);
            b = Math.Clamp(b, 0.0, upper);

            return (r, g, b);
        }

        private static double ClampFinite(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

        /// <summary>
        /// Composes two adjustments on the -50..+50 temperature scale in MIRED space
        /// (reciprocal megakelvin, the perceptually uniform CCT axis) and returns the
        /// composed value on the same scale, clamped to the supported range.
        /// </summary>
        /// <remarks>
        /// CCT is a reciprocal quantity: summing offsets on the linear (K−6500)/70 scale
        /// makes the same nominal step a far larger perceptual step at the warm end, so a
        /// user white trim stacked on a night shift over-warms. Each component is converted
        /// to its mired offset from the 6500K reference, the offsets are summed, and the sum
        /// is converted back. A single non-zero component composes to itself exactly, so this
        /// is behavior-preserving whenever only one adjustment is active. The clamp keeps the
        /// existing Kelvin bounds (1900K–10000K).
        /// </remarks>
        public static double ComposeTemperatureScaleMired(double scaleA, double scaleB)
        {
            scaleA = ClampFinite(scaleA, CalibrationSettings.MinimumTemperatureScale, CalibrationSettings.MaximumTemperatureScale, 0.0);
            scaleB = ClampFinite(scaleB, CalibrationSettings.MinimumTemperatureScale, CalibrationSettings.MaximumTemperatureScale, 0.0);

            // Single-component composition is exact by construction; skip the mired
            // round-trip so existing single-adjustment behavior is bit-identical.
            if (scaleA == 0.0 || scaleB == 0.0)
                return scaleA + scaleB;

            const double referenceMired = 1e6 / 6500.0;
            double miredA = 1e6 / (6500.0 + scaleA * 70.0);
            double miredB = 1e6 / (6500.0 + scaleB * 70.0);

            // Both components are clamped to [1900K, 10000K], so the composed mired value
            // stays strictly positive (worst case ≈ 46 mired at double-maximum cooling).
            double composedMired = miredA + (miredB - referenceMired);
            double composedKelvin = 1e6 / composedMired;

            return ClampFinite(
                (composedKelvin - 6500.0) / 70.0,
                CalibrationSettings.MinimumTemperatureScale,
                CalibrationSettings.MaximumTemperatureScale,
                0.0);
        }

        private static double EffectiveTemperatureScale(CalibrationSettings settings) =>
            ComposeTemperatureScaleMired(settings.Temperature, settings.TemperatureOffset);

        private static int ClampNightModeKelvin(int kelvin) =>
            Math.Clamp(kelvin, MinimumNightModeKelvin, MaximumNightModeKelvin);

        private static double ClampSignal(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

        private static double PositiveFiniteOrZero(double value) =>
            double.IsFinite(value) ? Math.Max(value, 0.0) : 0.0;

        /// <summary>
        /// The per-channel multipliers the applied state puts on WHITE, spectral SHAPE only:
        /// temperature (with the constant-Y rescale when active), tint and RGB gains —
        /// brightness dimming deliberately excluded (it scales magnitude, not spectral
        /// shape; melanopic math applies it once to the absolute luminance instead, see
        /// MelanopicCalculator). Offsets are also excluded (an additive lift is zero at the
        /// white-shape question this answers).
        /// </summary>
        public static (double R, double G, double B) GetAppliedWhiteShapeMultipliers(
            CalibrationSettings settings, NightBasis basis)
        {
            settings = (settings ?? CalibrationSettings.Default).Sanitized();
            double r = 1.0, g = 1.0, b = 1.0;

            double effectiveTemperature = EffectiveTemperatureScale(settings);
            if (Math.Abs(effectiveTemperature) > 0.01)
            {
                var temp = GetTemperatureMultipliers(
                    effectiveTemperature, settings.Algorithm, settings.UseUltraWarmMode,
                    settings.PerceptualStrength, settings.NightMelanopicCoefficients, basis);
                if (settings.PreserveNightLuminance &&
                    settings.Algorithm != NightModeAlgorithm.UltraNight)
                {
                    temp = RescaleToConstantLuminance(
                        temp, basis, settings.NightLuminanceCeiling,
                        settings.Brightness < 100.0
                            ? ApplyDimming(1.0, settings.Brightness, settings.UseLinearBrightness)
                            : 1.0);
                }
                r *= temp.R; g *= temp.G; b *= temp.B;
            }

            if (Math.Abs(settings.Tint) > 0.01)
            {
                var tint = GetTintMultipliers(settings.Tint);
                r *= tint.R; g *= tint.G; b *= tint.B;
            }

            r *= settings.RedGain;
            g *= settings.GreenGain;
            b *= settings.BlueGain;

            return (r, g, b);
        }

        /// <summary>
        /// Relative luminance of the white produced by per-channel multipliers in the given
        /// wire basis: the dot product with the Y row of the basis RGB→XYZ matrix. Both
        /// matrices are white-normalized (rows sum to 1 at Y=1), so this photometric wire-Y
        /// IS CIE Y — there is no separate colorimetric quantity to choose.
        /// </summary>
        internal static double WhiteLuminance((double R, double G, double B) multipliers, NightBasis basis)
        {
            var m = basis == NightBasis.Rec2020
                ? ColorMath.Rec2020ToXyzMatrix
                : ColorMath.SrgbToXyzMatrix;
            return m[1, 0] * multipliers.R + m[1, 1] * multipliers.G + m[1, 2] * multipliers.B;
        }

        /// <summary>
        /// Constant-Y night mode: rescales normalize-to-max temperature multipliers so the
        /// shifted white keeps the luminance of the unshifted white, within representable
        /// headroom. The ideal rescale is s = 1/Y(m); it is clamped so no channel exceeds
        /// <paramref name="ceiling"/> after the dimmed input white passes through
        /// (s ≤ ceiling / (dimmedWhiteFraction · max(m))) — partial preservation is the
        /// graceful degradation when headroom runs out, never an error.
        /// </summary>
        /// <remarks>
        /// The rescale compensates ONLY the temperature-induced luminance loss: user dimming
        /// d cancels in the preserved ratio Y(d·s·m)/Y(d·1) = s·Y(m) = 1, and appears here
        /// solely because dimming CREATES headroom (a dimmed SDR ramp has room above the
        /// dimmed white). On SDR at full brightness ceiling = 1 and max(m) = 1, so s = 1 and
        /// the feature is honestly inert — constant-Y is an HDR-headroom / dimmed-SDR
        /// feature. The preserved Y is the ramp's pre-MHC2 Y (the GPU ramp runs before
        /// Windows' compositor correction); for a calibrated display MHC2 is near-neutral on
        /// the white axis, an accepted approximation. 6500K identity multipliers short-circuit
        /// so the (1,1,1) invariant stays bit-exact rather than relying on the Y row summing
        /// to 1.0 in floating point.
        /// </remarks>
        internal static (double R, double G, double B) RescaleToConstantLuminance(
            (double R, double G, double B) multipliers,
            NightBasis basis,
            double ceiling,
            double dimmedWhiteFraction)
        {
            if (multipliers is (1.0, 1.0, 1.0)) return multipliers;
            if (!IsUsableRgb((multipliers.R, multipliers.G, multipliers.B))) return multipliers;

            double y = WhiteLuminance(multipliers, basis);
            if (!double.IsFinite(y) || y <= 0.0 || y >= 1.0) return multipliers;

            ceiling = ClampFinite(ceiling, 1.0, 4.0, 1.0);
            dimmedWhiteFraction = ClampFinite(dimmedWhiteFraction, 1e-6, 1.0, 1.0);
            double maxChannel = MaxChannel((multipliers.R, multipliers.G, multipliers.B));
            if (maxChannel <= 0.0) return multipliers;

            double s = Math.Min(1.0 / y, ceiling / (dimmedWhiteFraction * maxChannel));
            if (!double.IsFinite(s) || s <= 1.0) return multipliers;

            return (multipliers.R * s, multipliers.G * s, multipliers.B * s);
        }

        private static double MaxChannel((double r, double g, double b) rgb) =>
            Math.Max(rgb.r, Math.Max(rgb.g, rgb.b));

        private static bool IsUsableRgb((double r, double g, double b) rgb) =>
            double.IsFinite(rgb.r) && double.IsFinite(rgb.g) && double.IsFinite(rgb.b) &&
            rgb.r >= 0.0 && rgb.g >= 0.0 && rgb.b >= 0.0 && MaxChannel(rgb) > 0.0;

        private static (double R, double G, double B) ClampMultipliers(double r, double g, double b) =>
            (ClampFinite(r, 0.0, 1.5, 1.0),
             ClampFinite(g, 0.0, 1.5, 1.0),
             ClampFinite(b, 0.0, 1.5, 1.0));
    }
}
