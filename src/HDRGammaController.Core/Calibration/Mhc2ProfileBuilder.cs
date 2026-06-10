using System;
using System.IO;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Patches a working MHC2 ICC template with a measured calibration: a 3×4 gamut matrix
    /// (primary/white correction) plus three per-channel 1024-point tone LUTs. The result is
    /// installed as the display's Windows color profile, so the Desktop Window Manager applies
    /// the full gamut+tone correction natively (including in HDR) — the correction the GPU
    /// gamma ramp alone cannot do, because the ramp is per-channel 1D with no cross-channel
    /// matrix.
    ///
    /// MHC2 tag binary layout (reverse-engineered from the shipped templates, which were
    /// produced by MHC2Gen):
    ///   +0   'MHC2'                       (type signature)
    ///   +4   uint32  reserved (0)
    ///   +8   uint32  lutCount (1024)
    ///   +12  s15Fixed16 minLuminance
    ///   +16  s15Fixed16 maxLuminance
    ///   +20  uint32  matrixOffset   (relative to tag start)
    ///   +24  uint32  lut0Offset     (red)
    ///   +28  uint32  lut1Offset     (green)
    ///   +32  uint32  lut2Offset     (blue)
    ///   matrix:  3 rows × 4 cols of s15Fixed16, output = M·[r,g,b,1]  (no element header)
    ///   each LUT: 'sf32'(4) + reserved(4) + lutCount × s15Fixed16  (signal→signal, [0,1])
    /// </summary>
    public static class Mhc2ProfileBuilder
    {
        private const int IccHeaderSize = 128;
        private const int IccTagEntrySize = 12;
        private const int Mhc2Signature = 0x4D484332; // 'MHC2'
        private const int Sf32Signature = 0x73663332; // 'sf32'
        private const int LutSamples = 1024;

        /// <summary>
        /// Reads <paramref name="templatePath"/>, patches its MHC2 tag with the given matrix
        /// and per-channel LUTs, and writes <paramref name="outputPath"/>.
        /// </summary>
        /// <param name="matrix3x3">Gamut correction matrix (content-linear-RGB → display-linear-RGB). The 4th column (offset) is written as 0.</param>
        /// <param name="lutR">Red tone LUT, 1024 entries in [0,1].</param>
        public static void Build(
            string templatePath, string outputPath,
            double[,] matrix3x3, double[] lutR, double[] lutG, double[] lutB)
        {
            if (matrix3x3.GetLength(0) != 3 || matrix3x3.GetLength(1) != 3)
                throw new ArgumentException("matrix must be 3x3", nameof(matrix3x3));
            ValidateLut(lutR, nameof(lutR));
            ValidateLut(lutG, nameof(lutG));
            ValidateLut(lutB, nameof(lutB));

            byte[] data = File.ReadAllBytes(templatePath);
            int tagStart = FindMhc2Tag(data);

            int matrixOff = tagStart + ReadU32(data, tagStart + 20);
            int lut0Off = tagStart + ReadU32(data, tagStart + 24);
            int lut1Off = tagStart + ReadU32(data, tagStart + 28);
            int lut2Off = tagStart + ReadU32(data, tagStart + 32);
            int lutCount = ReadU32(data, tagStart + 8);
            if (lutCount != LutSamples)
                throw new InvalidDataException($"Template MHC2 LUT count is {lutCount}, expected {LutSamples}.");

            // Matrix: 3 rows × 4 cols s15Fixed16; column 3 is the offset (0).
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                    WriteS15Fixed16(data, matrixOff + (r * 4 + c) * 4, matrix3x3[r, c]);
                WriteS15Fixed16(data, matrixOff + (r * 4 + 3) * 4, 0.0); // offset column
            }

            WriteLut(data, lut0Off, lutR);
            WriteLut(data, lut1Off, lutG);
            WriteLut(data, lut2Off, lutB);

            // Zero the profile ID (bytes 84–99): we changed the body, so the stored MD5 is stale.
            for (int z = 84; z < 100; z++) data[z] = 0;

            File.WriteAllBytes(outputPath, data);
        }

        /// <summary>
        /// Gamut correction matrix mapping content (in the target's primaries, linear) to the
        /// display's native linear RGB so the panel reproduces the target chromaticities:
        /// M = displayXyzToRgb · targetRgbToXyz.
        /// </summary>
        public static double[,] BuildGamutMatrix(DisplayCharacterization characterization, CalibrationTarget target)
        {
            var displayRgbToXyz = characterization.RgbToXyzMatrix
                ?? throw new InvalidOperationException("Characterization has no measured RGB→XYZ matrix.");
            var displayXyzToRgb = ColorMath.Invert3x3(displayRgbToXyz);
            return ColorMath.MultiplyMatrices(displayXyzToRgb, target.RgbToXyzMatrix);
        }

        private static void WriteLut(byte[] data, int lutOffset, double[] lut)
        {
            if (ReadU32(data, lutOffset) != Sf32Signature)
                throw new InvalidDataException("MHC2 LUT element is not 'sf32' typed where expected.");
            int dataStart = lutOffset + 8; // skip 'sf32' + reserved
            for (int i = 0; i < LutSamples; i++)
                WriteS15Fixed16(data, dataStart + i * 4, Math.Clamp(lut[i], 0.0, 1.0));
        }

        private static int FindMhc2Tag(byte[] data)
        {
            if (data.Length < IccHeaderSize + 4) throw new InvalidDataException("ICC profile too small.");
            int tagCount = ReadU32(data, IccHeaderSize);
            if (tagCount < 0 || tagCount > 4096) throw new InvalidDataException($"Bad ICC tag count {tagCount}.");
            for (int i = 0; i < tagCount; i++)
            {
                int e = IccHeaderSize + 4 + i * IccTagEntrySize;
                if (e + 12 > data.Length) break;
                if (ReadU32(data, e) == Mhc2Signature)
                {
                    int off = ReadU32(data, e + 4);
                    int size = ReadU32(data, e + 8);
                    if (off < 0 || size < 36 || off + size > data.Length)
                        throw new InvalidDataException("MHC2 tag offset/size out of range.");
                    if (ReadU32(data, off) != Mhc2Signature)
                        throw new InvalidDataException("MHC2 tag data does not start with 'MHC2'.");
                    return off;
                }
            }
            throw new InvalidDataException("Template has no MHC2 tag.");
        }

        private static void ValidateLut(double[] lut, string name)
        {
            if (lut == null || lut.Length != LutSamples)
                throw new ArgumentException($"{name} must have exactly {LutSamples} entries.", name);
        }

        private static int ReadU32(byte[] b, int o) =>
            (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        private static void WriteS15Fixed16(byte[] b, int o, double value)
        {
            // s15Fixed16: signed 16.16. Range roughly [-32768, 32767.9999].
            long fixed16 = (long)Math.Round(value * 65536.0);
            fixed16 = Math.Clamp(fixed16, int.MinValue, int.MaxValue);
            int v = (int)fixed16;
            b[o] = (byte)((v >> 24) & 0xFF);
            b[o + 1] = (byte)((v >> 16) & 0xFF);
            b[o + 2] = (byte)((v >> 8) & 0xFF);
            b[o + 3] = (byte)(v & 0xFF);
        }
    }
}
