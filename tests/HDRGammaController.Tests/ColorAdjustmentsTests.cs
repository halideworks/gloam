using Xunit;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using System;

namespace HDRGammaController.Tests
{
    public class ColorAdjustmentsTests
    {
        private const double Tolerance = 0.001;

        #region Dimming Tests

        [Fact]
        public void ApplyDimming_100Percent_ReturnsOriginal()
        {
            double result = ColorAdjustments.ApplyDimming(0.5, 100.0);
            Assert.Equal(0.5, result, 4);
        }

        [Fact]
        public void ApplyDimming_0Percent_ReturnsZero()
        {
            double result = ColorAdjustments.ApplyDimming(0.5, 0.0);
            Assert.Equal(0.0, result, 4);
        }

        [Fact]
        public void ApplyDimming_NonFiniteInputs_ReturnsFiniteBoundedValue()
        {
            Assert.Equal(0.0, ColorAdjustments.ApplyDimming(double.NaN, 50.0), 10);
            Assert.Equal(0.5, ColorAdjustments.ApplyDimming(0.5, double.NaN), 10);
            Assert.Equal(0.0, ColorAdjustments.ApplyDimmingNits(double.NaN, 50.0, 200.0), 10);
            Assert.Equal(100.0, ColorAdjustments.ApplyDimmingNits(100.0, double.NaN, 200.0), 10);
        }

        [Fact]
        public void ApplyDimming_Linear_HalvesBrightness()
        {
            double result = ColorAdjustments.ApplyDimming(1.0, 50.0, linear: true);
            Assert.Equal(0.5, result, 4);
        }

        [Fact]
        public void ApplyDimming_Perceptual_PreservesShadows()
        {
            // Perceptual dimming should preserve shadows better than linear
            double perceptual = ColorAdjustments.ApplyDimming(0.1, 50.0, linear: false);
            double linear = ColorAdjustments.ApplyDimming(0.1, 50.0, linear: true);

            // Perceptual should be brighter in shadows
            Assert.True(perceptual > linear);
        }

        [Fact]
        public void ApplyDimmingNits_MatchesSdrDimmingAtWhiteLevel()
        {
            // The headroom curve must meet the SDR curve exactly at the SDR white level,
            // otherwise the LUT has a brightness shelf at the SDR/HDR boundary.
            const double sdrWhite = 200.0;
            foreach (double brightness in new[] { 90.0, 50.0, 20.0 })
            {
                double sdrSide = ColorAdjustments.ApplyDimming(1.0, brightness) * sdrWhite;
                double hdrSide = ColorAdjustments.ApplyDimmingNits(sdrWhite, brightness, sdrWhite);
                Assert.Equal(sdrSide, hdrSide, 6);
            }
        }

        [Fact]
        public void ApplyDimmingNits_MonotonicInNits()
        {
            const double sdrWhite = 200.0;
            double prev = 0;
            for (double nits = 0; nits <= 10000; nits += 50)
            {
                double dimmed = ColorAdjustments.ApplyDimmingNits(nits, 50.0, sdrWhite);
                Assert.True(dimmed >= prev, $"Dimming not monotonic at {nits} nits: {prev} -> {dimmed}");
                prev = dimmed;
            }
        }

        [Fact]
        public void ApplyDimmingNits_ReducesHighlights()
        {
            // A 1000-nit highlight at 50% brightness must come down meaningfully,
            // but not below the dimmed SDR white.
            const double sdrWhite = 200.0;
            double dimmed = ColorAdjustments.ApplyDimmingNits(1000.0, 50.0, sdrWhite);
            double dimmedWhite = ColorAdjustments.ApplyDimming(1.0, 50.0) * sdrWhite;

            Assert.True(dimmed < 1000.0, $"Highlight not dimmed: {dimmed}");
            Assert.True(dimmed > dimmedWhite, $"Highlight crushed below dimmed white: {dimmed} <= {dimmedWhite}");
        }

        #endregion

        #region Temperature Tests - Standard (Helland)

