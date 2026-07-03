using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Solves and writes CCMX (colorimeter correction matrix) files: given the same
    /// W/R/G/B patches measured by a reference spectrometer and by the user's everyday
    /// colorimeter, computes the 3x3 matrix that maps the colorimeter's XYZ readings onto
    /// the spectrometer's, and emits it as CGATS text in the exact shape ArgyllCMS's
    /// ccxxmake writes and spotread -X consumes (the same -X flag that takes a .ccss).
    ///
    /// Argyll convention (verified against ccxxmake output and the DisplayCAL corrections
    /// database): the stored MATRIX maps instrument (colorimeter) XYZ to reference
    /// (spectrometer) XYZ, one matrix row per data row, row-major.
    /// </summary>
    public static class CcmxWriter
    {
        /// <summary>
        /// Default weight applied to the white patch when solving. White errors dominate
        /// perception — an off-white is visible at a fraction of the delta that makes a
        /// saturated primary look wrong — so white is emphasized in the fit, matching the
        /// emphasis ccxxmake places on the white sample.
        /// </summary>
        public const double DefaultWhiteWeight = 2.0;

        /// <summary>
        /// Relative determinant threshold below which the colorimeter readings are treated
        /// as near-coplanar in XYZ (the normal-equations matrix is a Gram matrix, and its
        /// determinant collapses to ~0 when the patch vectors do not span 3D). Healthy
        /// W/R/G/B readings from any real display sit orders of magnitude above this.
        /// </summary>
        private const double CoplanarDeterminantThreshold = 1e-6;

        /// <summary>
        /// Solves the 3x3 correction matrix M minimizing the weighted least-squares error
        /// sum_i w_i * ||M*c_i - r_i||^2 over N paired readings, where c_i are the
        /// colorimeter's XYZ readings and r_i the reference spectrometer's XYZ readings of
        /// the SAME patches. Solved via the normal equations: each row m_j of M satisfies
        /// (sum w c c^T) m_j = sum w r_j c, inverted with the shared
        /// <see cref="ColorMath.Invert3x3"/> after conditioning checks.
        /// </summary>
        /// <param name="colorimeterXyz">Instrument-to-correct readings c_i, patch order matching <paramref name="referenceXyz"/>.</param>
        /// <param name="referenceXyz">Reference spectrometer readings r_i.</param>
        /// <param name="whiteIndex">Index of the white patch within the lists (default 0, the W/R/G/B capture order).</param>
        /// <param name="whiteWeight">Weight applied to the white patch (see <see cref="DefaultWhiteWeight"/>).</param>
        /// <exception cref="ArgumentException">Counts mismatch or fewer than 4 pairs.</exception>
        /// <exception cref="InvalidOperationException">Non-physical readings, dark white, or near-coplanar patches.</exception>
        public static double[,] SolveCorrectionMatrix(
            IReadOnlyList<CieXyz> colorimeterXyz,
            IReadOnlyList<CieXyz> referenceXyz,
            int whiteIndex = 0,
            double whiteWeight = DefaultWhiteWeight)
        {
            if (colorimeterXyz == null) throw new ArgumentNullException(nameof(colorimeterXyz));
            if (referenceXyz == null) throw new ArgumentNullException(nameof(referenceXyz));
            if (colorimeterXyz.Count != referenceXyz.Count)
                throw new ArgumentException(
                    $"Paired readings required: {colorimeterXyz.Count} colorimeter vs {referenceXyz.Count} reference readings.");
            if (colorimeterXyz.Count < 4)
                throw new ArgumentException(
                    $"A correction matrix needs at least 4 paired patches (White, Red, Green, Blue); got {colorimeterXyz.Count}.");
            if (whiteIndex < 0 || whiteIndex >= colorimeterXyz.Count)
                throw new ArgumentOutOfRangeException(nameof(whiteIndex));
            if (!double.IsFinite(whiteWeight) || !(whiteWeight > 0))
                throw new ArgumentOutOfRangeException(nameof(whiteWeight), "White weight must be a positive finite number.");

            for (int i = 0; i < colorimeterXyz.Count; i++)
            {
                if (!IsPhysical(colorimeterXyz[i]))
                    throw new InvalidOperationException(
                        $"Colorimeter reading {i + 1} is non-physical " +
                        $"({colorimeterXyz[i].X:F4}, {colorimeterXyz[i].Y:F4}, {colorimeterXyz[i].Z:F4}); refusing to fit a matrix to it.");
                if (!IsPhysical(referenceXyz[i]))
                    throw new InvalidOperationException(
                        $"Reference reading {i + 1} is non-physical " +
                        $"({referenceXyz[i].X:F4}, {referenceXyz[i].Y:F4}, {referenceXyz[i].Z:F4}); refusing to fit a matrix to it.");
            }

            if (!(colorimeterXyz[whiteIndex].Y > 0) || !(referenceXyz[whiteIndex].Y > 0))
                throw new InvalidOperationException(
                    "The white patch measured no luminance on one of the instruments; a correction matrix cannot be anchored to a dark white.");

            // Weighted normal equations. A = sum w c c^T is symmetric positive
            // semi-definite; B's row j is sum w r_j c.
            var a = new double[3, 3];
            var b = new double[3, 3];
            for (int i = 0; i < colorimeterXyz.Count; i++)
            {
                double w = i == whiteIndex ? whiteWeight : 1.0;
                double[] c = { colorimeterXyz[i].X, colorimeterXyz[i].Y, colorimeterXyz[i].Z };
                double[] r = { referenceXyz[i].X, referenceXyz[i].Y, referenceXyz[i].Z };
                for (int p = 0; p < 3; p++)
                {
                    for (int q = 0; q < 3; q++)
                    {
                        a[p, q] += w * c[p] * c[q];
                        b[p, q] += w * r[p] * c[q];
                    }
                }
            }

            // Conditioning guard, scale-invariant: normalize by the mean diagonal (the
            // average squared reading magnitude) so the same data passes/fails whether the
            // readings are in cd/m2 or normalized units. det(A)/s^3 collapses toward zero
            // exactly when the colorimeter readings are (near-)coplanar in XYZ — e.g. the
            // probe never saw one of the primaries, or all patches were nearly identical.
            double s = (a[0, 0] + a[1, 1] + a[2, 2]) / 3.0;
            if (!(s > 0) || !double.IsFinite(s))
                throw new InvalidOperationException("Colorimeter readings are all zero; nothing to fit a matrix to.");

            double det = Det3x3(a);
            if (!double.IsFinite(det) || Math.Abs(det) < CoplanarDeterminantThreshold * s * s * s)
                throw new InvalidOperationException(
                    "The four patch readings are near-coplanar in XYZ — they do not span enough of color space to " +
                    "determine a 3x3 correction matrix. This usually means the instrument was misplaced for one or " +
                    "more patches, or the patches did not actually change on screen. Re-run the capture.");

            var aNorm = new double[3, 3];
            for (int p = 0; p < 3; p++)
                for (int q = 0; q < 3; q++)
                    aNorm[p, q] = a[p, q] / s;
            double[,] aInv = ColorMath.Invert3x3(aNorm);

            // Row j of M = A^-1 * b_j (A symmetric). With the normalization: m_j = (A/s)^-1 (b_j/s).
            var m = new double[3, 3];
            for (int j = 0; j < 3; j++)
            {
                for (int q = 0; q < 3; q++)
                {
                    double sum = 0;
                    for (int k = 0; k < 3; k++)
                        sum += aInv[q, k] * (b[j, k] / s);
                    if (!double.IsFinite(sum))
                        throw new InvalidOperationException("Correction matrix solve produced non-finite coefficients.");
                    m[j, q] = sum;
                }
            }

            // Diagnostic trail: per-patch residuals of the fit, in the reference scale.
            var residuals = new StringBuilder();
            for (int i = 0; i < colorimeterXyz.Count; i++)
            {
                var mapped = Apply(m, colorimeterXyz[i]);
                double e = Math.Sqrt(
                    Square(mapped.X - referenceXyz[i].X) +
                    Square(mapped.Y - referenceXyz[i].Y) +
                    Square(mapped.Z - referenceXyz[i].Z));
                residuals.Append(residuals.Length > 0 ? ", " : "").Append($"patch {i + 1}: {e:F4}");
            }
            Log.Info($"CcmxWriter: solved correction matrix from {colorimeterXyz.Count} patches (white x{whiteWeight:F1}): " +
                     $"[{m[0, 0]:F6} {m[0, 1]:F6} {m[0, 2]:F6}; {m[1, 0]:F6} {m[1, 1]:F6} {m[1, 2]:F6}; " +
                     $"{m[2, 0]:F6} {m[2, 1]:F6} {m[2, 2]:F6}]. Residual XYZ errors: {residuals}.");

            return m;
        }

        /// <summary>Applies a correction matrix to one XYZ reading (M * xyz).</summary>
        public static CieXyz Apply(double[,] matrix, CieXyz xyz)
        {
            ValidateMatrix(matrix);
            return new CieXyz(
                matrix[0, 0] * xyz.X + matrix[0, 1] * xyz.Y + matrix[0, 2] * xyz.Z,
                matrix[1, 0] * xyz.X + matrix[1, 1] * xyz.Y + matrix[1, 2] * xyz.Z,
                matrix[2, 0] * xyz.X + matrix[2, 1] * xyz.Y + matrix[2, 2] * xyz.Z);
        }

        /// <summary>
        /// Builds the complete CCMX file content in Argyll's CGATS shape: CCMX file-type
        /// keyword, KEYWORD-declared metadata, XYZ_X/XYZ_Y/XYZ_Z data format and the three
        /// matrix rows (row-major, instrument XYZ -&gt; reference XYZ).
        /// </summary>
        /// <param name="displayName">EDID/friendly model of the measured panel (DISPLAY keyword).</param>
        /// <param name="colorimeterInstrument">The colorimeter the matrix corrects (INSTRUMENT keyword).</param>
        /// <param name="referenceInstrument">The spectrometer the reference readings came from (REFERENCE keyword).</param>
        /// <param name="matrix">3x3 correction matrix from <see cref="SolveCorrectionMatrix"/>.</param>
        /// <param name="technology">Optional panel technology string; omitted when null/empty.</param>
        /// <param name="created">Timestamp for the CREATED keyword; defaults to now.</param>
        public static string Build(
            string displayName,
            string colorimeterInstrument,
            string referenceInstrument,
            double[,] matrix,
            string? technology = null,
            DateTime? created = null)
        {
            ValidateMatrix(matrix);

            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;

            string display = CleanText(displayName, "Unknown display");
            string instrument = CleanText(colorimeterInstrument, "Unknown colorimeter");
            string reference = CleanText(referenceInstrument, "Unknown spectrometer");
            DateTime stamp = created ?? DateTime.Now;

            // File-type keyword, padded the way Argyll writes it (bare token also accepted).
            sb.Append("CCMX   \n\n");
            sb.Append($"DESCRIPTOR \"{display} - Gloam correction matrix ({instrument} to {reference})\"\n");
            sb.Append("ORIGINATOR \"Gloam\"\n");
            // ctime()-style date, matching Argyll's writer.
            sb.Append($"CREATED \"{stamp.ToString("ddd MMM d HH:mm:ss yyyy", inv)}\"\n");
            AppendKeyword(sb, "INSTRUMENT", instrument);
            AppendKeyword(sb, "REFERENCE", reference);
            AppendKeyword(sb, "DISPLAY", display);
            if (!string.IsNullOrWhiteSpace(technology))
                AppendKeyword(sb, "TECHNOLOGY", CleanText(technology, "Unknown"));
            AppendKeyword(sb, "COLOR_REP", "XYZ");

            sb.Append("\nNUMBER_OF_FIELDS 3\n");
            sb.Append("BEGIN_DATA_FORMAT\n");
            sb.Append("XYZ_X XYZ_Y XYZ_Z\n");
            sb.Append("END_DATA_FORMAT\n\n");

            sb.Append("NUMBER_OF_SETS 3\n");
            sb.Append("BEGIN_DATA\n");
            for (int row = 0; row < 3; row++)
            {
                sb.Append(matrix[row, 0].ToString("0.000000", inv)).Append(' ')
                  .Append(matrix[row, 1].ToString("0.000000", inv)).Append(' ')
                  .Append(matrix[row, 2].ToString("0.000000", inv)).Append('\n');
            }
            sb.Append("END_DATA\n");

            return sb.ToString();
        }

        /// <summary>
        /// Builds the CCMX content, validates it with <see cref="CgatsValidator"/>, and
        /// writes it into <paramref name="folder"/> with a sanitized, uniquified filename
        /// (the same convention <see cref="CcssWriter.SaveToFolder"/> and
        /// <see cref="CcssDatabaseClient.Save"/> use, so the file is immediately listed by
        /// the browser and the setup correction picker). Returns the saved path.
        /// </summary>
        public static string SaveToFolder(
            string displayName,
            string colorimeterInstrument,
            string referenceInstrument,
            double[,] matrix,
            string folder,
            string? technology = null,
            DateTime? created = null)
        {
            string content = Build(displayName, colorimeterInstrument, referenceInstrument, matrix, technology, created);

            var validation = CgatsValidator.Validate(content, "ccmx");
            if (!validation.IsValid)
                throw new InvalidDataException(
                    $"Generated CCMX failed structural validation and was not saved: {validation.Error}");

            Directory.CreateDirectory(folder);

            string name = $"{CleanText(displayName, "display")} - {CleanText(colorimeterInstrument, "colorimeter")} - " +
                          $"{CleanText(referenceInstrument, "spectrometer")} - Gloam";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
            name = string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (name.Length > 80) name = name[..80].Trim();

            string path = Path.Combine(folder, name + ".ccmx");
            int n = 2;
            while (File.Exists(path))
                path = Path.Combine(folder, $"{name} ({n++}).ccmx");

            File.WriteAllText(path, content);
            Log.Info($"CcmxWriter: wrote CCMX for '{displayName}' ('{colorimeterInstrument}' corrected to " +
                     $"'{referenceInstrument}') to {path}");
            return path;
        }

        /// <summary>
        /// Parses the 3x3 matrix out of a CCMX body (any Argyll/DisplayCAL/Gloam variant:
        /// the XYZ_X/XYZ_Y/XYZ_Z columns are located by name inside the DATA_FORMAT, so
        /// extra fields such as SAMPLE_ID are tolerated). Returns false on any structural
        /// problem; never throws on malformed input.
        /// </summary>
        public static bool TryParseMatrix(string? content, out double[,] matrix)
        {
            matrix = new double[3, 3];
            if (string.IsNullOrWhiteSpace(content)) return false;

            string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            var fields = ExtractBlock(lines, "BEGIN_DATA_FORMAT", "END_DATA_FORMAT");
            if (fields.Count == 0) return false;
            string[] fieldNames = fields[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int ix = IndexOfField(fieldNames, "XYZ_X");
            int iy = IndexOfField(fieldNames, "XYZ_Y");
            int iz = IndexOfField(fieldNames, "XYZ_Z");
            if (ix < 0 || iy < 0 || iz < 0) return false;

            var rows = ExtractBlock(lines, "BEGIN_DATA", "END_DATA");
            if (rows.Count != 3) return false;

            for (int r = 0; r < 3; r++)
            {
                string[] values = rows[r].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length < fieldNames.Length) return false;
                if (!TryParseValue(values[ix], out matrix[r, 0]) ||
                    !TryParseValue(values[iy], out matrix[r, 1]) ||
                    !TryParseValue(values[iz], out matrix[r, 2]))
                {
                    return false;
                }
            }
            return true;
        }

        // --- helpers -------------------------------------------------------------

        private static bool IsPhysical(CieXyz xyz)
        {
            const double tolerance = -1e-6;
            return double.IsFinite(xyz.X) && double.IsFinite(xyz.Y) && double.IsFinite(xyz.Z) &&
                   xyz.X >= tolerance && xyz.Y >= tolerance && xyz.Z >= tolerance;
        }

        private static double Square(double v) => v * v;

        private static double Det3x3(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
            - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
            + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

        private static void ValidateMatrix(double[,] matrix)
        {
            if (matrix == null) throw new ArgumentNullException(nameof(matrix));
            if (matrix.GetLength(0) != 3 || matrix.GetLength(1) != 3)
                throw new ArgumentException("Correction matrix must be 3x3.", nameof(matrix));
            foreach (double v in matrix)
            {
                if (!double.IsFinite(v))
                    throw new ArgumentException("Correction matrix contains non-finite values.", nameof(matrix));
            }
        }

        private static int IndexOfField(string[] fieldNames, string name)
        {
            for (int i = 0; i < fieldNames.Length; i++)
            {
                if (string.Equals(fieldNames[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static bool TryParseValue(string token, out double value) =>
            double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value);

        private static List<string> ExtractBlock(string[] lines, string begin, string end)
        {
            var result = new List<string>();
            bool inBlock = false;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith(begin, StringComparison.OrdinalIgnoreCase) &&
                    (line.Length == begin.Length || char.IsWhiteSpace(line[begin.Length])))
                {
                    inBlock = true;
                    continue;
                }
                if (line.StartsWith(end, StringComparison.OrdinalIgnoreCase) &&
                    (line.Length == end.Length || char.IsWhiteSpace(line[end.Length])))
                {
                    break;
                }
                if (!inBlock || line.Length == 0 || line[0] == '#') continue;
                result.Add(line);
            }
            return result;
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
    }
}
