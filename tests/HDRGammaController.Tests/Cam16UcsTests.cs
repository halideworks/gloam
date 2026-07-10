using System;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// CAM16-UCS forward model. The headline pin is the canonical Li et al. 2017 test
    /// vector (the CIECAM02/CAM16 worked example every published implementation reproduces);
    /// the rest are structural invariants the 3.2 dose solver relies on.
    /// </summary>
    public class Cam16UcsTests
    {
        [Fact]
        public void CanonicalTestVector_MatchesPublishedCam16()
        {
            // Li et al. (2017) worked example: sample XYZ (19.01, 20.00, 21.78) under white
            // (95.05, 100, 108.88), L_A = 318.31, Y_b = 20, average surround. Published
            // CAM16 outputs: J = 41.73, C = 0.1033, h = 217.07°, M = 0.1074.
            var vc = new Cam16Ucs.ViewingConditions(
                new CieXyz(95.05, 100.0, 108.88),
                AdaptingLuminanceCdM2: 318.31,
                BackgroundYRelative: 20.0,
                Cam16Ucs.Surround.Average);

            var jab = Cam16Ucs.ToJabPrime(new CieXyz(19.01, 20.00, 21.78), vc);

            // J' = 1.7·41.73 / (1 + 0.007·41.73) ≈ 54.90
            Assert.InRange(jab.J, 54.7, 55.1);

            // M' = ln(1 + 0.0228·0.1074)/0.0228 ≈ 0.1073, split by h = 217.07°.
            double mPrime = Math.Sqrt(jab.A * jab.A + jab.B * jab.B);
            Assert.InRange(mPrime, 0.09, 0.12);
            double hue = Math.Atan2(jab.B, jab.A) * 180.0 / Math.PI + 360.0;
            Assert.InRange(hue % 360.0, 215.0, 219.0);
        }

        [Fact]
        public void White_MapsToJPrime100_WithBoundedResidualChroma()
        {
            // Under incomplete adaptation (dim surround, low L_A → D < 1) CAM16 correctly
            // leaves the reference white slightly chromatic — that is the model working,
            // not a bug. J′ must still be exactly 100 (A/Aw = 1), and the residual chroma
            // bounded; it cancels to first order in the ΔE′ differences the solver takes.
            var white = new CieXyz(95.047, 100.0, 108.883);
            var vc = Cam16Ucs.DisplayConditions(white);

            var jab = Cam16Ucs.ToJabPrime(white, vc);

            Assert.InRange(jab.J, 99.5, 100.5);
            Assert.InRange(Math.Sqrt(jab.A * jab.A + jab.B * jab.B), 0.0, 5.0);

            // At high adapting luminance D → 1 and the white becomes truly achromatic.
            var fullAdaptation = new Cam16Ucs.ViewingConditions(white, 1000.0, 20.0, Cam16Ucs.Surround.Average);
            var adapted = Cam16Ucs.ToJabPrime(white, fullAdaptation);
            Assert.InRange(Math.Sqrt(adapted.A * adapted.A + adapted.B * adapted.B), 0.0, 0.3);
        }

        [Fact]
        public void DeltaEPrime_IsAMetric()
        {
            var vc = Cam16Ucs.DisplayConditions(new CieXyz(190.1, 200.0, 217.8)); // 200-nit D65
            var a = new CieXyz(100, 105, 90);
            var b = new CieXyz(80, 90, 100);

            Assert.Equal(0.0, Cam16Ucs.DeltaEPrime(a, a, vc), 9);
            Assert.Equal(Cam16Ucs.DeltaEPrime(a, b, vc), Cam16Ucs.DeltaEPrime(b, a, vc), 9);
            Assert.True(Cam16Ucs.DeltaEPrime(a, b, vc) > 0);
        }

        [Fact]
        public void WarmerWhite_IsFartherFromNeutral_MonotonicallyInKelvin()
        {
            // The solver's cost must grow monotonically as the white warms — this is what
            // makes the 1D scan sound.
            var neutralXyz = JndPacedFade.AdaptedWhiteXyz(6500, NightModeAlgorithm.Perceptual, 0.8, false, false);
            var vc = Cam16Ucs.DisplayConditions(neutralXyz);
            var neutral = Cam16Ucs.ToJabPrime(neutralXyz, vc);

            double previous = 0.0;
            foreach (int kelvin in new[] { 6000, 5000, 4000, 3000, 2200 })
            {
                var warm = Cam16Ucs.ToJabPrime(
                    JndPacedFade.AdaptedWhiteXyz(kelvin, NightModeAlgorithm.Perceptual, 0.8, false, false), vc);
                double d = Cam16Ucs.DeltaEPrime(neutral, warm);
                Assert.True(d > previous,
                    $"ΔE′ must grow with warmth: {kelvin}K gave {d:F2}, previous {previous:F2}");
                previous = d;
            }
        }

        [Fact]
        public void Dimming_CostsLessThanEquivalentMelanopicWarming()
        {
            // Sanity of the appearance tradeoff the solver exploits: a modest luminance drop
            // is perceptually cheaper than the deep warm shift with similar melanopic effect.
            // (Not a physical law — a characterization of the model at desktop conditions.)
            var white = new CieXyz(190.1, 200.0, 217.8);
            var vc = Cam16Ucs.DisplayConditions(white);
            var baseline = Cam16Ucs.ToJabPrime(white, vc);

            var dimmed30 = Cam16Ucs.ToJabPrime(new CieXyz(133.1, 140.0, 152.4), vc); // −30% Y
            var warm2200 = Cam16Ucs.ToJabPrime(
                JndPacedFade.AdaptedWhiteXyz(2200, NightModeAlgorithm.AccurateCIE1931, 1.0, false, false)
                    is { } w ? new CieXyz(w.X * 2, w.Y * 2, w.Z * 2) : default, vc); // 200-nit scale

            Assert.True(Cam16Ucs.DeltaEPrime(baseline, dimmed30) <
                        Cam16Ucs.DeltaEPrime(baseline, warm2200));
        }

        [Fact]
        public void NonPhysicalInputs_DoNotThrow()
        {
            var vc = Cam16Ucs.DisplayConditions(new CieXyz(95.047, 100.0, 108.883));
            _ = Cam16Ucs.ToJabPrime(new CieXyz(0, 0, 0), vc);
            _ = Cam16Ucs.ToJabPrime(new CieXyz(-1, 0.5, 2), vc);
        }
    }
}
