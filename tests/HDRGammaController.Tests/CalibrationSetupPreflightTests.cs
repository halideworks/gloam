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
