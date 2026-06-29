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
        public void BuildMessages_HdrPeakMetadataDisappeared_Warns()
        {
            var messages = CalibrationInstallPreflight.BuildMessages(
                Monitor(hdrActive: true, sdrWhite: 200, hdrPeakNits: 900),
                Monitor(hdrActive: true, sdrWhite: 200, hdrPeakNits: 0),
                measuredHdrMode: true,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "before.icm",
                currentDefaultProfile: "before.icm",
                target: StandardTargets.Rec709Pq);

            Assert.Contains(messages, m =>
                m.Severity == CalibrationInstallPreflight.Warn &&
                m.Message.Contains("peak luminance metadata is unavailable", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildMessages_HdrPeakMetadataChanged_Warns()
        {
            var messages = CalibrationInstallPreflight.BuildMessages(
                Monitor(hdrActive: true, sdrWhite: 200, hdrPeakNits: 1000, hdrMinNits: 0.01),
                Monitor(hdrActive: true, sdrWhite: 200, hdrPeakNits: 700, hdrMinNits: 0.01),
                measuredHdrMode: true,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "before.icm",
                currentDefaultProfile: "before.icm",
                target: StandardTargets.Rec709Pq);

            Assert.Contains(messages, m =>
                m.Severity == CalibrationInstallPreflight.Warn &&
                m.Message.Contains("changed from 1000 to 700", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildMessages_HdrTargetAboveCurrentPeak_Warns()
        {
            var messages = CalibrationInstallPreflight.BuildMessages(
                Monitor(hdrActive: true, sdrWhite: 200, hdrPeakNits: 600, hdrMinNits: 0.01),
                Monitor(hdrActive: true, sdrWhite: 200, hdrPeakNits: 600, hdrMinNits: 0.01),
                measuredHdrMode: true,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "before.icm",
                currentDefaultProfile: "before.icm",
                target: StandardTargets.Rec709Pq);

            Assert.Contains(messages, m =>
                m.Severity == CalibrationInstallPreflight.Warn &&
                m.Message.Contains("above the display", StringComparison.OrdinalIgnoreCase) &&
                m.Message.Contains("600", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildMessages_HdrReferenceWhiteNearCurrentPeak_Warns()
        {
            var messages = CalibrationInstallPreflight.BuildMessages(
                Monitor(hdrActive: true, sdrWhite: 200, hdrPeakNits: 220, hdrMinNits: 0.01),
                Monitor(hdrActive: true, sdrWhite: 200, hdrPeakNits: 220, hdrMinNits: 0.01),
                measuredHdrMode: true,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "before.icm",
                currentDefaultProfile: "before.icm",
                target: StandardTargets.Rec709Pq);

            Assert.Contains(messages, m =>
                m.Severity == CalibrationInstallPreflight.Warn &&
                m.Message.Contains("reference white", StringComparison.OrdinalIgnoreCase) &&
                m.Message.Contains("220", StringComparison.OrdinalIgnoreCase));
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
            double hdrPeakNits = 0,
            double hdrMinNits = 0,
            double hdrMaxFullFrameNits = 0,
            string path = @"MONITOR\TEST\0001") => new()
        {
            DeviceName = @"\\.\DISPLAY1",
            FriendlyName = "Test Display",
            MonitorDevicePath = path,
            IsHdrActive = hdrActive,
            IsHdrCapable = true,
            SdrWhiteLevel = sdrWhite,
            HdrPeakNits = hdrPeakNits,
            HdrMinNits = hdrMinNits,
            HdrMaxFullFrameNits = hdrMaxFullFrameNits
        };
    }
}
