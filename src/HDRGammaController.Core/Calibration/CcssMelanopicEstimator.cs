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

        /// <summary>
        /// Melanopic Daylight (D65) Efficacy Ratio (γ_mel,v, CIE S 026:2018) for a spectral power
        /// distribution: melanopic ELR / 1.3262 mW·lm⁻¹, i.e. how melanopic the source is relative
        /// to D65 (which is 1.0). Scale-invariant, so it works on the relative SPD in a .ccss row.
        /// Returns NaN when the SPD carries no photopic power. Provided so callers can later report
        /// standardized melanopic units; existing callers are unaffected.
        /// </summary>
        public static double MelanopicDer(IReadOnlyList<double> wavelengths, IReadOnlyList<double> spectralValues)
        {
            if (wavelengths == null || spectralValues == null ||
                wavelengths.Count != spectralValues.Count || wavelengths.Count < 2)
                return double.NaN;

            double melanopic = TrapezoidIntegrate(wavelengths, spectralValues, MelanopicSensitivity);
            double photopic = TrapezoidIntegrate(wavelengths, spectralValues, PhotopicSensitivity);
            if (!(photopic > 0.0) || !double.IsFinite(melanopic))
                return double.NaN;

            // Melanopic ELR = (Kmel · ∫SPD·s_mel) / (Km · ∫SPD·V), with Km = 683 lm/W and the
            // melanopic radiometric scale Kmel = 1000 (mW/W); ELR is then in mW/lm.
            const double photopicLumEff = 683.0;
            double melanopicElrMilliWattPerLumen = 1000.0 * melanopic / (photopicLumEff * photopic);
            const double d65MelanopicElr = 1.3262; // mW/lm, melanopic ELR of CIE D65
            return melanopicElrMilliWattPerLumen / d65MelanopicElr;
        }

        /// <summary>
        /// Greedy per-channel selector: picks the highest-purity row for each primary. This band
        /// heuristic is robust for conventional RGB emissive stacks, but it can misattribute
        /// channels on displays whose primaries overlap heavily or add extra emitters — e.g.
        /// quantum-dot (narrow, sometimes multi-peak) or RGBW panels where a white sub-pixel's
        /// broadband SPD has no single dominant band. Treat its output as a ranking hint, not a
        /// measured primary decomposition.
        /// </summary>
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
                => TrapezoidIntegrate(Wavelengths, Values, sensitivity);
        }

        /// <summary>
        /// Trapezoidal integration of SPD·sensitivity over wavelength, weighting each sample by its
        /// own Δλ (half the distance to its neighbours). This handles non-uniform wavelength grids
        /// correctly, unlike a plain Σ f(λ) which silently assumes a constant, unit step.
        /// </summary>
        private static double TrapezoidIntegrate(
            IReadOnlyList<double> wavelengths, IReadOnlyList<double> values, Func<double, double> sensitivity)
        {
            int count = Math.Min(wavelengths.Count, values.Count);
            if (count == 0) return 0.0;
            if (count == 1) return Math.Max(0.0, values[0]) * sensitivity(wavelengths[0]);

            double sum = 0.0;
            for (int i = 0; i < count; i++)
            {
                double f = Math.Max(0.0, values[i]) * sensitivity(wavelengths[i]);
                double dLambda;
                if (i == 0)
                    dLambda = (wavelengths[1] - wavelengths[0]) * 0.5;
                else if (i == count - 1)
                    dLambda = (wavelengths[count - 1] - wavelengths[count - 2]) * 0.5;
                else
                    dLambda = (wavelengths[i + 1] - wavelengths[i - 1]) * 0.5;
                sum += f * Math.Abs(dLambda);
            }
            return sum;
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

        // Standardized spectral sensitivities, tabulated at 5 nm from 380–780 nm and linearly
        // interpolated to the SPD's wavelength grid. Replaces the previous Gaussian stand-ins.
        //   PhotopicSensitivity  = CIE 1924 V(λ) (identical to the CIE 1931 ȳ colour-matching fn).
        //   MelanopicSensitivity = CIE S 026:2018 melanopic action spectrum s_mel(λ), peak 1 at 490 nm.

        private static double PhotopicSensitivity(double nm) => InterpolateTable(VLambda1924, nm);
        private static double MelanopicSensitivity(double nm) => InterpolateTable(SMelS026, nm);

        private const double TableStartNm = 380.0;
        private const double TableStepNm = 5.0;

        private static double InterpolateTable(double[] table, double nm)
        {
            if (!double.IsFinite(nm)) return 0.0;
            double pos = (nm - TableStartNm) / TableStepNm;
            if (pos <= 0.0) return table[0] * (pos == 0.0 ? 1.0 : 0.0); // below 380 nm: no weight
            int last = table.Length - 1;
            if (pos >= last) return pos == last ? table[last] : 0.0;      // above 780 nm: no weight
            int i = (int)pos;
            double f = pos - i;
            return table[i] + f * (table[i + 1] - table[i]);
        }

        // CIE 1924 photopic luminous efficiency V(λ) = CIE 1931 2° ȳ(λ), 5 nm, 380–780 nm.
        private static readonly double[] VLambda1924 =
        {
            3.900000e-05, 6.400000e-05, 1.200000e-04, 2.170000e-04, 3.960000e-04, 6.400000e-04,
            1.210000e-03, 2.180000e-03, 4.000000e-03, 7.300000e-03, 1.160000e-02, 1.684000e-02,
            2.300000e-02, 2.980000e-02, 3.800000e-02, 4.800000e-02, 6.000000e-02, 7.390000e-02,
            9.098000e-02, 1.126000e-01, 1.390200e-01, 1.693000e-01, 2.080200e-01, 2.586000e-01,
            3.230000e-01, 4.073000e-01, 5.030000e-01, 6.082000e-01, 7.100000e-01, 7.932000e-01,
            8.620000e-01, 9.148501e-01, 9.540000e-01, 9.803000e-01, 9.949501e-01, 1.000000e+00,
            9.950000e-01, 9.786000e-01, 9.520000e-01, 9.154000e-01, 8.700000e-01, 8.163000e-01,
            7.570000e-01, 6.949000e-01, 6.310000e-01, 5.668000e-01, 5.030000e-01, 4.412000e-01,
            3.810000e-01, 3.210000e-01, 2.650000e-01, 2.170000e-01, 1.750000e-01, 1.382000e-01,
            1.070000e-01, 8.160000e-02, 6.100000e-02, 4.458000e-02, 3.200000e-02, 2.320000e-02,
            1.700000e-02, 1.192000e-02, 8.210000e-03, 5.723000e-03, 4.102000e-03, 2.929000e-03,
            2.091000e-03, 1.484000e-03, 1.047000e-03, 7.400000e-04, 5.200000e-04, 3.611000e-04,
            2.492000e-04, 1.719000e-04, 1.200000e-04, 8.480000e-05, 6.000000e-05, 4.240000e-05,
            3.000000e-05, 2.120000e-05, 1.499000e-05,
        };

        // CIE S 026:2018 melanopic action spectrum s_mel(λ), 5 nm, 380–780 nm, peak 1.0 at 490 nm.
        private static readonly double[] SMelS026 =
        {
            9.181650e-04, 1.667240e-03, 3.094420e-03, 5.880350e-03, 1.142770e-02, 2.281120e-02,
            4.615500e-02, 7.947660e-02, 1.372370e-01, 1.870960e-01, 2.538650e-01, 3.206790e-01,
            4.015870e-01, 4.740020e-01, 5.537150e-01, 6.296540e-01, 7.080490e-01, 7.852160e-01,
            8.602910e-01, 9.177340e-01, 9.656050e-01, 9.906210e-01, 1.000000e+00, 9.920220e-01,
            9.659520e-01, 9.222990e-01, 8.628880e-01, 7.852330e-01, 6.996280e-01, 6.094220e-01,
            5.193090e-01, 4.325330e-01, 3.517070e-01, 2.791350e-01, 2.157220e-01, 1.620560e-01,
            1.185260e-01, 8.434570e-02, 5.870130e-02, 4.000890e-02, 2.687470e-02, 1.786240e-02,
            1.179010e-02, 7.734300e-03, 5.066860e-03, 3.317660e-03, 2.176980e-03, 1.433140e-03,
            9.473130e-04, 6.276480e-04, 4.179550e-04, 2.798010e-04, 1.883410e-04, 1.273370e-04,
            8.657510e-05, 5.919140e-05, 4.069450e-05, 2.813200e-05, 1.955350e-05, 1.364800e-05,
            9.576370e-06, 6.754250e-06, 4.788040e-06, 3.408410e-06, 2.438190e-06, 1.752520e-06,
            1.265600e-06, 9.180780e-07, 6.689910e-07, 4.895310e-07, 3.597660e-07, 2.654930e-07,
            1.967400e-07, 1.463700e-07, 1.093320e-07, 8.195870e-08, 6.167490e-08, 4.659160e-08,
            3.532720e-08, 2.688030e-08, 2.052580e-08,
        };
    }
}
