using HDRGammaController.Core;
using Xunit;

namespace HDRGammaController.Tests
{
    public class DxgiColorSpaceInfoTests
    {
        [Theory]
        [InlineData(12, "RGB_FULL_G2084_NONE_P2020")]
        [InlineData(13, "YCBCR_STUDIO_G2084_LEFT_P2020")]
        [InlineData(14, "RGB_STUDIO_G2084_NONE_P2020")]
        [InlineData(16, "YCBCR_STUDIO_G2084_TOPLEFT_P2020")]
        [InlineData(18, "YCBCR_STUDIO_GHLG_TOPLEFT_P2020")]
        [InlineData(19, "YCBCR_FULL_GHLG_TOPLEFT_P2020")]
        public void IsHdr_ReturnsTrue_ForPqAndHlgOutputPaths(int value, string expectedName)
        {
            Assert.True(DxgiColorSpaceInfo.IsHdr(value));
            Assert.Equal(expectedName, DxgiColorSpaceInfo.DecodeColorSpace(value));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(10)]
        [InlineData(11)]
        [InlineData(17)]
        [InlineData(20)]
        [InlineData(24)]
        public void IsHdr_ReturnsFalse_ForSdrAndWideGamutSdrPaths(int value)
        {
            Assert.False(DxgiColorSpaceInfo.IsHdr(value));
        }

        [Fact]
        public void DecodeBitsPerColor_ReturnsHumanLabel()
        {
            Assert.Equal("10 bpc", DxgiColorSpaceInfo.DecodeBitsPerColor(3));
            Assert.Equal("Unknown (99)", DxgiColorSpaceInfo.DecodeBitsPerColor(99));
        }
    }
}
