using Xunit;
using HDRGammaController.Core;
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
        public void GetStandardMultipliers_10000K_IsCool()
        {
            // Cool temperature should have lower red, higher blue
            var (r, g, b) = ColorAdjustments.GetStandardMultipliers(10000);

            Assert.True(b >= r, "Blue should be >= Red at cool temperatures");
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

        #endregion
    }
}
