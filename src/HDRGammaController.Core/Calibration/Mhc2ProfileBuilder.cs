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

        private const int DescSignature = 0x64657363; // 'desc'
        private const int MlucSignature = 0x6D6C7563; // 'mluc'

        /// <summary>
        /// Reads <paramref name="templatePath"/>, patches its MHC2 tag with the given matrix
        /// and per-channel LUTs, and writes <paramref name="outputPath"/>.
        /// </summary>
        /// <param name="matrix3x3">Gamut correction matrix (content-linear-RGB → display-linear-RGB). The 4th column (offset) is written as 0.</param>
        /// <param name="lutR">Red tone LUT, 1024 entries in [0,1].</param>
        /// <param name="description">Profile description shown by Windows Color Management
        /// (replaces the template's leftover "SDR ACM: srgb_d50 [...]" text). Null keeps the
        /// template's description.</param>
        /// <param name="minLuminanceNits">Display black level for the MHC2 header (and lumi
        /// tag). Null keeps the template values. Required for HDR: Windows uses these to
        /// scale tone mapping (the Windows HDR Calibration app's profile carries exactly
        /// this — measured min/max with identity transforms).</param>
        public static void Build(
            string templatePath, string outputPath,
            double[,] matrix3x3, double[] lutR, double[] lutG, double[] lutB,
            string? description = null,
            double? minLuminanceNits = null, double? maxLuminanceNits = null)
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

            if (minLuminanceNits is double minL)
                WriteS15Fixed16(data, tagStart + 12, Math.Clamp(minL, 0, 32767));
            if (maxLuminanceNits is double maxL)
            {
                WriteS15Fixed16(data, tagStart + 16, Math.Clamp(maxL, 0, 32767));
                PatchLumiTag(data, maxL);
            }

            if (!string.IsNullOrWhiteSpace(description))
                data = PatchDescription(data, description);

            // Zero the profile ID (bytes 84–99): we changed the body, so the stored MD5 is stale.
            for (int z = 84; z < 100; z++) data[z] = 0;

            File.WriteAllBytes(outputPath, data);
        }

        /// <summary>
        /// Replaces the profile's 'desc' tag (what Windows Color Management displays) with
        /// <paramref name="description"/>. The new text rarely fits the template's allocation,
        /// so a fresh 'mluc' (en-US) block is appended at the end of the file and the tag
        /// table entry is repointed — tag data may live anywhere, readers follow the table.
        /// The template's old desc bytes become unused slack.
        /// </summary>
        private static byte[] PatchDescription(byte[] data, string description)
        {
            int tagCount = ReadU32(data, IccHeaderSize);
            int descEntry = -1;
            for (int i = 0; i < tagCount; i++)
            {
                int e = IccHeaderSize + 4 + i * IccTagEntrySize;
                if (e + 12 <= data.Length && ReadU32(data, e) == DescSignature) { descEntry = e; break; }
            }
            if (descEntry < 0) return data; // template has no desc tag; nothing to clean up

            // mluc: sig(4) reserved(4) recordCount=1(4) recordSize=12(4)
            //       lang 'enUS'(4) stringLength(4) stringOffset=28(4) + UTF-16BE text
            byte[] text = System.Text.Encoding.BigEndianUnicode.GetBytes(description);
            byte[] mluc = new byte[28 + text.Length];
            WriteU32(mluc, 0, MlucSignature);
            WriteU32(mluc, 8, 1);
            WriteU32(mluc, 12, 12);
            mluc[16] = (byte)'e'; mluc[17] = (byte)'n'; mluc[18] = (byte)'U'; mluc[19] = (byte)'S';
            WriteU32(mluc, 20, text.Length);
            WriteU32(mluc, 24, 28);
            Array.Copy(text, 0, mluc, 28, text.Length);

            int newOffset = (data.Length + 3) & ~3; // 4-byte aligned tag start
            int padded = (mluc.Length + 3) & ~3;
            byte[] result = new byte[newOffset + padded];
            Array.Copy(data, result, data.Length);
            Array.Copy(mluc, 0, result, newOffset, mluc.Length);

            WriteU32(result, descEntry + 4, newOffset);
            WriteU32(result, descEntry + 8, mluc.Length);
            WriteU32(result, 0, result.Length); // header profile-size field
            return result;
        }

        /// <summary>Writes the display peak luminance into the 'lumi' tag's XYZ Y field.</summary>
        private static void PatchLumiTag(byte[] data, double maxNits)
        {
            const int LumiSignature = 0x6C756D69; // 'lumi'
            int tagCount = ReadU32(data, IccHeaderSize);
            for (int i = 0; i < tagCount; i++)
            {
                int e = IccHeaderSize + 4 + i * IccTagEntrySize;
                if (e + 12 > data.Length || ReadU32(data, e) != LumiSignature) continue;
                int off = ReadU32(data, e + 4);
                // XYZType: sig(4) + reserved(4) + X(4) + Y(4) + Z(4) — luminance lives in Y.
                if (off + 20 <= data.Length)
                    WriteS15Fixed16(data, off + 12, Math.Clamp(maxNits, 0, 32767));
                return;
            }
        }

        private static void WriteU32(byte[] b, int o, int v)
        {
            b[o] = (byte)((v >> 24) & 0xFF);
            b[o + 1] = (byte)((v >> 16) & 0xFF);
            b[o + 2] = (byte)((v >> 8) & 0xFF);
            b[o + 3] = (byte)(v & 0xFF);
        }

        /// <summary>
        /// ABSOLUTE gamut correction matrix mapping content (in the target's primaries,
        /// linear) to the display's native linear RGB so the panel reproduces the target
        /// chromaticities AND the target white point: M = displayXyzToRgb · targetRgbToXyz.
        ///
        /// On a panel whose white differs from the target, some channel exceeds 1.0 for
        /// white (e.g. red 1.09 on a blue-ish panel) — resolve that with
        /// <see cref="UniformScale"/>, which dims ALL channels equally. Do NOT resolve it
        /// with per-channel gains: a diagonal gain matrix applied after this one re-tints
        /// every non-neutral color the matrix just placed (that's what pushed the verified
        /// primaries ΔE from 1.39 to 2.46 on the first HDR pass). Uniform scaling preserves
        /// every chromaticity exactly and only costs peak luminance.
        /// </summary>
        public static double[,] BuildGamutMatrix(DisplayCharacterization characterization, CalibrationTarget target)
        {
            var displayRgbToXyz = characterization.RgbToXyzMatrix
                ?? throw new InvalidOperationException("Characterization has no measured RGB→XYZ matrix.");
            var displayXyzToRgb = ColorMath.Invert3x3(displayRgbToXyz);
            return ColorMath.MultiplyMatrices(displayXyzToRgb, target.RgbToXyzMatrix);
        }

        /// <summary>
        /// The uniform luminance scale that brings the matrix's largest drive value down to
        /// full scale (1.0 when nothing exceeds it). Apply with <see cref="ScaleMatrix"/>.
        /// </summary>
        public static double UniformScale(double maxTargetDrive) =>
            maxTargetDrive > 1.0 ? 1.0 / maxTargetDrive : 1.0;

        public static double[,] ScaleMatrix(double[,] m, double scale)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i, j] = m[i, j] * scale;
            return r;
        }

        /// <summary>
        /// Converts an RGB→RGB correction matrix into the domain Windows actually applies the
        /// MHC2 matrix in. The DWM/driver pipeline evaluates the tag's matrix sandwiched
        /// between FIXED sRGB↔XYZ conversions (it is effectively an XYZ→XYZ transform):
        ///
        ///     wire = LUT( xyzToSrgb_fixed · M_tag · srgbToXyz_fixed · linearContent )
        ///
        /// (MHC2Gen left-multiplies its matrix by sRGB→XYZ with the comment "hack: eliminate
        /// fixed sRGB to XYZ" — that fixed stage is the engine's, not the profile's.)
        /// Writing a plain RGB→RGB matrix here is what caused the strong magenta cast: the
        /// engine's sandwich turned our gentle warm correction into red ≈1.39 (clipped) with
        /// green crushed to ≈0.87. Wrapping the matrix as srgbToXyz · M · xyzToSrgb makes the
        /// engine's fixed conversions cancel exactly, so it applies precisely M in linear RGB.
        /// </summary>
        public static double[,] ToMhc2MatrixDomain(double[,] rgbToRgbMatrix)
        {
            var srgbToXyz = ColorMath.CalculateRgbToXyzMatrix(
                Chromaticity.Rec709Red, Chromaticity.Rec709Green, Chromaticity.Rec709Blue, Chromaticity.D65);
            return ColorMath.MultiplyMatrices(srgbToXyz,
                ColorMath.MultiplyMatrices(rgbToRgbMatrix, ColorMath.Invert3x3(srgbToXyz)));
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
