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
    /// Besides the MHC2 tag, the builder can synthesize ICC characterization tags
    /// (wtpt/chad/rXYZ/gXYZ/bXYZ/rTRC/gTRC/bTRC/chrm) for the colorimetry the display
    /// presents once the profile is active. The ordinary ICC tags describe the app-facing
    /// presentation space, which remains sRGB-encoded under Windows Advanced Color; HDR
    /// PQ/HLG wire correction belongs only in MHC2. See <see cref="Build"/>'s colorimetry
    /// parameter.
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

        // ICC characterization tags synthesized from the installed colorimetry (M6).
        private const int WtptSignature = 0x77747074; // 'wtpt'
        private const int ChadSignature = 0x63686164; // 'chad'
        private const int RXyzSignature = 0x7258595A; // 'rXYZ'
        private const int GXyzSignature = 0x6758595A; // 'gXYZ'
        private const int BXyzSignature = 0x6258595A; // 'bXYZ'
        private const int RTrcSignature = 0x72545243; // 'rTRC'
        private const int GTrcSignature = 0x67545243; // 'gTRC'
        private const int BTrcSignature = 0x62545243; // 'bTRC'
        private const int ChrmSignature = 0x6368726D; // 'chrm'
        private const int LumiSignature = 0x6C756D69; // 'lumi'
        private const int CurvSignature = 0x63757276; // 'curv'
        private const int XyzTypeSignature = 0x58595A20; // 'XYZ '
        private const int TrcSampleCount = 1024;

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
        /// <param name="colorimetry">The colorimetry the display presents once this profile
        /// is active (primaries, white point, transfer function). When set, the profile's
        /// wtpt/chad/rXYZ/gXYZ/bXYZ/rTRC/gTRC/bTRC/chrm tags are rewritten so ICC-aware apps
        /// see the real display instead of the template's synthetic sRGB/G2.2 values
        /// (ICC v4 convention: wtpt = PCS D50, chad = Bradford(actual white → D50),
        /// r/g/bXYZ = chad-adapted primaries). SDR targets use their requested EOTF. HDR
        /// targets use the app-facing sRGB EOTF here; their PQ/HLG calibration remains in
        /// the MHC2 payload. Null keeps the template's tags untouched.</param>
        /// <param name="lumiPeakNits">Measured peak luminance written to the 'lumi' tag ONLY
        /// (the MHC2 header stays untouched) — the SDR install path uses this so the lumi tag
        /// carries the measured peak without altering the MHC2 header range Windows reads for
        /// HDR tone mapping. Redundant when <paramref name="maxLuminanceNits"/> is set (that
        /// already patches lumi).</param>
        public static void Build(
            string templatePath, string outputPath,
            double[,] matrix3x3, double[] lutR, double[] lutG, double[] lutB,
            string? description = null,
            double? minLuminanceNits = null, double? maxLuminanceNits = null,
            CalibrationTarget? colorimetry = null,
            double? lumiPeakNits = null)
        {
            if (matrix3x3.GetLength(0) != 3 || matrix3x3.GetLength(1) != 3)
                throw new ArgumentException("matrix must be 3x3", nameof(matrix3x3));
            ValidateMatrix(matrix3x3, nameof(matrix3x3));
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
            {
                ValidateLuminanceMetadata(minL, nameof(minLuminanceNits), "Minimum");
                WriteS15Fixed16(data, tagStart + 12, minL);
            }
            if (maxLuminanceNits is double maxL)
            {
                ValidateLuminanceMetadata(maxL, nameof(maxLuminanceNits), "Maximum");
                if (minLuminanceNits is double minForRange && maxL <= minForRange)
                    throw new ArgumentOutOfRangeException(nameof(maxLuminanceNits), maxL,
                        "Maximum luminance must be greater than minimum luminance.");
                PatchLumiTag(data, maxL);
                WriteS15Fixed16(data, tagStart + 16, maxL);
            }

            if (lumiPeakNits is double lumiPeak)
            {
                ValidateLuminanceMetadata(lumiPeak, nameof(lumiPeakNits), "Peak");
                PatchLumiTag(data, lumiPeak);
            }

            if (colorimetry != null)
                data = PatchCharacterizationTags(data, colorimetry);

            if (!string.IsNullOrWhiteSpace(description))
                data = PatchDescription(data, description);

            // Zero the profile ID (bytes 84–99): we changed the body, so the stored MD5 is stale.
            for (int z = 84; z < 100; z++) data[z] = 0;

            File.WriteAllBytes(outputPath, data);
        }

        /// <summary>
        /// Repairs an existing HDR/Advanced Color profile created by an older Gloam build
        /// without touching its measured MHC2 matrix, LUTs, or luminance range. Only the
        /// ordinary ICC TRCs, luminance tag, description, and profile ID are rewritten.
        /// The output must be installed under a fresh filename so Windows cannot reuse a
        /// cached transform for the old profile.
        /// </summary>
        public static void RepairAdvancedColorIccCharacterization(
            string sourcePath, string outputPath, string? description = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source profile path is required.", nameof(sourcePath));
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output profile path is required.", nameof(outputPath));
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "Repair output must use a new path so the source profile remains recoverable.",
                    nameof(outputPath));

            byte[] data = File.ReadAllBytes(sourcePath);
            int mhc2 = FindMhc2Tag(data);
            double peakNits = ReadS15Fixed16(data, mhc2 + 16);
            ValidateLuminanceMetadata(peakNits, nameof(sourcePath), "MHC2 peak");
            if (peakNits <= 0.0)
                throw new InvalidDataException("MHC2 peak luminance must be greater than zero for HDR repair.");

            // Do not rebuild from a stock template: the existing MHC2 block is the measured
            // calibration and must survive byte-for-byte. Repoint only the three classic ICC
            // TRC entries at a new shared sRGB curve.
            data = AppendTagBlock(data, BuildSampledTrc(ColorMath.SrgbEotf),
                RTrcSignature, GTrcSignature, BTrcSignature);
            PatchLumiTag(data, peakNits);

            if (!string.IsNullOrWhiteSpace(description))
                data = PatchDescription(data, description);

            for (int z = 84; z < 100; z++) data[z] = 0;
            File.WriteAllBytes(outputPath, data);
        }

        /// <summary>
        /// Returns true when an MHC2 profile still advertises a non-sRGB classic ICC TRC.
        /// Advanced Color presents ordinary integer app content in sRGB; PQ/HLG belongs in
        /// the private MHC2 wire correction only. Older Gloam profiles advertised PQ here,
        /// which made ICC-aware editors such as Photoshop apply a second HDR conversion.
        /// Invalid or non-MHC2 inputs throw so migration never rewrites an unrelated profile.
        /// </summary>
        public static bool NeedsAdvancedColorIccCharacterizationRepair(string profilePath)
        {
            if (string.IsNullOrWhiteSpace(profilePath))
                throw new ArgumentException("Profile path is required.", nameof(profilePath));

            byte[] data = File.ReadAllBytes(profilePath);
            _ = FindMhc2Tag(data);
            return !IsSrgbTrc(data, RTrcSignature) ||
                   !IsSrgbTrc(data, GTrcSignature) ||
                   !IsSrgbTrc(data, BTrcSignature);
        }

        private static bool IsSrgbTrc(byte[] data, int signature)
        {
            int entry = FindTagEntry(data, signature);
            if (entry < 0) throw new InvalidDataException($"Profile lacks '{SignatureName(signature)}'.");
            int offset = ReadU32(data, entry + 4);
            int size = ReadU32(data, entry + 8);
            if (offset < 0 || size < 12 || offset + size > data.Length ||
                ReadU32(data, offset) != CurvSignature)
                throw new InvalidDataException($"Profile has an invalid '{SignatureName(signature)}' curve.");

            int count = ReadU32(data, offset + 8);
            if (count < 2 || count > 1_000_000 || size < 12 + count * 2)
                return false;

            // Multiple samples prevent a coincidental midpoint match from classifying a
            // gamma/PQ/HLG curve as safe. Tolerance covers 16-bit quantization and linear
            // interpolation of profiles written by other conforming tools.
            double[] probes = { 0.05, 0.18, 0.25, 0.50, 0.75, 0.90 };
            foreach (double signal in probes)
            {
                double position = signal * (count - 1);
                int lo = (int)Math.Floor(position);
                int hi = Math.Min(lo + 1, count - 1);
                double fraction = position - lo;
                double a = ReadU16(data, offset + 12 + lo * 2) / 65535.0;
                double b = ReadU16(data, offset + 12 + hi * 2) / 65535.0;
                double actual = a + (b - a) * fraction;
                if (Math.Abs(actual - ColorMath.SrgbEotf(signal)) > 0.002)
                    return false;
            }
            return true;
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
            if (FindTagEntry(data, DescSignature) < 0)
                return data; // template has no desc tag; nothing to clean up

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

            return AppendTagBlock(data, mluc, DescSignature);
        }

        /// <summary>
        /// Rewrites the profile's colorimetric characterization tags so they describe the
        /// display the profile actually calibrates (the M6 fix) instead of the template's
        /// synthetic sRGB/G2.2 values. ICC v4 display-profile convention:
        ///   wtpt = the PCS illuminant D50 (the media white AFTER chromatic adaptation),
        ///   chad = the Bradford matrix adapting the display's ACTUAL white to D50,
        ///   rXYZ/gXYZ/bXYZ = the display primaries pushed through chad (PCS-adapted),
        ///   rTRC/gTRC/bTRC = the app-facing EOTF (shared 'curv' tag, like the template),
        ///     which is the target EOTF in SDR and sRGB in HDR/Advanced Color,
        ///   chrm = the raw device chromaticities (informational, patched when present).
        /// The fixed-size tags (wtpt/chad/XYZ/chrm) are patched in place; the TRC curve is
        /// appended and the three TRC tag-table entries repointed at the shared block, the
        /// same mechanism <see cref="PatchDescription"/> uses.
        /// </summary>
        private static byte[] PatchCharacterizationTags(byte[] data, CalibrationTarget colorimetry)
        {
            // Bradford adaptation from the profile's actual white to the D50 PCS.
            CieXyz actualWhite = colorimetry.WhitePoint.ToXyz(1.0);
            double[,] chad = BradfordAdaptationMatrix(actualWhite, ColorMath.D50White);

            // PCS-adapted primaries: the columns of the device RGB→XYZ matrix (white-relative,
            // Y_white = 1 by CalculateRgbToXyzMatrix's normalization) pushed through chad.
            double[,] pcsPrimaries = ColorMath.MultiplyMatrices(chad, colorimetry.RgbToXyzMatrix);

            WriteXyzTag(data, WtptSignature, ColorMath.D50White.X, ColorMath.D50White.Y, ColorMath.D50White.Z);
            WriteChadTag(data, chad);
            WriteXyzTag(data, RXyzSignature, pcsPrimaries[0, 0], pcsPrimaries[1, 0], pcsPrimaries[2, 0]);
            WriteXyzTag(data, GXyzSignature, pcsPrimaries[0, 1], pcsPrimaries[1, 1], pcsPrimaries[2, 1]);
            WriteXyzTag(data, BXyzSignature, pcsPrimaries[0, 2], pcsPrimaries[1, 2], pcsPrimaries[2, 2]);
            PatchChrmTag(data, colorimetry);

            return AppendTagBlock(data, BuildIccCharacterizationTrc(colorimetry),
                RTrcSignature, GTrcSignature, BTrcSignature);
        }

        /// <summary>
        /// The 3×3 Bradford chromatic adaptation matrix mapping XYZ relative to
        /// <paramref name="sourceWhite"/> onto XYZ relative to <paramref name="destWhite"/>.
        /// Reconstructed column-by-column from <see cref="ColorMath.ChromaticAdaptation"/>
        /// (a linear transform), so the tag agrees exactly with the app's adaptation math.
        /// </summary>
        private static double[,] BradfordAdaptationMatrix(CieXyz sourceWhite, CieXyz destWhite)
        {
            var m = new double[3, 3];
            for (int c = 0; c < 3; c++)
            {
                var basis = new CieXyz(c == 0 ? 1 : 0, c == 1 ? 1 : 0, c == 2 ? 1 : 0);
                var adapted = ColorMath.ChromaticAdaptation(basis, sourceWhite, destWhite);
                m[0, c] = adapted.X;
                m[1, c] = adapted.Y;
                m[2, c] = adapted.Z;
            }
            return m;
        }

        /// <summary>
        /// Builds the shared classic ICC TRC. Windows Advanced Color's app-facing integer
        /// presentation space remains sRGB even while the display wire uses PQ/HLG. Writing
        /// a PQ/HLG curve here makes ICC-aware apps convert ordinary images into a wire
        /// encoding that DWM then color-manages again, producing a large washed-out shift.
        /// Therefore HDR targets always advertise sRGB in the ordinary ICC matrix/TRC path;
        /// their actual HDR correction stays exclusively in the MHC2 matrix and 1D LUTs.
        /// SDR targets retain their requested transfer function.
        /// </summary>
        private static byte[] BuildIccCharacterizationTrc(CalibrationTarget colorimetry)
        {
            if (colorimetry.IsHdr)
                return BuildSampledTrc(ColorMath.SrgbEotf);

            if (colorimetry.TransferFunction == TransferFunctionType.Gamma &&
                colorimetry.Gamma is double gamma &&
                double.IsFinite(gamma) && gamma is >= 1.0 and <= 4.0)
            {
                // curv with count=1: a single u8Fixed8 gamma value.
                byte[] tag = new byte[14];
                WriteU32(tag, 0, CurvSignature);
                WriteU32(tag, 8, 1);
                WriteU16(tag, 12, (int)Math.Round(gamma * 256.0));
                return tag;
            }

            if (colorimetry.TransferFunction == TransferFunctionType.Linear)
            {
                // curv with count=0 is the ICC identity curve.
                byte[] tag = new byte[12];
                WriteU32(tag, 0, CurvSignature);
                return tag;
            }

            return BuildSampledTrc(colorimetry.ApplyEotf);
        }

        private static byte[] BuildSampledTrc(Func<double, double> eotf)
        {
            byte[] sampled = new byte[12 + TrcSampleCount * 2];
            WriteU32(sampled, 0, CurvSignature);
            WriteU32(sampled, 8, TrcSampleCount);
            for (int i = 0; i < TrcSampleCount; i++)
            {
                double signal = i / (double)(TrcSampleCount - 1);
                double linear = Math.Clamp(eotf(signal), 0.0, 1.0);
                WriteU16(sampled, 12 + i * 2, (int)Math.Round(linear * 65535.0));
            }
            return sampled;
        }

        /// <summary>Writes an in-place XYZType tag (sig + reserved + 3 × s15Fixed16).</summary>
        private static void WriteXyzTag(byte[] data, int tagSignature, double x, double y, double z)
        {
            int off = RequireTagDataOffset(data, tagSignature, 20);
            if (ReadU32(data, off) != XyzTypeSignature)
                throw new InvalidDataException($"Tag '{SignatureName(tagSignature)}' is not XYZ-typed where expected.");
            WriteS15Fixed16(data, off + 8, x);
            WriteS15Fixed16(data, off + 12, y);
            WriteS15Fixed16(data, off + 16, z);
        }

        /// <summary>Writes the chad tag's 3×3 s15Fixed16 matrix (sf32 type) in place, row-major.</summary>
        private static void WriteChadTag(byte[] data, double[,] chad)
        {
            int off = RequireTagDataOffset(data, ChadSignature, 8 + 9 * 4);
            if (ReadU32(data, off) != Sf32Signature)
                throw new InvalidDataException("chad tag is not 'sf32' typed where expected.");
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    WriteS15Fixed16(data, off + 8 + (r * 3 + c) * 4, chad[r, c]);
        }

        /// <summary>
        /// Patches the optional 'chrm' tag in place with the raw device chromaticities
        /// (phosphor/colorant type set to 0 = unknown, since these are measured/target
        /// values, not a standard set). Skipped silently when the template lacks the tag.
        /// </summary>
        private static void PatchChrmTag(byte[] data, CalibrationTarget colorimetry)
        {
            int entry = FindTagEntry(data, ChrmSignature);
            if (entry < 0) return;
            int off = ReadU32(data, entry + 4);
            int size = ReadU32(data, entry + 8);
            // chrm: sig(4) reserved(4) channels u16(2) phosphorType u16(2) + 3 × (x,y) u16Fixed16.
            if (off < 0 || size < 12 + 3 * 8 || off + size > data.Length) return;

            WriteU16(data, off + 10, 0); // phosphor/colorant type: unknown
            var xy = new[] { colorimetry.RedPrimary, colorimetry.GreenPrimary, colorimetry.BluePrimary };
            for (int i = 0; i < 3; i++)
            {
                WriteU16Fixed16(data, off + 12 + i * 8, xy[i].X);
                WriteU16Fixed16(data, off + 12 + i * 8 + 4, xy[i].Y);
            }
        }

        /// <summary>
        /// Appends <paramref name="tagData"/> as a new 4-byte-aligned block at the end of the
        /// profile and repoints every listed tag-table entry at it (shared data is legal —
        /// the template's three TRC entries already share one block). Updates the header
        /// profile-size field; the old tag bytes become unused slack, which readers ignore
        /// because they only follow the tag table.
        /// </summary>
        private static byte[] AppendTagBlock(byte[] data, byte[] tagData, params int[] tagSignatures)
        {
            var entries = new int[tagSignatures.Length];
            for (int i = 0; i < tagSignatures.Length; i++)
            {
                entries[i] = FindTagEntry(data, tagSignatures[i]);
                if (entries[i] < 0)
                    throw new InvalidDataException(
                        $"Template lacks required tag '{SignatureName(tagSignatures[i])}'.");
            }

            int newOffset = (data.Length + 3) & ~3; // 4-byte aligned tag start
            int padded = (tagData.Length + 3) & ~3;
            byte[] result = new byte[newOffset + padded];
            Array.Copy(data, result, data.Length);
            Array.Copy(tagData, 0, result, newOffset, tagData.Length);

            foreach (int entry in entries)
            {
                WriteU32(result, entry + 4, newOffset);
                WriteU32(result, entry + 8, tagData.Length);
            }
            WriteU32(result, 0, result.Length); // header profile-size field
            return result;
        }

        /// <summary>Tag-table entry offset for <paramref name="signature"/>, or -1 when absent.</summary>
        private static int FindTagEntry(byte[] data, int signature)
        {
            int tagCount = ReadU32(data, IccHeaderSize);
            for (int i = 0; i < tagCount; i++)
            {
                int e = IccHeaderSize + 4 + i * IccTagEntrySize;
                if (e + 12 <= data.Length && ReadU32(data, e) == signature)
                    return e;
            }
            return -1;
        }

        private static int RequireTagDataOffset(byte[] data, int signature, int minSize)
        {
            int entry = FindTagEntry(data, signature);
            if (entry < 0)
                throw new InvalidDataException($"Template lacks required tag '{SignatureName(signature)}'.");
            int off = ReadU32(data, entry + 4);
            int size = ReadU32(data, entry + 8);
            if (off < 0 || size < minSize || off + size > data.Length)
                throw new InvalidDataException($"Tag '{SignatureName(signature)}' offset/size out of range.");
            return off;
        }

        private static string SignatureName(int signature) => new(new[]
        {
            (char)((signature >> 24) & 0xFF),
            (char)((signature >> 16) & 0xFF),
            (char)((signature >> 8) & 0xFF),
            (char)(signature & 0xFF),
        });

        /// <summary>
        /// Writes the display peak luminance into the 'lumi' tag. ICC.1 defines luminance
        /// as absolute cd/m² in Y and requires X/Z to be zero; rewrite the full XYZ payload
        /// so stale template chromaticity components cannot leak into color-managed apps.
        /// </summary>
        private static void PatchLumiTag(byte[] data, double maxNits)
        {
            ValidateLuminanceMetadata(maxNits, nameof(maxNits), "Maximum");

            int entry = FindTagEntry(data, LumiSignature);
            if (entry < 0) return;

            int off = RequireTagDataOffset(data, LumiSignature, 20);
            if (ReadU32(data, off) != XyzTypeSignature)
                throw new InvalidDataException("Tag 'lumi' is not XYZ-typed where expected.");

            WriteS15Fixed16(data, off + 8, 0.0);
            WriteS15Fixed16(data, off + 12, maxNits);
            WriteS15Fixed16(data, off + 16, 0.0);
        }

        private static void WriteU32(byte[] b, int o, int v)
        {
            b[o] = (byte)((v >> 24) & 0xFF);
            b[o + 1] = (byte)((v >> 16) & 0xFF);
            b[o + 2] = (byte)((v >> 8) & 0xFF);
            b[o + 3] = (byte)(v & 0xFF);
        }

        private static void WriteU16(byte[] b, int o, int v)
        {
            b[o] = (byte)((v >> 8) & 0xFF);
            b[o + 1] = (byte)(v & 0xFF);
        }

        /// <summary>u16Fixed16: unsigned 16.16 (used by the 'chrm' tag's xy coordinates).</summary>
        private static void WriteU16Fixed16(byte[] b, int o, double value)
        {
            long fixed16 = (long)Math.Round(value * 65536.0);
            fixed16 = Math.Clamp(fixed16, 0, uint.MaxValue);
            WriteU32(b, o, unchecked((int)(uint)fixed16));
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
        ///
        /// NOTE (m8): the sandwich — and the exact sRGB/D65 constants of its fixed stages —
        /// is UNDOCUMENTED Windows behavior, reverse-engineered from MHC2Gen and validated
        /// on-screen against the Windows 11 22621 DWM (the magenta-cast repro machine). A
        /// regression test pins the identity wrap (ToMhc2MatrixDomain(I) ≈ I) so any change
        /// to the D65/sRGB wrap constants is caught before it can re-tint installs.
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

            for (int i = 0; i < lut.Length; i++)
            {
                if (!double.IsFinite(lut[i]))
                    throw new ArgumentException($"{name}[{i}] is not finite.", name);
                if (lut[i] < -1e-6 || lut[i] > 1.0 + 1e-6)
                    throw new ArgumentException($"{name}[{i}]={lut[i]:F6} is outside the normalized signal range [0,1].", name);
                if (i > 0 && lut[i] + 1e-9 < lut[i - 1])
                    throw new ArgumentException($"{name} must be monotonic non-decreasing; index {i - 1}->{i} is {lut[i - 1]:F6}->{lut[i]:F6}.", name);
            }
        }

        private static void ValidateMatrix(double[,] matrix, string name)
        {
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    if (!double.IsFinite(matrix[r, c]))
                        throw new ArgumentException($"{name}[{r},{c}] is not finite.", name);
        }

        private static void ValidateLuminanceMetadata(double value, string paramName, string label)
        {
            if (!double.IsFinite(value))
                throw new ArgumentException($"{label} luminance must be finite.", paramName);
            if (value < 0.0 || value > 32767.0)
                throw new ArgumentOutOfRangeException(paramName, value,
                    $"{label} luminance must be in the representable MHC2 s15Fixed16 range [0, 32767] nits.");
        }

        private static int ReadU32(byte[] b, int o) =>
            (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        private static int ReadU16(byte[] b, int o) => (b[o] << 8) | b[o + 1];

        private static double ReadS15Fixed16(byte[] b, int o) => ReadU32(b, o) / 65536.0;

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
