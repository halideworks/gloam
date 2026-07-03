using System;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Tests for the pure Night Light blob transform. The registry-facing TryDisable is a
    /// thin wrapper and is not unit-tested (it would mutate live user state); everything
    /// decision-shaped lives in BuildDisabledBlob.
    /// </summary>
    public class WindowsNightLightTests
    {
        /// <summary>
        /// A synthetic 43-byte ON blob matching the documented CloudStore layout: state
        /// header 0x15 at index 18 and the extra 0x10 0x00 field at 23/24 that the ON
        /// form carries. Payload bytes are arbitrary but deterministic.
        /// </summary>
        private static byte[] OnBlob(byte timestamp10 = 0x42)
        {
            var data = new byte[43];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(0x80 + i);
            data[10] = timestamp10; // first varint timestamp byte
            data[18] = 0x15;        // state: ON
            data[23] = 0x10;        // extra field the ON form carries
            data[24] = 0x00;
            return data;
        }

        [Fact]
        public void BuildDisabledBlob_OnBlob_ProducesVerifiableOffBlob()
        {
            var on = OnBlob();

            var off = WindowsNightLight.BuildDisabledBlob(on);

            Assert.NotNull(off);
            Assert.Equal(on.Length - 2, off!.Length);
            Assert.Equal(0x13, off[18]);
            Assert.False(CalibrationInstallPreflight.IsNightLightActiveBlob(off));

            // The 0x10 0x00 pair at 23/24 is removed: the byte that used to be at 25
            // now sits at 23, and the tail is intact.
            Assert.Equal(on[25], off[23]);
            Assert.Equal(on[^1], off[^1]);

            // Bytes before the state field are untouched except the bumped timestamp.
            Assert.Equal(on[0], off[0]);
            Assert.Equal(on[9], off[9]);
            Assert.Equal((byte)(on[10] + 1), off[10]);
            Assert.Equal(on[11], off[11]);
        }

        [Fact]
        public void BuildDisabledBlob_TimestampByteSaturated_IncrementsNextByte()
        {
            // Community togglers increment the first non-0xFF byte of the varint
            // timestamp at 10..14; mirror that exactly.
            var on = OnBlob(timestamp10: 0xFF);

            var off = WindowsNightLight.BuildDisabledBlob(on);

            Assert.NotNull(off);
            Assert.Equal(0xFF, off![10]);
            Assert.Equal((byte)(on[11] + 1), off[11]);
        }

        [Fact]
        public void BuildDisabledBlob_OffBlob_ReturnsNull()
        {
            var off = OnBlob();
            off[18] = 0x13;

            Assert.Null(WindowsNightLight.BuildDisabledBlob(off));
        }

        [Fact]
        public void BuildDisabledBlob_UnexpectedExtraField_ReturnsNull()
        {
            // State header says ON but the documented 0x10 0x00 pair is absent — an
            // unknown layout variant. The transform must refuse rather than guess.
            var odd = OnBlob();
            odd[23] = 0x11;

            Assert.Null(WindowsNightLight.BuildDisabledBlob(odd));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(new byte[0])]
        [InlineData(new byte[] { 0x15 })]
        public void BuildDisabledBlob_ShortOrMissing_ReturnsNull(byte[]? data)
        {
            Assert.Null(WindowsNightLight.BuildDisabledBlob(data));
        }
    }
}
