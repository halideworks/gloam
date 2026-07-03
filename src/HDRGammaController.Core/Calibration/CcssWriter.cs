using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Writes CCSS (Colorimeter Calibration Spectral Set) files from measured R/G/B/W
    /// emission spectra. The output is CGATS text in the exact shape ArgyllCMS's
    /// ccxxmake emits and its ccss reader (and spotread -X) consumes, and which this
    /// app's own consumers (CgatsValidator, CcssMelanopicEstimator, CcssDatabaseClient)
    /// already parse: KEYWORD-declared metadata, a SAMPLE_ID + SPEC_nnn DATA_FORMAT, and
    /// four data rows ordered white, red, green, blue.
    /// </summary>
    public static class CcssWriter
    {
        /// <summary>
        /// The four full-drive channel spectra of one panel, all on the same wavelength
        /// grid. We emit them white-first, then R, G, B. This row order is Gloam's own
        /// convention — CCSS has no canonical channel order and consumers key on the SPEC_nnn
        /// wavelength fields, not the row sequence — but a stable order keeps the files easy
        /// to eyeball and diff.
        /// </summary>
        public sealed record SpectralSet(
            SpectralSample White,
            SpectralSample Red,
            SpectralSample Green,
            SpectralSample Blue)
        {
            public IEnumerable<SpectralSample> InCcssOrder()
            {
                yield return White;
                yield return Red;
                yield return Green;
                yield return Blue;
            }
        }

        /// <summary>
        /// Builds the complete CCSS file content. Throws <see cref="ArgumentException"/>
        /// when the four spectra are not on an identical wavelength grid.
        /// </summary>
        /// <param name="displayName">EDID/friendly model of the measured panel (DISPLAY keyword).</param>
        /// <param name="referenceInstrument">The spectrometer the spectra came from (REFERENCE keyword).</param>
        /// <param name="set">White/R/G/B spectra, luminance-normalized or raw (scale does not matter to consumers).</param>
        /// <param name="technology">Optional panel technology string; omitted from the file when null/empty.</param>
        /// <param name="created">Timestamp for the CREATED keyword; defaults to now.</param>
        public static string Build(
            string displayName,
            string referenceInstrument,
            SpectralSet set,
            string? technology = null,
            DateTime? created = null)
        {
            if (set == null) throw new ArgumentNullException(nameof(set));
            ValidateGrid(set);

            var reference = set.White;
            int bands = reference.Bands;
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;

            string display = CleanText(displayName, "Unknown display");
            string instrument = CleanText(referenceInstrument, "Unknown spectrometer");
            DateTime stamp = created ?? DateTime.Now;

            // File-type keyword. Argyll writes "CCSS   " padded; the bare token is accepted
            // by every consumer including Argyll's own cgats reader.
            sb.Append("CCSS   \n\n");
            sb.Append($"DESCRIPTOR \"{display} - Gloam spectral capture\"\n");
            sb.Append("ORIGINATOR \"Gloam\"\n");
            // ctime()-style date, matching Argyll's writer ("Fri Feb 21 18:52:31 2014").
            // C's ctime() space-pads a single-digit day to two columns ("Fri Jul  3 ..."),
            // so pad the day the same way rather than emitting a single digit.
            sb.Append($"CREATED \"{FormatCtime(stamp, inv)}\"\n");
            AppendKeyword(sb, "DEVICE_CLASS", "DISPLAY");
            AppendKeyword(sb, "DISPLAY", display);
            if (!string.IsNullOrWhiteSpace(technology))
                AppendKeyword(sb, "TECHNOLOGY", CleanText(technology, "Unknown"));
            AppendKeyword(sb, "DISPLAY_TYPE_REFRESH", "NO");
            AppendKeyword(sb, "REFERENCE", instrument);
            AppendKeyword(sb, "SPECTRAL_BANDS", bands.ToString(inv));
            AppendKeyword(sb, "SPECTRAL_START_NM", reference.StartNm.ToString("F6", inv));
            AppendKeyword(sb, "SPECTRAL_END_NM", reference.EndNm.ToString("F6", inv));
            AppendKeyword(sb, "SPECTRAL_NORM", "1.000000");

            // Non-standard CGATS fields must be KEYWORD-declared; Argyll's writer does this
            // for every SPEC_nnn field, so we match it exactly.
            string[] fieldNames = BuildSpectralFieldNames(reference);
            foreach (string field in fieldNames)
                sb.Append($"KEYWORD \"{field}\"\n");

            sb.Append($"NUMBER_OF_FIELDS {(fieldNames.Length + 1).ToString(inv)}\n");
            sb.Append("BEGIN_DATA_FORMAT\n");
            sb.Append("SAMPLE_ID ").Append(string.Join(" ", fieldNames)).Append('\n');
            sb.Append("END_DATA_FORMAT\n\n");

            sb.Append("NUMBER_OF_SETS 4\n");
            sb.Append("BEGIN_DATA\n");
            int sampleId = 1;
            foreach (var sample in set.InCcssOrder())
            {
                sb.Append(sampleId.ToString(inv));
                foreach (double v in sample.Values)
                    sb.Append(' ').Append(v.ToString("0.000000e+00", inv));
                sb.Append('\n');
                sampleId++;
            }
            sb.Append("END_DATA\n");

            return sb.ToString();
        }

        /// <summary>
        /// Builds the CCSS content, validates it with <see cref="CgatsValidator"/>, and
        /// writes it into <paramref name="folder"/> with a sanitized, uniquified filename
        /// (the same convention <see cref="CcssDatabaseClient.Save"/> uses, so the file is
        /// immediately listed by the browser and the correction pickers). Returns the path.
        /// </summary>
        public static string SaveToFolder(
            string displayName,
            string referenceInstrument,
            SpectralSet set,
            string folder,
            string? technology = null,
            DateTime? created = null)
        {
            string content = Build(displayName, referenceInstrument, set, technology, created);

            var validation = CgatsValidator.Validate(content, "ccss");
            if (!validation.IsValid)
                throw new InvalidDataException(
                    $"Generated CCSS failed structural validation and was not saved: {validation.Error}");

            Directory.CreateDirectory(folder);

            string name = $"{CleanText(displayName, "display")} - {CleanText(referenceInstrument, "spectrometer")} - Gloam";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
            name = string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (name.Length > 80) name = name[..80].Trim();

            string path = Path.Combine(folder, name + ".ccss");
            int n = 2;
            while (File.Exists(path))
                path = Path.Combine(folder, $"{name} ({n++}).ccss");

            File.WriteAllText(path, content);
            Log.Info($"CcssWriter: wrote CCSS for '{displayName}' ({set.White.Bands} bands, " +
                     $"{set.White.StartNm:F1}-{set.White.EndNm:F1} nm, ref '{referenceInstrument}') to {path}");
            return path;
        }

        /// <summary>
        /// SPEC_nnn field names on the sample's grid, wavelengths rounded to whole nm the
        /// way Argyll names them (unique for any step &gt; 1 nm, including i1 Pro 3.3 nm
        /// high-res grids).
        /// </summary>
        internal static string[] BuildSpectralFieldNames(SpectralSample reference)
        {
            var names = new string[reference.Bands];
            for (int i = 0; i < reference.Bands; i++)
                names[i] = "SPEC_" + ((int)Math.Round(reference.WavelengthAt(i))).ToString(CultureInfo.InvariantCulture);
            return names;
        }

        private static void ValidateGrid(SpectralSet set)
        {
            var w = set.White;
            if (w.Bands < 3)
                throw new ArgumentException($"CCSS needs at least 3 spectral bands (got {w.Bands}).");

            foreach (var sample in set.InCcssOrder())
            {
                if (sample.Bands != w.Bands ||
                    Math.Abs(sample.StartNm - w.StartNm) > 1e-6 ||
                    Math.Abs(sample.EndNm - w.EndNm) > 1e-6)
                {
                    throw new ArgumentException(
                        "All four channel spectra must share one wavelength grid " +
                        $"(white is {w.Bands} bands {w.StartNm}-{w.EndNm} nm, " +
                        $"another sample is {sample.Bands} bands {sample.StartNm}-{sample.EndNm} nm).");
                }
                foreach (double v in sample.Values)
                {
                    if (!double.IsFinite(v))
                        throw new ArgumentException("Spectral values must all be finite.");
                }
            }

            // Field names must be unique or CGATS consumers reject the file. Only possible
            // with sub-1nm grids, which no supported instrument produces — but check anyway.
            var names = BuildSpectralFieldNames(w);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string n in names)
            {
                if (!seen.Add(n))
                    throw new ArgumentException($"Wavelength grid produces duplicate field name {n}; the grid is too fine for CCSS.");
            }
        }

        private static void AppendKeyword(StringBuilder sb, string keyword, string value)
        {
            sb.Append($"KEYWORD \"{keyword}\"\n");
            sb.Append($"{keyword} \"{value}\"\n");
        }

        private static string CleanText(string? text, string fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            // CGATS string values are double-quoted; strip quotes and line breaks so the
            // emitted file stays a valid single-line keyword.
            return text.Replace("\"", "'").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        // C ctime() format: "Www Mmm dd HH:MM:SS yyyy" with the day space-padded to two
        // columns (e.g. "Fri Jul  3 12:00:00 2026"), matching Argyll's CGATS writer.
        private static string FormatCtime(DateTime stamp, CultureInfo inv) =>
            stamp.ToString("ddd MMM", inv) + " " +
            stamp.Day.ToString(inv).PadLeft(2) + " " +
            stamp.ToString("HH:mm:ss yyyy", inv);
    }
}
