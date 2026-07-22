using Xunit;
using HDRGammaController.Core.Calibration;
using System;
using System.IO;

namespace HDRGammaController.Tests
{
    public class CgatsValidatorTests
    {
        // A minimal, structurally-valid .ccmx (matches Argyll's own sample format: CCMX
        // header, NUMBER_OF_FIELDS + data-format block, NUMBER_OF_SETS + data block).
        private const string ValidCcmx = @"
CCMX

DESCRIPTOR ""Xrite DTP94 & Dell 2408WFP""
KEYWORD ""INSTRUMENT""
INSTRUMENT ""Xrite DTP94""
KEYWORD ""DISPLAY""
DISPLAY ""Dell 2408WFP""
KEYWORD ""REFERENCE""
REFERENCE ""GretagMacbeth i1 Pro""
ORIGINATOR ""Argyll ccmx""
CREATED ""Mon Sep 20 10:37:13 2010""
KEYWORD ""COLOR_REP""
COLOR_REP ""XYZ""

NUMBER_OF_FIELDS 3
BEGIN_DATA_FORMAT
XYZ_X XYZ_Y XYZ_Z
END_DATA_FORMAT

NUMBER_OF_SETS 3
BEGIN_DATA
0.86461 0.030195 0.012871
-0.069159 1.0244 0.017185
-0.010324 0.011396 0.96486
END_DATA
";

        // A minimal valid .ccss: CCSS header + SPECTRAL_BANDS + the same data-format/data
        // skeleton (truncated spectral data for brevity).
        private const string ValidCcss = @"CCSS

DESCRIPTOR ""CCSS for CRT""
ORIGINATOR ""Argyll ccxxmake""
CREATED ""Wed Aug 31 22:46:24 2011""
KEYWORD ""DISPLAY""
DISPLAY ""Test""
KEYWORD ""SPECTRAL_BANDS""
SPECTRAL_BANDS ""3""
KEYWORD ""SPECTRAL_START_NM""
SPECTRAL_START_NM ""380.000000""
KEYWORD ""SPECTRAL_END_NM""
SPECTRAL_END_NM ""740.000000""

NUMBER_OF_FIELDS 4
BEGIN_DATA_FORMAT
SAMPLE_ID SPEC_380 SPEC_560 SPEC_740
END_DATA_FORMAT

NUMBER_OF_SETS 2
BEGIN_DATA
1 0.5 0.6 0.1
2 0.4 0.7 0.2
END_DATA
";

        private const string RgbCcss = @"CCSS

DESCRIPTOR ""RGB sample""
KEYWORD ""SPECTRAL_BANDS""
SPECTRAL_BANDS ""5""

NUMBER_OF_FIELDS 6
BEGIN_DATA_FORMAT
SAMPLE_ID SPEC_430 SPEC_490 SPEC_540 SPEC_610 SPEC_660
END_DATA_FORMAT

NUMBER_OF_SETS 4
BEGIN_DATA
1 0.01 0.01 0.01 0.01 0.01
2 0.02 0.02 0.02 0.80 1.00
3 0.03 0.20 1.00 0.08 0.02
4 1.00 0.80 0.10 0.02 0.01
END_DATA
";

        [Fact]
        public void ValidCcmx_Passes()
        {
            var result = CgatsValidator.Validate(ValidCcmx, "ccmx");
            Assert.True(result.IsValid, result.Error ?? "expected valid");
        }

        [Fact]
        public void Ccmx_AllowsNegativeMatrixCoefficients()
        {
            var result = CgatsValidator.Validate(ValidCcmx, "ccmx");
            Assert.True(result.IsValid, result.Error ?? "expected negative matrix coefficients to be valid");
        }

        [Fact]
        public void ValidCcss_Passes()
        {
            var result = CgatsValidator.Validate(ValidCcss, "ccss");
            Assert.True(result.IsValid, result.Error ?? "expected valid");
        }

        [Fact]
        public void EmptyContent_Rejected()
        {
            var result = CgatsValidator.Validate("");
            Assert.False(result.IsValid);
        }

        [Fact]
        public void WhitespaceOnly_Rejected()
        {
            var result = CgatsValidator.Validate("   \n\t  \n");
            Assert.False(result.IsValid);
        }

        [Fact]
        public void WrongHeaderKeyword_Rejected()
        {
            var result = CgatsValidator.Validate("JUNK\nNUMBER_OF_SETS 1\nBEGIN_DATA\nEND_DATA\n");
            Assert.False(result.IsValid);
        }

        [Fact]
        public void MissingNumberofSets_Rejected()
        {
            var broken = ValidCcmx.Replace("NUMBER_OF_SETS 3", "");
            var result = CgatsValidator.Validate(broken);
            Assert.False(result.IsValid);
            Assert.Contains("NUMBER_OF_SETS", result.Error ?? "");
        }

        [Fact]
        public void NonPositiveSetCount_Rejected()
        {
            var broken = ValidCcmx.Replace("NUMBER_OF_SETS 3", "NUMBER_OF_SETS 0");
            var result = CgatsValidator.Validate(broken);
            Assert.False(result.IsValid);
            Assert.Contains("positive integer", result.Error ?? "");
        }

        [Fact]
        public void NonNumericSetCount_Rejected()
        {
            var broken = ValidCcmx.Replace("NUMBER_OF_SETS 3", "NUMBER_OF_SETS abc");
            var result = CgatsValidator.Validate(broken);
            Assert.False(result.IsValid);
        }

        [Fact]
        public void UnbalancedDataBlock_Rejected()
        {
            // Remove END_DATA so the BEGIN_DATA block is unclosed.
            var broken = ValidCcmx.Replace("END_DATA", "");
            var result = CgatsValidator.Validate(broken);
            Assert.False(result.IsValid);
            Assert.Contains("BEGIN_DATA", result.Error ?? "");
        }

