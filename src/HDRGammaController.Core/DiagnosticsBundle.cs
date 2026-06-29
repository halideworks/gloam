using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Creates a support bundle with logs, sanitized settings, monitor/HDR topology, and
    /// calibration-profile summaries. The zip is intentionally text-only; it does not include
    /// raw ICC files, correction files, or saved calibration reports unless the caller opts in.
    /// </summary>
    public sealed class DiagnosticsBundle
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public string Create(
            string outputDirectory,
            IEnumerable<MonitorInfo> monitors,
            SettingsManager settingsManager,
            bool includeCalibrationReports = false)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
            if (monitors == null) throw new ArgumentNullException(nameof(monitors));
            if (settingsManager == null) throw new ArgumentNullException(nameof(settingsManager));

            Directory.CreateDirectory(outputDirectory);
            string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            string zipPath = Path.Combine(outputDirectory, $"Gloam-Diagnostics-{stamp}.zip");

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            AddText(zip, "manifest.json", BuildManifest(monitors, includeCalibrationReports));
            AddSanitizedFileIfExists(zip, Path.Combine(AppPaths.DataDir, "settings.json"), "settings.sanitized.json");
            AddLogs(zip);
            AddCalibrationProfileSummary(zip, settingsManager);
            if (includeCalibrationReports)
                AddCalibrationReports(zip);
            AddThirdPartyNotices(zip);

            return zipPath;
        }

        internal static string SanitizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)] = "%USERPROFILE%",
                [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)] = "%LOCALAPPDATA%",
                [Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)] = "%APPDATA%",
                [Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)] = "%TEMP%"
            };

            string sanitized = text;
            foreach (var (source, replacement) in replacements.OrderByDescending(kvp => kvp.Key.Length))
            {
                if (!string.IsNullOrWhiteSpace(source))
                    sanitized = sanitized.Replace(source, replacement, StringComparison.OrdinalIgnoreCase);
            }

            string userName = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(userName))
                sanitized = sanitized.Replace(userName, "%USERNAME%", StringComparison.OrdinalIgnoreCase);

            return sanitized;
        }

        private static string BuildManifest(IEnumerable<MonitorInfo> monitors, bool includeCalibrationReports)
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(DiagnosticsBundle).Assembly;
            var manifest = new
            {
                CreatedAtUtc = DateTimeOffset.UtcNow,
                App = new
                {
                    Product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Gloam",
                    Version = assembly.GetName().Version?.ToString() ?? "unknown",
                    BaseDirectory = SanitizeText(AppContext.BaseDirectory),
                    DataDirectory = SanitizeText(AppPaths.DataDir)
                },
                Runtime = new
                {
                    OS = RuntimeInformation.OSDescription,
                    Framework = RuntimeInformation.FrameworkDescription,
                    ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Is64BitProcess = Environment.Is64BitProcess
                },
                Diagnostics = new
                {
                    IncludeCalibrationReports = includeCalibrationReports
                },
                Monitors = monitors.Select(ToDiagnosticMonitor).ToList()
            };

            return JsonSerializer.Serialize(manifest, JsonOptions);
        }

        private static object ToDiagnosticMonitor(MonitorInfo monitor)
        {
            return new
            {
                monitor.DeviceName,
                monitor.FriendlyName,
                MonitorDevicePathHash = HashId(monitor.MonitorDevicePath),
                monitor.IsHdrCapable,
                monitor.IsHdrActive,
                monitor.DxgiColorSpace,
                monitor.BitsPerColor,
                monitor.SdrWhiteLevel,
                monitor.HdrMinNits,
                monitor.HdrPeakNits,
                monitor.HdrMaxFullFrameNits,
                AdapterLuid = new { monitor.AdapterLuid.HighPart, monitor.AdapterLuid.LowPart },
                monitor.OutputId,
                Bounds = new
                {
                    monitor.MonitorBounds.Left,
                    monitor.MonitorBounds.Top,
                    monitor.MonitorBounds.Right,
                    monitor.MonitorBounds.Bottom
                },
                monitor.HasDisplayConfigIds,
                DisplayConfigAdapterId = new
                {
                    monitor.DisplayConfigAdapterId.HighPart,
                    monitor.DisplayConfigAdapterId.LowPart
                },
                monitor.DisplayConfigSourceId,
                EdidColor = monitor.EdidColor
            };
        }

        private static void AddLogs(ZipArchive zip)
        {
            if (!Directory.Exists(AppPaths.DataDir)) return;

            foreach (string file in Directory.GetFiles(AppPaths.DataDir, "*.log*").OrderBy(Path.GetFileName))
            {
                AddSanitizedFileIfExists(zip, file, $"logs/{Path.GetFileName(file)}");
            }
        }

        private static void AddCalibrationProfileSummary(ZipArchive zip, SettingsManager settingsManager)
        {
            try
            {
                var profiles = settingsManager.ListCalibrationProfiles()
                    .Select(p => new
                    {
                        p.Id,
                        p.Name,
                        MonitorDevicePathHash = HashId(p.MonitorDevicePath),
                        p.MonitorName,
                        p.Mode,
                        p.CalibratedAt,
                        p.TargetColorSpace,
                        p.TargetGamma,
                        p.ColorimeterModel,
                        p.BlackLevel,
                        p.PeakLuminance,
                        p.MeasuredGamma,
                        HasToneCurves = p.RedToneCurve != null || p.GreenToneCurve != null || p.BlueToneCurve != null,
                        ReferenceLutSize = p.ReferenceLutSize
                    })
                    .ToList();

                AddText(zip, "calibration-profiles-summary.json",
                    JsonSerializer.Serialize(profiles, JsonOptions));
            }
            catch (Exception ex)
            {
                AddText(zip, "calibration-profiles-summary.error.txt", SanitizeText(ex.Message));
            }
        }

        private static void AddCalibrationReports(ZipArchive zip)
        {
            string reportsDir = Path.Combine(AppPaths.DataDir, "reports");
            if (!Directory.Exists(reportsDir))
            {
                AddText(zip, "reports/README.txt", "No saved calibration reports were found.");
                return;
            }

            var reports = new List<CalibrationProfile>();
            foreach (string file in Directory.GetFiles(reportsDir, "*.json").OrderBy(Path.GetFileName))
            {
                string fileName = Path.GetFileName(file);
                AddSanitizedFileIfExists(zip, file, $"reports/json/{fileName}");

                try
                {
                    reports.Add(CalibrationProfile.LoadFromFile(file));
                }
                catch (Exception ex)
                {
                    AddText(zip, $"reports/errors/{fileName}.txt",
                        $"Failed to load {fileName}: {SanitizeText(ex.Message)}");
                }
            }

            if (reports.Count == 0)
            {
                AddText(zip, "reports/README.txt", "No readable calibration reports were found.");
                AddRawMeasurementCsvs(zip, reportsDir);
                return;
            }

            AddText(zip, "reports/calibration-report-summary.csv", BuildCalibrationReportSummaryCsv(reports));

            string detailedCsv = BuildDetailedVerificationCsv(reports);
            if (!string.IsNullOrWhiteSpace(detailedCsv))
                AddText(zip, "reports/detailed-verification-patches.csv", detailedCsv);
            AddRawMeasurementCsvs(zip, reportsDir);
        }

        private static void AddRawMeasurementCsvs(ZipArchive zip, string reportsDir)
        {
            string measurementDir = Path.Combine(reportsDir, "measurements");
            if (!Directory.Exists(measurementDir)) return;

            foreach (string file in Directory.GetFiles(measurementDir, "*.csv").OrderBy(Path.GetFileName))
            {
                AddSanitizedFileIfExists(zip, file, $"reports/raw-measurements/{Path.GetFileName(file)}");
            }
        }

        internal static string BuildCalibrationReportSummaryCsv(IEnumerable<CalibrationProfile> reports)
        {
            var sb = new StringBuilder();
            AppendCsvRow(sb,
                "report_id", "monitor_name", "monitor_device_path_hash", "calibrated_at_utc",
                "target", "colorimeter", "software_version", "patch_count",
                "peak_luminance_nits", "black_level_nits", "measured_gamma",
                "white_x", "white_y", "quality_grade",
                "avg_delta_e", "max_delta_e", "grayscale_delta_e", "primary_delta_e",
                "after_avg_delta_e", "after_max_delta_e", "after_grayscale_delta_e", "after_primary_delta_e",
                "detailed_grayscale_delta_e", "detailed_primaries_delta_e",
                "detailed_saturation_delta_e", "detailed_memory_colors_delta_e");

            foreach (var report in reports.OrderByDescending(r => r.LastCalibratedAt ?? r.CreatedAt))
            {
                var summary = report.ReportSummary;
                var characteristics = report.MeasuredCharacteristics;
                AppendCsvRow(sb,
                    report.Id,
                    report.MonitorName,
                    HashId(report.MonitorDevicePath),
                    report.LastCalibratedAt ?? report.CreatedAt,
                    report.Target.Name,
                    report.ColorimeterModel,
                    report.SoftwareVersion,
                    report.PatchCount,
                    characteristics?.PeakLuminance,
                    characteristics?.BlackLevel,
                    characteristics?.MeasuredGamma,
                    characteristics?.MeasuredWhite.X,
                    characteristics?.MeasuredWhite.Y,
                    report.QualityGrade,
                    summary?.AvgDeltaE,
                    summary?.MaxDeltaE,
                    summary?.GrayscaleDeltaE,
                    summary?.PrimaryDeltaE,
                    summary?.AfterAvgDeltaE,
                    summary?.AfterMaxDeltaE,
                    summary?.AfterGrayscaleDeltaE,
                    summary?.AfterPrimaryDeltaE,
                    summary?.DetailedGrayscaleDeltaE,
                    summary?.DetailedPrimariesDeltaE,
                    summary?.DetailedSaturationDeltaE,
                    summary?.DetailedMemoryColorsDeltaE);
            }

            return sb.ToString();
        }

        internal static string BuildDetailedVerificationCsv(IEnumerable<CalibrationProfile> reports)
        {
            var rows = new StringBuilder();
            AppendCsvRow(rows,
                "report_id", "monitor_name", "monitor_device_path_hash", "calibrated_at_utc",
                "patch_index", "patch_name", "category", "delta_e");

            int count = 0;
            foreach (var report in reports.OrderByDescending(r => r.LastCalibratedAt ?? r.CreatedAt))
            {
                var detailed = report.ReportSummary?.DetailedPatches;
                if (detailed == null || detailed.Count == 0) continue;

                for (int i = 0; i < detailed.Count; i++)
                {
                    var patch = detailed[i];
                    AppendCsvRow(rows,
                        report.Id,
                        report.MonitorName,
                        HashId(report.MonitorDevicePath),
                        report.LastCalibratedAt ?? report.CreatedAt,
                        i,
                        patch.Name,
                        patch.Category,
                        patch.DeltaE);
                    count++;
                }
            }

            return count == 0 ? string.Empty : rows.ToString();
        }

        private static void AddThirdPartyNotices(ZipArchive zip)
        {
            string notices = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt");
            if (!File.Exists(notices))
                notices = Path.Combine(Environment.CurrentDirectory, "THIRD_PARTY_NOTICES.txt");
            AddSanitizedFileIfExists(zip, notices, "THIRD_PARTY_NOTICES.txt");
        }

        private static void AddSanitizedFileIfExists(ZipArchive zip, string path, string entryName)
        {
            if (!File.Exists(path)) return;

            string text;
            try
            {
                text = File.ReadAllText(path);
                if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                    text = SanitizeJson(text);
                else
                    text = SanitizeText(text);
            }
            catch (Exception ex)
            {
                text = $"Failed to read {SanitizeText(path)}: {ex.Message}";
            }

            AddText(zip, entryName, text);
        }

        private static string SanitizeJson(string text)
        {
            try
            {
                var node = JsonNode.Parse(text);
                if (node == null) return SanitizeText(text);
                SanitizeJsonNode(node);
                return node.ToJsonString(JsonOptions);
            }
            catch
            {
                return SanitizeText(text);
            }
        }

        private static void SanitizeJsonNode(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var key in obj.Select(kvp => kvp.Key).ToList())
                {
                    var child = obj[key];
                    if (child == null) continue;

                    if (child is JsonValue value && value.TryGetValue<string>(out var str))
                    {
                        obj[key] = SanitizeText(str);
                    }
                    else
                    {
                        SanitizeJsonNode(child);
                    }
                }
            }
            else if (node is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child == null) continue;
                    if (child is JsonValue value && value.TryGetValue<string>(out var str))
                    {
                        arr[i] = SanitizeText(str);
                    }
                    else
                    {
                        SanitizeJsonNode(child);
                    }
                }
            }
        }

        private static void AddText(ZipArchive zip, string entryName, string text)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(text);
        }

        private static void AppendCsvRow(StringBuilder sb, params object?[] values)
        {
            sb.AppendLine(string.Join(",", values.Select(CsvValue)));
        }

        private static string CsvValue(object? value)
        {
            string text = value switch
            {
                null => string.Empty,
                DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                double d when double.IsFinite(d) => d.ToString("G17", CultureInfo.InvariantCulture),
                float f when float.IsFinite(f) => f.ToString("G9", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };

            text = SanitizeText(text);
            return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? $"\"{text.Replace("\"", "\"\"")}\""
                : text;
        }

        private static string HashId(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash, 0, 8);
        }
    }
}
