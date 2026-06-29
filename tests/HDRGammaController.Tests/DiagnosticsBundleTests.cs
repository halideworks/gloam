using System;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
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
    }
}
