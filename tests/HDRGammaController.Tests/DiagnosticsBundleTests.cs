using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Interop;
using Xunit;

namespace HDRGammaController.Tests
{
    public class DiagnosticsBundleTests
    {
        [Fact]
        public void SanitizeText_ReplacesUserScopedPathsAndUserName()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string input = $"{userProfile}\\Documents\\probe.txt\n{localAppData}\\Gloam\\app.log\nuser={Environment.UserName}";

            string result = DiagnosticsBundle.SanitizeText(input);

            Assert.DoesNotContain(userProfile, result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(localAppData, result, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(Environment.UserName))
                Assert.DoesNotContain(Environment.UserName, result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", result);
            Assert.Contains("%LOCALAPPDATA%", result);
        }

        [Fact]
        public void SanitizeText_RedactsMonitorIdentifiersInLogs()
        {
            string displayPath = @"\\?\DISPLAY#ACME123#INSTANCE#UID";
            string monitorPath = @"MONITOR\ACME123\{4d36e96e-e325-11ce-bfc1-08002be10318}\0008";
            string input = $"Installing profile for {displayPath}\nGDI monitor={monitorPath}";

            string result = DiagnosticsBundle.SanitizeText(input);

            Assert.DoesNotContain(displayPath, result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(monitorPath, result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("monitor-", result);
        }

        [Fact]
        public void BuildCalibrationReportSummaryCsv_SanitizesPathsAndEscapesFields()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var report = BuildReport($"{userProfile}\\Displays\\Panel, One", "Panel, One");

            string csv = DiagnosticsBundle.BuildCalibrationReportSummaryCsv(new[] { report });

            Assert.Contains("monitor_device_path_hash", csv);
            Assert.Contains("\"Panel, One\"", csv);
            Assert.DoesNotContain(userProfile, csv, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Displays", csv, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2.399", csv);
            Assert.Contains("0.312", csv);
            Assert.Contains("0.329", csv);
        }

        [Fact]
        public void BuildDetailedVerificationCsv_ExportsPatchRowsWithoutRawMonitorPath()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var report = BuildReport($"{userProfile}\\Displays\\PanelTwo", "Panel Two");

            string csv = DiagnosticsBundle.BuildDetailedVerificationCsv(new[] { report });

            Assert.Contains("patch_index,patch_name,category,delta_e", csv);
            Assert.Contains("0,\"Skin, bright\",MemoryColor,1.25", csv);
            Assert.Contains("1,Blue Primary,Primary,2.75", csv);
            Assert.DoesNotContain(userProfile, csv, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PanelTwo", csv, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildDetailedVerificationCsv_ReturnsEmptyWhenNoDetailedPatchesExist()
        {
            var report = BuildReport(@"DISPLAY\NO-DETAILED", "No Detailed");
            report.ReportSummary!.DetailedPatches = null;

            string csv = DiagnosticsBundle.BuildDetailedVerificationCsv(new[] { report });

            Assert.Equal(string.Empty, csv);
        }

        [Fact]
        public void BuildManifest_IncludesDecodedDisplayTopologyWithoutRawMonitorPath()
        {
            string rawPath = @"\\?\DISPLAY#ACME123#INSTANCE#UID";
            var monitor = new MonitorInfo
            {
                DeviceName = @"\\.\DISPLAY7",
                FriendlyName = "Reference HDR",
                MonitorDevicePath = rawPath,
                IsHdrCapable = true,
                IsHdrActive = true,
                DxgiColorSpace = 12,
                BitsPerColor = 3,
                SdrWhiteLevel = 203,
                HdrMinNits = 0.005,
                HdrPeakNits = 1000,
                HdrMaxFullFrameNits = 650,
                AdapterLuid = new Dxgi.LUID { HighPart = 1, LowPart = 2 },
                OutputId = 4,
                MonitorBounds = new Dxgi.RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1080 },
                HasDisplayConfigIds = true,
                DisplayConfigAdapterId = new Dxgi.LUID { HighPart = 3, LowPart = 4 },
                DisplayConfigSourceId = 9
            };

            string manifest = DiagnosticsBundle.BuildManifest(new[] { monitor }, includeCalibrationReports: true);
            var root = JsonNode.Parse(manifest)!;
            var mon = root["Monitors"]![0]!;

            Assert.Equal(12, mon["DxgiColorSpace"]!.GetValue<int>());
            Assert.Equal("RGB_FULL_G2084_NONE_P2020", mon["DxgiColorSpaceName"]!.GetValue<string>());
            Assert.Equal(3, mon["BitsPerColor"]!.GetValue<int>());
            Assert.Equal("10 bpc", mon["BitsPerColorName"]!.GetValue<string>());
            Assert.Equal(1920, mon["Bounds"]!["Width"]!.GetValue<int>());
            Assert.Equal(1080, mon["Bounds"]!["Height"]!.GetValue<int>());
            Assert.Equal(9, mon["DisplayConfigSourceId"]!.GetValue<int>());
            Assert.NotEmpty(mon["MonitorDevicePathHash"]!.GetValue<string>());
            Assert.DoesNotContain(rawPath, manifest, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SanitizeJson_RedactsMonitorPathKeysAndUserScopedValues()
        {
            string rawMonitorPath = @"\\?\DISPLAY#ACME123#INSTANCE#UID";
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string json = $$"""
            {
              "MonitorProfiles": {
                "{{JsonEscape(rawMonitorPath)}}": {
                  "MeterCorrectionPath": "{{JsonEscape(Path.Combine(userProfile, "Corrections", "panel.ccss"))}}"
                }
              }
            }
            """;

            string sanitized = DiagnosticsBundle.SanitizeJson(json);
            var root = JsonNode.Parse(sanitized)!;
            var profileKey = root["MonitorProfiles"]!.AsObject().Select(kvp => kvp.Key).Single();

            Assert.StartsWith("monitor-", profileKey, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(rawMonitorPath, sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(userProfile, sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", sanitized);
        }

        [Fact]
        public void SanitizeJson_RedactsMonitorPathStringValues()
        {
            string rawMonitorPath = @"\\?\DISPLAY#ACME123#INSTANCE#UID";
            string json = $$"""
            {
              "MonitorDevicePath": "{{JsonEscape(rawMonitorPath)}}",
              "Notes": "Measured {{JsonEscape(rawMonitorPath)}} during HDR verification"
            }
            """;

            string sanitized = DiagnosticsBundle.SanitizeJson(json);

            Assert.DoesNotContain(rawMonitorPath, sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DISPLAY#ACME123", sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("monitor-", sanitized);
        }

        [Fact]
        public void SanitizeJson_RedactsUserScopedObjectKeys()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string rawKey = Path.Combine(userProfile, "Documents", "probe.txt");
            string json = $$"""
            {
              "{{JsonEscape(rawKey)}}": "present"
            }
            """;

            string sanitized = DiagnosticsBundle.SanitizeJson(json);

            Assert.DoesNotContain(userProfile, sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", sanitized);
        }

        [Fact]
        public void ResolveThirdPartyNoticesPath_UsesApplicationBaseDirectory()
        {
            string appBase = CreateTempDirectory();
            try
            {
                string notices = Path.Combine(appBase, "THIRD_PARTY_NOTICES.txt");
                File.WriteAllText(notices, "notices");

                Assert.Equal(notices, DiagnosticsBundle.ResolveThirdPartyNoticesPath(appBase));
            }
            finally
            {
                DeleteDirectory(appBase);
            }
        }

        [Fact]
        public void ResolveThirdPartyNoticesPath_DoesNotFallBackToCurrentDirectory()
        {
            string originalCurrentDirectory = Environment.CurrentDirectory;
            string appBase = CreateTempDirectory();
            string currentDirectory = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(currentDirectory, "THIRD_PARTY_NOTICES.txt"), "wrong file");
                Environment.CurrentDirectory = currentDirectory;

                Assert.Null(DiagnosticsBundle.ResolveThirdPartyNoticesPath(appBase));
            }
            finally
            {
                Environment.CurrentDirectory = originalCurrentDirectory;
                DeleteDirectory(appBase);
                DeleteDirectory(currentDirectory);
            }
        }

        [Fact]
        public void BuildUniqueBundlePath_UsesPlainTimestampWhenAvailable()
        {
            string dir = CreateTempDirectory();
            try
            {
                var timestamp = new DateTimeOffset(2026, 6, 29, 10, 11, 12, TimeSpan.Zero);

                string path = DiagnosticsBundle.BuildUniqueBundlePath(dir, timestamp);

                Assert.Equal(Path.Combine(dir, "Gloam-Diagnostics-20260629-101112.zip"), path);
            }
            finally
            {
                DeleteDirectory(dir);
            }
        }

        [Fact]
        public void BuildUniqueBundlePath_AddsSuffixWhenTimestampCollides()
        {
            string dir = CreateTempDirectory();
            try
            {
                var timestamp = new DateTimeOffset(2026, 6, 29, 10, 11, 12, TimeSpan.Zero);
                File.WriteAllText(Path.Combine(dir, "Gloam-Diagnostics-20260629-101112.zip"), "");
                File.WriteAllText(Path.Combine(dir, "Gloam-Diagnostics-20260629-101112-02.zip"), "");

                string path = DiagnosticsBundle.BuildUniqueBundlePath(dir, timestamp);

                Assert.Equal(Path.Combine(dir, "Gloam-Diagnostics-20260629-101112-03.zip"), path);
            }
            finally
            {
                DeleteDirectory(dir);
            }
        }

        private static CalibrationProfile BuildReport(string monitorDevicePath, string monitorName)
        {
            return new CalibrationProfile
            {
                MonitorDevicePath = monitorDevicePath,
                MonitorName = monitorName,
                Target = StandardTargets.SrgbGamma22,
                CreatedAt = new DateTime(2026, 06, 28, 20, 30, 00, DateTimeKind.Utc),
                LastCalibratedAt = new DateTime(2026, 06, 28, 21, 00, 00, DateTimeKind.Utc),
                ColorimeterModel = "i1 Display Pro",
                SoftwareVersion = "9.9.9-test",
                PatchCount = 128,
                QualityGrade = CalibrationGrade.A,
                MeasuredCharacteristics = new DisplayCharacteristics
                {
                    MeasuredRed = new Chromaticity(0.64, 0.33),
                    MeasuredGreen = new Chromaticity(0.30, 0.60),
                    MeasuredBlue = new Chromaticity(0.15, 0.06),
                    MeasuredWhite = new Chromaticity(0.3127, 0.3290),
                    PeakLuminance = 203.5,
                    BlackLevel = 0.031,
                    MeasuredGamma = 2.4
                },
                ReportSummary = new CalibrationReportSummary
                {
                    AvgDeltaE = 0.9,
                    MaxDeltaE = 2.1,
                    GrayscaleDeltaE = 0.7,
                    PrimaryDeltaE = 1.2,
                    AfterAvgDeltaE = 0.6,
                    AfterMaxDeltaE = 1.8,
                    AfterGrayscaleDeltaE = 0.5,
                    AfterPrimaryDeltaE = 1.0,
                    DetailedGrayscaleDeltaE = 0.55,
                    DetailedPrimariesDeltaE = 1.1,
                    DetailedSaturationDeltaE = 1.3,
                    DetailedMemoryColorsDeltaE = 0.8,
                    DetailedPatches = new()
                    {
                        new VerifiedPatchResult { Name = "Skin, bright", Category = "MemoryColor", DeltaE = 1.25 },
                        new VerifiedPatchResult { Name = "Blue Primary", Category = "Primary", DeltaE = 2.75 }
                    }
                }
            };
        }

        private static string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "GloamDiagnosticsBundleTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string JsonEscape(string value)
            => value.Replace(@"\", @"\\").Replace("\"", "\\\"");

        private static void DeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for test temp files.
            }
        }
    }
}