        [Fact]
        public void EndBeforeBegin_Rejected()
        {
            // END_DATA_FORMAT appears before BEGIN_DATA_FORMAT — out of order is invalid.
            var reversed = "CCMX\nNUMBER_OF_FIELDS 3\nEND_DATA_FORMAT\nXYZ_X XYZ_Y XYZ_Z\nBEGIN_DATA_FORMAT\nNUMBER_OF_SETS 1\nBEGIN_DATA\n1 2 3\nEND_DATA\n";
            var result = CgatsValidator.Validate(reversed);
            Assert.False(result.IsValid);
        }

        [Fact]
        public void NulBytes_Rejected()
        {
            var poisoned = ValidCcmx.Replace("DESCRIPTOR", "DESCR\0IPT");
            var result = CgatsValidator.Validate(poisoned);
            Assert.False(result.IsValid);
            Assert.Contains("NUL", result.Error ?? "");
        }

        [Fact]
        public void ImplausiblyLarge_Rejected()
        {
            // Build a file over the 2 MB ceiling.
            var sb = new System.Text.StringBuilder(2_100_000);
            sb.Append("CCMX\n");
            sb.Append('x', 2_100_000);
            var result = CgatsValidator.Validate(sb.ToString());
            Assert.False(result.IsValid);
            Assert.Contains("large", result.Error ?? "");
        }

        [Fact]
        public void IsValidFile_RejectsOversizedFileBeforeParsing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"gloam-oversized-{Guid.NewGuid():N}.ccss");
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.SetLength(CgatsValidator.MaxFileBytes + 1);

                Assert.False(CgatsValidator.IsValidFile(path, "ccss"));
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void CgatsKeyword_AlsoAccepted()
        {
            // Some Argyll files begin with the generic CGATS keyword rather than CCMX/CCSS.
            var cgats = ValidCcmx.Replace("CCMX", "CGATS");
            var result = CgatsValidator.Validate(cgats);
            Assert.True(result.IsValid, result.Error ?? "");
        }

        [Fact]
        public void TypeMismatch_Rejected()
        {
            // Content is a CCMX but caller claims it's a ccss.
            var result = CgatsValidator.Validate(ValidCcmx, "ccss");
            Assert.False(result.IsValid);
            Assert.Contains("ccss", result.Error ?? "");
        }

        [Fact]
        public void NonFiniteNumericPayload_Rejected()
        {
            var broken = ValidCcmx.Replace("1.0244", "NaN");
            var result = CgatsValidator.Validate(broken, "ccmx");
            Assert.False(result.IsValid);
            Assert.Contains("finite", result.Error ?? "");
        }

        [Fact]
        public void DataRowCountMismatch_Rejected()
        {
            var broken = ValidCcss.Replace("NUMBER_OF_SETS 2", "NUMBER_OF_SETS 3");
            var result = CgatsValidator.Validate(broken, "ccss");
            Assert.False(result.IsValid);
            Assert.Contains("NUMBER_OF_SETS", result.Error ?? "");
        }

        [Fact]
        public void NegativeCcssSpectralSample_Passes()
        {
            var correction = ValidCcss.Replace("0.7 0.2", "-0.7 0.2");
            var result = CgatsValidator.Validate(correction, "ccss");
            Assert.True(result.IsValid, result.Error ?? "expected negative spectral samples to be accepted");
        }

        [Fact]
        public void CcssMelanopicEstimator_InfersPrimaryCoefficients()
        {
            var coefficients = CcssMelanopicEstimator.TryEstimate(RgbCcss, "test.ccss");

            Assert.NotNull(coefficients);
            Assert.True(coefficients!.BlueMelanopicPerLuminance > coefficients.RedMelanopicPerLuminance);
            Assert.True(coefficients.GreenMelanopicPerLuminance > coefficients.RedMelanopicPerLuminance);
            Assert.Equal("test.ccss", coefficients.SourceName);
        }

        [Fact]
        public void CcssDatabaseClient_Save_ReturnsExistingFileForDuplicateContent()
        {
            string dir = Path.Combine(Path.GetTempPath(), "GloamCcssTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var entry = new CcssDatabaseClient.Entry(
                    "ccss", "M27Q P", "", "", "i1 Pro", "2026-01-01", ValidCcss);

                string first = CcssDatabaseClient.Save(entry, dir);
                string second = CcssDatabaseClient.Save(entry, dir);

                Assert.Equal(first, second);
                Assert.Single(Directory.GetFiles(dir, "*.ccss"));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void CcssDatabaseClient_ListSaved_DeduplicatesMatchingLocalFiles()
        {
            string dir = Path.Combine(Path.GetTempPath(), "GloamCcssTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "M27Q P.ccss"), ValidCcss);
                File.WriteAllText(Path.Combine(dir, "M27Q P (2).ccss"), ValidCcss);

                var saved = CcssDatabaseClient.ListSaved(dir, "M27Q", "ccss");

                Assert.Single(saved);
                Assert.Equal("Saved", saved[0].Source);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void CcssDatabaseClient_ListSaved_RejectsUnknownCorrectionType()
        {
            string dir = Path.Combine(Path.GetTempPath(), "GloamCcssTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Assert.Throws<ArgumentException>(() =>
                    CcssDatabaseClient.ListSaved(dir, "", "executable"));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Validate_RejectsUnknownExpectedCorrectionType()
        {
            var result = CgatsValidator.Validate(ValidCcss, "executable");

            Assert.False(result.IsValid);
            Assert.Contains("ccmx", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }
}
