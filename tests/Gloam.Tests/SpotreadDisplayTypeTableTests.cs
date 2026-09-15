using System.Collections.Generic;
using Gloam.Core.Calibration;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// Issue #7: spotread's -y selectors are built per instrument and per installed
    /// correction. These fixtures are verbatim usage output from three real machines.
    /// </summary>
    public class SpotreadDisplayTypeTableTests
    {
        // Argyll 3.5.0 downloaded by Gloam, i1Display3, no corrections imported (the reporter).
        internal const string CleanI1D3Usage =
            " -rw                  Use reflection white point relative chromatically adjusted mode\n" +
            " -y n|l                i1D3: Non-Refresh display [Default,CB1]\n" +
            "    r|c                i1D3: Refresh display [CB2]\n" +
            "    1                  i1D3: LCD CCFL PVA (Resolve JVC CPF)\n" +
            "    l|c                Other: l = LCD, c = CRT\n" +
            " -I illum             Set simulated instrument illumination using FWA (def -i illum):\n" +
            "                       M0, M1, M2, A, C, D50, D50M2, D65, F5, F8, F10 or file.sp]\n";

        // Same Argyll build on a machine where the X-Rite EDR corrections were imported.
        internal const string EdrRichI1D3Usage =
            " -y n                  i1D3: Non-Refresh display [Default,CB1]\n" +
            "    r                  i1D3: Refresh display [CB2]\n" +
            "    c                  i1D3: CRT (Hitachi CM2112MET, Diamond View 1772ie)\n" +
            "    l                  i1D3: LCD CCFL IPS (CCFL AC EIZO HP with CORRECTION)\n" +
            "    L                  i1D3: LCD CCFL Wide Gamut IPS (WG CCFL NEC241 271)\n" +
            "    i                  i1D3: LCD GB-R Phosphor IPS (Dell U2413)\n" +
            "    h                  i1D3: LCD PFS Phosphor IPS (LED display backlight using potassium fluorosilicate (PFS) phosphors)\n" +
            "    1                  i1D3: LCD PFS Phosphor IPS (Panasonic VVX17P051J00 in Lenovo P70)\n" +
            "    2                  i1D3: LCD RG Phosphor (Addition of Dell Ultrasharp U2413 to the RG-Phosphor Family EDR (created originall\n" +
            "    b                  i1D3: LCD RGB LED IPS (RGBLED HP SOYO)\n" +
            "    e                  i1D3: LCD White LED IPS (WLED AC LG Samsung)\n" +
            "    o                  i1D3: LED OLED (EDR for OLED created with primaries R, G, B, White)\n" +
            "    5                  i1D3: LED OLED (Sony PVM_2541 & Samsung Galaxy S7 & LEN4140)\n" +
            "    6                  i1D3: LED WOLED (LG OLED 6-Series)\n" +
            "    m                  i1D3: Plasma (EDR for Plasma created with primaries R, G, B and White)\n" +
            "    p                  i1D3: Projector (Marantz HP Panasonic Projectors Hybrid EDR)\n" +
            "    U                  i1D3: Unknown ()\n" +
            "    l|c                Other: l = LCD, c = CRT\n" +
            " -I illum             Set simulated instrument illumination using FWA (def -i illum):\n";

        // spotread -? on the reporter's machine at app start: only a serial port was
        // enumerated, so the usage carries no instrument table at all.
        internal const string NoInstrumentUsage =
            " -c listno            Set instrument port from the following list (default 1)\n" +
            "    1 = 'COM6'\n" +
            " -rw                  Use reflection white point relative chromatically adjusted mode\n" +
            " -y l|c                Other: l = LCD, c = CRT\n" +
            " -I illum             Set simulated instrument illumination using FWA (def -i illum):\n";

        [Fact]
        public void Parse_CleanI1D3_ListsBaseRowsAndOemCcss()
        {
            var table = SpotreadDisplayTypeTable.Parse(CleanI1D3Usage);

            Assert.Equal(4, table.Count);
            Assert.Equal(new[] { "n", "l" }, table[0].Selectors);
            Assert.Equal("i1D3", table[0].Instrument);
            Assert.Equal("Non-Refresh display [Default,CB1]", table[0].Description);
            Assert.True(table[0].IsDefault);
            Assert.True(table[0].IsNonRefreshBase);
            Assert.False(table[0].IsRefreshBase);

            Assert.Equal(new[] { "r", "c" }, table[1].Selectors);
            Assert.True(table[1].IsRefreshBase);
            Assert.False(table[1].IsNonRefreshBase);

            Assert.Equal(new[] { "1" }, table[2].Selectors);
            Assert.False(table[2].IsBaseOrGeneric);

            Assert.Equal(new[] { "l", "c" }, table[3].Selectors);
            Assert.True(table[3].IsGeneric);
        }

        [Fact]
        public void Parse_StopsAtNextOption()
        {
            // The "-I illum" continuation line ("M0, M1, ...") is indented like a table row
            // and must not be read as one.
            var table = SpotreadDisplayTypeTable.Parse(CleanI1D3Usage);
            Assert.DoesNotContain(table, e => e.Description.Contains("M0"));
        }

        [Fact]
        public void Parse_EdrRich_KeepsTruncatedDescriptionsAndCaseSensitiveSelectors()
        {
            var table = SpotreadDisplayTypeTable.Parse(EdrRichI1D3Usage);

            Assert.Equal(18, table.Count);
            Assert.Contains(table, e => e.HasSelector("L") && e.Description.StartsWith("LCD CCFL Wide Gamut"));
            Assert.Contains(table, e => e.HasSelector("l") && e.Description.StartsWith("LCD CCFL IPS"));
            Assert.Contains(table, e => e.HasSelector("2") && e.Description.EndsWith("created originall"));
        }

        [Fact]
        public void Parse_NoInstrument_YieldsOnlyGenericRow()
        {
            var table = SpotreadDisplayTypeTable.Parse(NoInstrumentUsage);

            var row = Assert.Single(table);
            Assert.True(row.IsGeneric);
            Assert.Equal(new[] { "l", "c" }, row.Selectors);
        }

        [Fact]
        public void Parse_LogPrefixedLines_ParseLikeRawOutput()
        {
            string logged =
                "[2026-09-15 09:59:06.930] stderr>  -y n|l                i1D3: Non-Refresh display [Default,CB1]\n" +
                "[2026-09-15 09:59:06.931] stderr>     r|c                i1D3: Refresh display [CB2]\n" +
                "[2026-09-15 09:59:06.931] stderr>  -I illum             Set simulated instrument illumination\n";

            var table = SpotreadDisplayTypeTable.Parse(logged);

            Assert.Equal(2, table.Count);
            Assert.Equal(new[] { "r", "c" }, table[1].Selectors);
        }

        [Fact]
        public void Parse_TwoCharacterSelector_PreferredIsSingleCharacter()
        {
            var table = SpotreadDisplayTypeTable.Parse(
                " -y _a|b               K10: Two-character selector row\n");

            var row = Assert.Single(table);
            Assert.Equal(new[] { "_a", "b" }, row.Selectors);
            Assert.Equal("b", row.PreferredSelector);
        }

        [Fact]
        public void Parse_EmptyOrNull_ReturnsEmpty()
        {
            Assert.Empty(SpotreadDisplayTypeTable.Parse(null));
            Assert.Empty(SpotreadDisplayTypeTable.Parse(""));
            Assert.Empty(SpotreadDisplayTypeTable.Parse("Measure spot values, Version 3.5.0\n"));
        }

        [Theory]
        [InlineData(DisplayType.LcdLed, "n")]
        [InlineData(DisplayType.Oled, "n")]
        [InlineData(DisplayType.LcdWideGamut, "n")]
        [InlineData(DisplayType.LcdCcfl, "1")]
        [InlineData(DisplayType.Crt, "r")]
        [InlineData(DisplayType.Plasma, "r")]
        [InlineData(DisplayType.Projector, "r")]
        public void Resolve_CleanI1D3_UsesBaseCalibrationForTheRefreshClass(DisplayType type, string expected)
        {
            // No OLED/WLED/RGB-LED rows exist on a clean install, so the reporter's OLED must
            // land on the Non-Refresh base (Argyll's disptechs.c marks OLED as non-refresh),
            // never on "o". The one CCFL row (built-in OEM CCSS) still serves LcdCcfl.
            var table = SpotreadDisplayTypeTable.Parse(CleanI1D3Usage);

            string selector = SpotreadDisplayTypeTable.Resolve(type, table, out string reason);

            Assert.Equal(expected, selector);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Theory]
        [InlineData(DisplayType.LcdLed, "e")]
        [InlineData(DisplayType.Oled, "o")]
        [InlineData(DisplayType.LcdWideGamut, "b")]
        [InlineData(DisplayType.LcdCcfl, "l")]
        [InlineData(DisplayType.Crt, "c")]
        [InlineData(DisplayType.Plasma, "m")]
        [InlineData(DisplayType.Projector, "p")]
        public void Resolve_EdrRich_UsesArgyllTechnologyLetters(DisplayType type, string expected)
        {
            // The letters the old fixed map emitted are exactly right when the EDR rows exist.
            var table = SpotreadDisplayTypeTable.Parse(EdrRichI1D3Usage);

            Assert.Equal(expected, SpotreadDisplayTypeTable.Resolve(type, table, out _));
        }

        [Fact]
        public void Resolve_EdrRich_PrefersArgyllLetterOverOtherRowsOfSameTechnology()
        {
            // Three OLED rows exist ("o", "5", "6"); "o" is the generic X-Rite OLED EDR.
            var table = SpotreadDisplayTypeTable.Parse(EdrRichI1D3Usage);

            Assert.Equal("o", SpotreadDisplayTypeTable.Resolve(DisplayType.Oled, table, out string reason));
            Assert.Contains("EDR for OLED", reason);
        }

        [Fact]
        public void Resolve_WideGamut_WithoutRgbLedRow_NeverPicksCcflWideGamutRow()
        {
            // "L LCD CCFL Wide Gamut IPS" is a CCFL correction; an LED wide-gamut panel must
            // land on one of the LED phosphor rows instead.
            string usage = EdrRichI1D3Usage.Replace(
                "    b                  i1D3: LCD RGB LED IPS (RGBLED HP SOYO)\n", "");
            var table = SpotreadDisplayTypeTable.Parse(usage);

            string selector = SpotreadDisplayTypeTable.Resolve(DisplayType.LcdWideGamut, table, out string reason);

            Assert.Equal("i", selector);
            Assert.DoesNotContain("CCFL", reason);
        }

        [Fact]
        public void Resolve_DefaultRowNamingATechnology_IsACorrectionNotABase()
        {
            // Drivers other than the i1D3 mark a technology row as the default.
            var table = SpotreadDisplayTypeTable.Parse(
                " -y l                  X: LCD, CCFL Backlight [Default,CB1]\n" +
                "    2                  X: Wide Gamut LCD, CCFL Backlight\n" +
                "    c                  X: CRT [CB2]\n");

            Assert.False(table[0].IsNonRefreshBase);
            Assert.Equal("l", SpotreadDisplayTypeTable.Resolve(DisplayType.LcdCcfl, table, out _));
            Assert.Equal("l", SpotreadDisplayTypeTable.Resolve(DisplayType.Oled, table, out _));
            Assert.Equal("c", SpotreadDisplayTypeTable.Resolve(DisplayType.Crt, table, out _));
        }

        [Fact]
        public void HasInstrumentRows_FalseForGenericOnlyOrEmpty()
        {
            Assert.False(SpotreadDisplayTypeTable.HasInstrumentRows(null));
            Assert.False(SpotreadDisplayTypeTable.HasInstrumentRows(SpotreadDisplayTypeTable.Parse(NoInstrumentUsage)));
            Assert.True(SpotreadDisplayTypeTable.HasInstrumentRows(SpotreadDisplayTypeTable.Parse(CleanI1D3Usage)));
        }

        [Fact]
        public void Resolve_KeywordMatchWithoutPreferredLetter_UsesThatRow()
        {
            var table = SpotreadDisplayTypeTable.Parse(
                " -y n|l                i1D3: Non-Refresh display [Default,CB1]\n" +
                "    r|c                i1D3: Refresh display [CB2]\n" +
                "    6                  i1D3: LED WOLED (LG OLED 6-Series)\n");

            Assert.Equal("6", SpotreadDisplayTypeTable.Resolve(DisplayType.Oled, table, out _));
        }

        [Theory]
        [InlineData(DisplayType.Oled, "l")]
        [InlineData(DisplayType.LcdLed, "l")]
        [InlineData(DisplayType.Crt, "c")]
        public void Resolve_NoInstrumentTable_UsesGenericRow(DisplayType type, string expected)
        {
            var table = SpotreadDisplayTypeTable.Parse(NoInstrumentUsage);

            Assert.Equal(expected, SpotreadDisplayTypeTable.Resolve(type, table, out _));
        }

        [Theory]
        [InlineData(DisplayType.Oled, "l")]
        [InlineData(DisplayType.LcdWideGamut, "l")]
        [InlineData(DisplayType.Plasma, "c")]
        public void Resolve_UnknownTable_UsesGenericSelector(DisplayType type, string expected)
        {
            Assert.Equal(expected, SpotreadDisplayTypeTable.Resolve(type, null, out string reason));
            Assert.Contains("unknown", reason);
            Assert.Equal(expected, SpotreadDisplayTypeTable.Resolve(type, new List<SpotreadDisplayTypeEntry>(), out _));
            Assert.Equal(expected, SpotreadDisplayTypeTable.GenericSelector(type));
        }

        [Fact]
        public void Resolve_TableWithoutBaseOrGenericRows_FallsBackToGenericSelector()
        {
            var table = SpotreadDisplayTypeTable.Parse(
                " -y 1                  i1D3: LCD CCFL PVA (Resolve JVC CPF)\n");

            Assert.Equal("l", SpotreadDisplayTypeTable.Resolve(DisplayType.Oled, table, out _));
        }

        [Fact]
        public void Describe_ListsSelectorsAndDescriptions()
        {
            var table = SpotreadDisplayTypeTable.Parse(CleanI1D3Usage);

            string text = SpotreadDisplayTypeTable.Describe(table);

            Assert.StartsWith("n|l Non-Refresh display [Default,CB1]; r|c Refresh display [CB2]", text);
            Assert.Equal("(none)", SpotreadDisplayTypeTable.Describe(null));
        }
    }
}
