using System;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class CalibrationInstallPreflightTests
    {
        [Fact]
        public void BuildMessages_MissingCurrentMonitor_BlocksInstall()
        {
            var messages = CalibrationInstallPreflight.BuildMessages(
                Monitor(hdrActive: true, sdrWhite: 200),
                currentMonitor: null,
                measuredHdrMode: true,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "before.icm",
                currentDefaultProfile: null);

            Assert.Contains(messages, m => m.Severity == CalibrationInstallPreflight.Error);
        }

        [Fact]
        public void BuildMessages_HdrModeChanged_BlocksInstall()
        {
            var messages = CalibrationInstallPreflight.BuildMessages(
                Monitor(hdrActive: true, sdrWhite: 200),
                Monitor(hdrActive: false, sdrWhite: 200),
                measuredHdrMode: true,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "before.icm",
                currentDefaultProfile: "before.icm");

            Assert.Contains(messages, m =>
                m.Severity == CalibrationInstallPreflight.Error &&
                m.Message.Contains("HDR", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildMessages_PhysicalDisplayChanged_BlocksInstall()
        {
            var measured = Monitor(hdrActive: false, sdrWhite: 200, path: @"MONITOR\MEASURED\0001");
            var current = Monitor(hdrActive: false, sdrWhite: 200, path: @"MONITOR\OTHER\0001");

            var messages = CalibrationInstallPreflight.BuildMessages(
                measured,
                current,
                measuredHdrMode: false,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "before.icm",
                currentDefaultProfile: "before.icm");

            Assert.Contains(messages, m =>
                m.Severity == CalibrationInstallPreflight.Error &&
                m.Message.Contains("different physical display", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildMessages_SamePhysicalDisplayWithDifferentCasing_DoesNotWarn()
        {
            var measured = Monitor(hdrActive: false, sdrWhite: 200, path: @"MONITOR\TEST\0001");
            var current = Monitor(hdrActive: false, sdrWhite: 200, path: @" monitor\test\0001 ");

            var messages = CalibrationInstallPreflight.BuildMessages(
                measured,
                current,
                measuredHdrMode: false,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "default.icm",
                currentDefaultProfile: "default.icm");

            Assert.Empty(messages);
        }

        [Fact]
        public void BuildMessages_HdrSdrWhiteChanged_Warns()
        {
            var messages = CalibrationInstallPreflight.BuildMessages(
                Monitor(hdrActive: true, sdrWhite: 200),
                Monitor(hdrActive: true, sdrWhite: 240),
                measuredHdrMode: true,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "before.icm",
                currentDefaultProfile: "before.icm");

            Assert.Contains(messages, m =>
                m.Severity == CalibrationInstallPreflight.Warn &&
                m.Message.Contains("SDR white", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildMessages_DefaultProfileChanged_Warns()
        {
            var messages = CalibrationInstallPreflight.BuildMessages(
                Monitor(hdrActive: true, sdrWhite: 200),
                Monitor(hdrActive: true, sdrWhite: 200),
                measuredHdrMode: true,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "before.icm",
                currentDefaultProfile: "after.icm");

            Assert.Contains(messages, m =>
                m.Severity == CalibrationInstallPreflight.Warn &&
                m.Message.Contains("Advanced Color profile", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildMessages_UnchangedState_ReturnsNoMessages()
        {
            var messages = CalibrationInstallPreflight.BuildMessages(
                Monitor(hdrActive: false, sdrWhite: 200),
                Monitor(hdrActive: false, sdrWhite: 240),
                measuredHdrMode: false,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "default.icm",
                currentDefaultProfile: " default.icm ");

            Assert.Empty(messages);
        }

        private static MonitorInfo Monitor(
            bool hdrActive,
            double sdrWhite,
            string path = @"MONITOR\TEST\0001") => new()
        {
            DeviceName = @"\\.\DISPLAY1",
            FriendlyName = "Test Display",
            MonitorDevicePath = path,
            IsHdrActive = hdrActive,
            IsHdrCapable = true,
            SdrWhiteLevel = sdrWhite
        };
    }
}
