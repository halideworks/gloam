using System;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class CalibrationTargetTests
    {
        [Fact]
        public void StandardTargets_All_ExposesOnlyProductionHdrDesktopTarget()
        {
            Assert.Contains(StandardTargets.Rec709Pq, StandardTargets.All);
            Assert.DoesNotContain(StandardTargets.Rec2020Pq, StandardTargets.All);
            Assert.DoesNotContain(StandardTargets.Rec2020Hlg, StandardTargets.All);
            Assert.DoesNotContain(StandardTargets.P3Pq, StandardTargets.All);

            var hdrTargets = StandardTargets.All.Where(t => t.IsHdr).ToList();
            var target = Assert.Single(hdrTargets);
            Assert.Same(StandardTargets.Rec709Pq, target);
        }

        [Fact]
        public void StandardTargets_GetByName_ResolvesHdrDesktopToSupportedPqTarget()
        {
            var target = StandardTargets.GetByName("HDR Desktop");

            Assert.Same(StandardTargets.Rec709Pq, target);
            Assert.Equal(Chromaticity.Rec709Red, target!.RedPrimary);
            Assert.Equal(TransferFunctionType.Pq, target.TransferFunction);
        }

        [Fact]
        public void ApplyTransferFunctions_NonFiniteInputs_ReturnFiniteBoundedValues()
        {
            foreach (var target in StandardTargets.All.Concat(new[]
                     {
                         StandardTargets.Rec2020Pq,
                         StandardTargets.Rec2020Hlg,
                         StandardTargets.P3Pq
                     }))
            {
                AssertTransferOutputIsFiniteAndBounded(target.ApplyOetf(double.NaN), target.Name);
                AssertTransferOutputIsFiniteAndBounded(target.ApplyEotf(double.PositiveInfinity), target.Name);
            }
        }

        [Fact]
        public void CustomTarget_NonFiniteGammaAndPeak_FallBackToSafeTransferParameters()
        {
            var gammaTarget = StandardTargets.CreateNative(
                Chromaticity.Rec709Red,
                Chromaticity.Rec709Green,
                Chromaticity.Rec709Blue,
                Chromaticity.D65,
                gamma: double.NaN);

            Assert.Equal(Math.Pow(0.5, 2.2), gammaTarget.ApplyEotf(0.5), 10);

            var pqTarget = new CalibrationTarget
            {
                Name = "Corrupt PQ",
                RedPrimary = Chromaticity.Rec709Red,
                GreenPrimary = Chromaticity.Rec709Green,
                BluePrimary = Chromaticity.Rec709Blue,
                WhitePoint = Chromaticity.D65,
                TransferFunction = TransferFunctionType.Pq,
                PeakLuminance = double.NaN
            };

            Assert.Equal(1.0, pqTarget.ApplyEotf(1.0), 10);
        }

        [Fact]
        public void PqDesktopPatchTargets_UseSdrSrgbContentCurve()
        {
            var patches = PatchSetGenerator.GeneratePatchSet(
                StandardTargets.Rec709Pq,
                PatchSetGenerator.CalibrationPreset.GrayscaleOnly);

            var midGray = patches.Single(p => p.Name == "Gray 50%");

            // Patch signals are snapped to the 8-bit grid at generation time (M9), so the
            // target is computed from the snapped stimulus (128/255), not the ideal 0.5.
            double snapped = PatchSetGenerator.Snap8Bit(0.5);
            Assert.Equal(128.0 / 255.0, snapped, 12);
            Assert.NotNull(midGray.TargetXyz);
            Assert.Equal(ColorMath.SrgbEotf(snapped), midGray.TargetXyz!.Value.Y, 10);
            Assert.NotEqual(StandardTargets.Rec709Pq.ApplyEotf(snapped), midGray.TargetXyz.Value.Y, 6);
        }

        private static void AssertTransferOutputIsFiniteAndBounded(double value, string targetName)
        {
            Assert.True(double.IsFinite(value), $"{targetName} produced non-finite transfer output");
            Assert.InRange(value, 0.0, 1.0);
        }

        // ------------------------------------------------------------------ M3: tone semantics

        [Fact]
        public void SrgbGamma22_IsPurePowerGamma22_NotPiecewise()
        {
            // M3 semantic change: a display-calibration target named "Gamma 2.2" means a PURE
            // power 2.2 EOTF. The piecewise sRGB curve diverges by up to ~3x in linear
            // luminance below 10% signal; grading a pure-2.2 correction against it built a
            // permanent ~1.5-2 dE2000 shadow penalty into every report.
            var target = StandardTargets.SrgbGamma22;

            Assert.Equal(TransferFunctionType.Gamma, target.TransferFunction);
            foreach (double v in new[] { 0.01, 0.02, 0.05, 0.1, 0.5, 0.9 })
                Assert.Equal(Math.Pow(v, 2.2), target.ApplyEotf(v), 12);

            // Deep shadows: pure 2.2 and piecewise sRGB genuinely differ (the whole point).
            Assert.NotEqual(ColorMath.SrgbEotf(0.02), target.ApplyEotf(0.02), 4);
        }

        [Fact]
        public void SrgbPiecewise_ExistsButIsOutsideTheDefaultFlow()
        {
            var piecewise = StandardTargets.SrgbPiecewise;

            Assert.Equal(TransferFunctionType.Srgb, piecewise.TransferFunction);
            Assert.Equal(ColorMath.SrgbEotf(0.5), piecewise.ApplyEotf(0.5), 12);
            Assert.DoesNotContain(piecewise, StandardTargets.All);
        }

        // ------------------------------------------------------------------ m4: BT.1886 black

        [Fact]
        public void Bt1886_WithMeasuredBlack_MatchesStandardAbFormulation_AndDiffersFromPure24()
        {
            // BT.1886 with Lw=100, Lb=0.1 at v=0.5, against the standard's explicit a/b form:
            //   a = (Lw^(1/2.4) - Lb^(1/2.4))^2.4,  b = Lb^(1/2.4) / (Lw^(1/2.4) - Lb^(1/2.4))
            //   L(V) = a * (V + b)^2.4
            var target = StandardTargets.Rec709Gamma24.WithBlackLevel(0.1); // ReferenceWhite = 100

            const double lw = 100.0, lb = 0.1, g = 2.4;
            double lwRoot = Math.Pow(lw, 1.0 / g);
            double lbRoot = Math.Pow(lb, 1.0 / g);
            double a = Math.Pow(lwRoot - lbRoot, g);
            double b = lbRoot / (lwRoot - lbRoot);
            double expected = a * Math.Pow(0.5 + b, g) / lw;

            Assert.Equal(expected, target.ApplyEotf(0.5), 10);
            Assert.NotEqual(Math.Pow(0.5, 2.4), target.ApplyEotf(0.5), 4);

            // The un-wired target (BlackLevel = 0) still degenerates to pure 2.4.
            Assert.Equal(Math.Pow(0.5, 2.4), StandardTargets.Rec709Gamma24.ApplyEotf(0.5), 10);

            // Round trip through the inverse EOTF.
            Assert.Equal(0.5, target.ApplyOetf(target.ApplyEotf(0.5)), 8);
        }

        // ------------------------------------------------------------------ M2-HLG: OOTF

        [Fact]
        public void HlgEotf_At1000NitPeak_AppliesSystemGamma12()
        {
            // BT.2100-2: EOTF = OOTF[OETF^-1], gamma = 1.2 + 0.42*log10(Lw/1000); at the
            // 1000 cd/m2 reference display gamma is exactly 1.2. Signal 0.5 is the HLG knee:
            // scene linear = 1/12, display linear = (1/12)^1.2.
            var hlg = StandardTargets.Rec2020Hlg; // PeakLuminance = 1000
            double sceneMid = 1.0 / 12.0;

            Assert.Equal(Math.Pow(sceneMid, 1.2), hlg.ApplyEotf(0.5), 10);

            // Mid-signal luminance ratio vs the OOTF-less (inverse-OETF-only) value is the
            // Y_S^(gamma-1) = Y_S^0.2 factor.
            Assert.Equal(Math.Pow(sceneMid, 0.2), hlg.ApplyEotf(0.5) / sceneMid, 10);

            // Endpoints preserved.
            Assert.Equal(0.0, hlg.ApplyEotf(0.0), 12);
            Assert.Equal(1.0, hlg.ApplyEotf(1.0), 10);
        }

        [Fact]
        public void HlgEotfOetf_RoundTrip_IsIdentityWithin1e6()
        {
            var hlg = StandardTargets.Rec2020Hlg;
            for (int i = 0; i <= 100; i++)
            {
                double signal = i / 100.0;
                double roundTrip = hlg.ApplyOetf(hlg.ApplyEotf(signal));
                Assert.True(Math.Abs(roundTrip - signal) < 1e-6,
                    $"HLG round trip diverged at {signal:F2}: {roundTrip:R}");
            }
        }

        [Fact]
        public void HlgSystemGamma_TracksPeakLuminance()
        {
            // At Lw = 2000: gamma = 1.2 + 0.42*log10(2) ~= 1.3264.
            var hlg2000 = new CalibrationTarget
            {
                Name = "HLG 2000",
                RedPrimary = Chromaticity.Rec2020Red,
                GreenPrimary = Chromaticity.Rec2020Green,
                BluePrimary = Chromaticity.Rec2020Blue,
                WhitePoint = Chromaticity.D65,
                TransferFunction = TransferFunctionType.Hlg,
                PeakLuminance = 2000
            };
            double gamma = 1.2 + 0.42 * Math.Log10(2.0);

            Assert.Equal(Math.Pow(1.0 / 12.0, gamma), hlg2000.ApplyEotf(0.5), 10);
        }
    }
}
