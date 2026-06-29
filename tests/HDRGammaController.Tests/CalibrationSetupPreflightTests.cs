using System;
using System.Linq;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.ViewModels;
using Xunit;

namespace HDRGammaController.Tests
{
    public class CalibrationSetupPreflightTests
    {
        private static MonitorInfo Monitor(bool hdrActive = false, bool hdrCapable = false) => new()
        {
            FriendlyName = "Test Display",
            MonitorDevicePath = @"\\?\DISPLAY#TEST#0001",
            IsHdrActive = hdrActive,
            IsHdrCapable = hdrCapable || hdrActive,
            SdrWhiteLevel = 200
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
    }
}
