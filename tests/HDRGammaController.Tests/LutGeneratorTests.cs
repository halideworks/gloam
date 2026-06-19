using Xunit;
using HDRGammaController.Core;
using System.Linq;

namespace HDRGammaController.Tests
{
    public class LutGeneratorTests
    {
        [Fact]
        public void GenerateLut_NearbyTemperatures_DoNotAliasInCache()
        {
            LutGenerator.ClearCache();
            var first = new CalibrationSettings { Temperature = -10.00 };
            var second = new CalibrationSettings { Temperature = -9.98 };

            var firstLut = LutGenerator.GenerateLut(GammaMode.WindowsDefault, 200, first, isHdr: false);
            var secondLut = LutGenerator.GenerateLut(GammaMode.WindowsDefault, 200, second, isHdr: false);

            Assert.NotSame(firstLut.R, secondLut.R);
            Assert.NotEqual(firstLut.B[255], secondLut.B[255]);
        }

        [Fact]
        public void GenerateLut_NearbyMonitorWhiteLevels_DoNotAliasInCache()
        {
            LutGenerator.ClearCache();

            var first = LutGenerator.GenerateLut(GammaMode.Gamma22, 196, isHdr: true);
            var second = LutGenerator.GenerateLut(GammaMode.Gamma22, 204, isHdr: true);

            Assert.NotSame(first, second);
            Assert.NotEqual(first[512], second[512]);

            LutGenerator.ClearCache();
            var secondFirst = LutGenerator.GenerateLut(GammaMode.Gamma22, 204, isHdr: true);
            var firstSecond = LutGenerator.GenerateLut(GammaMode.Gamma22, 196, isHdr: true);
            Assert.Equal(second[512], secondFirst[512]);
            Assert.Equal(first[512], firstSecond[512]);
        }

        [Fact]
        public void WindowsDefault_IsIdentity()
        {
            double[] lut = LutGenerator.GenerateLut(GammaMode.WindowsDefault, 200.0);

            Assert.Equal(1024, lut.Length);
            Assert.Equal(0.0, lut[0], 6);
            Assert.Equal(1.0, lut[1023], 6);
            
            for(int i=0; i<1024; i++)
            {
                Assert.Equal(i / 1023.0, lut[i], 6);
            }
        }

        [Fact]
        public void Gamma24_Monotonic()
        {
            double[] lut = LutGenerator.GenerateLut(GammaMode.Gamma24, 200.0);

            for(int i=1; i<1024; i++)
            {
                Assert.True(lut[i] >= lut[i-1], $"LUT not monotonic at index {i}");
            }
        }
        
        [Fact]
        public void Gamma24_ShoulderBlend()
        {
            // At max signal (1.0), output should be 1.0 (bypass)
            double[] lut = LutGenerator.GenerateLut(GammaMode.Gamma24, 200.0);
            Assert.Equal(1.0, lut[1023], 6);
        }

        [Fact]
        public void HeadroomBlend_IsMonotonic_WithDimming()
        {
            // Regression test: the previous "blend to identity passthrough" curve
            // produced an S-shape where dimmed SDR met undimmed HDR highlights.
            // With the dim-aware target the LUT must remain non-decreasing across
            // the full range, including the headroom region.
            var cal = new CalibrationSettings { Brightness = 50.0 };
            var lut = LutGenerator.GenerateLut(GammaMode.Gamma22, 200.0, cal);
            for (int i = 1; i < lut.R.Length; i++)
            {
                Assert.True(lut.R[i] >= lut.R[i - 1] - 1e-9,
                    $"LUT R not monotonic at index {i}: {lut.R[i - 1]} -> {lut.R[i]}");
            }
        }

        [Fact]
        public void HeadroomBlend_DimmingFollowsIntoHighlights()
        {
            // With 50% brightness, the peak LUT value must be LESS than the undimmed
            // peak — otherwise dimming is being undone by the headroom blend. This is
            // the core "highlights rolloff when dimming" regression.
            var undimmed = LutGenerator.GenerateLut(GammaMode.Gamma22, 200.0,
                new CalibrationSettings { Brightness = 100.0 });
            var dimmed = LutGenerator.GenerateLut(GammaMode.Gamma22, 200.0,
                new CalibrationSettings { Brightness = 50.0 });

            Assert.True(dimmed.R[1023] < undimmed.R[1023],
                $"Dimming failed to reach headroom: peak undimmed={undimmed.R[1023]:F4} dimmed={dimmed.R[1023]:F4}");

            // And the gap should be meaningful — not the ~0.1% of the old curve.
            double reduction = undimmed.R[1023] - dimmed.R[1023];
            Assert.True(reduction > 0.02,
                $"Dimming at 50% should reduce peak by more than 0.02 in PQ signal space, got {reduction:F4}");
        }

