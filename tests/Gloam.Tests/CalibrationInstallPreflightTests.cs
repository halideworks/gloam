using System;
using System.Collections.Generic;
using Gloam.Core;
using Gloam.Core.Calibration;
using Xunit;

namespace Gloam.Tests
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

        // Composed-path tone warning: the live regrade re-encodes at a hardcoded 1/2.2, so a
        // calibration whose target EOTF is not a 2.2 power law composes only approximately
        // with it in the shadows. See CalibrationInstallPreflight.AddComposedTonePathMessage.

        [Fact]
        public void BuildMessages_Bt1886TargetUnderLiveRegrade_WarnsAboutComposedTone()
        {
            var messages = ComposedToneMessages(
                StandardTargets.Rec709Gamma24, GammaMode.Gamma24);

            Assert.Contains(messages, m =>
                m.Severity == CalibrationInstallPreflight.Warn &&
                m.Message.Contains("composed shadow tone", StringComparison.OrdinalIgnoreCase) &&
                m.Message.Contains("Windows Default", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildMessages_PiecewiseSrgbTargetUnderLiveRegrade_WarnsAboutComposedTone()
        {
            var messages = ComposedToneMessages(
                StandardTargets.SrgbPiecewise, GammaMode.Gamma22);

            Assert.Contains(messages, m =>
                m.Severity == CalibrationInstallPreflight.Warn &&
                m.Message.Contains("composed shadow tone", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildMessages_Gamma22Target_DoesNotWarnAboutComposedTone()
        {
            var messages = ComposedToneMessages(
                StandardTargets.SrgbGamma22, GammaMode.Gamma24);

            Assert.Empty(messages);
        }

        [Fact]
        public void BuildMessages_Bt1886TargetOnWindowsDefault_DoesNotWarnAboutComposedTone()
        {
            var messages = ComposedToneMessages(
                StandardTargets.Rec709Gamma24, GammaMode.WindowsDefault);

            Assert.Empty(messages);
        }

        [Fact]
        public void BuildMessages_HdrTargetUnderLiveRegrade_DoesNotWarnAboutComposedTone()
        {
            // PQ targets are corrected through the HDR branch, which never applies the
            // 1/2.2 encode this warning is about.
            var messages = ComposedToneMessages(
                StandardTargets.Rec709Pq, GammaMode.Gamma24, hdrActive: true);

            Assert.DoesNotContain(messages, m =>
                m.Message.Contains("composed shadow tone", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildMessages_UnknownLiveGammaMode_DoesNotWarnAboutComposedTone()
        {
            // MonitorInfo.CurrentGamma defaults to Gamma24 and MonitorManager never fills it
            // in, so "caller could not resolve the mode" must stay silent rather than warn
            // off a default. Passing the monitor alone, with no live mode, proves the check
            // does not fall back to reading the field.
            var messages = CalibrationInstallPreflight.BuildMessages(
                Monitor(hdrActive: false, sdrWhite: 200),
                Monitor(hdrActive: false, sdrWhite: 200),
                measuredHdrMode: false,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "default.icm",
                currentDefaultProfile: "default.icm",
                target: StandardTargets.Rec709Gamma24);

            Assert.Empty(messages);
        }

        /// <summary>
        /// Runs the preflight with everything else held steady (same display, same HDR state,
        /// same profile), so the only thing that can produce a message is the composed-path
        /// check.
        /// </summary>
        private static IReadOnlyList<(string Severity, string Message)> ComposedToneMessages(
            CalibrationTarget target,
            GammaMode liveGamma,
            bool hdrActive = false)
        {
            var measured = Monitor(hdrActive: hdrActive, sdrWhite: 200, hdrPeakNits: 1000);
            var current = Monitor(hdrActive: hdrActive, sdrWhite: 200, hdrPeakNits: 1000);

            return CalibrationInstallPreflight.BuildMessages(
                measured,
                current,
                measuredHdrMode: hdrActive,
                measuredSdrWhiteLevel: 200,
                measuredDefaultProfile: "default.icm",
                currentDefaultProfile: "default.icm",
                target: target,
                liveGammaMode: liveGamma);
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
