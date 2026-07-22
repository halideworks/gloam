using Xunit;
using HDRGammaController.Core.Calibration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

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

        private static MeasurementResult WireMeas(double requestedNits, double measuredY, bool isValid = true)
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
                IsValid = isValid
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
        public void MeasurementValidator_DriftCheckWhites_TenPercentDrift_Fails()
        {
            // Uncorrected 10% drift across the interleaved DriftCheck anchors (the
            // compensator refuses to fix >8%, leaving it for this gate).
            var list = GoodRamp(); // ramp white is ~120.1 cd/m²
            list.Add(Meas(1, 1, 1, 120.5, PatchCategory.DriftCheck));
            list.Add(Meas(1, 1, 1, 132.5, PatchCategory.DriftCheck)); // ~10% above ramp white

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.SrgbGamma22, hdrMode: false);

            Assert.False(result.IsValid);
            Assert.Contains("drifted", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MeasurementValidator_DriftCheckAnchorsWithinTolerance_Pass()
        {
            // Intended drift-check re-reads must NOT trip the duplicate-white gates when
            // the panel is stable (or the drift was compensated to near-zero).
            var list = GoodRamp();
            list.Add(Meas(1, 1, 1, 120.4, PatchCategory.DriftCheck));
            list.Add(Meas(1, 1, 1, 121.0, PatchCategory.DriftCheck));
            list.Add(Meas(0, 0, 0, 0.11, PatchCategory.DriftCheck));
            list.Add(Meas(0, 0, 0, 0.13, PatchCategory.DriftCheck));

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.SrgbGamma22, hdrMode: false);

            Assert.True(result.IsValid, result.Error);
        }

        [Fact]
        public void MeasurementValidator_DriftCheckBlacks_LargeBlackRise_Fails()
        {
            // Black creeping up during the run: ambient light hit the probe or a dynamic
            // dimming feature changed state. Additive, so drift compensation can't fix it.
            var list = GoodRamp();
            list.Add(Meas(0, 0, 0, 0.10, PatchCategory.DriftCheck));
            list.Add(Meas(0, 0, 0, 2.20, PatchCategory.DriftCheck));

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.SrgbGamma22, hdrMode: false);

            Assert.False(result.IsValid);
            Assert.Contains("black", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MeasurementValidator_NonFiniteAccuracyMeasurement_Fails()
        {
            var list = GoodRamp();
            list[5] = new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "NaN gray",
                    DisplayRgb = new LinearRgb(5 / 11.0, 5 / 11.0, 5 / 11.0),
                    Category = PatchCategory.Grayscale
                },
                Xyz = new CieXyz(double.NaN, 50, 50),
                IsValid = true
            };

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.SrgbGamma22, hdrMode: false);

            Assert.False(result.IsValid);
            Assert.Contains("non-finite", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MeasurementValidator_NegativeAccuracyMeasurement_Fails()
        {
            var list = GoodRamp();
            list[5] = new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "Negative XYZ gray",
                    DisplayRgb = new LinearRgb(5 / 11.0, 5 / 11.0, 5 / 11.0),
                    Category = PatchCategory.Grayscale
                },
                Xyz = new CieXyz(-0.01, 50, 50),
                IsValid = true
            };

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.SrgbGamma22, hdrMode: false);

            Assert.False(result.IsValid);
            Assert.Contains("non-physical", result.Error, StringComparison.OrdinalIgnoreCase);
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
        public void MeasurementValidator_HdrWireLadderWithFailedHighRungs_Fails()
        {
            var list = GoodRamp();
            foreach (double nits in new[] { 0, 2, 16, 64, 100 })
                list.Add(WireMeas(nits, Math.Max(nits * 0.9, 0.02)));
            foreach (double nits in new[] { 220, 450 })
                list.Add(WireMeas(nits, 0.0, isValid: false));

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.Rec709Pq, hdrMode: true);

            Assert.False(result.IsValid);
            Assert.Contains("coverage", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("high-luminance", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MeasurementValidator_HdrInvalidWireLadderAttempts_Fails()
        {
            var list = GoodRamp();
            foreach (double nits in new[] { 0, 2, 16, 100, 220, 450 })
                list.Add(WireMeas(nits, 0.0, isValid: false));

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.Rec709Pq, hdrMode: true);

            Assert.False(result.IsValid);
            Assert.Contains("wire-ladder", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("0 valid patches out of 6", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Windows HDR", CalibrationMeasurementValidator.BuildRecoveryText(result), StringComparison.OrdinalIgnoreCase);
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

        [Fact]
        public void MeasurementValidator_HdrNonFiniteWireLadder_Fails()
        {
            var list = GoodRamp();
            foreach (double nits in new[] { 0, 2, 16, 100, 220, 450 })
                list.Add(WireMeas(nits, Math.Max(nits * 0.9, 0.02)));
            list.Add(new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "HDR wire invalid",
                    DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                    Category = PatchCategory.General,
                    Nits = 650
                },
                Xyz = new CieXyz(1, double.PositiveInfinity, 1),
                IsValid = true
            });

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.Rec709Pq, hdrMode: true);

            Assert.False(result.IsValid);
            Assert.Contains("non-finite", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("wire-ladder", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MeasurementValidator_HdrNegativeWireLadder_Fails()
        {
            var list = GoodRamp();
            foreach (double nits in new[] { 0, 2, 16, 100, 220, 450 })
                list.Add(WireMeas(nits, Math.Max(nits * 0.9, 0.02)));
            list.Add(new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "HDR wire negative",
                    DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                    Category = PatchCategory.General,
                    Nits = 650
                },
                Xyz = new CieXyz(1, 650, -0.01),
                IsValid = true
            });

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.Rec709Pq, hdrMode: true);

            Assert.False(result.IsValid);
            Assert.Contains("non-physical", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("wire-ladder", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(-10.0)]
        public void MeasurementValidator_HdrInvalidRequestedWireNits_Fails(double requestedNits)
        {
            var list = GoodRamp();
            foreach (double nits in new[] { 0, 2, 16, 100, 220, 450 })
                list.Add(WireMeas(nits, Math.Max(nits * 0.9, 0.02)));
            list.Add(WireMeas(requestedNits, 500.0));

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                list, StandardTargets.Rec709Pq, hdrMode: true);

            Assert.False(result.IsValid);
            Assert.Contains("requested luminance", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("wire-ladder", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildCharacterizationOnly_HdrMode_ValidatesWireLadder()
        {
            var list = GoodRamp();
            foreach (double nits in new[] { 0, 2, 16, 100, 220, 450 })
                list.Add(WireMeas(nits, 0.02));

            var gen = new Lut3DGenerator(StandardTargets.Rec709Pq, list);

            var ex = Assert.Throws<InvalidOperationException>(() => gen.BuildCharacterizationOnly(hdrMode: true));
            Assert.Contains("HDR wire-ladder", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("near black", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildCharacterizationOnly_HdrMode_BuildsCharacterizationWithoutLut()
        {
            var list = GoodRamp();
            foreach (double nits in new[] { 0, 2, 16, 100, 220, 450 })
                list.Add(WireMeas(nits, Math.Max(nits * 0.9, 0.02)));

            var gen = new Lut3DGenerator(StandardTargets.Rec709Pq, list);

            var characterization = gen.BuildCharacterizationOnly(hdrMode: true);

            Assert.Same(characterization, gen.Characterization);
            Assert.True(characterization.PeakLuminance > 100);
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

        [Fact]
        public void BuildCharacterization_ToneCurveNormalizesAgainstMeasuredWhitePatch()
        {
            var list = new List<MeasurementResult>();
            for (int i = 0; i <= 16; i++)
            {
                double s = i / 16.0;
                double Y = 0.1 + 120.0 * Math.Pow(s, 2.2);
                if (i == 15)
                    Y = 122.0; // plausible near-white overshoot inside validator tolerance
                list.Add(Meas(s, s, s, Y));
            }

            var gen = new Lut3DGenerator(StandardTargets.SrgbGamma22, list);

            var characterization = gen.BuildCharacterizationOnly();

            Assert.InRange(characterization.RedToneCurve.Lookup(1.0), 0.999, 1.0);
            Assert.InRange(characterization.RedToneCurve.InverseLookup(1.0), 0.999, 1.0);
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

        /// <summary>
        /// Reference implementation of 6-tetrahedron interpolation: sort the fractional
        /// coordinates descending and accumulate corner differences along that axis order.
        /// This is the standard Kasson/Plouffe dissection, valid for every ordering.
        /// </summary>
        private static (float R, float G, float B) TetrahedralGroundTruth(
            Lut3D lut, float r, float g, float b)
        {
            int size = lut.Size;
            float rn = Math.Clamp(r, 0f, 1f) * (size - 1);
            float gn = Math.Clamp(g, 0f, 1f) * (size - 1);
            float bn = Math.Clamp(b, 0f, 1f) * (size - 1);
            int r0 = Math.Min((int)rn, size - 2);
            int g0 = Math.Min((int)gn, size - 2);
            int b0 = Math.Min((int)bn, size - 2);
            float rf = rn - r0, gf = gn - g0, bf = bn - b0;

            // Axis order sorted by descending fraction (0=R, 1=G, 2=B).
            var axes = new[] { (f: rf, a: 0), (f: gf, a: 1), (f: bf, a: 2) };
            Array.Sort(axes, (x, y) => y.f.CompareTo(x.f));

            var offset = new int[3];
            var prev = lut.GetEntry(r0, g0, b0);
            float outR = prev.R, outG = prev.G, outB = prev.B;
            foreach (var (f, a) in axes)
            {
                offset[a] = 1;
                var next = lut.GetEntry(r0 + offset[0], g0 + offset[1], b0 + offset[2]);
                outR += f * (next.R - prev.R);
                outG += f * (next.G - prev.G);
                outB += f * (next.B - prev.B);
                prev = next;
            }
            return (outR, outG, outB);
        }

        [Fact]
        public void Lut3D_LookupTetrahedral_MatchesGroundTruth_InAllSixOrderings()
        {
            // Asymmetric LUT: every corner distinct, so a wrong tetrahedron cannot
            // accidentally agree with the correct one (identity/affine LUTs would).
            var lut = new Lut3D(3);
            var rng = new Random(12345);
            for (int ri = 0; ri < 3; ri++)
                for (int gi = 0; gi < 3; gi++)
                    for (int bi = 0; bi < 3; bi++)
                        lut.SetEntry(ri, gi, bi,
                            (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());

            // One representative point per strict ordering, plus random coverage.
            var probes = new List<(float r, float g, float b)>
            {
                (0.30f, 0.20f, 0.10f), // r > g > b
                (0.30f, 0.10f, 0.20f), // r > b > g
                (0.20f, 0.10f, 0.30f), // b > r > g
                (0.10f, 0.20f, 0.30f), // b > g > r
                (0.10f, 0.30f, 0.20f), // g > b > r
                (0.20f, 0.30f, 0.10f), // g > r > b  (the historically broken region)
                (0.40f, 0.50f, 0.20f), // regression: previously returned out-of-hull values
            };
            for (int i = 0; i < 500; i++)
                probes.Add(((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()));

            foreach (var (r, g, b) in probes)
            {
                var expected = TetrahedralGroundTruth(lut, r, g, b);
                var actual = lut.Lookup(r, g, b);
                Assert.Equal(expected.R, actual.R, 5);
                Assert.Equal(expected.G, actual.G, 5);
                Assert.Equal(expected.B, actual.B, 5);
            }
        }

        [Fact]
        public void Lut3D_LookupTetrahedral_StaysInsideCornerValueHull()
        {
            // Spike at c011 only, all other corners zeroed (the constructor produces
            // an identity LUT). Interpolated values must stay within [0, 10]
            // everywhere; the broken g >= r > b branch produced negative output.
            var lut = new Lut3D(2);
            for (int ri = 0; ri < 2; ri++)
                for (int gi = 0; gi < 2; gi++)
                    for (int bi = 0; bi < 2; bi++)
                        lut.SetEntry(ri, gi, bi, 0f, 0f, 0f);
            lut.SetEntry(0, 1, 1, 10f, 10f, 10f);

            for (float r = 0f; r <= 1f; r += 0.1f)
                for (float g = 0f; g <= 1f; g += 0.1f)
                    for (float b = 0f; b <= 1f; b += 0.1f)
                    {
                        var v = lut.Lookup(r, g, b);
                        Assert.InRange(v.R, -1e-4f, 10.0001f);
                    }

            // The reviewer's original repro: (0.4, 0.5, 0.2) does not touch c011.
            var probe = lut.Lookup(0.4f, 0.5f, 0.2f);
            Assert.Equal(0f, probe.R, 5);
        }

        [Fact]
        public void Lut3D_LookupTetrahedral_ContinuousAcrossTetrahedronBoundaries()
        {
            var lut = new Lut3D(2);
            var rng = new Random(99);
            for (int ri = 0; ri < 2; ri++)
                for (int gi = 0; gi < 2; gi++)
                    for (int bi = 0; bi < 2; bi++)
                        lut.SetEntry(ri, gi, bi,
                            (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());

            const float eps = 1e-4f;
            // Sweep across the r == b diagonal plane inside the r <= g halfspace
            // (the boundary between tetrahedra 5 and 6), and the other planes too.
            for (float t = 0.05f; t < 0.95f; t += 0.05f)
            {
                foreach (var (lo, hi) in new[]
                {
                    (lut.Lookup(t - eps, 0.97f, t + eps), lut.Lookup(t + eps, 0.97f, t - eps)), // r==b plane
                    (lut.Lookup(t - eps, t + eps, 0.03f), lut.Lookup(t + eps, t - eps, 0.03f)), // r==g plane
                    (lut.Lookup(0.97f, t - eps, t + eps), lut.Lookup(0.97f, t + eps, t - eps)), // g==b plane
                })
                {
                    Assert.True(Math.Abs(lo.R - hi.R) < 0.01f, $"discontinuity at t={t}");
                    Assert.True(Math.Abs(lo.G - hi.G) < 0.01f, $"discontinuity at t={t}");
                    Assert.True(Math.Abs(lo.B - hi.B) < 0.01f, $"discontinuity at t={t}");
                }
            }
        }

        [Fact]
        public void Lut3D_Lookup_NonFiniteInputClampsToDomainMinimum()
        {
            var lut = Lut3D.CreateIdentity(9);

            var result = lut.Lookup(float.NaN, float.PositiveInfinity, float.NegativeInfinity);

            Assert.Equal(0.0f, result.R, 6);
            Assert.Equal(0.0f, result.G, 6);
            Assert.Equal(0.0f, result.B, 6);
        }

        [Fact]
        public void Lut3D_SetEntry_RejectsNonFiniteValues()
        {
            var lut = Lut3D.CreateIdentity(2);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                lut.SetEntry(0, 0, 0, float.NaN, 0.0f, 0.0f));
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

        [Fact]
        public void ToneCurve_NonFiniteGammaAndLookupInputs_ReturnFiniteSafeValues()
        {
            var curve = ToneCurve.CreateGamma(double.NaN);

            Assert.True(curve.IsMonotonic);
            Assert.Equal(Math.Pow(0.5, 2.2), curve.Lookup(0.5), 10);
            Assert.Equal(0.0, curve.Lookup(double.NaN), 10);
            Assert.Equal(0.0, curve.InverseLookup(double.PositiveInfinity), 10);
        }

        [Fact]
        public void ToneCurve_CreateFromArray_NonFiniteDataFallsBackToSafeGamma()
        {
            var curve = ToneCurve.CreateFromArray(new[] { 0.0, double.NaN, 1.0 }, enforceMonotonic: true);

            Assert.True(curve.IsMonotonic);
            Assert.Equal(Math.Pow(0.5, 2.2), curve.Lookup(0.5), 6);
            Assert.True(double.IsFinite(curve.InverseLookup(0.5)));
        }

        [Fact]
        public void ToneCurve_CreateFromArray_ClampsOutOfRangeData()
        {
            var curve = ToneCurve.CreateFromArray(new[] { -0.5, 0.5, 1.5 }, enforceMonotonic: true);

            Assert.Equal(0.0, curve.Lookup(0.0), 6);
            Assert.Equal(1.0, curve.Lookup(1.0), 6);
            Assert.True(curve.IsMonotonic);
        }

        [Fact]
        public void ToneCurve_CreateFromPoints_IgnoresInvalidPointsAndClampsOutput()
        {
            var points = new List<(double input, double output)>
            {
                (0.0, -1.0),
                (0.5, double.NaN),
                (1.0, 2.0)
            };

            var curve = ToneCurve.CreateFromPoints(points);

            Assert.Equal(0.0, curve.Lookup(0.0), 6);
            Assert.Equal(1.0, curve.Lookup(1.0), 6);
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

        [Fact]
        public void Lut3D_CubeRoundTrip_UsesInvariantCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            var originalUiCulture = CultureInfo.CurrentUICulture;
            string tempPath = System.IO.Path.GetTempFileName() + ".cube";

            try
            {
                var commaCulture = CultureInfo.GetCultureInfo("fr-FR");
                Thread.CurrentThread.CurrentCulture = commaCulture;
                Thread.CurrentThread.CurrentUICulture = commaCulture;

                var lut = Lut3D.CreateIdentity(3);
                lut.SetEntry(1, 1, 1, 0.125f, 0.5f, 0.875f);
                lut.SaveAsCube(tempPath, "Culture Test");

                string text = System.IO.File.ReadAllText(tempPath);
                Assert.Contains("0.125000 0.500000 0.875000", text);
                Assert.DoesNotContain("0,125000", text);

                var loaded = Lut3D.LoadFromCube(tempPath);
                var entry = loaded.GetEntry(1, 1, 1);
                Assert.Equal(0.125, entry.R, 6);
                Assert.Equal(0.5, entry.G, 6);
                Assert.Equal(0.875, entry.B, 6);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
                Thread.CurrentThread.CurrentUICulture = originalUiCulture;
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
        }

        [Fact]
        public void Lut3D_SaveAsCube_WritesRedFastestPerAdobeSpec()
        {
            // Identity LUT: every entry's value equals its own grid coordinates, so
            // the data line order directly reveals the loop nesting. The Adobe/IRIDAS
            // cube spec requires the RED index to change fastest.
            var lut = Lut3D.CreateIdentity(2);
            string tempPath = System.IO.Path.GetTempFileName() + ".cube";

            try
            {
                lut.SaveAsCube(tempPath);
                var dataLines = new List<string>();
                foreach (var line in System.IO.File.ReadAllLines(tempPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#') ||
                        char.IsLetter(trimmed[0]))
                        continue;
                    dataLines.Add(trimmed);
                }

                Assert.Equal(8, dataLines.Count);
                Assert.Equal("0.000000 0.000000 0.000000", dataLines[0]);
                Assert.Equal("1.000000 0.000000 0.000000", dataLines[1]); // red changes first
                Assert.Equal("0.000000 1.000000 0.000000", dataLines[2]);
                Assert.Equal("1.000000 1.000000 1.000000", dataLines[7]);
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
        }

        [Fact]
        public void Lut3D_LoadFromCube_ReadsRedFastestPerAdobeSpec()
        {
            // Hand-written spec-conformant cube: value encodes (ri, gi, bi).
            // A blue-fastest reader would transpose R and B.
            string tempPath = System.IO.Path.GetTempFileName() + ".cube";
            string[] cube =
            {
                "LUT_3D_SIZE 2",
                "0.0 0.0 0.0", // ri=0 gi=0 bi=0
                "1.0 0.0 0.0", // ri=1 gi=0 bi=0
                "0.0 1.0 0.0", // ri=0 gi=1 bi=0
                "1.0 1.0 0.0", // ri=1 gi=1 bi=0
                "0.0 0.0 1.0", // ri=0 gi=0 bi=1
                "1.0 0.0 1.0", // ri=1 gi=0 bi=1
                "0.0 1.0 1.0", // ri=0 gi=1 bi=1
                "1.0 1.0 1.0", // ri=1 gi=1 bi=1
            };

            try
            {
                System.IO.File.WriteAllLines(tempPath, cube);
                var lut = Lut3D.LoadFromCube(tempPath);

                var e100 = lut.GetEntry(1, 0, 0);
                Assert.Equal(1.0, e100.R, 6);
                Assert.Equal(0.0, e100.B, 6);
                var e001 = lut.GetEntry(0, 0, 1);
                Assert.Equal(0.0, e001.R, 6);
                Assert.Equal(1.0, e001.B, 6);
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
        }

        [Fact]
        public void Lut3D_LoadFromCube_RejectsUnsupportedDirectiveInsteadOfTreatingAsSample()
        {
            string tempPath = System.IO.Path.GetTempFileName() + ".cube";
            string[] cube =
            {
                "TITLE \"Bad metadata\"",
                "LUT_3D_SIZE 2",
                "DOMAIN_MIN 0.0 0.0 0.0",
                "DOMAIN_MAX 1.0 1.0 1.0",
                "LUT_1D_INPUT_RANGE 0.0 1.0",
                "0.0 0.0 0.0",
                "0.0 0.0 1.0",
                "0.0 1.0 0.0",
                "0.0 1.0 1.0",
                "1.0 0.0 0.0",
                "1.0 0.0 1.0",
                "1.0 1.0 0.0",
                "1.0 1.0 1.0"
            };

            try
            {
                System.IO.File.WriteAllLines(tempPath, cube);

                var ex = Assert.Throws<System.IO.InvalidDataException>(() => Lut3D.LoadFromCube(tempPath));
                Assert.Contains("unsupported", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
        }

        [Fact]
        public void Lut3D_LoadFromCube_RejectsMissingEntries()
        {
            string tempPath = System.IO.Path.GetTempFileName() + ".cube";
            string[] cube =
            {
                "LUT_3D_SIZE 2",
                "DOMAIN_MIN 0.0 0.0 0.0",
                "DOMAIN_MAX 1.0 1.0 1.0",
                "0.0 0.0 0.0",
                "0.0 0.0 1.0",
                "0.0 1.0 0.0",
                "0.0 1.0 1.0",
                "1.0 0.0 0.0",
                "1.0 0.0 1.0",
                "1.0 1.0 0.0"
            };

            try
            {
                System.IO.File.WriteAllLines(tempPath, cube);

                var ex = Assert.Throws<System.IO.InvalidDataException>(() => Lut3D.LoadFromCube(tempPath));
                Assert.Contains("expected 8", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
        }

        [Fact]
        public void Lut3D_LoadFromCube_RejectsNonFiniteEntries()
        {
            string tempPath = System.IO.Path.GetTempFileName() + ".cube";
            string[] cube =
            {
                "LUT_3D_SIZE 2",
                "DOMAIN_MIN 0.0 0.0 0.0",
                "DOMAIN_MAX 1.0 1.0 1.0",
                "NaN 0.0 0.0",
                "0.0 0.0 1.0",
                "0.0 1.0 0.0",
                "0.0 1.0 1.0",
                "1.0 0.0 0.0",
                "1.0 0.0 1.0",
                "1.0 1.0 0.0",
                "1.0 1.0 1.0"
            };

            try
            {
                System.IO.File.WriteAllLines(tempPath, cube);

                var ex = Assert.Throws<System.IO.InvalidDataException>(() => Lut3D.LoadFromCube(tempPath));
                Assert.Contains("non-finite", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
        }

        [Fact]
        public void Lut3D_FromBytes_RejectsNonFiniteDomainAndEntries()
        {
            var bytes = Lut3D.CreateIdentity(2).ToBytes();
            var badDomain = (byte[])bytes.Clone();
            Array.Copy(BitConverter.GetBytes(float.NaN), 0, badDomain, 9, sizeof(float));

            Assert.Throws<System.IO.InvalidDataException>(() => Lut3D.FromBytes(badDomain));

            var badEntry = (byte[])bytes.Clone();
            Array.Copy(BitConverter.GetBytes(float.NaN), 0, badEntry, 17, sizeof(float));

            Assert.Throws<System.IO.InvalidDataException>(() => Lut3D.FromBytes(badEntry));
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
        public void CompressToGamut_NonFiniteInput_ReturnsFiniteGamutValue()
        {
            var compressed = Lut3DGenerator.CompressToGamut(
                new LinearRgb(double.NaN, double.PositiveInfinity, double.NegativeInfinity));

            Assert.True(compressed.IsInGamut);
            Assert.True(double.IsFinite(compressed.R));
            Assert.True(double.IsFinite(compressed.G));
            Assert.True(double.IsFinite(compressed.B));
        }

        // Reformulated from the previous linear-RGB ratio assertion: the compressor now
        // works in Oklab, whose whole point is that the OKLAB HUE ANGLE — not a linear
        // signal-space ratio — is what stays constant. The actual claim is therefore
        // asserted directly: compressed hue matches source hue within 0.5 degrees.
        [Fact]
        public void CompressToGamut_PreservesOklabHue()
        {
            var source = new LinearRgb(1.4, 0.35, -0.1);
            var compressed = Lut3DGenerator.CompressToGamut(source);

            Assert.True(compressed.IsInGamut);
            Assert.True(OklabHueDeltaDegrees(source, compressed) < 0.5,
                $"Hue delta {OklabHueDeltaDegrees(source, compressed)} degrees");
        }

        [Fact]
        public void CompressToGamut_HueSweep_PreservesHueAndIsMonotoneInChroma()
        {
            // Sweep out-of-gamut colors: scaled/offset versions of saturated colors all
            // around the hue circle, plus lightness overshoots.
            var sources = new List<LinearRgb>();
            for (int hueStep = 0; hueStep < 24; hueStep++)
            {
                double angle = hueStep * Math.PI * 2 / 24;
                // Build an out-of-gamut color by pushing chroma in Oklab from mid gray.
                var pushed = ColorMath.OklabToLinearSrgb(
                    0.6, 0.5 * Math.Cos(angle), 0.5 * Math.Sin(angle));
                sources.Add(pushed);
            }
            sources.Add(new LinearRgb(1.4, 0.35, -0.1));
            sources.Add(new LinearRgb(-0.2, 1.3, 0.1));
            sources.Add(new LinearRgb(0.2, -0.15, 1.6));
            sources.Add(new LinearRgb(2.0, 1.5, 1.2)); // HDR overshoot (L above white)

            foreach (var source in sources)
            {
                if (source.IsInGamut)
                    continue; // only out-of-gamut inputs exercise compression

                var compressed = Lut3DGenerator.CompressToGamut(source);

                // Result strictly in gamut.
                Assert.True(compressed.IsInGamut, $"Not in gamut for source {source.R},{source.G},{source.B}");

                var (_, srcA, srcB) = ColorMath.LinearSrgbToOklab(source.R, source.G, source.B);
                var (_, cmpA, cmpB) = ColorMath.LinearSrgbToOklab(compressed.R, compressed.G, compressed.B);

                double sourceChroma = Math.Sqrt(srcA * srcA + srcB * srcB);
                double compressedChroma = Math.Sqrt(cmpA * cmpA + cmpB * cmpB);

                // Monotone chroma: compression never increases chroma.
                Assert.True(compressedChroma <= sourceChroma + 1e-9,
                    $"Chroma grew: {sourceChroma} -> {compressedChroma}");

                // Hue preserved within 0.5 degrees (skip near-achromatic results where
                // the hue angle is numerically meaningless).
                if (sourceChroma > 1e-6 && compressedChroma > 1e-6)
                {
                    double delta = OklabHueDeltaDegrees(source, compressed);
                    Assert.True(delta < 0.5,
                        $"Hue delta {delta} degrees for source {source.R},{source.G},{source.B}");
                }
            }
        }

        [Fact]
        public void CompressToGamut_InGamutInput_ReturnsBitExact()
        {
            var input = new LinearRgb(0.123456789, 0.987654321, 0.5);
            var result = Lut3DGenerator.CompressToGamut(input);

            Assert.Equal(input.R, result.R);
            Assert.Equal(input.G, result.G);
            Assert.Equal(input.B, result.B);
        }

        [Fact]
        public void CompressToGamut_GrayInputs_Untouched()
        {
            foreach (double v in new[] { 0.0, 0.18, 0.5, 0.75, 1.0 })
            {
                var gray = new LinearRgb(v, v, v);
                var result = Lut3DGenerator.CompressToGamut(gray);

                Assert.Equal(v, result.R);
                Assert.Equal(v, result.G);
                Assert.Equal(v, result.B);
            }
        }

        [Fact]
        public void CompressToGamut_LightnessOvershoot_ClampsThenCompresses()
        {
            // Brighter than display white in every channel: after the documented step-1
            // lightness clamp, the result should sit at (or extremely near) the white end
            // of the gamut rather than skewing hue.
            var overshoot = new LinearRgb(3.0, 3.0, 3.0);
            var result = Lut3DGenerator.CompressToGamut(overshoot);

            Assert.True(result.IsInGamut);
            Assert.True(result.R > 0.999 && result.G > 0.999 && result.B > 0.999,
                $"Expected ~white, got {result.R},{result.G},{result.B}");
        }

        private static double OklabHueDeltaDegrees(LinearRgb source, LinearRgb compressed)
        {
            var (_, sa, sb) = ColorMath.LinearSrgbToOklab(source.R, source.G, source.B);
            var (_, ca, cb) = ColorMath.LinearSrgbToOklab(compressed.R, compressed.G, compressed.B);

            double sourceHue = Math.Atan2(sb, sa);
            double compressedHue = Math.Atan2(cb, ca);
            double delta = Math.Abs(sourceHue - compressedHue);
            if (delta > Math.PI)
                delta = 2 * Math.PI - delta;

            return delta * 180.0 / Math.PI;
        }

        #endregion
    }
}