        [Fact]
        public void HeadroomBlend_NoDimmingMatchesPassthroughAtPeak()
        {
            // When Brightness = 100%, the headroom target reduces to pure PQ identity,
            // so the peak code value must still land at 1.0 after the blend.
            var lut = LutGenerator.GenerateLut(GammaMode.Gamma22, 200.0,
                new CalibrationSettings { Brightness = 100.0 });
            Assert.Equal(1.0, lut.R[1023], 4);
            Assert.Equal(1.0, lut.G[1023], 4);
            Assert.Equal(1.0, lut.B[1023], 4);
        }

        [Fact]
        public void HeadroomBlend_IsContinuousAtSdrWhite()
        {
            // Smoothstep eliminates the slope kink at the SDR/HDR boundary. The LUT
            // should have a bounded first difference across the transition — no single
            // pair of adjacent samples should jump wildly compared to its neighbors.
            var lut = LutGenerator.GenerateLut(GammaMode.Gamma22, 200.0,
                new CalibrationSettings { Brightness = 80.0, Temperature = -20.0 });

            double maxDelta = 0;
            for (int i = 1; i < lut.R.Length; i++)
            {
                double d = lut.R[i] - lut.R[i - 1];
                if (d > maxDelta) maxDelta = d;
            }
            // Every step covers ~0.0977% of the input range; the LUT delta per step
            // shouldn't exceed an order of magnitude beyond that even under aggressive
            // calibration.
            Assert.True(maxDelta < 0.02,
                $"Max LUT step was {maxDelta:F6}; a large jump suggests a discontinuity.");
        }

        [Fact]
        public void CacheReturnsIdenticalReferences()
        {
            // Contract: cache returns the same array reference on a hit (no per-call clone).
            var cal = new CalibrationSettings { Brightness = 75.0 };
            var a = LutGenerator.GenerateLut(GammaMode.Gamma22, 200.0, cal);
            var b = LutGenerator.GenerateLut(GammaMode.Gamma22, 200.0, cal);
            Assert.Same(a.R, b.R);
            Assert.Same(a.G, b.G);
            Assert.Same(a.B, b.B);
        }

        // --- SDR path (isHdr: false) regression tests ---
        // The SDR branch previously hardcoded decode 2.2 / encode 1/2.2, making the Gamma24
        // selection a silent no-op on SDR displays. These lock in the corrected behavior:
        // decode with the TARGET gamma, re-encode to the display's native ~2.2.

        [Fact]
        public void Sdr_Gamma22_IsIdentity_WhenNoCalibration()
        {
            // Gamma22 in SDR: decode 2.2, encode 1/2.2 → net passthrough, so the LUT should
            // be the identity (input == output) at every sample.
            var (r, g, b, _) = LutGenerator.GenerateLut(
                GammaMode.Gamma22, 200.0, CalibrationSettings.Default, isHdr: false);

            for (int i = 0; i < r.Length; i++)
            {
                double expected = i / 1023.0;
                Assert.Equal(expected, r[i], 4);
                Assert.Equal(expected, g[i], 4);
                Assert.Equal(expected, b[i], 4);
            }
        }

        [Fact]
        public void Sdr_Gamma24_DarkensMidtones()
        {
            // Gamma24 in SDR: decode 2.4 (darker shadows) then re-encode 1/2.2, so a mid
            // input must come out LOWER than the input — the whole point of selecting 2.4.
            var (r, _, _, _) = LutGenerator.GenerateLut(
                GammaMode.Gamma24, 200.0, CalibrationSettings.Default, isHdr: false);

            // Endpoints stay anchored: black stays black, white stays white.
            Assert.Equal(0.0, r[0], 4);
            Assert.Equal(1.0, r[1023], 4);

            // A mid-gray input must be pulled down (2.4 decode < 2.2 decode in linear).
            double inputMid = 0.5;
            int idx = (int)Math.Round(inputMid * 1023);
            Assert.True(r[idx] < inputMid,
                $"Gamma24 SDR should darken midtones: 0.5 input -> {r[idx]:F4} (expected < 0.5)");
        }

        [Fact]
        public void Sdr_Gamma24_MoreAggressiveThan_Gamma22()
        {
            // Gamma24 must darken shadows strictly more than Gamma22 across the lower range.
            var g22 = LutGenerator.GenerateLut(
                GammaMode.Gamma22, 200.0, CalibrationSettings.Default, isHdr: false).R;
            var g24 = LutGenerator.GenerateLut(
                GammaMode.Gamma24, 200.0, CalibrationSettings.Default, isHdr: false).R;

            for (int i = 1; i < 700; i++)
            {
                Assert.True(g24[i] <= g22[i] + 1e-9,
                    $"Gamma24 ({g24[i]:F4}) should be <= Gamma22 ({g22[i]:F4}) at index {i}");
            }
        }
    }
}
