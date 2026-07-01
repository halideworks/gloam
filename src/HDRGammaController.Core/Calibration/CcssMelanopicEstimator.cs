using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Estimates relative RGB melanopic/photopic coefficients from DisplayCAL/Argyll .ccss
    /// spectral samples. This is an Ultra Night heuristic: a community CCSS is not a measured
    /// profile for the user's exact panel, but it is much better than treating "blue channel"
    /// as a biological proxy.
    /// </summary>
    public static class CcssMelanopicEstimator
    {
        private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

        private sealed record CacheEntry(DateTime LastWriteUtc, long Length, NightMelanopicCoefficients? Coefficients);

        public static NightMelanopicCoefficients? TryLoad(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || !path.EndsWith(".ccss", StringComparison.OrdinalIgnoreCase))
                    return null;

                string key = info.FullName;
                if (Cache.TryGetValue(key, out var cached) &&
                    cached.LastWriteUtc == info.LastWriteTimeUtc &&
                    cached.Length == info.Length)
                {
                    return cached.Coefficients;
                }

                string content = File.ReadAllText(info.FullName);
                var coefficients = TryEstimate(content, Path.GetFileName(info.FullName));
                Cache[key] = new CacheEntry(info.LastWriteTimeUtc, info.Length, coefficients);
                return coefficients;
            }
            catch (Exception ex)
            {
                Log.Info($"CcssMelanopicEstimator: failed to load '{path}': {ex.Message}");
                return null;
            }
        }

        public static NightMelanopicCoefficients? TryEstimate(string content, string sourceName = "CCSS")
        {
            var validation = CgatsValidator.Validate(content, "ccss");
            if (!validation.IsValid)
                return null;

            var parsed = Parse(content);
            if (parsed == null || parsed.Rows.Count < 3)
                return null;

            var rows = parsed.Rows
                .Select(r => new SpectralRow(r.Name, parsed.Wavelengths, r.Values))
                .Where(r => r.Total > 1e-9)
                .ToList();
            if (rows.Count < 3) return null;

            // Remove an obvious black/leakage row before inferring primaries.
            double medianTotal = rows.Select(r => r.Total).OrderBy(v => v).ElementAt(rows.Count / 2);
            if (medianTotal > 0)
                rows = rows.Where(r => r.Total > medianTotal * 0.08).ToList();
            if (rows.Count < 3) return null;

            var red = SelectDistinct(rows, null, r => r.RedPurity);
            var green = SelectDistinct(rows, new[] { red }, r => r.GreenPurity);
            var blue = SelectDistinct(rows, new[] { red, green }, r => r.BluePurity);
            if (red == null || green == null || blue == null)
                return null;

            return new NightMelanopicCoefficients(
                red.Melanopic, green.Melanopic, blue.Melanopic,
                red.Photopic, green.Photopic, blue.Photopic,
                sourceName);
        }

        private static SpectralRow? SelectDistinct(
            IReadOnlyList<SpectralRow> rows,
            IEnumerable<SpectralRow?>? alreadyUsed,
            Func<SpectralRow, double> score)
        {
            var used = new HashSet<SpectralRow>(alreadyUsed?.Where(r => r != null).Cast<SpectralRow>() ?? Enumerable.Empty<SpectralRow>());
            return rows
                .Where(r => !used.Contains(r))
                .OrderByDescending(r => score(r))
                .FirstOrDefault();
        }

        private sealed class SpectralRow
        {
            public SpectralRow(string name, IReadOnlyList<double> wavelengths, IReadOnlyList<double> values)
            {
                Name = name;
                Wavelengths = wavelengths;
                Values = values;
                BlueBand = Band(430, 500);
                GreenBand = Band(500, 590);
                RedBand = Band(600, 700);
                Total = values.Sum(v => Math.Max(0.0, v));
                Photopic = Integrate(PhotopicSensitivity);
                Melanopic = Integrate(MelanopicSensitivity);
            }

            public string Name { get; }
            private IReadOnlyList<double> Wavelengths { get; }
            private IReadOnlyList<double> Values { get; }
            public double BlueBand { get; }
            public double GreenBand { get; }
            public double RedBand { get; }
            public double Total { get; }
            public double Photopic { get; }
            public double Melanopic { get; }

            public double RedPurity => RedBand / (BlueBand + GreenBand + 1e-9);
            public double GreenPurity => GreenBand / (BlueBand + RedBand + 1e-9);
            public double BluePurity => BlueBand / (GreenBand + RedBand + 1e-9);

            private double Band(double startNm, double endNm)
            {
                double sum = 0.0;
                for (int i = 0; i < Values.Count; i++)
                {
                    double w = Wavelengths[i];
                    if (w >= startNm && w <= endNm)
                        sum += Math.Max(0.0, Values[i]);
                }
                return sum;
            }

            private double Integrate(Func<double, double> sensitivity)
            {
                double sum = 0.0;
                for (int i = 0; i < Values.Count; i++)
                    sum += Math.Max(0.0, Values[i]) * sensitivity(Wavelengths[i]);
                return sum;
            }
        }

        private sealed record ParsedCcss(IReadOnlyList<double> Wavelengths, IReadOnlyList<ParsedRow> Rows);
        private sealed record ParsedRow(string Name, IReadOnlyList<double> Values);

        private static ParsedCcss? Parse(string content)
        {
            var lines = content.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l[0] != '#')
                .ToList();

            var fields = ExtractBlock(lines, "BEGIN_DATA_FORMAT", "END_DATA_FORMAT")
                .SelectMany(Tokenize)
                .ToList();
            if (fields.Count == 0) return null;

            var spectralFields = fields
                .Select((field, index) => new { field, index })
                .Where(x => TryParseSpecWavelength(x.field, out _))
                .ToList();
            if (spectralFields.Count < 3) return null;

            var wavelengths = spectralFields
                .Select(x =>
                {
                    TryParseSpecWavelength(x.field, out double nm);
                    return nm;
                })
                .ToList();

            var dataRows = ExtractBlock(lines, "BEGIN_DATA", "END_DATA");
            var rows = new List<ParsedRow>();
            foreach (string row in dataRows)
            {
                var tokens = Tokenize(row).ToList();
                if (tokens.Count <= spectralFields.Max(x => x.index)) continue;

                var values = new List<double>();
                bool ok = true;
                foreach (var spec in spectralFields)
                {
                    if (!double.TryParse(tokens[spec.index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                        !double.IsFinite(value))
                    {
                        ok = false;
                        break;
                    }
                    values.Add(value);
                }
                if (!ok) continue;

                string name = "";
                int nameIndex = fields.FindIndex(f =>
                    f.Equals("SAMPLE_NAME", StringComparison.OrdinalIgnoreCase) ||
                    f.Equals("SAMPLE_ID", StringComparison.OrdinalIgnoreCase));
                if (nameIndex >= 0 && nameIndex < tokens.Count)
                    name = tokens[nameIndex];

                rows.Add(new ParsedRow(name, values));
            }

            return rows.Count >= 3 ? new ParsedCcss(wavelengths, rows) : null;
        }

        private static List<string> ExtractBlock(IReadOnlyList<string> lines, string begin, string end)
        {
            var result = new List<string>();
            bool inBlock = false;
            foreach (string line in lines)
            {
                if (line.Equals(begin, StringComparison.OrdinalIgnoreCase))
                {
                    inBlock = true;
                    continue;
                }
                if (line.Equals(end, StringComparison.OrdinalIgnoreCase))
                    break;
                if (inBlock) result.Add(line);
            }
            return result;
        }

        private static IEnumerable<string> Tokenize(string line)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                if (i >= line.Length) break;

                if (line[i] == '"')
                {
                    int start = ++i;
                    while (i < line.Length && line[i] != '"') i++;
                    tokens.Add(line[start..Math.Min(i, line.Length)]);
                    if (i < line.Length) i++;
                }
                else
                {
                    int start = i;
                    while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                    tokens.Add(line[start..i]);
                }
            }
            return tokens;
        }

        private static bool TryParseSpecWavelength(string field, out double nm)
        {
            nm = 0.0;
            if (!field.StartsWith("SPEC_", StringComparison.OrdinalIgnoreCase))
                return false;
            return double.TryParse(field[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out nm);
        }

        // Fast approximations. The result is only used for relative channel ranking/tuning.
        private static double PhotopicSensitivity(double nm)
        {
            double t1 = (nm - 568.8) * (nm < 568.8 ? 0.0213 : 0.0247);
            double t2 = (nm - 530.9) * (nm < 530.9 ? 0.0613 : 0.0322);
            return 0.821 * Math.Exp(-0.5 * t1 * t1) + 0.286 * Math.Exp(-0.5 * t2 * t2);
        }

        private static double MelanopicSensitivity(double nm)
        {
            double sigma = nm < 490.0 ? 38.0 : 55.0;
            double t = (nm - 490.0) / sigma;
            return Math.Exp(-0.5 * t * t);
        }
    }
}
