using Xunit;
using HDRGammaController.Core;
using System.Linq;

namespace HDRGammaController.Tests
{
    public class LutGeneratorTests
    {
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
    }
}
