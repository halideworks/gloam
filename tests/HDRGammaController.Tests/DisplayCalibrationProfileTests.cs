using System;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class DisplayCalibrationProfileTests
    {
        [Fact]
        public void ToCharacterization_InvalidPersistedColorimetry_FallsBackToSafeDefaults()
        {
            var profile = new DisplayCalibrationProfile
            {
                RedPrimaryX = double.NaN,
                RedPrimaryY = 0.33,
                GreenPrimaryX = 0.30,
                GreenPrimaryY = double.PositiveInfinity,
                BluePrimaryX = -0.1,
                BluePrimaryY = 0.06,
                WhitePointX = 0.9,
                WhitePointY = 0.9,
                BlackLevel = double.NaN,
                PeakLuminance = -100,
                MeasuredGamma = double.PositiveInfinity
            };

            var characterization = profile.ToCharacterization();

            Assert.Equal(Chromaticity.Rec709Red.X, characterization.RedPrimary.X);
            Assert.Equal(Chromaticity.Rec709Green.Y, characterization.GreenPrimary.Y);
            Assert.Equal(Chromaticity.Rec709Blue.X, characterization.BluePrimary.X);
            Assert.Equal(Chromaticity.D65.X, characterization.WhitePoint.X);
            Assert.Equal(0.0, characterization.BlackLevel);
            Assert.Equal(100.0, characterization.PeakLuminance);
            Assert.Equal(2.2, characterization.MeasuredGamma);

            var matrix = characterization.RgbToXyzMatrix;
            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.True(double.IsFinite(matrix[r, c]));
        }

        [Fact]
        public void ToCharacterization_CorruptToneCurve_FallsBackToSafeGammaCurve()
        {
            var profile = ValidProfile();
            profile.RedToneCurve = new[] { 0.0, double.NaN, 1.0 };
            profile.GreenToneCurve = new[] { -1.0, 0.5, 2.0 };
            profile.BlueToneCurve = new[] { 0.0, 0.25, 1.0 };

            var characterization = profile.ToCharacterization();

            Assert.Equal(Math.Pow(0.5, 2.2), characterization.RedToneCurve.Lookup(0.5), 6);
            Assert.Equal(0.0, characterization.GreenToneCurve.Lookup(0.0), 6);
            Assert.Equal(1.0, characterization.GreenToneCurve.Lookup(1.0), 6);
            Assert.True(characterization.BlueToneCurve.IsMonotonic);
        }

        [Fact]
        public void ToCharacterization_ValidPersistedValues_RoundTrip()
        {
            var profile = ValidProfile();

            var characterization = profile.ToCharacterization();

            Assert.Equal(profile.RedPrimaryX, characterization.RedPrimary.X);
            Assert.Equal(profile.GreenPrimaryY, characterization.GreenPrimary.Y);
            Assert.Equal(profile.WhitePointX, characterization.WhitePoint.X);
            Assert.Equal(profile.BlackLevel, characterization.BlackLevel);
            Assert.Equal(profile.PeakLuminance, characterization.PeakLuminance);
            Assert.Equal(profile.MeasuredGamma, characterization.MeasuredGamma);
        }

        [Fact]
        public void ToCharacterization_ChromaticityOutsidePhysicalPlane_FallsBackToSafeDefaults()
        {
            var profile = ValidProfile();
            profile.RedPrimaryX = 0.70;
            profile.RedPrimaryY = 0.35;
            profile.WhitePointX = 0.70;
            profile.WhitePointY = 0.35;

            var characterization = profile.ToCharacterization();

            Assert.Equal(Chromaticity.Rec709Red, characterization.RedPrimary);
            Assert.Equal(Chromaticity.D65, characterization.WhitePoint);
            var matrix = characterization.RgbToXyzMatrix;
            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.True(double.IsFinite(matrix[r, c]));
        }

        [Fact]
        public void ToJson_SanitizesNonFinitePersistedValues()
        {
            var profile = new DisplayCalibrationProfile
            {
                TargetGamma = double.NaN,
                RedPrimaryX = double.NaN,
                RedPrimaryY = 0.33,
                GreenPrimaryX = 0.30,
                GreenPrimaryY = double.PositiveInfinity,
                BluePrimaryX = 0.15,
                BluePrimaryY = 0.06,
                WhitePointX = double.NegativeInfinity,
                WhitePointY = 0.32903,
                BlackLevel = double.NaN,
                PeakLuminance = double.NegativeInfinity,
                MeasuredGamma = double.PositiveInfinity,
                RedToneCurve = new[] { 0.0, double.NaN, 1.0 },
                GreenToneCurve = new[] { 0.0, 0.5, 1.0 },
                BlueToneCurve = new[] { 0.0, 2.0, 1.0 }
            };

            string json = profile.ToJson();

            Assert.DoesNotContain("NaN", json);
            Assert.DoesNotContain("Infinity", json);

            var loaded = DisplayCalibrationProfile.FromJson(json)!;
            Assert.Equal(Chromaticity.Rec709Red.X, loaded.RedPrimaryX);
            Assert.Equal(Chromaticity.Rec709Green.Y, loaded.GreenPrimaryY);
            Assert.Equal(Chromaticity.D65.X, loaded.WhitePointX);
            Assert.Equal(100.0, loaded.PeakLuminance);
            Assert.NotNull(loaded.RedToneCurve);
            Assert.All(loaded.RedToneCurve!, value => Assert.True(double.IsFinite(value)));
        }

        [Fact]
        public void FromCharacterization_SanitizesInvalidMeasuredValuesBeforeReturningProfile()
        {
            var characterization = new DisplayCharacterization
            {
                RedPrimary = new Chromaticity(double.NaN, 0.33),
                GreenPrimary = new Chromaticity(0.30, 0.95),
                BluePrimary = Chromaticity.Rec709Blue,
                WhitePoint = new Chromaticity(double.PositiveInfinity, 0.32903),
                BlackLevel = double.NegativeInfinity,
                PeakLuminance = 0.0,
                MeasuredGamma = double.NaN,
                RedToneCurve = ToneCurve.CreateFromArray(new[] { 0.0, double.NaN, 1.0 }),
                GreenToneCurve = ToneCurve.CreateFromArray(new[] { 0.0, 0.8, 0.4, 1.0 }, enforceMonotonic: true),
                BlueToneCurve = ToneCurve.CreateFromArray(new[] { 0.0, 0.5, 1.0 })
            };

            var profile = DisplayCalibrationProfile.FromCharacterization(
                characterization,
                CalibrationMode.Adaptive,
                TargetColorSpace.SRgb,
                double.PositiveInfinity);

            Assert.Equal(Chromaticity.Rec709Red.X, profile.RedPrimaryX);
            Assert.Equal(Chromaticity.Rec709Green.Y, profile.GreenPrimaryY);
            Assert.Equal(Chromaticity.D65.X, profile.WhitePointX);
            Assert.Equal(0.0, profile.BlackLevel);
            Assert.Equal(100.0, profile.PeakLuminance);
            Assert.Equal(2.2, profile.MeasuredGamma);
            Assert.Equal(2.2, profile.TargetGamma);
            Assert.NotNull(profile.RedToneCurve);
            Assert.All(profile.RedToneCurve!, value => Assert.InRange(value, 0.0, 1.0));
            Assert.NotNull(profile.GreenToneCurve);
            var greenToneCurve = profile.GreenToneCurve!;
            Assert.True(greenToneCurve.SequenceEqual(greenToneCurve.OrderBy(v => v)));
        }

        [Fact]
        public void FromJson_SanitizesLoadedProfileBeforeReturning()
        {
            string json = """
            {
              "version": -5,
              "id": "profile-1",
              "name": "Corrupt loaded profile",
              "mode": 999,
              "targetColorSpace": 999,
              "targetGamma": 99.0,
              "redPrimaryX": 0.9,
              "redPrimaryY": 0.33,
              "greenPrimaryX": 0.3,
              "greenPrimaryY": 0.95,
              "bluePrimaryX": 0.15,
              "bluePrimaryY": 0.06,
              "whitePointX": 0.7,
              "whitePointY": 0.7,
              "blackLevel": -1.0,
              "peakLuminance": 0.0,
              "measuredGamma": 0.2,
              "redToneCurve": [0.0, 0.8, 0.4, 1.0],
              "greenToneCurve": [0.0, 0.5, 1.0],
              "blueToneCurve": [0.0, 2.0, 1.0],
              "referenceLutSize": -17
            }
            """;

            var loaded = DisplayCalibrationProfile.FromJson(json)!;

            Assert.Equal(1, loaded.Version);
            Assert.True(loaded.WasRepairedOnLoad);
            Assert.False(LutGenerator.CanUseCalibratedLut(loaded));
            Assert.Equal(CalibrationMode.Adaptive, loaded.Mode);
            Assert.Equal(TargetColorSpace.SRgb, loaded.TargetColorSpace);
            Assert.Equal(2.2, loaded.TargetGamma);
            Assert.Equal(Chromaticity.Rec709Red.X, loaded.RedPrimaryX);
            Assert.Equal(Chromaticity.Rec709Green.Y, loaded.GreenPrimaryY);
            Assert.Equal(Chromaticity.D65.X, loaded.WhitePointX);
            Assert.Equal(0.0, loaded.BlackLevel);
            Assert.Equal(100.0, loaded.PeakLuminance);
            Assert.Equal(2.2, loaded.MeasuredGamma);
            Assert.Equal(0, loaded.ReferenceLutSize);
            Assert.NotNull(loaded.RedToneCurve);
            Assert.True(loaded.RedToneCurve!.SequenceEqual(loaded.RedToneCurve.OrderBy(v => v)));
            Assert.All(loaded.BlueToneCurve!, value => Assert.InRange(value, 0.0, 1.0));
        }

        [Fact]
        public void FromJson_ToneCurveRepairMarksProfileUnusable()
        {
            string json = """
            {
              "version": 1,
              "id": "profile-tone-curve",
              "name": "Tone curve repaired profile",
              "mode": 1,
              "targetColorSpace": 0,
              "targetGamma": 2.2,
              "redPrimaryX": 0.64,
              "redPrimaryY": 0.33,
              "greenPrimaryX": 0.30,
              "greenPrimaryY": 0.60,
              "bluePrimaryX": 0.15,
              "bluePrimaryY": 0.06,
              "whitePointX": 0.31272,
              "whitePointY": 0.32903,
              "blackLevel": 0.05,
              "peakLuminance": 120.0,
              "measuredGamma": 2.2,
              "redToneCurve": [0.0, 0.8, 0.4, 1.0],
              "greenToneCurve": [0.0, 0.5, 1.0],
              "blueToneCurve": [0.0, 0.5, 1.0]
            }
            """;

            var loaded = DisplayCalibrationProfile.FromJson(json)!;

            Assert.True(loaded.WasRepairedOnLoad);
            Assert.False(LutGenerator.CanUseCalibratedLut(loaded));
            Assert.NotNull(loaded.RedToneCurve);
            Assert.True(loaded.RedToneCurve!.SequenceEqual(loaded.RedToneCurve.OrderBy(v => v)));
        }

        private static DisplayCalibrationProfile ValidProfile() => new()
        {
            RedPrimaryX = Chromaticity.Rec709Red.X,
            RedPrimaryY = Chromaticity.Rec709Red.Y,
            GreenPrimaryX = Chromaticity.Rec709Green.X,
            GreenPrimaryY = Chromaticity.Rec709Green.Y,
            BluePrimaryX = Chromaticity.Rec709Blue.X,
            BluePrimaryY = Chromaticity.Rec709Blue.Y,
            WhitePointX = Chromaticity.D65.X,
            WhitePointY = Chromaticity.D65.Y,
            BlackLevel = 0.05,
            PeakLuminance = 120,
            MeasuredGamma = 2.3
        };
    }
}
