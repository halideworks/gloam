using Xunit;
using HDRGammaController.Core.Calibration;
using System;
using System.Collections.Generic;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Unit tests for 3D LUT operations including interpolation and tone curves.
    /// </summary>
    public class Lut3DTests
    {
        #region Measurement Validation Tests

        private static MeasurementResult Meas(double r, double g, double b, double Y,
            PatchCategory cat = PatchCategory.Grayscale)
        {
            // Neutral-ish XYZ scaled to luminance Y (D65-like chromaticity).
            var xyz = new CieXyz(0.95047 * Y, Y, 1.08883 * Y);
            return new MeasurementResult
            {
                Patch = new ColorPatch { Name = $"{r},{g},{b}", DisplayRgb = new LinearRgb(r, g, b), Category = cat },
                Xyz = xyz,
                IsValid = true
            };
        }

        // A plausible grayscale ramp from ~0.1 to ~120 nits across 12 patches.
        private static List<MeasurementResult> GoodRamp()
        {
            var list = new List<MeasurementResult>();
            for (int i = 0; i < 12; i++)
            {
                double s = i / 11.0;
                double Y = 0.1 + 120.0 * System.Math.Pow(s, 2.2);
                list.Add(Meas(s, s, s, Y));
            }
            return list;
        }

        private static MeasurementResult WireMeas(double requestedNits, double measuredY)
        {
            var xyz = new CieXyz(0.95047 * measuredY, measuredY, 1.08883 * measuredY);
            return new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = $"HDR wire {requestedNits:F0} nits",
                    DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                    Category = PatchCategory.General,
                    Nits = requestedNits
                },
                Xyz = xyz,
                IsValid = true
            };
        }

        [Fact]
        public void Generate_GoodMeasurements_DoesNotThrowValidation()
        {
            var gen = new Lut3DGenerator(StandardTargets.SrgbGamma22, GoodRamp());
            // Should build a LUT without the validation gate throwing.
            var lut = gen.Generate();
            Assert.NotNull(lut);
        }

        [Fact]
        public void Generate_AllBlackMeasurements_ThrowsActionableError()
        {
            // Probe connected but never read the screen: every patch ~0 nits.
            var list = new List<MeasurementResult>();
            for (int i = 0; i < 12; i++) { double s = i / 11.0; list.Add(Meas(s, s, s, 0.01)); }

            var gen = new Lut3DGenerator(StandardTargets.SrgbGamma22, list);
            var ex = Assert.Throws<InvalidOperationException>(() => gen.Generate());
            Assert.Contains("near-black", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Generate_FlatMeasurements_ThrowsActionableError()
        {
            // Same reading for every patch (e.g. stale data): bright but no range.
            var list = new List<MeasurementResult>();
            for (int i = 0; i < 12; i++) { double s = i / 11.0; list.Add(Meas(s, s, s, 100.0)); }

            var gen = new Lut3DGenerator(StandardTargets.SrgbGamma22, list);
            Assert.Throws<InvalidOperationException>(() => gen.Generate());
        }

        [Fact]
        public void MeasurementValidator_NonMonotonicGrayscale_Fails()
        {
            var list = GoodRamp();
            list[7] = Meas(7 / 11.0, 7 / 11.0, 7 / 11.0, list[5].Xyz.Y * 0.5);

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.SrgbGamma22, hdrMode: false);

            Assert.False(result.IsValid);
            Assert.Contains("non-monotonic", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MeasurementValidator_RepeatedWhiteDrift_Fails()
        {
            var list = GoodRamp();
            list.Add(Meas(1, 1, 1, 90));

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.SrgbGamma22, hdrMode: false);

            Assert.False(result.IsValid);
            Assert.Contains("white", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MeasurementValidator_RecoveryText_ForValidSet_ReportsPassedChecks()
        {
            var result = CalibrationMeasurementValidator.ValidateForProfile(
                GoodRamp(), StandardTargets.SrgbGamma22, hdrMode: false);

            string text = CalibrationMeasurementValidator.BuildRecoveryText(result);

            Assert.True(result.IsValid);
            Assert.Contains("passed integrity checks", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MeasurementValidator_RecoveryText_ForNonMonotonicRamp_TargetsDynamicToneChanges()
        {
            var list = GoodRamp();
            list[7] = Meas(7 / 11.0, 7 / 11.0, 7 / 11.0, list[5].Xyz.Y * 0.5);

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.SrgbGamma22, hdrMode: false);

            string text = CalibrationMeasurementValidator.BuildRecoveryText(result);

            Assert.False(result.IsValid);
            Assert.Contains(result.Error!, text);
            Assert.Contains("dynamic contrast", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MeasurementValidator_HdrWithoutWireLadder_AllowsSdrMappedFallback()
        {
            var result = CalibrationMeasurementValidator.ValidateForProfile(
                GoodRamp(), StandardTargets.Rec709Pq, hdrMode: true);

            Assert.True(result.IsValid, result.Error);
        }

        [Fact]
        public void MeasurementValidator_HdrPlausibleWireLadder_Passes()
        {
            var list = GoodRamp();
            foreach (double nits in new[] { 0, 2, 16, 100, 220, 450 })
                list.Add(WireMeas(nits, Math.Max(nits * 0.9, 0.02)));

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.Rec709Pq, hdrMode: true);

            Assert.True(result.IsValid, result.Error);
        }

        [Fact]
        public void MeasurementValidator_HdrPartialWireLadder_Fails()
        {
            var list = GoodRamp();
            foreach (double nits in new[] { 0, 2, 16, 100 })
                list.Add(WireMeas(nits, Math.Max(nits * 0.9, 0.02)));

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.Rec709Pq, hdrMode: true);

            Assert.False(result.IsValid);
            Assert.Contains("wire-ladder", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MeasurementValidator_HdrNearBlackWireLadder_Fails()
        {
            var list = GoodRamp();
            foreach (double nits in new[] { 0, 2, 16, 100, 220, 450 })
                list.Add(WireMeas(nits, 0.02));

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.Rec709Pq, hdrMode: true);

            Assert.False(result.IsValid);
            Assert.Contains("HDR wire-ladder", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("near black", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(2.2)]
        [InlineData(2.4)]
        [InlineData(1.8)]
        public void MeasuredGamma_LeastSquaresFit_RecoversTrueGamma(double trueGamma)
        {
            // Synthesize a perfect power-law grayscale: Y = signal^gamma, scaled to nits.
            // The least-squares fit should recover the gamma within tight tolerance.
            var list = new List<MeasurementResult>();
            for (int i = 0; i <= 16; i++)
            {
                double s = i / 16.0;
                double Y = 120.0 * Math.Pow(s, trueGamma);
                list.Add(Meas(s, s, s, Y));
            }

            var gen = new Lut3DGenerator(StandardTargets.SrgbGamma22, list);
            gen.Generate();
            Assert.NotNull(gen.Characterization);
            // Least-squares should recover the true gamma to within ~0.05.
            Assert.True(Math.Abs(gen.Characterization!.MeasuredGamma - trueGamma) < 0.05,
                $"Expected gamma ~{trueGamma}, got {gen.Characterization.MeasuredGamma:F3}");
        }

        #endregion

        #region Lut3D Basic Tests

        [Fact]
        public void Lut3D_Constructor_CreatesCorrectSize()
        {
            var lut = new Lut3D(17);

            Assert.Equal(17, lut.Size);
        }

        [Fact]
        public void Lut3D_SetEntry_GetEntry_RoundTrip()
        {
            var lut = new Lut3D(9);

            lut.SetEntry(4, 4, 4, 0.5f, 0.6f, 0.7f);
            var result = lut.GetEntry(4, 4, 4);

            Assert.Equal(0.5, (double)result.R, 4);
            Assert.Equal(0.6, (double)result.G, 4);
            Assert.Equal(0.7, (double)result.B, 4);
        }

        [Fact]
        public void Lut3D_Identity_ReturnsInputValues()
        {
            // Create an identity LUT where output = input
            var lut = Lut3D.CreateIdentity(9);

            // Sample some values
            var corners = new[]
            {
                (0f, 0f, 0f),
                (1f, 0f, 0f),
                (0f, 1f, 0f),
                (0f, 0f, 1f),
                (1f, 1f, 1f),
                (0.5f, 0.5f, 0.5f)
            };

            foreach (var (r, g, b) in corners)
            {
                var result = lut.Lookup(r, g, b);
                Assert.InRange(result.R, r - 0.01f, r + 0.01f);
                Assert.InRange(result.G, g - 0.01f, g + 0.01f);
                Assert.InRange(result.B, b - 0.01f, b + 0.01f);
            }
        }

        #endregion

        #region Tetrahedral Interpolation Tests

        [Fact]
        public void Lut3D_Lookup_AtGridPoints_ReturnsExactValues()
        {
            var lut = new Lut3D(9);

            // Set a specific grid point
            lut.SetEntry(3, 4, 5, 0.33f, 0.44f, 0.55f);

            // Lookup at exact grid point
            float r = 3f / 8f; // 3/(size-1) = 3/8
            float g = 4f / 8f;
            float b = 5f / 8f;

            var result = lut.Lookup(r, g, b);

            Assert.InRange(result.R, 0.32f, 0.34f);
            Assert.InRange(result.G, 0.43f, 0.45f);
            Assert.InRange(result.B, 0.54f, 0.56f);
        }

        [Fact]
        public void Lut3D_Lookup_InterpolatesSmooth()
        {
            var lut = Lut3D.CreateIdentity(9);

            // Apply a simple transformation: double all values
            for (int ri = 0; ri < 9; ri++)
                for (int gi = 0; gi < 9; gi++)
                    for (int bi = 0; bi < 9; bi++)
                    {
                        float r = ri / 8f;
                        float g = gi / 8f;
                        float b = bi / 8f;
                        lut.SetEntry(ri, gi, bi,
                            Math.Min(1f, r * 1.1f),
                            Math.Min(1f, g * 1.1f),
                            Math.Min(1f, b * 1.1f));
                    }

            // Test midpoints (between grid points)
            var result = lut.Lookup(0.5f, 0.5f, 0.5f);

            // Should be approximately 0.55 (0.5 * 1.1)
            Assert.InRange(result.R, 0.53f, 0.57f);
        }

        [Fact]
        public void Lut3D_Lookup_HandlesEdgeCases()
        {
            var lut = Lut3D.CreateIdentity(9);

            // Test at boundaries
            var black = lut.Lookup(0, 0, 0);
            var white = lut.Lookup(1, 1, 1);

            Assert.InRange(black.R, -0.01f, 0.01f);
            Assert.InRange(white.R, 0.99f, 1.01f);
        }

        [Fact]
        public void Lut3D_Lookup_ClampsOutOfRange()
        {
            var lut = Lut3D.CreateIdentity(9);

            // Values outside 0-1 should be clamped
            var result = lut.Lookup(1.5f, -0.5f, 0.5f);

            // Should be clamped to valid range
            Assert.InRange(result.R, 0.99f, 1.01f); // Clamped to 1
            Assert.InRange(result.G, -0.01f, 0.01f); // Clamped to 0
        }

        #endregion

        #region ToneCurve Tests

        [Fact]
        public void ToneCurve_Gamma_ReturnsCorrectValues()
        {
            var curve = ToneCurve.CreateGamma(2.2);

            // Test a few points
            double input = 0.5;
            double expected = Math.Pow(input, 2.2);
            double result = curve.Lookup(input);

            Assert.Equal(expected, result, 4);
        }

        [Fact]
        public void ToneCurve_Gamma_InverseLookup_RoundTrip()
        {
            var curve = ToneCurve.CreateGamma(2.4);

            double original = 0.5;
            double forward = curve.Lookup(original);
            double inverse = curve.InverseLookup(forward);

            Assert.Equal(original, inverse, 4);
        }

        [Fact]
        public void ToneCurve_CreateFromPoints_InterpolatesCorrectly()
        {
            var points = new List<(double input, double output)>
            {
                (0.0, 0.0),
                (0.5, 0.25), // Gamma-like curve
                (1.0, 1.0)
            };

            var curve = ToneCurve.CreateFromPoints(points);

            // At midpoint
            double result = curve.Lookup(0.5);
            Assert.InRange(result, 0.24, 0.26);

            // At endpoints
            Assert.InRange(curve.Lookup(0), -0.01, 0.01);
            Assert.InRange(curve.Lookup(1), 0.99, 1.01);
        }

        [Fact]
        public void ToneCurve_IsMonotonic_TrueForValidCurve()
        {
            var points = new List<(double input, double output)>
            {
                (0.0, 0.0),
                (0.25, 0.1),
                (0.5, 0.25),
                (0.75, 0.5),
                (1.0, 1.0)
            };

            var curve = ToneCurve.CreateFromPoints(points);

            Assert.True(curve.IsMonotonic);
        }

        [Fact]
        public void ToneCurve_EnforcesMonotonicity_WhenEnabled()
        {
            // Create a non-monotonic curve
            var points = new List<(double input, double output)>
            {
                (0.0, 0.0),
                (0.25, 0.3),
                (0.5, 0.2),  // Non-monotonic: 0.2 < 0.3
                (0.75, 0.5),
                (1.0, 1.0)
            };

            var curve = ToneCurve.CreateFromPoints(points, enforceMonotonic: true);

            // After enforcement, should be monotonic
            Assert.True(curve.IsMonotonic);
        }

        [Fact]
        public void ToneCurve_NonMonotonic_DetectedWhenNotEnforced()
        {
            var points = new List<(double input, double output)>
            {
                (0.0, 0.0),
                (0.25, 0.5),
                (0.5, 0.3),  // Non-monotonic
                (0.75, 0.7),
                (1.0, 1.0)
            };

            var curve = ToneCurve.CreateFromPoints(points, enforceMonotonic: false);

            Assert.False(curve.IsMonotonic);
        }

        [Fact]
        public void ToneCurve_InverseLookup_WithLut_Works()
        {
            var points = new List<(double input, double output)>
            {
                (0.0, 0.0),
                (0.5, 0.25),
                (1.0, 1.0)
            };

            var curve = ToneCurve.CreateFromPoints(points);

            // Test inverse lookup
            double output = 0.25;
            double input = curve.InverseLookup(output);

            Assert.InRange(input, 0.49, 0.51);
        }

        [Fact]
        public void ToneCurve_Gamma_IsAlwaysMonotonic()
        {
            var curve = ToneCurve.CreateGamma(2.2);

            Assert.True(curve.IsMonotonic);
        }

        #endregion

        #region DisplayCharacterization Tests

        [Fact]
        public void DisplayCharacterization_ContrastRatio_CalculatesCorrectly()
        {
            var char_ = new DisplayCharacterization
            {
                PeakLuminance = 400,
                BlackLevel = 0.4
            };

            Assert.Equal(1000, char_.ContrastRatio, 0);
        }

        [Fact]
        public void DisplayCharacterization_ContrastRatio_HandlesZeroBlackLevel()
        {
            var char_ = new DisplayCharacterization
            {
                PeakLuminance = 400,
                BlackLevel = 0
            };

            Assert.True(double.IsPositiveInfinity(char_.ContrastRatio));
        }

        #endregion

        #region CalibrationMetrics Tests

        [Fact]
        public void CalibrationMetrics_GetGrade_APlusForExcellent()
        {
            var metrics = new CalibrationMetrics { AverageDeltaE = 0.3 };
            Assert.Equal(CalibrationGrade.APLus, metrics.GetGrade());
        }

        [Fact]
        public void CalibrationMetrics_GetGrade_AForGood()
        {
            var metrics = new CalibrationMetrics { AverageDeltaE = 0.8 };
            Assert.Equal(CalibrationGrade.A, metrics.GetGrade());
        }

        [Fact]
        public void CalibrationMetrics_GetGrade_FForPoor()
        {
            var metrics = new CalibrationMetrics { AverageDeltaE = 10 };
            Assert.Equal(CalibrationGrade.F, metrics.GetGrade());
        }

        [Fact]
        public void CalibrationMetrics_AverageGrayscaleDeltaE_EmptyList_ReturnsZero()
        {
            var metrics = new CalibrationMetrics();
            Assert.Equal(0, metrics.AverageGrayscaleDeltaE);
        }

        [Fact]
        public void CalibrationMetrics_AverageGrayscaleDeltaE_CalculatesCorrectly()
        {
            var metrics = new CalibrationMetrics();
            metrics.GrayscaleDeltaEs.AddRange(new[] { 1.0, 2.0, 3.0 });

            Assert.Equal(2.0, metrics.AverageGrayscaleDeltaE, 4);
        }

        #endregion

        #region Lut3D Cube Export Tests

        [Fact]
        public void Lut3D_SaveAsCube_DoesNotThrow()
        {
            var lut = Lut3D.CreateIdentity(9);
            string tempPath = System.IO.Path.GetTempFileName() + ".cube";

            try
            {
                // Should not throw
                lut.SaveAsCube(tempPath, "Test LUT");
                Assert.True(System.IO.File.Exists(tempPath));
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
        }

        #endregion

        #region Gamut Mapping Tests

        [Fact]
        public void CompressToGamut_OutOfRangeColor_ReturnsInGamut()
        {
            var compressed = Lut3DGenerator.CompressToGamut(new LinearRgb(1.3, 0.4, -0.2));

            Assert.True(compressed.IsInGamut);
        }

        [Fact]
        public void CompressToGamut_PreservesHueDirectionBetterThanClipping()
        {
            var source = new LinearRgb(1.4, 0.35, -0.1);
            var compressed = Lut3DGenerator.CompressToGamut(source);
            var clipped = source.Clamp();

            double gray = Math.Clamp(0.2126 * source.R + 0.7152 * source.G + 0.0722 * source.B, 0, 1);
            double compressedRatioRg = (compressed.R - gray) / (compressed.G - gray);
            double sourceRatioRg = (source.R - gray) / (source.G - gray);
            double clippedRatioRg = (clipped.R - gray) / (clipped.G - gray);

            Assert.True(Math.Abs(compressedRatioRg - sourceRatioRg) < Math.Abs(clippedRatioRg - sourceRatioRg));
        }

        #endregion
    }
}
