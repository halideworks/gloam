using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
    /// raw ICC files, measurement reports, or correction files unless summarized.
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
            SettingsManager settingsManager)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
            if (monitors == null) throw new ArgumentNullException(nameof(monitors));
            if (settingsManager == null) throw new ArgumentNullException(nameof(settingsManager));

            Directory.CreateDirectory(outputDirectory);
            string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            string zipPath = Path.Combine(outputDirectory, $"Gloam-Diagnostics-{stamp}.zip");

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            AddText(zip, "manifest.json", BuildManifest(monitors));
            AddSanitizedFileIfExists(zip, Path.Combine(AppPaths.DataDir, "settings.json"), "settings.sanitized.json");
            AddLogs(zip);
            AddCalibrationProfileSummary(zip, settingsManager);
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

        private static string BuildManifest(IEnumerable<MonitorInfo> monitors)
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

        private static string HashId(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash, 0, 8);
        }
    }
}