        [Fact]
        public void GetStandardMultipliers_6500K_ReturnsNearNeutral()
        {
            // 6500K (D65) should return approximately neutral (1, 1, 1)
            var (r, g, b) = ColorAdjustments.GetStandardMultipliers(6500);

            Assert.InRange(r, 0.95, 1.05);
            Assert.InRange(g, 0.95, 1.05);
            Assert.InRange(b, 0.95, 1.05);
        }

        [Fact]
        public void GetStandardMultipliers_2700K_IsWarm()
        {
            // Warm temperature should have high red, lower blue
            var (r, g, b) = ColorAdjustments.GetStandardMultipliers(2700);

            Assert.True(r >= g, "Red should be >= Green at warm temperatures");
            Assert.True(g > b, "Green should be > Blue at warm temperatures");
        }

        [Fact]
        public void GetStandardMultipliers_2700K_UsesLinearLightRatios()
        {
            var (r, g, b) = ColorAdjustments.GetStandardMultipliers(2700);

            Assert.InRange(r, 0.99, 1.01);
            Assert.InRange(g, 0.35, 0.45);
            Assert.InRange(b, 0.07, 0.13);
        }

        [Fact]
        public void GetStandardMultipliers_10000K_IsCool()
        {
            // Cool temperature should have lower red, higher blue
            var (r, g, b) = ColorAdjustments.GetStandardMultipliers(10000);

            Assert.True(b >= r, "Blue should be >= Red at cool temperatures");
        }

        [Fact]
        public void TemperatureAndTintMultipliers_NonFiniteInputs_ReturnNeutral()
        {
            Assert.Equal((1.0, 1.0, 1.0),
                ColorAdjustments.GetTemperatureMultipliers(double.NaN, NightModeAlgorithm.Standard));
            Assert.Equal((1.0, 1.0, 1.0), ColorAdjustments.GetTintMultipliers(double.PositiveInfinity));
        }

        [Fact]
        public void DirectKelvinMultipliers_ExtremeInputs_ReturnFiniteBoundedValues()
        {
            foreach (int kelvin in new[] { int.MinValue, -10000, 0, 1000, 6500, 10000, int.MaxValue })
            {
                AssertFiniteBounded(ColorAdjustments.GetStandardMultipliers(kelvin));
                AssertFiniteBounded(ColorAdjustments.GetAccurateMultipliers(kelvin));
                AssertFiniteBounded(ColorAdjustments.GetBlueReductionMultipliers(kelvin));
            }
        }

        #endregion

        #region Temperature Tests - Accurate (CIE 1931)

        [Fact]
        public void GetAccurateMultipliers_6500K_ReturnsNearNeutral()
        {
            // D65 should return approximately neutral
            var (r, g, b) = ColorAdjustments.GetAccurateMultipliers(6500);

            // CIE-based should be close to neutral at D65
            Assert.InRange(r, 0.90, 1.10);
            Assert.InRange(g, 0.90, 1.10);
            Assert.InRange(b, 0.90, 1.10);
        }

        [Fact]
        public void GetAccurateMultipliers_2700K_IsWarm()
        {
            var (r, g, b) = ColorAdjustments.GetAccurateMultipliers(2700);

            Assert.True(r > b, "Red should be > Blue at warm temperatures");
        }

        [Fact]
        public void GetAccurateMultipliers_2700K_UsesLinearLightRatios()
        {
            var (r, g, b) = ColorAdjustments.GetAccurateMultipliers(2700);

            Assert.InRange(r, 0.99, 1.01);
            Assert.InRange(g, 0.40, 0.49);
            Assert.InRange(b, 0.07, 0.13);
        }

        [Fact]
        public void GetAccurateMultipliers_10000K_IsCoolWithoutRedBoost()
        {
            var (r, g, b) = ColorAdjustments.GetAccurateMultipliers(10000);

            Assert.InRange(r, 0.55, 0.65);
            Assert.InRange(g, 0.70, 0.78);
            Assert.InRange(b, 0.99, 1.05);
        }

        [Fact]
        public void GetAccurateMultipliers_BelowApproximationRange_ClampsToSupportedWarmestPoint()
        {
            var belowRange = ColorAdjustments.GetAccurateMultipliers(1000);
            var minSupported = ColorAdjustments.GetAccurateMultipliers(1667);

            Assert.Equal(minSupported.R, belowRange.R, 12);
            Assert.Equal(minSupported.G, belowRange.G, 12);
            Assert.Equal(minSupported.B, belowRange.B, 12);
        }

