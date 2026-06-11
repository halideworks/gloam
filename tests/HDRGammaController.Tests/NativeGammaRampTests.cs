using System;
using System.Linq;
using Xunit;
using HDRGammaController.Core;

namespace HDRGammaController.Tests
{
    public class NativeGammaRampTests
    {
        private static double[] IdentityLut(int size = 1024)
        {
            var lut = new double[size];
            for (int i = 0; i < size; i++) lut[i] = i / (double)(size - 1);
            return lut;
        }

        [Fact]
        public void BuildRampChannel_IdentityLut_ProducesLinearRamp()
        {
            var ramp = NativeGammaRamp.BuildRampChannel(IdentityLut());

            Assert.Equal(256, ramp.Length);
            Assert.Equal(0, ramp[0]);
            Assert.Equal(65535, ramp[255]);
            for (int i = 0; i < 256; i++)
            {
                // Linear interpolation of an identity LUT must land within rounding
                // distance of the ideal i/255 * 65535.
                double ideal = i / 255.0 * 65535.0;
                Assert.True(Math.Abs(ramp[i] - ideal) <= 1.0,
                    $"ramp[{i}]={ramp[i]} deviates from ideal {ideal:F1}");
            }
        }

        [Fact]
        public void BuildRampChannel_PreservesMonotonicity()
        {
            // A gamma-2.2 LUT is monotonic; the resampled ramp must be too.
            var lut = IdentityLut().Select(v => Math.Pow(v, 1.0 / 2.2)).ToArray();
            var ramp = NativeGammaRamp.BuildRampChannel(lut);

            for (int i = 1; i < 256; i++)
            {
                Assert.True(ramp[i] >= ramp[i - 1],
                    $"Ramp not monotonic at {i}: {ramp[i - 1]} -> {ramp[i]}");
            }
        }

        [Fact]
        public void BuildRampChannel_ClampsOutOfRangeValues()
        {
            var lut = new[] { -0.5, 0.25, 0.75, 1.5 };
            var ramp = NativeGammaRamp.BuildRampChannel(lut);

            Assert.Equal(0, ramp[0]);
            Assert.Equal(65535, ramp[255]);
        }

        [Fact]
        public void BuildRampChannel_MatchesLutValuesAtSamplePoints()
        {
            // With a 256-entry LUT, ramp index i maps exactly onto LUT index i.
            var lut = IdentityLut(256).Select(v => v * 0.5).ToArray();
            var ramp = NativeGammaRamp.BuildRampChannel(lut);

            for (int i = 0; i < 256; i++)
            {
                ushort expected = (ushort)Math.Round(lut[i] * 65535.0);
                Assert.Equal(expected, ramp[i]);
            }
        }

        [Fact]
        public void BuildRampChannel_RejectsTooShortLut()
        {
            Assert.Throws<ArgumentException>(() => NativeGammaRamp.BuildRampChannel(new double[] { 1.0 }));
        }
    }
}
