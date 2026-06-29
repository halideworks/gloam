namespace HDRGammaController.Core
{
    /// <summary>
    /// Decodes DXGI_COLOR_SPACE_TYPE values used by IDXGIOutput6. The active
    /// display path can report HDR as RGB, studio RGB, or YCbCr depending on
    /// GPU, transport, TV mode, and driver settings.
    /// </summary>
    public static class DxgiColorSpaceInfo
    {
        public static bool IsHdr(int value) => value is
            12 or // RGB_FULL_G2084_NONE_P2020
            13 or // YCBCR_STUDIO_G2084_LEFT_P2020
            14 or // RGB_STUDIO_G2084_NONE_P2020
            16 or // YCBCR_STUDIO_G2084_TOPLEFT_P2020
            18 or // YCBCR_STUDIO_GHLG_TOPLEFT_P2020
            19;   // YCBCR_FULL_GHLG_TOPLEFT_P2020

        public static string DecodeColorSpace(int value) => value switch
        {
            0 => "RGB_FULL_G22_NONE_P709",
            1 => "RGB_FULL_G10_NONE_P709",
            2 => "RGB_STUDIO_G22_NONE_P709",
            3 => "RGB_STUDIO_G22_NONE_P2020",
            4 => "RESERVED",
            5 => "YCBCR_FULL_G22_NONE_P709_X601",
            6 => "YCBCR_STUDIO_G22_LEFT_P601",
            7 => "YCBCR_FULL_G22_LEFT_P601",
            8 => "YCBCR_STUDIO_G22_LEFT_P709",
            9 => "YCBCR_FULL_G22_LEFT_P709",
            10 => "YCBCR_STUDIO_G22_LEFT_P2020",
            11 => "YCBCR_FULL_G22_LEFT_P2020",
            12 => "RGB_FULL_G2084_NONE_P2020",
            13 => "YCBCR_STUDIO_G2084_LEFT_P2020",
            14 => "RGB_STUDIO_G2084_NONE_P2020",
            15 => "YCBCR_STUDIO_G22_TOPLEFT_P2020",
            16 => "YCBCR_STUDIO_G2084_TOPLEFT_P2020",
            17 => "RGB_FULL_G22_NONE_P2020",
            18 => "YCBCR_STUDIO_GHLG_TOPLEFT_P2020",
            19 => "YCBCR_FULL_GHLG_TOPLEFT_P2020",
            20 => "RGB_STUDIO_G24_NONE_P709",
            21 => "RGB_STUDIO_G24_NONE_P2020",
            22 => "YCBCR_STUDIO_G24_LEFT_P709",
            23 => "YCBCR_STUDIO_G24_LEFT_P2020",
            24 => "YCBCR_STUDIO_G24_TOPLEFT_P2020",
            _ => $"Unknown ({value})"
        };

        public static string DecodeBitsPerColor(int value) => value switch
        {
            0 => "Unspecified",
            1 => "6 bpc",
            2 => "8 bpc",
            3 => "10 bpc",
            4 => "12 bpc",
            5 => "16 bpc",
            _ => $"Unknown ({value})"
        };
    }
}