        #endregion

        #region Temperature Tests - Blue Reduction

        [Fact]
        public void GetBlueReductionMultipliers_6500K_ReturnsNeutral()
        {
            var (r, g, b) = ColorAdjustments.GetBlueReductionMultipliers(6500);

            Assert.Equal(1.0, r, 4);
            Assert.Equal(1.0, g, 4);
            Assert.Equal(1.0, b, 4);
        }

        [Fact]
        public void GetBlueReductionMultipliers_1900K_MaxReduction()
        {
            var (r, g, b) = ColorAdjustments.GetBlueReductionMultipliers(1900);

            // Red should be preserved
            Assert.Equal(1.0, r, 4);
            // Green should be mildly reduced
            Assert.InRange(g, 0.6, 0.8);
            // Blue should be aggressively reduced
            Assert.InRange(b, 0.05, 0.2);
        }

        #endregion

        #region Temperature Tests - Perceptual (partial adaptation)

        [Fact]
        public void GetPerceptualMultipliers_6500K_ReturnsNeutral()
        {
            var (r, g, b) = ColorAdjustments.GetPerceptualMultipliers(6500);

            Assert.InRange(r, 0.99, 1.01);
            Assert.InRange(g, 0.99, 1.01);
            Assert.InRange(b, 0.99, 1.01);
        }

        [Fact]
        public void GetPerceptualMultipliers_2700K_IsWarmWithRedAtUnity()
        {
            var (r, g, b) = ColorAdjustments.GetPerceptualMultipliers(2700);

            // Red stays at 1.0 (brightest channel not scaled up -> no gamut clipping / colour
            // cast), and warmth still holds R > G > B.
            Assert.Equal(1.0, r, 3);
            Assert.True(r > g && g > b, $"Expected R>G>B, got ({r:F3},{g:F3},{b:F3})");
        }

        [Fact]
        public void GetPerceptualMultipliers_2700K_PreservesMoreBlueThanAccurate()
        {
            // Partial adaptation eases toward neutral, so blue/green are cut LESS than the full
            // colorimetric shift — the core "preserve colour" improvement — but still reduced.
            var perceptual = ColorAdjustments.GetPerceptualMultipliers(2700);
            var accurate = ColorAdjustments.GetAccurateMultipliers(2700);

            Assert.True(perceptual.B > accurate.B, "Perceptual should keep more blue than Accurate");
            Assert.True(perceptual.G > accurate.G, "Perceptual should keep more green than Accurate");
            Assert.True(perceptual.B < 1.0, "Perceptual should still reduce blue below neutral");
        }

        [Fact]
        public void DirectPerceptualMultipliers_ExtremeInputs_ReturnFiniteBoundedValues()
        {
            foreach (int kelvin in new[] { int.MinValue, -10000, 0, 1000, 6500, 10000, int.MaxValue })
            {
                AssertFiniteBounded(ColorAdjustments.GetPerceptualMultipliers(kelvin));
            }
        }

        [Fact]
        public void ApplyUserAdjustmentsLinear_Perceptual_WarmsWhite()
        {
            var settings = new CalibrationSettings
            {
                Algorithm = NightModeAlgorithm.Perceptual,
                Temperature = -20.0 // ~5100K
            };

            var (r, g, b) = ColorAdjustments.ApplyUserAdjustmentsLinear(1.0, 1.0, 1.0, settings);

            Assert.True(b < r, "Perceptual should reduce blue relative to red when warming");
            Assert.True(double.IsFinite(r) && double.IsFinite(g) && double.IsFinite(b));
        }

        [Fact]
        public void GetPerceptualMultipliers_LowerStrength_PreservesMoreColor()
        {
            var strong = ColorAdjustments.GetPerceptualMultipliers(2700, 1.0);
            var weak = ColorAdjustments.GetPerceptualMultipliers(2700, 0.5);

            // Lower strength eases toward neutral (1,1,1): blue/green closer to 1.
            Assert.True(weak.B > strong.B, "Lower strength keeps more blue");
            Assert.True(weak.G > strong.G, "Lower strength keeps more green");
            // Strength 1.0 equals the full colorimetric (Accurate) shift.
            var accurate = ColorAdjustments.GetAccurateMultipliers(2700);
            Assert.Equal(accurate.B, strong.B, 6);
        }

