using System;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Pure decision logic for the pre-measurement foreign-correction preflight:
    /// the MHC2 tag-table scan, the Night Light state-blob parse, and the
    /// foreign-default-profile assessment.
    /// </summary>
    public class ForeignCorrectionPreflightTests
    {
        #region MHC2 tag scan

        /// <summary>
        /// Minimal synthetic ICC container: 128-byte header, big-endian tag count at 128,
        /// then 12-byte tag entries [signature][offset][size]. Enough structure for the
        /// signature scan; tag payloads are irrelevant to it.
        /// </summary>
        private static byte[] IccProfile(params string[] tagSignatures)
        {
            int count = tagSignatures.Length;
            var bytes = new byte[128 + 4 + count * 12 + 16];
            WriteU32BE(bytes, 0, (uint)bytes.Length);      // profile size
            bytes[36] = (byte)'a';                          // 'acsp' magic
            bytes[37] = (byte)'c';
            bytes[38] = (byte)'s';
            bytes[39] = (byte)'p';
            WriteU32BE(bytes, 128, (uint)count);
            for (int i = 0; i < count; i++)
            {
                int entry = 132 + i * 12;
                for (int c = 0; c < 4; c++)
                    bytes[entry + c] = (byte)tagSignatures[i][c];
                WriteU32BE(bytes, entry + 4, (uint)(128 + 4 + count * 12)); // offset
                WriteU32BE(bytes, entry + 8, 16);                            // size
            }
            return bytes;
        }

        private static void WriteU32BE(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }

        [Fact]
        public void ContainsMhc2Tag_ProfileWithMhc2_ReturnsTrue()
        {
            var bytes = IccProfile("desc", "wtpt", "MHC2", "vcgt");
            Assert.True(CalibrationInstallPreflight.ContainsMhc2Tag(bytes));
        }

        [Fact]
        public void ContainsMhc2Tag_ProfileWithoutMhc2_ReturnsFalse()
        {
            var bytes = IccProfile("desc", "wtpt", "rXYZ", "gXYZ", "bXYZ", "vcgt");
            Assert.False(CalibrationInstallPreflight.ContainsMhc2Tag(bytes));
        }

        [Fact]
        public void ContainsMhc2Tag_NullOrShortData_ReturnsFalse()
        {
            Assert.False(CalibrationInstallPreflight.ContainsMhc2Tag(null));
            Assert.False(CalibrationInstallPreflight.ContainsMhc2Tag(Array.Empty<byte>()));
            Assert.False(CalibrationInstallPreflight.ContainsMhc2Tag(new byte[100]));
        }

        [Fact]
        public void ContainsMhc2Tag_AbsurdTagCount_ReturnsFalse()
        {
            var bytes = new byte[256];
            WriteU32BE(bytes, 128, 0xFFFFFFFF);
            Assert.False(CalibrationInstallPreflight.ContainsMhc2Tag(bytes));
        }

        [Fact]
        public void ContainsMhc2Tag_TruncatedTagTable_ReturnsFalseWithoutThrowing()
        {
            // Claims 8 tags but the buffer ends mid-table.
            var bytes = new byte[128 + 4 + 20];
            WriteU32BE(bytes, 128, 8);
            Assert.False(CalibrationInstallPreflight.ContainsMhc2Tag(bytes));
        }

        [Fact]
        public void ContainsMhc2Tag_Mhc2AsLastReachableEntry_ReturnsTrue()
        {
            var bytes = IccProfile("desc", "MHC2");
            Assert.True(CalibrationInstallPreflight.ContainsMhc2Tag(bytes));
        }

        #endregion

        #region Night Light blob parse

        /// <summary>
        /// FIXTURE PROVENANCE: synthetic blobs modeled on the reverse-engineered CloudStore
        /// record layout documented by community night-light tools (Rafael Rivera's toggle
        /// gist; superuser.com/a/1209192), observed Windows 10 1903 – Windows 11 23H2:
        /// a fixed 0x43 0x42 ("CB") preamble, a 5-byte varint timestamp, then field tags;
        /// the state field header sits at index 18 — 0x15 when Night Light is ON (blob is
        /// 43 bytes, with an extra 0x10 0x00 field), 0x13 when OFF (41 bytes). Only index
        /// 18 and the minimum length are load-bearing for the parser under test.
        /// </summary>
        private static byte[] NightLightBlob(byte stateByte, int length)
        {
            var data = new byte[length];
            byte[] preamble = { 0x43, 0x42, 0x01, 0x00, 0x0A, 0x02, 0x01, 0x00, 0x2A, 0x06 };
            Array.Copy(preamble, data, preamble.Length);
            // Plausible varint timestamp + field tags leading up to the state byte.
            data[10] = 0x95; data[11] = 0xC6; data[12] = 0xA0; data[13] = 0x99; data[14] = 0x06;
            data[15] = 0x2A; data[16] = 0x2B; data[17] = 0x0E;
            data[18] = stateByte;
            data[20] = 0x43; data[21] = 0x42; data[22] = 0x01;
            return data;
        }

        [Fact]
        public void IsNightLightActiveBlob_OnFixture_ReturnsTrue()
        {
            Assert.True(CalibrationInstallPreflight.IsNightLightActiveBlob(NightLightBlob(0x15, 43)));
        }

        [Fact]
        public void IsNightLightActiveBlob_OffFixture_ReturnsFalse()
        {
            Assert.False(CalibrationInstallPreflight.IsNightLightActiveBlob(NightLightBlob(0x13, 41)));
        }

        [Fact]
        public void IsNightLightActiveBlob_UnknownStateByte_ReturnsNull()
        {
            // A future build changing the layout must read as "cannot determine",
            // never as a confident answer.
            Assert.Null(CalibrationInstallPreflight.IsNightLightActiveBlob(NightLightBlob(0x00, 43)));
            Assert.Null(CalibrationInstallPreflight.IsNightLightActiveBlob(NightLightBlob(0xFF, 41)));
        }

        [Fact]
        public void IsNightLightActiveBlob_MissingOrShortBlob_ReturnsNull()
        {
            Assert.Null(CalibrationInstallPreflight.IsNightLightActiveBlob(null));
            Assert.Null(CalibrationInstallPreflight.IsNightLightActiveBlob(Array.Empty<byte>()));
            Assert.Null(CalibrationInstallPreflight.IsNightLightActiveBlob(new byte[16]));
        }

        #endregion

        #region Foreign default assessment

        private const string GloamPrefix = "Test Display - ";

        [Fact]
        public void AssessForeignDefaults_GloamProfiles_AreExcluded()
        {
            var result = CalibrationInstallPreflight.AssessForeignDefaults(
                "Test Display - sRGB G2.2 - 2026-06-09 2245.icm",
                "Test Display - HDR PQ - 2026-06-10 0900.icm",
                GloamPrefix,
                _ => true);

            Assert.Empty(result);
        }

        [Fact]
        public void AssessForeignDefaults_ForeignProfilesOnBothLists_AreReportedWithTagState()
        {
            var result = CalibrationInstallPreflight.AssessForeignDefaults(
                "DisplayCAL calibrated.icm",
                "Windows HDR Calibration.icm",
                GloamPrefix,
                name => name.StartsWith("DisplayCAL", StringComparison.Ordinal));

            Assert.Equal(2, result.Count);

            var sdr = Assert.Single(result, r => !r.IsAdvancedColor);
            Assert.Equal("DisplayCAL calibrated.icm", sdr.ProfileName);
            Assert.True(sdr.HasMhc2Tag);

            var ac = Assert.Single(result, r => r.IsAdvancedColor);
            Assert.Equal("Windows HDR Calibration.icm", ac.ProfileName);
            Assert.False(ac.HasMhc2Tag);
        }

        [Fact]
        public void AssessForeignDefaults_NullOrBlankDefaults_ReturnEmpty()
        {
            Assert.Empty(CalibrationInstallPreflight.AssessForeignDefaults(
                null, null, GloamPrefix, _ => true));
            Assert.Empty(CalibrationInstallPreflight.AssessForeignDefaults(
                "  ", "", GloamPrefix, _ => true));
        }

        [Fact]
        public void AssessForeignDefaults_TagLookupThrowing_DegradesToNoTag()
        {
            var result = CalibrationInstallPreflight.AssessForeignDefaults(
                "vendor.icm", null, GloamPrefix,
                _ => throw new InvalidOperationException("store unreadable"));

            var entry = Assert.Single(result);
            Assert.False(entry.HasMhc2Tag); // warn-only path: detection failure never blocks
        }

        [Fact]
        public void AssessForeignDefaults_SameProfileOnBothLists_ReportsBothAssociations()
        {
            // The same foreign MHC2 profile can be default on the SDR AND Advanced-Color
            // lists; both associations must be tracked so restore puts both back.
            var result = CalibrationInstallPreflight.AssessForeignDefaults(
                "vendor.icm", "vendor.icm", GloamPrefix, _ => true);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, r => !r.IsAdvancedColor);
            Assert.Contains(result, r => r.IsAdvancedColor);
        }

        [Fact]
        public void AssessForeignDefaults_TrimsAndComparesPrefixCaseInsensitively()
        {
            var result = CalibrationInstallPreflight.AssessForeignDefaults(
                "  test display - sRGB G2.2 - 2026-06-09 2245.icm  ",
                null,
                GloamPrefix,
                _ => true);

            Assert.Empty(result);
        }

        [Fact]
        public void AssessForeignDefaults_NoGloamPrefix_TreatsEverythingAsForeign()
        {
            var result = CalibrationInstallPreflight.AssessForeignDefaults(
                "anything.icm", null, gloamProfilePrefix: null, _ => false);

            Assert.Single(result);
            Assert.Equal("anything.icm", result.Single().ProfileName);
        }

        #endregion
    }
}
