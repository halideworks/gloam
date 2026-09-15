using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Gloam.Core.Calibration
{
    /// <summary>
    /// One row of the <c>-y</c> table that spotread prints in its usage output for the
    /// connected instrument, e.g. <c>n|l  i1D3: Non-Refresh display [Default,CB1]</c>.
    /// </summary>
    public sealed class SpotreadDisplayTypeEntry
    {
        public SpotreadDisplayTypeEntry(IReadOnlyList<string> selectors, string instrument, string description)
        {
            Selectors = selectors;
            Instrument = instrument;
            Description = description;
        }

        /// <summary>Selector tokens accepted by <c>-y</c> for this row ("n", "l", "1", "_X").</summary>
        public IReadOnlyList<string> Selectors { get; }

        /// <summary>Instrument short name printed before the colon ("i1D3"), or "Other" for the generic row.</summary>
        public string Instrument { get; }

        /// <summary>Calibration description, including any trailing "[Default,CB1]" marker.</summary>
        public string Description { get; }

        /// <summary>True for the instrument's default calibration ("[Default" marker).</summary>
        public bool IsDefault => Description.Contains("[Default", StringComparison.OrdinalIgnoreCase);

        /// <summary>The description without Argyll's trailing "[Default,CB1]" marker, for UI text.</summary>
        public string DisplayDescription
        {
            get
            {
                int bracket = Description.LastIndexOf(" [", StringComparison.Ordinal);
                return bracket > 0 && Description.EndsWith("]", StringComparison.Ordinal)
                    ? Description.Substring(0, bracket)
                    : Description;
            }
        }

        // Words that mark a row as technology-specific. A default row that names a technology
        // (some drivers' "LCD, CCFL Backlight [Default,CB1]") is a correction, not a base.
        private static readonly string[] TechnologyWords =
            { "lcd", "led", "oled", "ccfl", "crt", "plasma", "projector", "dlp", "phosphor" };

        private bool NamesTechnology
        {
            get
            {
                foreach (string word in TechnologyWords)
                {
                    if (Description.Contains(word, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
        }

        /// <summary>True for the instrument's generic non-refresh base calibration.</summary>
        public bool IsNonRefreshBase =>
            Description.Contains("non-refresh", StringComparison.OrdinalIgnoreCase) ||
            (IsDefault && !NamesTechnology && !IsRefreshBase);

        /// <summary>True for the instrument's generic refresh (CRT-style) base calibration.</summary>
        public bool IsRefreshBase =>
            !Description.Contains("non-refresh", StringComparison.OrdinalIgnoreCase) &&
            Description.Contains("refresh display", StringComparison.OrdinalIgnoreCase);

        /// <summary>True for the generic "Other: l = LCD, c = CRT" row spotread prints for serial ports.</summary>
        public bool IsGeneric => string.Equals(Instrument, "Other", StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the row is a base or generic calibration rather than a technology-specific one.</summary>
        public bool IsBaseOrGeneric => IsGeneric || IsNonRefreshBase || IsRefreshBase;

        public bool HasSelector(string selector)
        {
            foreach (string s in Selectors)
            {
                if (string.Equals(s, selector, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>The selector to pass on the command line: the first single-character one, else the first.</summary>
        public string PreferredSelector
        {
            get
            {
                foreach (string s in Selectors)
                {
                    if (s.Length == 1) return s;
                }
                return Selectors[0];
            }
        }

        public override string ToString() => $"{string.Join("|", Selectors)} {Instrument}: {Description}";
    }

    /// <summary>
    /// The outcome of resolving a <see cref="DisplayType"/> against an instrument's table.
    /// </summary>
    public sealed class SpotreadDisplayTypeChoice
    {
        public SpotreadDisplayTypeChoice(string selector, string reason, SpotreadDisplayTypeEntry? entry)
        {
            Selector = selector;
            Reason = reason;
            Entry = entry;
        }

        /// <summary>The value passed to spotread's <c>-y</c>.</summary>
        public string Selector { get; }

        /// <summary>Why it was chosen, for logs and the setup screen.</summary>
        public string Reason { get; }

        /// <summary>The table row the selector came from; null when the table was unknown.</summary>
        public SpotreadDisplayTypeEntry? Entry { get; }

        /// <summary>True when a technology-specific correction row was found on the instrument.</summary>
        public bool IsTechnologyMatch => Entry != null && !Entry.IsBaseOrGeneric;
    }

    /// <summary>
    /// Parses spotread's per-instrument <c>-y</c> table and resolves a <see cref="DisplayType"/>
    /// to a selector that instrument actually accepts.
    /// </summary>
    /// <remarks>
    /// The selector letters are not a stable ArgyllCMS contract. Argyll builds the table per
    /// instrument at run time: a driver's static base calibrations (the i1D3 has only
    /// Non-Refresh <c>n|l</c> and Refresh <c>r|c</c>) plus one row per installed CCSS/CCMX
    /// file. The technology letters (<c>e</c> white LED, <c>o</c> OLED, <c>b</c> RGB LED,
    /// <c>m</c> plasma, <c>p</c> projector) only appear when an X-Rite EDR-derived
    /// correction of that technology has been imported into Argyll's data directory, which a
    /// fresh Gloam install never does. Passing one of those letters to a clean install makes
    /// spotread exit with "Failed to locate display type matching 'o'" (issue #7).
    ///
    /// When a <c>-X file.ccss</c> correction is also passed, spotread replaces both the
    /// spectral calibration and the refresh mode with the ones implied by the CCSS
    /// (<c>i1d3_col_cal_spec_set</c> in Argyll's i1d3.c), so <c>-y</c> then only has to be
    /// a selector the instrument accepts.
    /// </remarks>
    public static class SpotreadDisplayTypeTable
    {
        // First row of the block:      " -y n|l                i1D3: Non-Refresh display [Default,CB1]"
        // Continuation rows:           "    r|c                i1D3: Refresh display [CB2]"
        // Generic row (serial ports):  "    l|c                 Other: l = LCD, c = CRT"
        // Selector tokens are one character, or '_' plus one character for the two-character form.
        private const string SelectorGroup = @"((?:_?[A-Za-z0-9])(?:\|_?[A-Za-z0-9])*)";
        private static readonly Regex FirstRowPattern = new(
            @"^\s*-y\s+" + SelectorGroup + @"\s{2,}(\S.*)$", RegexOptions.Compiled);
        private static readonly Regex ContinuationRowPattern = new(
            @"^\s{2,}" + SelectorGroup + @"\s{2,}(\S.*)$", RegexOptions.Compiled);

        /// <summary>
        /// Extracts the <c>-y</c> table from spotread usage text. Returns an empty list when
        /// the text contains no table (no instrument was enumerated, or the output is not a
        /// usage dump). Lines may carry a "stderr> " style prefix from a log.
        /// </summary>
        public static IReadOnlyList<SpotreadDisplayTypeEntry> Parse(string? usageOutput)
        {
            var entries = new List<SpotreadDisplayTypeEntry>();
            if (string.IsNullOrEmpty(usageOutput))
                return entries;

            bool inBlock = false;
            foreach (string rawLine in usageOutput.Split('\n'))
            {
                string line = StripLogPrefix(rawLine.TrimEnd('\r'));

                Match m;
                if (!inBlock)
                {
                    m = FirstRowPattern.Match(line);
                    if (!m.Success) continue;
                    inBlock = true;
                }
                else
                {
                    m = ContinuationRowPattern.Match(line);
                    if (!m.Success)
                    {
                        // The block ends at the next option (" -I illum ...") or any other line.
                        inBlock = false;
                        continue;
                    }
                }

                string[] selectors = m.Groups[1].Value.Split('|');
                string rest = m.Groups[2].Value.Trim();
                string instrument;
                string description;
                int colon = rest.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0)
                {
                    instrument = rest.Substring(0, colon).Trim();
                    description = rest.Substring(colon + 2).Trim();
                }
                else
                {
                    instrument = "";
                    description = rest;
                }
                entries.Add(new SpotreadDisplayTypeEntry(selectors, instrument, description));
            }
            return entries;
        }

        private static string StripLogPrefix(string line)
        {
            // Colorimeter log lines look like "[2026-09-15 09:59:06.930] stderr>  -y n|l ...".
            int idx = line.IndexOf("stderr> ", StringComparison.Ordinal);
            if (idx < 0) idx = line.IndexOf("stdout> ", StringComparison.Ordinal);
            if (idx < 0) idx = line.IndexOf("[e] ", StringComparison.Ordinal);
            if (idx < 0) idx = line.IndexOf("[o] ", StringComparison.Ordinal);
            if (idx < 0) return line;
            int prefixLen = line[idx] == '[' ? 4 : 8;
            return line.Substring(idx + prefixLen);
        }

        private readonly struct TypeRule
        {
            public TypeRule(DisplayType type, string[] preferredSelectors, string[] keywords)
            {
                PreferredSelectors = preferredSelectors;
                Keywords = keywords;
                Refresh = type.IsRefreshTechnology();
            }
            public string[] PreferredSelectors { get; }
            public string[] Keywords { get; }
            public bool Refresh { get; }
        }

        // Preferred selectors are the letters Argyll's disptechs.c assigns to the matching
        // technology; keywords match the description of any installed correction of that
        // technology.
        private static TypeRule RuleFor(DisplayType type) => type switch
        {
            DisplayType.LcdLed => new TypeRule(type, new[] { "e" }, new[] { "white led", "wled" }),
            DisplayType.Oled => new TypeRule(type, new[] { "o" }, new[] { "oled" }),
            // No "wide gamut" keyword: it would match the CCFL wide-gamut EDR row.
            DisplayType.LcdWideGamut => new TypeRule(type, new[] { "b" },
                new[] { "rgb led", "rg phosphor", "pfs phosphor", "gb-r phosphor" }),
            DisplayType.LcdCcfl => new TypeRule(type, new[] { "l" }, new[] { "ccfl" }),
            DisplayType.Crt => new TypeRule(type, new[] { "c" }, new[] { "crt" }),
            DisplayType.Plasma => new TypeRule(type, new[] { "m" }, new[] { "plasma" }),
            DisplayType.Projector => new TypeRule(type, new[] { "p" }, new[] { "projector", "dlp" }),
            _ => new TypeRule(type, Array.Empty<string>(), Array.Empty<string>())
        };

        /// <summary>
        /// The selector to pass as <c>-y</c> for <paramref name="type"/> on an instrument
        /// whose table is unknown. <c>l</c> (LCD) and <c>c</c> (CRT) are in every Argyll
        /// colorimeter driver's static list, so they are the safe generic choice.
        /// </summary>
        public static string GenericSelector(DisplayType type) => RuleFor(type).Refresh ? "c" : "l";

        /// <summary>
        /// Picks the <c>-y</c> selector for <paramref name="type"/> from the instrument's
        /// table: a technology-matched correction if one is installed, else the instrument's
        /// base calibration for the refresh class, else the generic selector.
        /// </summary>
        public static string Resolve(DisplayType type, IReadOnlyList<SpotreadDisplayTypeEntry>? table, out string reason)
        {
            var choice = Resolve(type, table);
            reason = choice.Reason;
            return choice.Selector;
        }

        /// <inheritdoc cref="Resolve(DisplayType, IReadOnlyList{SpotreadDisplayTypeEntry}?, out string)"/>
        public static SpotreadDisplayTypeChoice Resolve(DisplayType type, IReadOnlyList<SpotreadDisplayTypeEntry>? table)
        {
            var rule = RuleFor(type);
            string generic = GenericSelector(type);
            string genericName = rule.Refresh ? "CRT" : "LCD";

            if (table == null || table.Count == 0)
            {
                return new SpotreadDisplayTypeChoice(generic,
                    $"the instrument's display-type table is unknown; using the generic {genericName} selector", null);
            }

            // 1. Technology-specific rows (installed EDR/CCSS corrections) whose description
            //    names this technology. Prefer the row carrying Argyll's own letter for it.
            SpotreadDisplayTypeEntry? keywordMatch = null;
            foreach (var entry in table)
            {
                if (entry.IsBaseOrGeneric || !MatchesKeyword(entry.Description, rule.Keywords))
                    continue;
                keywordMatch ??= entry;
                foreach (string sel in rule.PreferredSelectors)
                {
                    if (entry.HasSelector(sel))
                        return new SpotreadDisplayTypeChoice(sel, $"matched the installed correction '{entry.Description}'", entry);
                }
            }
            if (keywordMatch != null)
            {
                return new SpotreadDisplayTypeChoice(keywordMatch.PreferredSelector,
                    $"matched the installed correction '{keywordMatch.Description}'", keywordMatch);
            }

            // 2. The instrument's base calibration for this refresh class.
            foreach (var entry in table)
            {
                if (rule.Refresh ? entry.IsRefreshBase : entry.IsNonRefreshBase)
                {
                    return new SpotreadDisplayTypeChoice(entry.PreferredSelector,
                        $"no {type}-specific correction is installed for this instrument; using its base calibration '{entry.Description}'",
                        entry);
                }
            }

            // 3. Any row that accepts the generic selector (including the "Other" row).
            foreach (var entry in table)
            {
                if (entry.HasSelector(generic))
                {
                    return new SpotreadDisplayTypeChoice(generic,
                        $"no {type}-specific correction is installed for this instrument; using '{entry.Description}'", entry);
                }
            }

            // 4. The table lists nothing usable at all (unexpected); the generic selector is
            //    still the most likely to be accepted.
            return new SpotreadDisplayTypeChoice(generic,
                $"the instrument's display-type table lists no base calibration; using the generic {genericName} selector", null);
        }

        // Argyll insttypes.c: inst_name(), printed inside the parentheses of the -c port
        // list, and inst_sname(), printed before every -y row. Matched longest name first so
        // "SpyderX2" is not read as "SpyderX".
        private static readonly (string LongName, string ShortName)[] InstrumentNames = Sorted(new[]
        {
            ("X-Rite DTP20", "DTP20"), ("X-Rite DTP22", "DTP22"), ("X-Rite DTP41", "DTP41"),
            ("X-Rite DTP51", "DTP51"), ("X-Rite DTP92", "DTP92"), ("X-Rite DTP94", "DTP94"),
            ("GretagMacbeth Spectrolino", "Spectrolino"), ("GretagMacbeth SpectroScanT", "SpectroScanT"),
            ("GretagMacbeth SpectroScan", "SpectroScan"), ("Spectrocam", "Spectrocam"),
            ("GretagMacbeth i1 Display 1", "i1D1"), ("GretagMacbeth i1 Display 2", "i1D2"),
            ("X-Rite i1 DisplayPro, ColorMunki Display", "i1D3"), ("GretagMacbeth i1 Monitor", "i1 Monitor"),
            ("GretagMacbeth i1 Pro", "i1Pro"), ("X-Rite i1 Pro 2", "i1Pro2"), ("X-Rite i1 Pro 3", "i1Pro3"),
            ("X-Rite ColorMunki", "ColorMunki"), ("Colorimtre HCFR", "HCFR"),
            ("ColorVision Spyder1", "Spyder1"), ("ColorVision Spyder2", "Spyder2"),
            ("Datacolor Spyder3", "Spyder3"), ("Datacolor Spyder4", "Spyder4"), ("Datacolor Spyder5", "Spyder5"),
            ("Datacolor SpyderX2", "SpyderX2"), ("Datacolor SpyderX", "SpyderX"), ("Datacolor Spyder 2024", "Spyder 2024"),
            ("GretagMacbeth Huey", "Huey"), ("ColorMunki Smile", "Smile"),
            ("JETI specbos 1201", "specbos 1201"), ("JETI specbos 2501", "specbos 2501"), ("JETI specbos", "specbos"),
            ("JETI spectraval", "spectraval"), ("Klein K-10", "K-10"), ("Image Engineering EX1", "EX1"),
            ("SwatchMate Cube", "Cube"), ("Hughski ColorHug2", "ColorHug2"), ("Hughski ColorHug", "ColorHug"),
        });

        private static (string LongName, string ShortName)[] Sorted((string LongName, string ShortName)[] names)
        {
            Array.Sort(names, (a, b) => b.LongName.Length.CompareTo(a.LongName.Length));
            return names;
        }

        /// <summary>
        /// The short instrument name spotread prints before each <c>-y</c> row ("i1D3") for a
        /// port-list descriptor such as "hid:/33 (X-Rite i1 DisplayPro, ColorMunki Display)".
        /// Null when the descriptor names no known instrument (serial ports, unknown models).
        /// </summary>
        public static string? ShortNameForDescriptor(string? descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor)) return null;
            foreach (var (longName, shortName) in InstrumentNames)
            {
                if (descriptor.Contains(longName, StringComparison.OrdinalIgnoreCase))
                    return shortName;
            }
            return null;
        }

        /// <summary>
        /// Restricts a table to the rows printed by the instrument named in
        /// <paramref name="descriptor"/> (plus the generic row). spotread prints one merged
        /// block for every enumerated instrument, so with two meters connected a row from the
        /// other meter would otherwise be selected. Returns the table unchanged when the
        /// instrument is unknown or none of its rows are present.
        /// </summary>
        public static IReadOnlyList<SpotreadDisplayTypeEntry> ForInstrument(
            IReadOnlyList<SpotreadDisplayTypeEntry> table, string? descriptor)
        {
            string? shortName = ShortNameForDescriptor(descriptor);
            if (shortName == null || table.Count == 0) return table;

            var scoped = new List<SpotreadDisplayTypeEntry>();
            bool anyInstrumentRow = false;
            foreach (var entry in table)
            {
                if (entry.IsGeneric)
                {
                    scoped.Add(entry);
                }
                else if (string.Equals(entry.Instrument, shortName, StringComparison.OrdinalIgnoreCase))
                {
                    scoped.Add(entry);
                    anyInstrumentRow = true;
                }
            }
            return anyInstrumentRow ? scoped : table;
        }

        private static bool MatchesKeyword(string description, string[] keywords)
        {
            foreach (string keyword in keywords)
            {
                if (description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>True when the table has at least one row printed by an instrument (not just the generic row).</summary>
        public static bool HasInstrumentRows(IReadOnlyList<SpotreadDisplayTypeEntry>? table)
        {
            if (table == null) return false;
            foreach (var entry in table)
            {
                if (!entry.IsGeneric) return true;
            }
            return false;
        }

        /// <summary>One-line summary for logs: "n|l Non-Refresh display [Default,CB1]; r|c Refresh display [CB2]; ...".</summary>
        public static string Describe(IReadOnlyList<SpotreadDisplayTypeEntry>? table)
        {
            if (table == null || table.Count == 0) return "(none)";
            var sb = new StringBuilder();
            foreach (var entry in table)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(string.Join("|", entry.Selectors)).Append(' ').Append(entry.Description);
            }
            return sb.ToString();
        }
    }
}