        [Fact]
        public void GetUltraNightMultipliers_6500K_ReturnsNeutral()
        {
            var (r, g, b) = ColorAdjustments.GetUltraNightMultipliers(6500);

            Assert.Equal(1.0, r, 4);
            Assert.Equal(1.0, g, 4);
            Assert.Equal(1.0, b, 4);
        }

        [Fact]
        public void GetUltraNightMultipliers_Warm_IsAmberDriverSafeAndDimmed()
        {
            var (r, g, b) = ColorAdjustments.GetUltraNightMultipliers(2200);

            Assert.True(b > 0.0, "Blue must stay above zero so the gamma ramp is not a flat white→black channel");
            Assert.True(b <= 0.12, $"Blue should still be deeply cut (~90%), got {b:F3}");
            // Amber order R > G > B (green ABOVE blue avoids a magenta cast).
            Assert.True(r > g && g > b, $"Expected R>G>B amber, got ({r:F3},{g:F3},{b:F3})");
            // Deepest-evening mode is dimmed, so red is pulled below full.
            Assert.True(r < 1.0 && r > 0.5, $"Ultra Night should dim red below full, got {r:F3}");
        }

        [Fact]
        public void GetUltraNightMultipliers_AllChannelsStayAboveZero()
        {
            // A zero multiplier makes a flat gamma ramp Windows rejects; every channel must
            // keep a floor across the whole night-mode range.
            foreach (int kelvin in new[] { 1000, 1900, 2000, 2426, 2700, 3400, 6500, 10000 })
            {
                var (r, g, b) = ColorAdjustments.GetUltraNightMultipliers(kelvin);
                Assert.True(r > 0.0 && g > 0.0 && b > 0.0, $"{kelvin}K produced a zero channel: ({r:F3},{g:F3},{b:F3})");
            }
        }

        [Fact]
        public void GetUltraNightMultipliers_WithMelanopicCoefficients_ReducesGreenWhenGreenIsCostly()
        {
            var generic = ColorAdjustments.GetUltraNightMultipliers(2200);
            var coefficients = new NightMelanopicCoefficients(
                redMelanopic: 0.1, greenMelanopic: 1.0, blueMelanopic: 2.0,
                redLuminance: 1.0, greenLuminance: 1.0, blueLuminance: 0.4,
                sourceName: "test.ccss");

            var spectral = ColorAdjustments.GetUltraNightMultipliers(2200, coefficients);

            Assert.True(spectral.G < generic.G, $"Expected spectral green cut: generic={generic.G:F3}, spectral={spectral.G:F3}");
            Assert.True(spectral.G >= spectral.B, "Green must stay at or above blue to avoid a magenta cast");
        }

        #endregion

        #region Tint Tests

        [Fact]
        public void GetTintMultipliers_Zero_ReturnsNeutral()
        {
            var (r, g, b) = ColorAdjustments.GetTintMultipliers(0.0);

            Assert.Equal(1.0, r, 4);
            Assert.Equal(1.0, g, 4);
            Assert.Equal(1.0, b, 4);
        }

        [Fact]
        public void GetTintMultipliers_Negative_IncreasesGreen()
        {
            var (r, g, b) = ColorAdjustments.GetTintMultipliers(-25.0);

            Assert.True(g > r, "Negative tint should increase green relative to red");
            Assert.True(g > b, "Negative tint should increase green relative to blue");
        }

        [Fact]
        public void GetTintMultipliers_Positive_IncreasesMagenta()
        {
            var (r, g, b) = ColorAdjustments.GetTintMultipliers(25.0);

            Assert.True(r > g, "Positive tint should increase red relative to green");
            Assert.True(b > g, "Positive tint should increase blue relative to green");
        }

        #endregion

        #region GetTemperatureMultipliers Tests

