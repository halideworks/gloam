using System;
using System.Globalization;
using System.IO;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Structural validation for ArgyllCMS/DisplayCAL colorimeter-correction files
    /// (.ccss spectral samples and .ccmx correction matrices) before they are written to
    /// disk and handed to spotread via <c>-X</c>.
    ///
    /// These files come from a third-party, community-contributed database
    /// (colorimetercorrections.displaycal.net) and are passed verbatim to an external
    /// process. This validator does NOT attempt to parse the full CGATS grammar — it only
    /// confirms the structural skeleton every legitimate correction file has, so that
    /// truncated, malformed, or hostile content is rejected before it reaches spotread's
    /// parser. Real Argyll/DisplayCAL .ccss and .ccmx files always carry these tokens.
    ///
    /// The checked shape (verified against Argyll V3.3.0's bundled .ccmx/.ccss samples):
    ///   <code>
    ///   CCMX            (or CCSS)   — the file-type keyword on the first non-empty line
    ///   ...
    ///   NUMBER_OF_FIELDS &lt;n&gt;
    ///   BEGIN_DATA_FORMAT
    ///   &lt;fields...&gt;
    ///   END_DATA_FORMAT
    ///   NUMBER_OF_SETS &lt;n&gt;          — n is a positive integer
    ///   BEGIN_DATA
    ///   &lt;rows...&gt;
    ///   END_DATA
    ///   </code>
    /// </summary>
    public static class CgatsValidator
    {
        /// <summary>Result of validating a correction-file body.</summary>
        public readonly struct Result
        {
            public bool IsValid { get; init; }
            public string? Error { get; init; }

            public static Result Ok() => new() { IsValid = true };
            public static Result Fail(string error) => new() { IsValid = false, Error = error };
        }

        /// <summary>
        /// Validates the structural integrity of a CGATS correction body. Returns a result
        /// with an explanatory error on failure; never throws on malformed input.
        /// </summary>
        /// <param name="content">The raw text of the .ccss/.ccmx file.</param>
        /// <param name="expectedType">Optional: "ccss" or "ccmx". When supplied, the leading
        /// keyword is checked against the expected type.</param>
        public static Result Validate(string? content, string? expectedType = null)
        {
            if (string.IsNullOrWhiteSpace(content))
                return Result.Fail("Correction file is empty.");

            // Reject implausibly large bodies up front. A real .ccss is ~5-40 KB; a .ccmx is
            // under 1 KB. A multi-megabyte payload is not a correction file.
            if (content.Length > 2_000_000)
                return Result.Fail("Correction file is implausibly large.");

            // NUL bytes / control chars that don't belong in a text CGATS file — a strong
            // signal of binary garbage or an embedded payload.
            foreach (char c in content.AsSpan())
            {
                if (c == '\0')
                    return Result.Fail("Correction file contains NUL bytes (not valid CGATS text).");
            }

            // First non-empty, non-comment line must be the CGATS file-type keyword. Argyll
            // correction files begin with "CCMX" or "CCSS" (the generic "CGATS" keyword is
            // also accepted for robustness). Everything is matched case-insensitively, as
            // Argyll tolerates case variation.
            ReadOnlySpan<char> firstLine = GetFirstNonEmptyLine(content.AsSpan());
            if (firstLine.IsEmpty)
                return Result.Fail("Correction file has no recognizable header keyword.");

            bool isCcmx = StartsWithToken(firstLine, "CCMX");
            bool isCcss = StartsWithToken(firstLine, "CCSS");
            bool isCgats = StartsWithToken(firstLine, "CGATS");

            if (!isCcmx && !isCcss && !isCgats)
                return Result.Fail("Correction file does not start with a recognized CGATS keyword (CCMX/CCSS/CGATS).");

            if (!string.IsNullOrEmpty(expectedType))
            {
                string t = expectedType.ToLowerInvariant();
                if (t == "ccmx" && !isCcmx && !isCgats)
                    return Result.Fail($"Correction file is not a .ccmx (header is not CCMX).");
                if (t == "ccss" && !isCcss && !isCgats)
                    return Result.Fail($"Correction file is not a .ccss (header is not CCSS).");
            }

            // Required structural block tokens, in order.
            if (!TryFindToken(content, "NUMBER_OF_FIELDS"))
                return Result.Fail("Missing NUMBER_OF_FIELDS declaration.");
            if (!HasBalancedBlock(content, "BEGIN_DATA_FORMAT", "END_DATA_FORMAT"))
                return Result.Fail("Missing or unbalanced BEGIN_DATA_FORMAT/END_DATA_FORMAT block.");

            if (!TryFindTokenLine(content, "NUMBER_OF_SETS", out string? setsLine))
                return Result.Fail("Missing NUMBER_OF_SETS declaration.");

            if (!TryParseTrailingInt(setsLine.AsSpan(), "NUMBER_OF_SETS", out int sets) || sets <= 0)
                return Result.Fail($"NUMBER_OF_SETS must be a positive integer (got '{setsLine?.Trim()}').");

            if (!HasBalancedBlock(content, "BEGIN_DATA", "END_DATA"))
                return Result.Fail("Missing or unbalanced BEGIN_DATA/END_DATA block.");

            if (!TryValidateDataPayload(content, sets, out string? payloadError))
                return Result.Fail(payloadError);

            return Result.Ok();
        }

        /// <summary>
        /// Validates a correction file on disk by path. Returns true if valid; otherwise
        /// logs the reason and returns false. Never throws.
        /// </summary>
        public static bool IsValidFile(string path, string? expectedType = null)
        {
            try
            {
                if (!File.Exists(path)) return false;
                string content = File.ReadAllText(path);
                var result = Validate(content, expectedType);
                if (!result.IsValid)
                    Log.Info($"CgatsValidator: rejected '{path}': {result.Error}");
                return result.IsValid;
            }
            catch (Exception ex)
            {
                Log.Info($"CgatsValidator: could not read '{path}': {ex.Message}");
                return false;
            }
        }

        // --- helpers -------------------------------------------------------------

        private static ReadOnlySpan<char> GetFirstNonEmptyLine(ReadOnlySpan<char> content)
        {
            foreach (var line in content.EnumerateLines())
            {
                var trimmed = line.Trim();
                if (!trimmed.IsEmpty) return trimmed;
            }
            return ReadOnlySpan<char>.Empty;
        }

        /// <summary>True if <paramref name="line"/> begins with <paramref name="token"/> (case-insensitive), followed by whitespace or EOL.</summary>
        private static bool StartsWithToken(ReadOnlySpan<char> line, string token)
        {
            if (line.Length < token.Length) return false;
            if (!line.Slice(0, token.Length).Equals(token, StringComparison.OrdinalIgnoreCase)) return false;
            if (line.Length == token.Length) return true;
            char next = line[token.Length];
            return char.IsWhiteSpace(next);
        }

        private static bool TryFindToken(string content, string token) => ContainsTokenLine(content, token);

        /// <summary>Finds the first line whose first token matches <paramref name="token"/> and returns it.</summary>
        private static bool TryFindTokenLine(string content, string token, out string? line)
        {
            foreach (var raw in content.Split('\n'))
            {
                var t = raw.AsSpan().Trim();
                if (StartsWithToken(t, token))
                {
                    line = raw.TrimEnd('\r', '\n');
                    return true;
                }
            }
            line = null;
            return false;
        }

        /// <summary>
        /// True if both the begin and end tokens appear as standalone lines, in that order.
        /// A malformed file with the end before the begin is treated as invalid.
        /// </summary>
        private static bool HasBalancedBlock(string content, string begin, string end)
        {
            if (!FindTokenLineIndex(content, begin, out int beginIdx)) return false;
            if (!FindTokenLineIndex(content, end, out int endIdx)) return false;
            return endIdx > beginIdx;
        }

        /// <summary>True if any line's first token matches <paramref name="token"/>.</summary>
        private static bool ContainsTokenLine(string content, string token)
            => FindTokenLineIndex(content, token, out _);

        private static bool FindTokenLineIndex(string content, string token, out int lineIndex)
        {
            lineIndex = 0;
            foreach (var raw in content.Split('\n'))
            {
                if (StartsWithToken(raw.AsSpan().Trim(), token))
                    return true;
                lineIndex++;
            }
            return false;
        }

        /// <summary>Parses the integer that follows a "KEYWORD n" directive line.</summary>
        private static bool TryParseTrailingInt(ReadOnlySpan<char> line, string keyword, out int value)
        {
            value = 0;
            int kwEnd = -1;
            // Locate the keyword, then read the next whitespace-delimited token.
            for (int i = 0; i <= line.Length - keyword.Length; i++)
            {
                if (line.Slice(i, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    // Ensure it's token-bounded on the left (start of line or preceded by whitespace).
                    if (i == 0 || char.IsWhiteSpace(line[i - 1]))
                    {
                        kwEnd = i + keyword.Length;
                        break;
                    }
                }
            }
            if (kwEnd < 0) return false;

            var rest = line.Slice(kwEnd).Trim();
            if (rest.IsEmpty) return false;

            // Take the first whitespace-delimited token after the keyword.
            int space = rest.IndexOfAny(new[] { ' ', '\t' });
            var num = space < 0 ? rest : rest.Slice(0, space);

            return int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryValidateDataPayload(string content, int expectedSets, out string error)
        {
            error = "";

            var fields = ExtractBlockLines(content, "BEGIN_DATA_FORMAT", "END_DATA_FORMAT");
            if (fields.Count == 0)
            {
                error = "BEGIN_DATA_FORMAT block contains no fields.";
                return false;
            }

            string[] fieldNames = fields[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fieldNames.Length == 0)
            {
                error = "BEGIN_DATA_FORMAT block contains no field names.";
                return false;
            }

            var rows = ExtractBlockLines(content, "BEGIN_DATA", "END_DATA");
            if (rows.Count != expectedSets)
            {
                error = $"NUMBER_OF_SETS declares {expectedSets} rows, but BEGIN_DATA contains {rows.Count}.";
                return false;
            }

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                string[] values = rows[rowIndex].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length < fieldNames.Length)
                {
                    error = $"Data row {rowIndex + 1} has {values.Length} value(s), expected at least {fieldNames.Length}.";
                    return false;
                }

                for (int fieldIndex = 0; fieldIndex < fieldNames.Length; fieldIndex++)
                {
                    string field = fieldNames[fieldIndex];
                    if (!IsColorNumericField(field))
                        continue;

                    if (!double.TryParse(values[fieldIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                        !double.IsFinite(value))
                    {
                        error = $"Data row {rowIndex + 1} field {field} is not a finite number.";
                        return false;
                    }

                    // CCSS spectral samples and CCMX matrix coefficients are both numeric
                    // calibration data. Real Argyll/DisplayCAL CCSS files may contain small
                    // negative samples after instrument/reference correction; spotread accepts
                    // those, so validation only rejects non-finite values here.
                }
            }

            return true;
        }

        private static System.Collections.Generic.List<string> ExtractBlockLines(
            string content,
            string begin,
            string end)
        {
            var lines = new System.Collections.Generic.List<string>();
            bool inBlock = false;
            foreach (var raw in content.Split('\n'))
            {
                var trimmed = raw.AsSpan().Trim();
                if (StartsWithToken(trimmed, begin))
                {
                    inBlock = true;
                    continue;
                }

                if (StartsWithToken(trimmed, end))
                    break;

                if (!inBlock || trimmed.IsEmpty || trimmed[0] == '#')
                    continue;

                lines.Add(trimmed.ToString());
            }

            return lines;
        }

        private static bool IsColorNumericField(string field) =>
            field.StartsWith("XYZ_", StringComparison.OrdinalIgnoreCase) ||
            field.StartsWith("SPEC_", StringComparison.OrdinalIgnoreCase);
    }
}
