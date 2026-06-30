using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Serializes raw colorimeter measurements to a support-friendly CSV artifact.
    /// The CSV contains measured XYZ/xy/CCT/Duv plus patch target metadata; it intentionally
    /// omits colorimeter process output so support bundles stay compact and predictable.
    /// </summary>
    public static class MeasurementCsvExporter
    {
        public static void Save(
            string path,
            string reportId,
            string phase,
            IEnumerable<MeasurementResult> measurements)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Output path is required.", nameof(path));

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, BuildCsv(reportId, phase, measurements),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        internal static string BuildCsv(
            string reportId,
            string phase,
            IEnumerable<MeasurementResult> measurements)
        {
            if (measurements == null) throw new ArgumentNullException(nameof(measurements));

            var sb = new StringBuilder();
            AppendRow(sb,
                "report_id", "phase", "sequence_index", "measurement_id", "timestamp_utc",
                "patch_index", "patch_name", "patch_category",
                "display_r", "display_g", "display_b", "patch_nits",
                "target_x", "target_y", "target_z",
                "measured_x", "measured_y_nits", "measured_z",
                "measured_xy_x", "measured_xy_y", "cct_k", "duv",
                "integration_time_ms", "is_valid", "error_message");

            foreach (var measurement in measurements.OrderBy(m => m.SequenceIndex).ThenBy(m => m.Patch.Index))
            {
                var patch = measurement.Patch;
                var derived = TryDeriveChromaticity(measurement);
                AppendRow(sb,
                    reportId,
                    phase,
                    measurement.SequenceIndex,
                    measurement.Id,
                    measurement.Timestamp,
                    patch.Index,
                    patch.Name,
                    patch.Category,
                    patch.DisplayRgb.R,
                    patch.DisplayRgb.G,
                    patch.DisplayRgb.B,
                    patch.Nits,
                    patch.TargetXyz?.X,
                    patch.TargetXyz?.Y,
                    patch.TargetXyz?.Z,
                    measurement.Xyz.X,
                    measurement.Xyz.Y,
                    measurement.Xyz.Z,
                    derived?.X,
                    derived?.Y,
                    derived?.Cct,
                    derived?.Duv,
                    measurement.IntegrationTimeMs,
                    measurement.IsValid,
                    measurement.ErrorMessage);
            }

            return sb.ToString();
        }

        private static void AppendRow(StringBuilder sb, params object?[] values)
            => sb.AppendLine(string.Join(",", values.Select(CsvValue)));

        private static (double X, double Y, double Cct, double Duv)? TryDeriveChromaticity(MeasurementResult measurement)
        {
            if (!measurement.IsValid)
                return null;

            var xyz = measurement.Xyz;
            if (!double.IsFinite(xyz.X) || !double.IsFinite(xyz.Y) || !double.IsFinite(xyz.Z) ||
                xyz.X < -1e-6 || xyz.Y < -1e-6 || xyz.Z < -1e-6)
            {
                return null;
            }

            double sum = xyz.X + xyz.Y + xyz.Z;
            if (!double.IsFinite(sum) || sum <= 1e-12)
                return null;

            double x = xyz.X / sum;
            double y = xyz.Y / sum;
            if (!double.IsFinite(x) || !double.IsFinite(y) || x <= 0.0 || y <= 0.0 || x + y > 1.000001)
                return null;

            var xy = new Chromaticity(x, y);
            return (x, y, ColorMath.ChromaticityToCct(xy), ColorMath.CalculateDuv(xy));
        }

        private static string CsvValue(object? value)
        {
            string text = value switch
            {
                null => string.Empty,
                DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                double d when double.IsFinite(d) => d.ToString("G17", CultureInfo.InvariantCulture),
                double => string.Empty,
                float f when float.IsFinite(f) => f.ToString("G9", CultureInfo.InvariantCulture),
                float => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };

            return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? $"\"{text.Replace("\"", "\"\"")}\""
                : text;
        }
    }
}