        [Fact]
        public void GetTemperatureMultipliers_ZeroTemp_ReturnsNearNeutral()
        {
            // Temperature 0 = 6500K in the -50 to +50 scale
            var (r, g, b) = ColorAdjustments.GetTemperatureMultipliers(0.0, NightModeAlgorithm.Standard);

            Assert.InRange(r, 0.95, 1.05);
            Assert.InRange(g, 0.95, 1.05);
            Assert.InRange(b, 0.95, 1.05);
        }

        [Fact]
        public void GetTemperatureMultipliers_Negative50_IsVeryWarm()
        {
            // -50 = 2700K (very warm)
            var (r, g, b) = ColorAdjustments.GetTemperatureMultipliers(-50.0, NightModeAlgorithm.Standard);

            Assert.True(r > b, "Very warm temperature should have more red than blue");
        }

        [Fact]
        public void GetTemperatureMultipliers_MinNightModeScaleResolvesTo1900K()
        {
            var fromScale = ColorAdjustments.GetTemperatureMultipliers(
                CalibrationSettings.MinimumTemperatureScale,
                NightModeAlgorithm.Standard);
            var directKelvin = ColorAdjustments.GetStandardMultipliers(1900);

            Assert.Equal(directKelvin.R, fromScale.R, 12);
            Assert.Equal(directKelvin.G, fromScale.G, 12);
            Assert.Equal(directKelvin.B, fromScale.B, 12);
        }

        #endregion

        #region ApplyCalibration Integration Tests

        [Fact]
        public void ApplyCalibration_DefaultSettings_ReturnsOriginal()
        {
            var settings = CalibrationSettings.Default;
            var (r, g, b) = ColorAdjustments.ApplyCalibration(0.5, 0.5, 0.5, settings);

            Assert.Equal(0.5, r, 3);
            Assert.Equal(0.5, g, 3);
            Assert.Equal(0.5, b, 3);
        }

        [Fact]
        public void ApplyCalibration_WithDimming_ReducesOutput()
        {
            var settings = new CalibrationSettings { Brightness = 50.0 };
            var (r, g, b) = ColorAdjustments.ApplyCalibration(1.0, 1.0, 1.0, settings);

            Assert.True(r < 1.0);
            Assert.True(g < 1.0);
            Assert.True(b < 1.0);
        }

        [Fact]
        public void ApplyCalibration_WithRedGain_IncreasesRed()
        {
            var settings = new CalibrationSettings { RedGain = 1.2 };
            var (r, g, b) = ColorAdjustments.ApplyCalibration(0.5, 0.5, 0.5, settings);

            Assert.True(r > g, "Red gain should increase red relative to green");
        }

        [Fact]
        public void ApplyCalibration_UserAdjustmentsRunInLinearLight()
        {
            var settings = new CalibrationSettings { RedGain = 1.2 };

            var (r, g, b) = ColorAdjustments.ApplyCalibration(0.5, 0.5, 0.5, settings);

            double expectedR = ColorMath.SrgbOetf(ColorMath.SrgbEotf(0.5) * 1.2);
            Assert.Equal(expectedR, r, 10);
            Assert.Equal(0.5, g, 10);
            Assert.Equal(0.5, b, 10);
            Assert.NotEqual(0.5 * 1.2, r, 6);
        }

        [Fact]
        public void ApplyCalibration_ClampsBelowZero()
        {
            var settings = new CalibrationSettings { RedOffset = -1.0 };
            var (r, g, b) = ColorAdjustments.ApplyCalibration(0.1, 0.5, 0.5, settings);

            Assert.Equal(0.0, r, 4);
        }

        [Fact]
        public void ApplyCalibration_ClampsAboveOne()
        {
            var settings = new CalibrationSettings { RedGain = 1.5, RedOffset = 0.5 };
            var (r, g, b) = ColorAdjustments.ApplyCalibration(1.0, 0.5, 0.5, settings);

            Assert.Equal(1.0, r, 4);
        }

