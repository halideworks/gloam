using System;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Color-science tests for the night-mode pipeline: wire-basis correctness of the
    /// white-point multipliers (a diagonal scale is basis-dependent), the 6500K identity
    /// invariant in every basis, and mired-space temperature composition.
    /// </summary>
    public class NightModeAlgorithmTests
    {
        // Rec.2020 primaries (ITU-R BT.2020), D65 white. Built independently here so the
        // wire matrix used for verification is not the same constant table the
        // implementation consumed.
        private static readonly Chromaticity Rec2020Red = new(0.708, 0.292);
        private static readonly Chromaticity Rec2020Green = new(0.170, 0.797);
        private static readonly Chromaticity Rec2020Blue = new(0.131, 0.046);

        private static double[,] Rec2020RgbToXyz() =>
            ColorMath.CalculateRgbToXyzMatrix(Rec2020Red, Rec2020Green, Rec2020Blue, Chromaticity.D65);

        private static double[,] SrgbRgbToXyz() =>
            ColorMath.CalculateRgbToXyzMatrix(
                ColorMath.SrgbRedPrimary, ColorMath.SrgbGreenPrimary, ColorMath.SrgbBluePrimary, Chromaticity.D65);

        #region 6500K identity in both bases

        [Theory]
        [InlineData(NightBasis.Srgb)]
        [InlineData(NightBasis.Rec2020)]
        public void AccurateMultipliers_6500K_IsExactIdentityInEveryBasis(NightBasis basis)
        {
            // Load-bearing invariant: the target and the 6500K reference are derived through
            // the same path in the same basis, so 6500K is an exact identity — the display's
            // native white is never touched when night mode is neutral.
            var (r, g, b) = ColorAdjustments.GetAccurateMultipliers(6500, basis);

            Assert.Equal(1.0, r, 15);
            Assert.Equal(1.0, g, 15);
            Assert.Equal(1.0, b, 15);
        }

        [Theory]
        [InlineData(NightBasis.Srgb)]
        [InlineData(NightBasis.Rec2020)]
        public void PerceptualAndUltraNightMultipliers_6500K_AreIdentityInEveryBasis(NightBasis basis)
        {
            var perceptual = ColorAdjustments.GetPerceptualMultipliers(6500, basis: basis);
            Assert.Equal(1.0, perceptual.R, 15);
            Assert.Equal(1.0, perceptual.G, 15);
            Assert.Equal(1.0, perceptual.B, 15);

            var ultra = ColorAdjustments.GetUltraNightMultipliers(6500, basis: basis);
            Assert.Equal(1.0, ultra.R, 15);
            Assert.Equal(1.0, ultra.G, 15);
            Assert.Equal(1.0, ultra.B, 15);
        }

        #endregion

        #region Wire-basis correctness of the achieved chromaticity

        [Fact]
        public void AccurateMultipliers_SrgbBasis_LandOnPlanckianTargetThroughSrgbWire()
        {
            double error = AchievedChromaticityError(2700, NightBasis.Srgb, SrgbRgbToXyz());
            Assert.True(error < 0.002, $"sRGB-basis multipliers missed the 2700K target by {error:F5} in xy");
        }

        [Fact]
        public void AccurateMultipliers_Rec2020Basis_LandOnPlanckianTargetThroughRec2020Wire()
        {
            double error = AchievedChromaticityError(2700, NightBasis.Rec2020, Rec2020RgbToXyz());
            Assert.True(error < 0.002, $"Rec.2020-basis multipliers missed the 2700K target by {error:F5} in xy");
        }

        [Fact]
        public void AccurateMultipliers_SrgbBasisOnRec2020Wire_MissesTarget()
        {
            // The F1 bug this suite guards against: sRGB-derived ratios applied to a
            // Rec.2020-encoded (HDR10) wire signal land far off the Planckian target.
            double wrongBasisError = AchievedChromaticityError(2700, NightBasis.Srgb, Rec2020RgbToXyz());
            double rightBasisError = AchievedChromaticityError(2700, NightBasis.Rec2020, Rec2020RgbToXyz());

            Assert.True(wrongBasisError > 0.01,
                $"Expected a gross chromaticity miss from the basis mismatch, got {wrongBasisError:F5}");
            Assert.True(wrongBasisError > 10 * rightBasisError,
                $"Wrong basis ({wrongBasisError:F5}) should be far worse than right basis ({rightBasisError:F5})");
        }

        [Fact]
        public void AccurateMultipliers_BasesDifferMateriallyAtWarmTargets()
        {
            // Rec.2020's more saturated primaries need a smaller green cut than sRGB to reach
            // the same warm chromaticity — the bases must not produce interchangeable ratios.
            var srgb = ColorAdjustments.GetAccurateMultipliers(2700, NightBasis.Srgb);
            var rec2020 = ColorAdjustments.GetAccurateMultipliers(2700, NightBasis.Rec2020);

            Assert.True(rec2020.G > srgb.G + 0.05,
                $"Expected materially higher green in Rec.2020 basis: sRGB={srgb.G:F4}, Rec2020={rec2020.G:F4}");
        }

        /// <summary>
        /// Pushes the multipliers through the given wire RGB→XYZ matrix and returns the
        /// max |Δx|,|Δy| against the Planckian target chromaticity.
        /// </summary>
        /// <remarks>
        /// Anchor: the multipliers are normalized so 6500K is an exact identity, i.e. they
        /// encode the shift FROM the 6500K locus point TO the target as channel ratios in the
        /// wire basis (at apply time the display's native white stands in for the 6500K
        /// point). So the verification applies them to the wire-basis representation of the
        /// 6500K locus point and reads back the achieved chromaticity.
        /// </remarks>
        private static double AchievedChromaticityError(int kelvin, NightBasis multiplierBasis, double[,] wireRgbToXyz)
        {
            var m = ColorAdjustments.GetAccurateMultipliers(kelvin, multiplierBasis);

            var refXyz = ColorMath.CctToChromaticity(6500).ToXyz(1.0);
            double[,] wireXyzToRgb = ColorMath.Invert3x3(wireRgbToXyz);
            double[] start = Multiply(wireXyzToRgb, new[] { refXyz.X, refXyz.Y, refXyz.Z });

            double[] shifted = { start[0] * m.R, start[1] * m.G, start[2] * m.B };
            double[] xyz = Multiply(wireRgbToXyz, shifted);

            double sum = xyz[0] + xyz[1] + xyz[2];
            Assert.True(sum > 0, "Achieved white collapsed to zero");
            double x = xyz[0] / sum;
            double y = xyz[1] / sum;

            var target = ColorMath.CctToChromaticity(kelvin);
            return Math.Max(Math.Abs(x - target.X), Math.Abs(y - target.Y));
        }

        private static double[] Multiply(double[,] m, double[] v) => new[]
        {
            m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
            m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
            m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2]
        };

        #endregion

        #region Mired-space temperature composition

        [Fact]
        public void ComposeTemperatureScaleMired_SingleComponent_IsExact()
        {
            Assert.Equal(-20.0, ColorAdjustments.ComposeTemperatureScaleMired(-20.0, 0.0), 15);
            Assert.Equal(-20.0, ColorAdjustments.ComposeTemperatureScaleMired(0.0, -20.0), 15);
            Assert.Equal(35.0, ColorAdjustments.ComposeTemperatureScaleMired(35.0, 0.0), 15);
            Assert.Equal(0.0, ColorAdjustments.ComposeTemperatureScaleMired(0.0, 0.0), 15);
        }

        [Fact]
        public void ComposeTemperatureScaleMired_TwoWarmComponents_SumInMiredNotKelvin()
        {
            // -20 scale = 5100K = 196.08 mired (+42.23 from the 6500K reference of 153.85).
            // Two of them: 153.85 + 2*42.23 = 238.31 mired = 4196.2K -> scale ≈ -32.91.
            // The old linear-scale sum gave -40 (3700K): visibly over-warmed.
            double composed = ColorAdjustments.ComposeTemperatureScaleMired(-20.0, -20.0);

            double expected = (1e6 / (2.0 * (1e6 / 5100.0) - 1e6 / 6500.0) - 6500.0) / 70.0;
            Assert.Equal(expected, composed, 10);
            Assert.True(composed > -40.0, $"Mired composition must warm less than the linear sum, got {composed:F3}");
        }

        [Fact]
        public void ComposeTemperatureScaleMired_ExtremeInputs_StayBoundedAndFinite()
        {
            foreach (double a in new[] { double.NaN, double.NegativeInfinity, -500.0, -65.0, 0.0, 50.0, 500.0 })
            foreach (double b in new[] { double.NaN, double.PositiveInfinity, -500.0, -65.0, 0.0, 50.0, 500.0 })
            {
                double composed = ColorAdjustments.ComposeTemperatureScaleMired(a, b);
                Assert.True(double.IsFinite(composed));
                Assert.InRange(composed,
                    CalibrationSettings.MinimumTemperatureScale,
                    CalibrationSettings.MaximumTemperatureScale);
            }
        }

        [Fact]
        public void ApplyNightModeToCalibration_ComposesNightShiftInMired()
        {
            // Base user temperature -10 (5800K) stacked with a 3400K night shift must land
            // at the mired sum, not the linear-scale sum.
            var calibration = new CalibrationSettings { Temperature = -10.0 };

            GammaApplyService.ApplyNightModeToCalibration(calibration, 3400, new NightModeSettings());

            double expectedKelvin = 1e6 / (1e6 / 5800.0 + 1e6 / 3400.0 - 1e6 / 6500.0);
            double expectedScale = (expectedKelvin - 6500.0) / 70.0;
            Assert.Equal(expectedScale, calibration.Temperature, 10);
        }

        [Fact]
        public void ApplyUserAdjustmentsLinear_TemperatureAndOffset_ComposeInMired()
        {
            var stacked = new CalibrationSettings { Temperature = -20.0, TemperatureOffset = -20.0 };
            double composedScale = ColorAdjustments.ComposeTemperatureScaleMired(-20.0, -20.0);
            var equivalent = new CalibrationSettings { Temperature = composedScale };

            var fromStacked = ColorAdjustments.ApplyUserAdjustmentsLinear(1.0, 1.0, 1.0, stacked);
            var fromEquivalent = ColorAdjustments.ApplyUserAdjustmentsLinear(1.0, 1.0, 1.0, equivalent);

            Assert.Equal(fromEquivalent.R, fromStacked.R, 12);
            Assert.Equal(fromEquivalent.G, fromStacked.G, 12);
            Assert.Equal(fromEquivalent.B, fromStacked.B, 12);
        }

        #endregion

        #region Pipeline wiring: HDR LUT uses the Rec.2020 basis

        [Fact]
        public void GenerateLut_HdrTemperature_DiffersFromSrgbDerivedMultipliers()
        {
            // The HDR LUT path must derive its temperature diagonal in the Rec.2020 basis;
            // if it silently reverted to sRGB the green channel would be over-cut. Compare a
            // warm HDR LUT against the same settings with no temperature to confirm the green
            // attenuation matches the Rec.2020-basis ratio, not the sRGB one.
            LutGenerator.ClearCache();
            var warm = new CalibrationSettings { Temperature = -50.0, Algorithm = NightModeAlgorithm.AccurateCIE1931 };
            var neutral = new CalibrationSettings();

            var warmLut = LutGenerator.GenerateLut(GammaMode.WindowsDefault, 200.0, warm, isHdr: true);
            var neutralLut = LutGenerator.GenerateLut(GammaMode.WindowsDefault, 200.0, neutral, isHdr: true);

            // Pick a mid-scale index safely inside the SDR region (below 200 nits in PQ).
            int index = 500;
            double linearNeutral = TransferFunctions.PqEotf(neutralLut.G[index]);
            double linearWarm = TransferFunctions.PqEotf(warmLut.G[index]);
            double achievedGreenRatio = linearWarm / linearNeutral;

            var rec2020 = ColorAdjustments.GetAccurateMultipliers(3000, NightBasis.Rec2020);
            var srgb = ColorAdjustments.GetAccurateMultipliers(3000, NightBasis.Srgb);

            Assert.Equal(rec2020.G, achievedGreenRatio, 3);
            Assert.NotEqual(Math.Round(srgb.G, 3), Math.Round(achievedGreenRatio, 3));
        }

        #endregion
    }
}
