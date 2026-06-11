using System;
using System.IO;

namespace HDRGammaController.Core
{
    public static class ProfileTemplatePatching
    {
        // ICC v4 header is 128 bytes; the tag count is at offset 128, tag table follows at 132.
        // Each tag table entry is 12 bytes: signature (4), offset (4), size (4). All big-endian.
        // Hard caps keep a malformed or hostile profile from steering us into arbitrary reads.
        private const int IccHeaderSize = 128;
        private const int IccTagEntrySize = 12;
        private const int MaxTagCount = 4096;           // defensive; real profiles have tens
        private const int LutSamples = 1024;
        private const int LutBytes = LutSamples * 4;    // 16.16 fixed, 4 bytes per sample

        public static void PatchProfile(string templatePath, string outputPath, double[] newLut)
        {
            if (newLut == null) throw new ArgumentNullException(nameof(newLut));
            if (newLut.Length != LutSamples)
                throw new ArgumentException($"LUT must contain exactly {LutSamples} entries.", nameof(newLut));

            byte[] data = File.ReadAllBytes(templatePath);

            if (data.Length < IccHeaderSize + 4)
                throw new ArgumentException("Invalid ICC profile (too small).");

            int declaredSize = ReadInt32BigEndian(data, 0);
            if (declaredSize != data.Length)
            {
                // Don't hard-fail: many real profiles have a header size that matches the file.
                // If it's wildly off, reject — that's a strong signal of corruption or tampering.
                if (declaredSize < IccHeaderSize || declaredSize > data.Length + 1024)
                    throw new ArgumentException("ICC profile declared size is inconsistent with file size.");
            }

            int tagCount = ReadInt32BigEndian(data, IccHeaderSize);
            if (tagCount < 0 || tagCount > MaxTagCount)
                throw new ArgumentException($"ICC profile tag count ({tagCount}) is out of range.");

            int tagTableEnd = IccHeaderSize + 4 + (tagCount * IccTagEntrySize);
            if (tagTableEnd > data.Length)
                throw new ArgumentException("ICC profile tag table extends past end of file.");

            int mhc2Offset = -1;
            int mhc2Size = -1;
            for (int i = 0; i < tagCount; i++)
            {
                int entryStart = IccHeaderSize + 4 + (i * IccTagEntrySize);
                int sig = ReadInt32BigEndian(data, entryStart);
                if (sig == 0x6D686332) // 'mhc2'
                {
                    mhc2Offset = ReadInt32BigEndian(data, entryStart + 4);
                    mhc2Size = ReadInt32BigEndian(data, entryStart + 8);
                    break;
                }
            }

            if (mhc2Offset < 0) throw new ArgumentException("Template does not contain an 'mhc2' tag.");
            if (mhc2Size < 0 || mhc2Offset + mhc2Size > data.Length)
                throw new ArgumentException("'mhc2' tag offset/size exceeds file bounds.");

            // Locate the 1024-entry LUT inside the mhc2 tag by finding the count field.
            // mhc2 is a Microsoft-private tag not formally specified in ICC, so we scan for
            // either a big-endian or little-endian 1024 count, require there to be room for
            // 4096 bytes of samples after it, and perform a monotonicity sanity-check on the
            // first several candidate samples to avoid landing on an unrelated 1024 word.
            int lutAbsOffset = FindLutStart(data, mhc2Offset, mhc2Size);
            if (lutAbsOffset < 0)
                throw new InvalidOperationException("Could not locate LUT start in mhc2 tag.");

            for (int k = 0; k < LutSamples; k++)
            {
                double val = Math.Clamp(newLut[k], 0.0, 1.0);
                int fixedPt = (int)Math.Round(val * 65536.0);
                if (fixedPt > 0xFFFF) fixedPt = 0xFFFF; // saturate; 1.0 → 65536 overflows unsigned 16.16
                WriteInt32BigEndian(data, lutAbsOffset + (k * 4), fixedPt);
            }

            // Profile ID at bytes 84-99: must be zeroed because we just changed the body.
            // Consumers that verify the MD5 would otherwise reject the patched profile.
            for (int z = 84; z < 100; z++) data[z] = 0;

            File.WriteAllBytes(outputPath, data);
        }

        private static int FindLutStart(byte[] data, int mhc2Offset, int mhc2Size)
        {
            // Iterate 4-byte-aligned within the mhc2 tag. The count precedes the sample array.
            int end = Math.Min(mhc2Offset + mhc2Size, data.Length) - LutBytes - 4;
            for (int j = 0; j <= mhc2Size - 4 && mhc2Offset + j <= end; j += 4)
            {
                int valBe = ReadInt32BigEndian(data, mhc2Offset + j);
                int valLe = ReadInt32LittleEndian(data, mhc2Offset + j);
                if (valBe == 1024 || valLe == 1024)
                {
                    int candidate = mhc2Offset + j + 4;
                    if (LooksLikeLut(data, candidate)) return candidate;
                }
            }
            return -1;
        }

        /// <summary>
        /// Cheap plausibility check on a candidate LUT location. A real 1024-point calibration
        /// LUT is monotonically non-decreasing over a large stretch; random data from a
        /// coincidentally-matching 1024 word typically isn't.
        /// </summary>
        private static bool LooksLikeLut(byte[] data, int offset)
        {
            if (offset + LutBytes > data.Length) return false;
            int increases = 0;
            int prev = ReadInt32BigEndian(data, offset);
            for (int k = 1; k < 64; k++)
            {
                int cur = ReadInt32BigEndian(data, offset + k * 4);
                if (cur >= prev) increases++;
                prev = cur;
            }
            return increases >= 58; // allow a few equal/noisy samples near black
        }

        private static int ReadInt32BigEndian(byte[] buf, int offset)
        {
            return (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];
        }

        private static int ReadInt32LittleEndian(byte[] buf, int offset)
        {
            return (buf[offset + 3] << 24) | (buf[offset + 2] << 16) | (buf[offset + 1] << 8) | buf[offset];
        }

        private static void WriteInt32BigEndian(byte[] buf, int offset, int val)
        {
            buf[offset] = (byte)((val >> 24) & 0xFF);
            buf[offset + 1] = (byte)((val >> 16) & 0xFF);
            buf[offset + 2] = (byte)((val >> 8) & 0xFF);
            buf[offset + 3] = (byte)(val & 0xFF);
        }
    }
}
