using System;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Constant-Y night mode (roadmap 3.3): the optional rescale that compensates the
    /// luminance the warm shift removes, within headroom. The exactness tests read the
    /// wire-basis Y row from the live ColorMath matrices (never restated literals) so a
    /// wrong-basis regression cannot pass.
    /// </summary>
    public class LuminancePreservingNightModeTests
    {
        private static double BasisY((double R, double G, double B) m, NightBasis basis)
        {
            var mat = basis == NightBasis.Rec2020
                ? ColorMath.Rec2020ToXyzMatrix
                : ColorMath.SrgbToXyzMatrix;
            return mat[1, 0] * m.R + mat[1, 1] * m.G + mat[1, 2] * m.B;
        }

        private static double KelvinToScale(int kelvin) => (kelvin - 6500) / 70.0;

        private static CalibrationSettings NightSettings(
            int kelvin,
            NightModeAlgorithm algorithm = NightModeAlgorithm.Perceptual,
            bool preserve = true,
            double ceiling = 2.0,
            double brightness = 100.0) => new()
        {
            Temperature = KelvinToScale(kelvin),
            Algorithm = algorithm,
            PreserveNightLuminance = preserve,
            NightLuminanceCeiling = ceiling,
            Brightness = brightness,
        };

        // ---- Rescale math ------------------------------------------------------------------

        [Theory]
        [InlineData(NightBasis.Srgb)]
        [InlineData(NightBasis.Rec2020)]
        public void Rescale_Perceptual2700K_RestoresUnitLuminance_Exactly(NightBasis basis)
        {
            var m = ColorAdjustments.GetPerceptualMultipliers(2700, 0.8, basis);
            var rescaled = ColorAdjustments.RescaleToConstantLuminance(m, basis, ceiling: 2.0, dimmedWhiteFraction: 1.0);

            Assert.True(BasisY(m, basis) < 0.999, "test setup: warm shift should lose luminance");
            Assert.Equal(1.0, BasisY(rescaled, basis), 9);
        }

        [Theory]
        [InlineData(NightBasis.Srgb)]
        [InlineData(NightBasis.Rec2020)]
        public void Rescale_Accurate2700K_RestoresUnitLuminance_Exactly(NightBasis basis)
        {
            var m = ColorAdjustments.GetAccurateMultipliers(2700, basis);
            var rescaled = ColorAdjustments.RescaleToConstantLuminance(m, basis, 2.0, 1.0);
            Assert.Equal(1.0, BasisY(rescaled, basis), 9);
        }

        [Fact]
        public void Rescale_Standard2700K_RestoresUnitLuminance_Exactly()
        {
            // Standard (Helland) at 2700K is aggressive (Y ≈ 0.50, needing s ≈ 2.01), so full
            // preservation needs a ceiling above 2 — with the default 2× HDR cap it lands at
            // ~0.995, the correct clamped partial. Use an unclamped ceiling for exactness.
            var m = ColorAdjustments.GetStandardMultipliers(2700);
            var rescaled = ColorAdjustments.RescaleToConstantLuminance(m, NightBasis.Srgb, 4.0, 1.0);
            Assert.Equal(1.0, BasisY(rescaled, NightBasis.Srgb), 9);
        }

        [Fact]
        public void Rescale_FactorsDifferBetweenBases()
        {
            // The wrong-Y-row catcher: the same kelvin needs a different rescale in each wire
            // basis (different multipliers AND different luminance coefficients).
            var srgb = ColorAdjustments.GetPerceptualMultipliers(2700, 0.8, NightBasis.Srgb);
            var rec = ColorAdjustments.GetPerceptualMultipliers(2700, 0.8, NightBasis.Rec2020);

            double sSrgb = ColorAdjustments.RescaleToConstantLuminance(srgb, NightBasis.Srgb, 4.0, 1.0).R / srgb.R;
            double sRec = ColorAdjustments.RescaleToConstantLuminance(rec, NightBasis.Rec2020, 4.0, 1.0).R / rec.R;

            Assert.True(sSrgb > 1.0 && sRec > 1.0);
            Assert.NotEqual(sSrgb, sRec, 4);
        }

        [Fact]
        public void Rescale_IdentityMultipliers_ReturnedBitExact()
        {
            var identity = (1.0, 1.0, 1.0);
            var rescaled = ColorAdjustments.RescaleToConstantLuminance(identity, NightBasis.Srgb, 2.0, 1.0);
            Assert.Equal(identity, rescaled);
        }

        [Fact]
        public void Rescale_ClampedByCeiling_MaxChannelLandsOnCeilingExactly()
        {
            // Accurate 2700K needs s ≈ 1.3+; a ceiling of 1.2 must bind and put the max
            // channel exactly on the ceiling (partial preservation, graceful).
            var m = ColorAdjustments.GetAccurateMultipliers(2700, NightBasis.Srgb);
            double needed = 1.0 / BasisY(m, NightBasis.Srgb);
            Assert.True(needed > 1.2, $"test setup: needed rescale {needed:F3} should exceed the 1.2 ceiling");

            var rescaled = ColorAdjustments.RescaleToConstantLuminance(m, NightBasis.Srgb, 1.2, 1.0);
            double maxChannel = Math.Max(rescaled.R, Math.Max(rescaled.G, rescaled.B));

            Assert.Equal(1.2, maxChannel, 9);
            Assert.True(BasisY(rescaled, NightBasis.Srgb) < 1.0, "clamped rescale cannot fully restore Y");
            Assert.True(BasisY(rescaled, NightBasis.Srgb) > BasisY(m, NightBasis.Srgb),
                "clamped rescale must still recover some luminance");
        }

        [Fact]
        public void Rescale_SdrAtFullBrightness_IsInert()
        {
            // Ceiling 1.0, no dimming: normalize-to-max already has the max channel at 1.0,
            // so there is no headroom and the feature must be honestly inert.
            var m = ColorAdjustments.GetPerceptualMultipliers(2700, 0.8, NightBasis.Srgb);
            var rescaled = ColorAdjustments.RescaleToConstantLuminance(m, NightBasis.Srgb, 1.0, 1.0);
            Assert.Equal(m, rescaled);
        }

        // ---- Full adjustment chain -----------------------------------------------------------

        [Fact]
        public void Chain_Preserve_RestoresWhiteLuminance_InRec2020()
        {
            var settings = NightSettings(2700, ceiling: 2.0);
            var outWhite = ColorAdjustments.ApplyUserAdjustmentsLinear(1, 1, 1, settings, NightBasis.Rec2020);

            var offSettings = NightSettings(2700, preserve: false);
            var offWhite = ColorAdjustments.ApplyUserAdjustmentsLinear(1, 1, 1, offSettings, NightBasis.Rec2020);

            Assert.True(BasisY(offWhite, NightBasis.Rec2020) < 0.95, "unpreserved warm white should be dimmer");
            Assert.Equal(1.0, BasisY(outWhite, NightBasis.Rec2020), 6);
        }

        [Fact]
        public void Chain_PreserveOnSdrFullBrightness_MatchesPreserveOff()
        {
            // Ceiling 1.0 (the SDR path): outputs are bit-identical with the flag on or off.
            var on = ColorAdjustments.ApplyUserAdjustmentsLinear(
                1, 1, 1, NightSettings(2700, ceiling: 1.0), NightBasis.Srgb);
            var off = ColorAdjustments.ApplyUserAdjustmentsLinear(
                1, 1, 1, NightSettings(2700, preserve: false, ceiling: 1.0), NightBasis.Srgb);
            Assert.Equal(off, on);
        }

        [Fact]
        public void Chain_DimmedSdr_CompensatesTemperatureOnly()
        {
            // Brightness 50 creates SDR headroom: constant-Y restores the TEMPERATURE loss
            // (Y equals the dimmed neutral white) without fighting the user's dimming.
            var settings = NightSettings(2700, ceiling: 1.0, brightness: 50.0);
            var outWhite = ColorAdjustments.ApplyUserAdjustmentsLinear(1, 1, 1, settings, NightBasis.Srgb);

            double dimmedNeutral = ColorAdjustments.ApplyDimming(1.0, 50.0);
            Assert.Equal(dimmedNeutral, BasisY(outWhite, NightBasis.Srgb), 6);

            // And nothing clips: every channel stays in the SDR-representable range.
            Assert.InRange(outWhite.R, 0.0, 1.0);
            Assert.InRange(outWhite.G, 0.0, 1.0);
            Assert.InRange(outWhite.B, 0.0, 1.0);
        }

        [Fact]
        public void Chain_UltraNight_IsExcludedFromPreservation()
        {
            var on = ColorAdjustments.ApplyUserAdjustmentsLinear(
                1, 1, 1, NightSettings(2200, NightModeAlgorithm.UltraNight, preserve: true), NightBasis.Srgb);
            var off = ColorAdjustments.ApplyUserAdjustmentsLinear(
                1, 1, 1, NightSettings(2200, NightModeAlgorithm.UltraNight, preserve: false), NightBasis.Srgb);
            Assert.Equal(off, on);
        }

        [Fact]
        public void Chain_At6500K_RemainsExactIdentity()
        {
            var settings = NightSettings(6500, ceiling: 2.0);
            var outWhite = ColorAdjustments.ApplyUserAdjustmentsLinear(1, 1, 1, settings, NightBasis.Rec2020);
            Assert.Equal((1.0, 1.0, 1.0), outWhite);
        }

        // ---- LUT level -----------------------------------------------------------------------

        private static CalibrationSettings HdrBoostSettings() =>
            NightSettings(2700, ceiling: 2.0);

        [Fact]
        public void HdrLut_WithBoost_IsMonotoneAndEndsAtPassthrough()
        {
            var lut = LutGenerator.GenerateLut(GammaMode.Gamma22, 200.0, HdrBoostSettings(), isHdr: true);

            foreach (var (channel, name) in new[] { (lut.R, "R"), (lut.G, "G"), (lut.B, "B"), (lut.Grey, "Grey") })
            {
                for (int i = 0; i < channel.Length; i++)
                {
                    Assert.InRange(channel[i], 0.0, 1.0);
                    if (i > 0)
                    {
                        Assert.True(channel[i] >= channel[i - 1] - 1e-12,
                            $"{name} not monotone at {i}: {channel[i - 1]:G9} -> {channel[i]:G9}");
                        Assert.True(channel[i] - channel[i - 1] < 0.02,
                            $"{name} has a discontinuity at {i}: step {channel[i] - channel[i - 1]:G6}");
                    }
                }
                Assert.Equal(1.0, channel[^1], 9); // top of PQ = true passthrough
            }
        }

        [Fact]
        public void HdrLut_WithBoost_RaisesSdrRegionWhite()
        {
            var boosted = LutGenerator.GenerateLut(GammaMode.Gamma22, 200.0, HdrBoostSettings(), isHdr: true);
            var plain = LutGenerator.GenerateLut(GammaMode.Gamma22, 200.0,
                NightSettings(2700, preserve: false), isHdr: true);

            // At the SDR-white wire position, the boosted green/blue channels must sit above
            // the unpreserved ones (the max channel R is pinned near white level either way).
            int idx = (int)Math.Round(TransferFunctions.PqInverseEotf(200.0) * 1023);
            Assert.True(boosted.G[idx] > plain.G[idx] + 1e-6,
                $"boosted G {boosted.G[idx]:G6} should exceed plain {plain.G[idx]:G6}");
            Assert.True(boosted.B[idx] > plain.B[idx] + 1e-6);
        }

        [Fact]
        public void LutCache_TogglingPreserve_DoesNotServeStaleLut()
        {
            // Same settings except the flag: if the cache key missed the new fields the
            // second call would return the first call's cached LUT.
            var withBoost = LutGenerator.GenerateLut(GammaMode.Gamma22, 201.5, HdrBoostSettings(), isHdr: true);
            var without = LutGenerator.GenerateLut(GammaMode.Gamma22, 201.5,
                NightSettings(2700, preserve: false), isHdr: true);

            Assert.NotEqual(withBoost.G, without.G);
        }

        [Fact]
        public void LutCache_ChangingCeiling_DoesNotServeStaleLut()
        {
            var high = LutGenerator.GenerateLut(GammaMode.Gamma22, 202.5,
                NightSettings(2700, ceiling: 2.0), isHdr: true);
            var low = LutGenerator.GenerateLut(GammaMode.Gamma22, 202.5,
                NightSettings(2700, ceiling: 1.2), isHdr: true);

            Assert.NotEqual(high.G, low.G);
        }

        // ---- Settings plumbing ---------------------------------------------------------------

        [Fact]
        public void NightModeSettingsData_RoundTripsPreserveLuminance()
        {
            var settings = new NightModeSettings { PreserveLuminance = true };
            var data = NightModeSettingsData.FromNightModeSettings(settings);
            Assert.True(data.PreserveLuminance);
            Assert.True(data.ToNightModeSettings().PreserveLuminance);
        }

        [Fact]
        public void NightModeService_CloneSettings_CarriesPreserveLuminance()
        {
            var clone = NightModeService.CloneSettings(new NightModeSettings { PreserveLuminance = true });
            Assert.True(clone.PreserveLuminance);
        }

        [Fact]
        public void ApplyNightModeToCalibration_CopiesPreserveLuminance()
        {
            var calibration = new CalibrationSettings();
            GammaApplyService.ApplyNightModeToCalibration(
                calibration, 2700, new NightModeSettings { PreserveLuminance = true });
            Assert.True(calibration.PreserveNightLuminance);
        }

        [Fact]
        public void CalibrationSettings_CloneAndSanitize_CarryBothFields()
        {
            var settings = new CalibrationSettings
            {
                PreserveNightLuminance = true,
                NightLuminanceCeiling = 1.7,
            };

            var clone = settings.Clone();
            Assert.True(clone.PreserveNightLuminance);
            Assert.Equal(1.7, clone.NightLuminanceCeiling);

            var sanitized = settings.Sanitized();
            Assert.True(sanitized.PreserveNightLuminance);
            Assert.Equal(1.7, sanitized.NightLuminanceCeiling);

            settings.NightLuminanceCeiling = 99.0;
            Assert.Equal(4.0, settings.Sanitized().NightLuminanceCeiling); // clamped
        }
    }
}
