using System;
using System.IO;
using System.Linq;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.ViewModels;
using Xunit;

namespace HDRGammaController.Tests
{
    public class CalibrationSetupPreflightTests
    {
        private static MonitorInfo Monitor(
            bool hdrActive = false,
            bool hdrCapable = false,
            double hdrPeakNits = 0,
            double hdrMinNits = 0,
            double hdrMaxFullFrameNits = 0) => new()
        {
            FriendlyName = "Test Display",
            MonitorDevicePath = @"\\?\DISPLAY#TEST#0001",
            IsHdrActive = hdrActive,
            IsHdrCapable = hdrCapable || hdrActive,
            SdrWhiteLevel = 200,
            HdrPeakNits = hdrPeakNits,
            HdrMinNits = hdrMinNits,
            HdrMaxFullFrameNits = hdrMaxFullFrameNits
        };

        [Fact]
        public void Preflight_HdrActiveWithSdrTarget_BlocksStart()
        {
            var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                Monitor(hdrActive: true),
                StandardTargets.SrgbGamma22,
                selectedOption: null,
                DisplayType.LcdLed,
                detectedDisplayType: null,
                correction: new CorrectionChoice("Built-in", null),
                whitePointOnly: false,
                monitorProfile: null);

            Assert.Contains(messages, m => m.Severity == "ERROR" && m.Message.Contains("HDR is active", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ViewModel_HdrInitialMonitor_SelectsOnlyHdrTarget()
        {
            var vm = new CalibrationSetupViewModel(new[] { Monitor(hdrActive: true) }.ToList(), settingsManager: null);

            var selected = vm.Targets.Where(t => t.IsSelected).ToList();

            var target = Assert.Single(selected);
            Assert.Same(StandardTargets.Rec709Pq, target.Target);
            Assert.True(target.IsEnabled);
        }

        [Fact]
        public void ViewModel_PreferredMonitorPath_SelectsThatMonitor()
        {
            var first = Monitor();
            first.FriendlyName = "First";
            first.MonitorDevicePath = @"\\?\DISPLAY#FIRST#0001";
            var second = Monitor();
            second.FriendlyName = "Second";
            second.MonitorDevicePath = @"\\?\DISPLAY#SECOND#0001";

            var vm = new CalibrationSetupViewModel(
                new[] { first, second }.ToList(),
                settingsManager: null,
                preferredMonitorDevicePath: second.MonitorDevicePath);

            Assert.Equal(second.MonitorDevicePath, vm.SelectedMonitor?.Model.MonitorDevicePath);
        }

        [Fact]
        public void ViewModel_PresetLabelsMatchGeneratedPatchCounts()
        {
            var vm = new CalibrationSetupViewModel(new[] { Monitor(hdrActive: false) }.ToList(), settingsManager: null);

            int quick = PatchSetGenerator.GeneratePatchSet(StandardTargets.SrgbGamma22, PatchSetGenerator.CalibrationPreset.Quick).Count;
            int standard = PatchSetGenerator.GeneratePatchSet(StandardTargets.SrgbGamma22, PatchSetGenerator.CalibrationPreset.Standard).Count;
            int thorough = PatchSetGenerator.GeneratePatchSet(StandardTargets.SrgbGamma22, PatchSetGenerator.CalibrationPreset.Thorough).Count;

            Assert.Contains($"({quick} patches)", vm.QuickPresetLabel);
            Assert.Contains($"({standard} patches)", vm.StandardPresetLabel);
            Assert.Contains($"({thorough} patches)", vm.ThoroughPresetLabel);
        }

        [Fact]
        public void ViewModel_SavedCalibrationPrefs_RestoreTargetAndPreset()
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string tempDir = Path.Combine(Path.GetTempPath(), "gloam-test-" + Guid.NewGuid().ToString("N"));
            var monitor = Monitor();

            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(tempDir, Path.Combine(tempDir, "roaming"));
                var settings = new SettingsManager();
                settings.SetCalibrationPrefs(
                    monitor.MonitorDevicePath,
                    ccssPath: null,
                    displayType: DisplayType.Oled.ToString(),
                    whitePointOnly: true,
                    targetName: StandardTargets.Rec709Gamma24.Name,
                    preset: PatchSetGenerator.CalibrationPreset.Thorough.ToString());

                var vm = new CalibrationSetupViewModel(new[] { monitor }.ToList(), settings);

                var selected = Assert.Single(vm.Targets, t => t.IsSelected);
                Assert.Same(StandardTargets.Rec709Gamma24, selected.Target);
                Assert.True(vm.IsPresetThorough);
                Assert.True(vm.WhitePointOnly);
                Assert.True(vm.IsOled);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Preflight_HdrCapableDisplayInSdr_WarnsAboutHdrTargets()
        {
            var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                Monitor(hdrActive: false, hdrCapable: true),
                StandardTargets.SrgbGamma22,
                selectedOption: null,
                DisplayType.LcdLed,
                detectedDisplayType: null,
                correction: new CorrectionChoice("Built-in", null),
                whitePointOnly: false,
                monitorProfile: null);

            Assert.Contains(messages, m => m.Severity == "WARN" && m.Message.Contains("HDR-capable", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Preflight_OledWithoutCorrection_Warns()
        {
            var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                Monitor(hdrActive: false),
                StandardTargets.SrgbGamma22,
                selectedOption: null,
                DisplayType.Oled,
                detectedDisplayType: DisplayType.Oled,
                correction: new CorrectionChoice("Built-in", null),
                whitePointOnly: true,
                monitorProfile: null);

            Assert.Contains(messages, m => m.Severity == "WARN" && m.Message.Contains("CCSS/CCMX", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Preflight_MissingCorrectionFile_BlocksStart()
        {
            string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ccss");

            var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                Monitor(hdrActive: false),
                StandardTargets.SrgbGamma22,
                selectedOption: null,
                DisplayType.Oled,
                detectedDisplayType: DisplayType.Oled,
                correction: new CorrectionChoice("missing.ccss", missingPath),
                whitePointOnly: true,
                monitorProfile: null);

            Assert.Contains(messages, m =>
                m.Severity == "ERROR" &&
                m.Message.Contains("no longer exists", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Preflight_InvalidCorrectionFile_BlocksStart()
        {
            string path = Path.Combine(Path.GetTempPath(), $"invalid-{Guid.NewGuid():N}.ccss");
            try
            {
                File.WriteAllText(path, "not a cgats correction");

                var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                    Monitor(hdrActive: false),
                    StandardTargets.SrgbGamma22,
                    selectedOption: null,
                    DisplayType.Oled,
                    detectedDisplayType: DisplayType.Oled,
                    correction: new CorrectionChoice("invalid.ccss", path),
                    whitePointOnly: true,
                    monitorProfile: null);

                Assert.Contains(messages, m =>
                    m.Severity == "ERROR" &&
                    m.Message.Contains("not valid", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void Preflight_ValidCorrectionFile_AllowsCorrectionSelection()
        {
            string path = Path.Combine(Path.GetTempPath(), $"valid-{Guid.NewGuid():N}.ccmx");
            try
            {
                File.WriteAllText(path, @"CCMX
DESCRIPTOR ""Test correction""
NUMBER_OF_FIELDS 3
BEGIN_DATA_FORMAT
XYZ_X XYZ_Y XYZ_Z
END_DATA_FORMAT
NUMBER_OF_SETS 3
BEGIN_DATA
1 0 0
0 1 0
0 0 1
END_DATA
");

                var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                    Monitor(hdrActive: false),
                    StandardTargets.SrgbGamma22,
                    selectedOption: null,
                    DisplayType.Oled,
                    detectedDisplayType: DisplayType.Oled,
                    correction: new CorrectionChoice("valid.ccmx", path),
                    whitePointOnly: true,
                    monitorProfile: null);

                Assert.DoesNotContain(messages, m => m.Severity == "ERROR");
                Assert.DoesNotContain(messages, m => m.Message.Contains("CCSS/CCMX", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void Preflight_ExistingProfile_ExplainsBypass()
        {
            var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                Monitor(hdrActive: false),
                StandardTargets.SrgbGamma22,
                selectedOption: null,
                DisplayType.LcdLed,
                detectedDisplayType: null,
                correction: new CorrectionChoice("Built-in", null),
                whitePointOnly: false,
                monitorProfile: new MonitorProfileData { Mhc2ProfileName = "Gloam Test.icm" });

            Assert.Contains(messages, m => m.Severity == "INFO" && m.Message.Contains("bypassed", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Preflight_NightLightActive_Warns()
        {
            var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                Monitor(hdrActive: false),
                StandardTargets.SrgbGamma22,
                selectedOption: null,
                DisplayType.LcdLed,
                detectedDisplayType: null,
                correction: new CorrectionChoice("Built-in", null),
                whitePointOnly: false,
                monitorProfile: null,
                nightLightActive: true);

            Assert.Contains(messages, m =>
                m.Severity == "WARN" &&
                m.Message.Contains("Night Light", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Preflight_NightLightUnknownOrOff_StaysSilent()
        {
            foreach (bool? state in new bool?[] { null, false })
            {
                var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                    Monitor(hdrActive: false),
                    StandardTargets.SrgbGamma22,
                    selectedOption: null,
                    DisplayType.LcdLed,
                    detectedDisplayType: null,
                    correction: new CorrectionChoice("Built-in", null),
                    whitePointOnly: false,
                    monitorProfile: null,
                    nightLightActive: state);

                Assert.DoesNotContain(messages, m =>
                    m.Message.Contains("Night Light", StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void Preflight_SdrAcmActive_WarnsForSdrCalibration()
        {
            var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                Monitor(hdrActive: false),
                StandardTargets.SrgbGamma22,
                selectedOption: null,
                DisplayType.LcdLed,
                detectedDisplayType: null,
                correction: new CorrectionChoice("Built-in", null),
                whitePointOnly: false,
                monitorProfile: null,
                sdrAcmActive: true);

            Assert.Contains(messages, m =>
                m.Severity == "WARN" &&
                m.Message.Contains("Auto Color Management", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Preflight_SdrAcm_DoesNotWarnInHdrMode()
        {
            // In HDR the Advanced Color pipeline is expected; the ACM warning is only
            // meaningful for SDR-basis calibrations.
            var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                Monitor(hdrActive: true),
                StandardTargets.Rec709Pq,
                selectedOption: null,
                DisplayType.LcdLed,
                detectedDisplayType: null,
                correction: new CorrectionChoice("Built-in", null),
                whitePointOnly: false,
                monitorProfile: null,
                sdrAcmActive: true);

            Assert.DoesNotContain(messages, m =>
                m.Message.Contains("Auto Color Management", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Preflight_HdrTargetWithMissingPeakMetadata_Warns()
        {
            var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                Monitor(hdrActive: true, hdrPeakNits: 0),
                StandardTargets.Rec709Pq,
                selectedOption: null,
                DisplayType.LcdLed,
                detectedDisplayType: null,
                correction: new CorrectionChoice("Built-in", null),
                whitePointOnly: false,
                monitorProfile: null);

            Assert.Contains(messages, m =>
                m.Severity == "WARN" &&
                m.Message.Contains("peak luminance metadata", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Preflight_HdrTargetAboveReportedPeak_Warns()
        {
            var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                Monitor(hdrActive: true, hdrPeakNits: 600, hdrMinNits: 0.01, hdrMaxFullFrameNits: 420),
                StandardTargets.Rec709Pq,
                selectedOption: null,
                DisplayType.LcdLed,
                detectedDisplayType: null,
                correction: new CorrectionChoice("Built-in", null),
                whitePointOnly: false,
                monitorProfile: null);

            Assert.Contains(messages, m =>
                m.Severity == "WARN" &&
                m.Message.Contains("above this display", StringComparison.OrdinalIgnoreCase) &&
                m.Message.Contains("600", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Preflight_HdrReferenceWhiteNearReportedPeak_Warns()
        {
            var messages = CalibrationSetupViewModel.BuildPreflightMessages(
                Monitor(hdrActive: true, hdrPeakNits: 220, hdrMinNits: 0.01, hdrMaxFullFrameNits: 180),
                StandardTargets.Rec709Pq,
                selectedOption: null,
                DisplayType.LcdLed,
                detectedDisplayType: null,
                correction: new CorrectionChoice("Built-in", null),
                whitePointOnly: false,
                monitorProfile: null);

            Assert.Contains(messages, m =>
                m.Severity == "WARN" &&
                m.Message.Contains("reference white", StringComparison.OrdinalIgnoreCase) &&
                m.Message.Contains("220", StringComparison.OrdinalIgnoreCase));
        }
    }
}
