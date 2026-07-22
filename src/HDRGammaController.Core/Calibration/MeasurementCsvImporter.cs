using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Reads the CSV artifact written by <see cref="MeasurementCsvExporter"/> back into
    /// <see cref="MeasurementResult"/>s so recorded real-panel measurement sets can be
    /// replayed through the calibration pipeline (golden-sample regression fixtures).
    /// </summary>
    /// <remarks>
    /// Columns are resolved by header name, not position, so files written by newer
    /// exporter versions with extra columns still import; unknown columns are ignored.
    /// Derived columns (measured_xy_*, cct_k, duv) are not read back — they are
    /// recomputed from XYZ on demand by <see cref="MeasurementResult"/> itself.
    /// </remarks>
    public static class MeasurementCsvImporter
    {
        internal const long MaxFileBytes = 64L * 1024 * 1024;
        internal const int MaxRows = 1_000_000;
        internal const int MaxFieldsPerRow = 256;
        internal const int MaxFieldCharacters = 1_000_000;

        public sealed record ImportedSet(
            string ReportId,
            string Phase,
            IReadOnlyList<MeasurementResult> Measurements);

        public static ImportedSet Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Input path is required.", nameof(path));
            if (new FileInfo(path).Length > MaxFileBytes)
                throw new InvalidDataException("Measurement CSV exceeds the size limit.");

            using var reader = new StreamReader(path);
            return Parse(reader);
        }

        public static ImportedSet Parse(TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            var rows = ReadCsvRows(reader);
            if (rows.Count == 0)
                throw new InvalidDataException("Measurement CSV is empty (no header row).");

            var header = BuildHeaderIndex(rows[0]);
            string reportId = string.Empty;
            string phase = string.Empty;
            var measurements = new List<MeasurementResult>(rows.Count - 1);

            for (int i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))
                    continue; // trailing blank line

                string Cell(string column) => GetCell(header, row, column);

                if (measurements.Count == 0)
                {
                    reportId = Cell("report_id");
                    phase = Cell("phase");
                }

                var patch = new ColorPatch
                {
                    Index = ParseInt(Cell("patch_index"), "patch_index", i),
                    Name = Cell("patch_name"),
                    Category = ParseCategory(Cell("patch_category")),
                    DisplayRgb = new LinearRgb(
                        ParseDouble(Cell("display_r"), "display_r", i),
                        ParseDouble(Cell("display_g"), "display_g", i),
                        ParseDouble(Cell("display_b"), "display_b", i)),
                    Nits = ParseOptionalDouble(Cell("patch_nits")),
                    TargetXyz = ParseOptionalXyz(
                        Cell("target_x"), Cell("target_y"), Cell("target_z")),
                };

                measurements.Add(new MeasurementResult
                {
                    Id = ParseGuid(Cell("measurement_id")),
                    Timestamp = ParseTimestamp(Cell("timestamp_utc"), i),
                    SequenceIndex = ParseInt(Cell("sequence_index"), "sequence_index", i),
                    Patch = patch,
                    Xyz = new CieXyz(
                        ParseDouble(Cell("measured_x"), "measured_x", i, allowEmpty: true),
                        ParseDouble(Cell("measured_y_nits"), "measured_y_nits", i, allowEmpty: true),
                        ParseDouble(Cell("measured_z"), "measured_z", i, allowEmpty: true)),
                    IntegrationTimeMs = ParseOptionalDouble(Cell("integration_time_ms")),
                    IsValid = ParseBool(Cell("is_valid"), i),
                    ErrorMessage = NullIfEmpty(Cell("error_message")),
                });
            }

            return new ImportedSet(reportId, phase, measurements);
        }

        private static Dictionary<string, int> BuildHeaderIndex(IReadOnlyList<string> headerRow)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headerRow.Count; i++)
            {
                string name = headerRow[i].Trim();
                if (name.Length > 0 && !index.ContainsKey(name))
                    index[name] = i;
            }

            string[] required =
            {
                "sequence_index", "patch_index", "patch_name", "patch_category",
                "display_r", "display_g", "display_b",
                "measured_x", "measured_y_nits", "measured_z", "is_valid",
            };
            var missing = required.Where(c => !index.ContainsKey(c)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidDataException(
                    $"Measurement CSV is missing required column(s): {string.Join(", ", missing)}.");
            }

            return index;
        }

        private static string GetCell(
            Dictionary<string, int> header, IReadOnlyList<string> row, string column)
        {
            if (!header.TryGetValue(column, out int idx))
                return string.Empty; // optional column absent in older files
            return idx < row.Count ? row[idx] : string.Empty;
        }

        private static int ParseInt(string text, string column, int rowNumber)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return value;
            throw new InvalidDataException(
                $"Row {rowNumber}: cannot parse '{text}' as integer for column '{column}'.");
        }

        private static double ParseDouble(
            string text, string column, int rowNumber, bool allowEmpty = false)
        {
            if (allowEmpty && string.IsNullOrWhiteSpace(text))
                return double.NaN; // exporter writes empty for non-finite values
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
                double.IsFinite(value))
                return value;
            throw new InvalidDataException(
                $"Row {rowNumber}: cannot parse '{text}' as number for column '{column}'.");
        }

        private static double? ParseOptionalDouble(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
                   double.IsFinite(value)
                ? value
                : null;
        }

        private static CieXyz? ParseOptionalXyz(string x, string y, string z)
        {
            double? px = ParseOptionalDouble(x);
            double? py = ParseOptionalDouble(y);
            double? pz = ParseOptionalDouble(z);
            if (px is null || py is null || pz is null)
                return null;
            return new CieXyz(px.Value, py.Value, pz.Value);
        }

        private static PatchCategory ParseCategory(string text)
            => Enum.TryParse(text, ignoreCase: true, out PatchCategory category)
                ? category
                : PatchCategory.General;

        private static Guid ParseGuid(string text)
            => Guid.TryParse(text, out var id) ? id : Guid.NewGuid();

        private static DateTime ParseTimestamp(string text, int rowNumber)
        {
            if (string.IsNullOrWhiteSpace(text))
                return DateTime.UtcNow;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dt))
                return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            throw new InvalidDataException(
                $"Row {rowNumber}: cannot parse '{text}' as timestamp for column 'timestamp_utc'.");
        }

        private static bool ParseBool(string text, int rowNumber)
        {
            if (bool.TryParse(text, out bool value))
                return value;
            throw new InvalidDataException(
                $"Row {rowNumber}: cannot parse '{text}' as boolean for column 'is_valid'.");
        }

        private static string? NullIfEmpty(string text)
            => string.IsNullOrEmpty(text) ? null : text;

        /// <summary>
        /// RFC-4180 CSV reader matching the exporter's quoting (fields containing comma,
        /// quote, or newline are quoted; embedded quotes doubled).
        /// </summary>
        private static List<List<string>> ReadCsvRows(TextReader reader)
        {
            var rows = new List<List<string>>();
            var currentRow = new List<string>();
            var field = new System.Text.StringBuilder();
            bool inQuotes = false;
            bool rowHasContent = false;
            long charactersRead = 0;

            int ch;
            while ((ch = reader.Read()) >= 0)
            {
                if (++charactersRead > MaxFileBytes)
                    throw new InvalidDataException("Measurement CSV exceeds the size limit.");

                char c = (char)ch;
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (reader.Peek() == '"')
                        {
                            reader.Read();
                            if (++charactersRead > MaxFileBytes)
                                throw new InvalidDataException("Measurement CSV exceeds the size limit.");
                            field.Append('"');
                            if (field.Length > MaxFieldCharacters)
                                throw new InvalidDataException("Measurement CSV contains an oversized field.");
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                        if (field.Length > MaxFieldCharacters)
                            throw new InvalidDataException("Measurement CSV contains an oversized field.");
                    }
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        rowHasContent = true;
                        break;
                    case ',':
                        currentRow.Add(field.ToString());
                        if (currentRow.Count > MaxFieldsPerRow)
                            throw new InvalidDataException("Measurement CSV contains too many columns.");
                        field.Clear();
                        rowHasContent = true;
                        break;
                    case '\r':
                        break; // handled with the following \n (or ignored alone)
                    case '\n':
                        if (rowHasContent || field.Length > 0)
                        {
                            currentRow.Add(field.ToString());
                            if (currentRow.Count > MaxFieldsPerRow)
                                throw new InvalidDataException("Measurement CSV contains too many columns.");
                            rows.Add(currentRow);
                            if (rows.Count > MaxRows)
                                throw new InvalidDataException("Measurement CSV contains too many rows.");
                            currentRow = new List<string>();
                            field.Clear();
                        }
                        rowHasContent = false;
                        break;
                    default:
                        field.Append(c);
                        if (field.Length > MaxFieldCharacters)
                            throw new InvalidDataException("Measurement CSV contains an oversized field.");
                        rowHasContent = true;
                        break;
                }
            }

            if (inQuotes)
                throw new InvalidDataException("Measurement CSV ends inside a quoted field.");

            if (rowHasContent || field.Length > 0 || currentRow.Count > 0)
            {
                currentRow.Add(field.ToString());
                if (currentRow.Count > MaxFieldsPerRow)
                    throw new InvalidDataException("Measurement CSV contains too many columns.");
                rows.Add(currentRow);
                if (rows.Count > MaxRows)
                    throw new InvalidDataException("Measurement CSV contains too many rows.");
            }

            return rows;
        }
    }
}
