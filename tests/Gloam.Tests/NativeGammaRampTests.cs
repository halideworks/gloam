using System;
using System.Linq;
using Xunit;
using Gloam.Core;

namespace Gloam.Tests
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
        public void BuildRampChannel_NonFiniteValuesClampToBlack()
        {
            // Envelope off: this test pins the non-finite -> 0 semantics alone. (With the
            // envelope on, entries above 128 are lifted to the validation floor instead,
            // because a zero there is exactly what SetDeviceGammaRamp rejects.)
            var lut = new[] { 0.0, double.NaN, double.PositiveInfinity, 1.0 };
            var ramp = NativeGammaRamp.BuildRampChannel(lut, applyGdiEnvelope: false, out _);

            Assert.Equal(0, ramp[0]);
            Assert.Equal(0, ramp[85]);
            Assert.Equal(0, ramp[170]);
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

        // ---- Windows gamma-range validation envelope (SetDeviceGammaRamp) ----
        // win32k rejects the whole call when any entry's high byte deviates more than
        // ±128 from its index. The envelope clamp must produce the closest ramp Windows
        // accepts; without it, an over-warm night ramp applies NOTHING on SDR displays.

        [Fact]
        public void GdiEnvelope_BoundsMatchMeasuredValidationRule()
        {
            // Measured on Windows 11 22621: top entry 32512 accepted, 32000 rejected.
            Assert.Equal(32512, NativeGammaRamp.GdiEnvelopeLower(255));
            Assert.Equal(0, NativeGammaRamp.GdiEnvelopeLower(128));
            Assert.Equal(0, NativeGammaRamp.GdiEnvelopeLower(0));
            // Constant +32768 lift accepted, +33000 rejected (binds at mid entries).
            Assert.Equal(65535, NativeGammaRamp.GdiEnvelopeUpper(127));
            Assert.Equal(33023, NativeGammaRamp.GdiEnvelopeUpper(0)); // (0+129)*256 - 1
            for (int i = 0; i < 256; i++)
            {
                int lo = NativeGammaRamp.GdiEnvelopeLower(i);
                int hiBound = NativeGammaRamp.GdiEnvelopeUpper(i);
                Assert.True(lo <= i * 257 && i * 257 <= hiBound,
                    $"identity entry {i} ({i * 257}) must sit inside the envelope [{lo}, {hiBound}]");
                Assert.True(Math.Abs((lo >> 8) - i) <= 128 && Math.Abs((hiBound >> 8) - i) <= 128,
                    $"envelope bounds at {i} must themselves satisfy the ±128 high-byte rule");
            }
        }

        [Fact]
        public void BuildRampChannel_ClampsNightModeBlueIntoAcceptedEnvelope()
        {
            // 2700K Perceptual 0.8 cuts blue to 0.206846 linear -> encoded slope ~0.4886,
            // whose top entry (32013) Windows rejects. The clamp must lift only the
            // extreme end up to the envelope floor.
            double bSlope = Math.Pow(0.206846, 1.0 / 2.2);
            var lut = IdentityLut().Select(v => v * bSlope).ToArray();

            var raw = NativeGammaRamp.BuildRampChannel(lut, applyGdiEnvelope: false, out bool rawClamped);
            var clamped = NativeGammaRamp.BuildRampChannel(lut, applyGdiEnvelope: true, out bool wasClamped);

            Assert.False(rawClamped);
            Assert.True(wasClamped);
            Assert.True(raw[255] < NativeGammaRamp.GdiEnvelopeLower(255));
            Assert.Equal(NativeGammaRamp.GdiEnvelopeLower(255), clamped[255]);

            int firstDivergence = -1;
            for (int i = 0; i < 256; i++)
            {
                Assert.True(clamped[i] >= NativeGammaRamp.GdiEnvelopeLower(i));
                Assert.True(clamped[i] <= NativeGammaRamp.GdiEnvelopeUpper(i));
                if (firstDivergence < 0 && clamped[i] != raw[i]) firstDivergence = i;
            }
            // Only the top of the ramp may move; the mid-tones keep the exact intent.
            Assert.True(firstDivergence > 200,
                $"clamp must only bend the extreme end, but diverged from entry {firstDivergence}");
        }

        [Fact]
        public void BuildRampChannel_EnvelopeClampPreservesMonotonicity()
        {
            double bSlope = 0.30; // far warmer than the envelope allows
            var lut = IdentityLut().Select(v => v * bSlope).ToArray();
            var ramp = NativeGammaRamp.BuildRampChannel(lut, applyGdiEnvelope: true, out bool wasClamped);

            Assert.True(wasClamped);
            for (int i = 1; i < 256; i++)
            {
                Assert.True(ramp[i] >= ramp[i - 1],
                    $"Clamped ramp not monotonic at {i}: {ramp[i - 1]} -> {ramp[i]}");
            }
        }

        [Fact]
        public void BuildRampChannel_EnvelopeLeavesCompliantRampsUntouched()
        {
            // A mild warm shift (encoded slope 0.7, ~3400K-class) is inside the envelope.
            var lut = IdentityLut().Select(v => v * 0.7).ToArray();
            var raw = NativeGammaRamp.BuildRampChannel(lut, applyGdiEnvelope: false, out _);
            var clamped = NativeGammaRamp.BuildRampChannel(lut, applyGdiEnvelope: true, out bool wasClamped);

            Assert.False(wasClamped);
            Assert.Equal(raw, clamped);
        }

        [Fact]
        public void BuildRampChannel_PublicOverload_HonorsGammaRangeUnlock()
        {
            double bSlope = Math.Pow(0.206846, 1.0 / 2.2);
            var lut = IdentityLut().Select(v => v * bSlope).ToArray();
            try
            {
                NativeGammaRamp.GammaRangeUnlockOverride = true;
                var unlocked = NativeGammaRamp.BuildRampChannel(lut);
                Assert.True(unlocked[255] < NativeGammaRamp.GdiEnvelopeLower(255),
                    "unlocked range must not clamp");

                NativeGammaRamp.GammaRangeUnlockOverride = false;
                var locked = NativeGammaRamp.BuildRampChannel(lut);
                Assert.Equal(NativeGammaRamp.GdiEnvelopeLower(255), locked[255]);
            }
            finally
            {
                NativeGammaRamp.GammaRangeUnlockOverride = null;
            }
        }
    }
}