        [Fact]
        public void ApplyCalibration_AppliesMeasuredSignalDomainLutBeforeUserAdjustments()
        {
            var lut = new Lut3D(2);
            for (int ri = 0; ri < 2; ri++)
            for (int gi = 0; gi < 2; gi++)
            for (int bi = 0; bi < 2; bi++)
                lut.SetEntry(ri, gi, bi, 0.25f, 0.5f, 0.75f);

            var settings = new CalibrationSettings { MeasuredCorrectionLut = lut };

            var (r, g, b) = ColorAdjustments.ApplyCalibration(0.8, 0.8, 0.8, settings);

            Assert.Equal(0.25, r, 4);
            Assert.Equal(0.5, g, 4);
            Assert.Equal(0.75, b, 4);
        }

        [Fact]
        public void ApplyCalibration_NullSettingsAndNonFiniteInputs_ReturnFiniteBoundedValues()
        {
            var (r, g, b) = ColorAdjustments.ApplyCalibration(
                double.NaN,
                double.PositiveInfinity,
                double.NegativeInfinity,
                null!);

            Assert.True(double.IsFinite(r));
            Assert.True(double.IsFinite(g));
            Assert.True(double.IsFinite(b));
            Assert.InRange(r, 0.0, 1.0);
            Assert.InRange(g, 0.0, 1.0);
            Assert.InRange(b, 0.0, 1.0);
        }

        [Fact]
        public void ApplyUserAdjustmentsLinear_DoesNotApplyMeasuredSignalDomainLut()
        {
            var lut = new Lut3D(2);
            for (int ri = 0; ri < 2; ri++)
            for (int gi = 0; gi < 2; gi++)
            for (int bi = 0; bi < 2; bi++)
                lut.SetEntry(ri, gi, bi, 0.0f, 0.0f, 0.0f);

            var settings = new CalibrationSettings { MeasuredCorrectionLut = lut };

            var (r, g, b) = ColorAdjustments.ApplyUserAdjustmentsLinear(0.25, 0.5, 0.75, settings);

            Assert.Equal(0.25, r, 4);
            Assert.Equal(0.5, g, 4);
            Assert.Equal(0.75, b, 4);
        }

        [Fact]
        public void ApplyUserAdjustmentsLinear_ComposesTemperatureOffset()
        {
            var temperatureOnly = new CalibrationSettings { Temperature = -20.0 };
            var offsetOnly = new CalibrationSettings { TemperatureOffset = -20.0 };

            var fromTemperature = ColorAdjustments.ApplyUserAdjustmentsLinear(1.0, 1.0, 1.0, temperatureOnly);
            var fromOffset = ColorAdjustments.ApplyUserAdjustmentsLinear(1.0, 1.0, 1.0, offsetOnly);

            Assert.Equal(fromTemperature.R, fromOffset.R, 12);
            Assert.Equal(fromTemperature.G, fromOffset.G, 12);
            Assert.Equal(fromTemperature.B, fromOffset.B, 12);
            Assert.True(fromOffset.B < fromOffset.R);
        }

        [Fact]
        public void ApplyUserAdjustmentsLinear_NonFiniteInputsAndSettings_ReturnsFiniteBoundedValues()
        {
            var settings = new CalibrationSettings
            {
                Brightness = double.NaN,
                Temperature = double.PositiveInfinity,
                Tint = double.NegativeInfinity,
                RedGain = double.NaN,
                BlueOffset = double.PositiveInfinity
            };

            var (r, g, b) = ColorAdjustments.ApplyUserAdjustmentsLinear(
                double.NaN, double.PositiveInfinity, double.NegativeInfinity, settings);

            Assert.True(double.IsFinite(r));
            Assert.True(double.IsFinite(g));
            Assert.True(double.IsFinite(b));
            Assert.InRange(r, 0.0, 1.0);
            Assert.InRange(g, 0.0, 1.0);
            Assert.InRange(b, 0.0, 1.0);
        }

        #endregion

        private static void AssertFiniteBounded((double R, double G, double B) multipliers)
        {
            Assert.True(double.IsFinite(multipliers.R));
            Assert.True(double.IsFinite(multipliers.G));
            Assert.True(double.IsFinite(multipliers.B));
            Assert.InRange(multipliers.R, 0.0, 1.5);
            Assert.InRange(multipliers.G, 0.0, 1.5);
            Assert.InRange(multipliers.B, 0.0, 1.5);
        }
    }
}
