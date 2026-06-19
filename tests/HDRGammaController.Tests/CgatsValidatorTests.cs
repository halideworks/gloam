using Xunit;
using HDRGammaController.Core.Calibration;

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

        [Fact]
        public void ValidCcmx_Passes()
        {
            var result = CgatsValidator.Validate(ValidCcmx, "ccmx");
            Assert.True(result.IsValid, result.Error ?? "expected valid");
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
    }
}
