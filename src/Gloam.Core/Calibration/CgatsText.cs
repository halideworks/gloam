using System;
using System.Globalization;
using System.Text;

namespace Gloam.Core.Calibration
{
    /// <summary>
    /// The CGATS text primitives shared by <see cref="CcmxWriter"/> and <see cref="CcssWriter"/>.
    /// Both emit files ArgyllCMS has to read back, so the quoting and the ctime format are part
    /// of the wire format, not formatting taste — kept in one place because two copies of an
    /// escaping rule is one copy too many.
    /// </summary>
    internal static class CgatsText
    {
        public static void AppendKeyword(StringBuilder sb, string keyword, string value)
        {
            sb.Append($"KEYWORD \"{keyword}\"\n");
            sb.Append($"{keyword} \"{value}\"\n");
        }

        public static string CleanText(string? text, string fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            // CGATS string values are double-quoted; strip quotes and line breaks so the
            // emitted file stays a valid single-line keyword.
            return text.Replace("\"", "'").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        // C ctime() format: "Www Mmm dd HH:MM:SS yyyy" with the day space-padded to two
        // columns (e.g. "Fri Jul  3 12:00:00 2026"), matching Argyll's CGATS writer.
        public static string FormatCtime(DateTime stamp, CultureInfo inv) =>
            stamp.ToString("ddd MMM", inv) + " " +
            stamp.Day.ToString(inv).PadLeft(2) + " " +
            stamp.ToString("HH:mm:ss yyyy", inv);
    }
}
