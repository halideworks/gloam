using System;
using System.Collections.Generic;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Last-chance checks before installing a measured calibration profile. Setup preflight
    /// catches bad starting conditions; this catches drift between measurement and install.
    /// </summary>
    public static class CalibrationInstallPreflight
    {
        public const string Error = "ERROR";
        public const string Warn = "WARN";

        public static IReadOnlyList<(string Severity, string Message)> BuildMessages(
            MonitorInfo measuredMonitor,
            MonitorInfo? currentMonitor,
            bool measuredHdrMode,
            double measuredSdrWhiteLevel,
            string? measuredDefaultProfile,
            string? currentDefaultProfile)
        {
            var messages = new List<(string Severity, string Message)>();

            if (currentMonitor == null)
            {
                messages.Add((Error,
                    "Gloam could not refresh this display before installing the profile. " +
                    "Re-open calibration after Windows display detection settles."));
                return messages;
            }

            if (!SameIdentity(measuredMonitor.MonitorDevicePath, currentMonitor.MonitorDevicePath))
            {
                messages.Add((Error,
                    "The refreshed display no longer matches the monitor that was measured. " +
                    "Do not install this calibration on a different physical display; re-open calibration after display detection settles."));
            }

            if (currentMonitor.IsHdrActive != measuredHdrMode)
            {
                messages.Add((Error, measuredHdrMode
                    ? "This calibration was measured with Windows HDR active, but the display is now in SDR. Turn HDR back on before installing."
                    : "This calibration was measured with Windows HDR off, but the display is now in HDR. Turn HDR off before installing or re-run calibration in HDR."));
            }

            if (measuredHdrMode)
            {
                double delta = Math.Abs(currentMonitor.SdrWhiteLevel - measuredSdrWhiteLevel);
                double threshold = Math.Max(10.0, measuredSdrWhiteLevel * 0.05);
                if (delta > threshold)
                {
                    messages.Add((Warn,
                        $"Windows SDR white changed from {measuredSdrWhiteLevel:F0} to {currentMonitor.SdrWhiteLevel:F0} nits since measurement. " +
                        "Install can continue, but re-measuring at the current SDR white level is more accurate."));
                }
            }

            if (!SameProfile(measuredDefaultProfile, currentDefaultProfile))
            {
                string listName = measuredHdrMode ? "Advanced Color profile" : "default color profile";
                messages.Add((Warn,
                    $"The Windows {listName} changed since measurement " +
                    $"({DisplayProfile(measuredDefaultProfile)} -> {DisplayProfile(currentDefaultProfile)}). " +
                    "Continuing will replace the current profile; cancel if another calibration tool just changed it."));
            }

            return messages;
        }

        private static bool SameProfile(string? left, string? right)
            => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

        private static string? Normalize(string? profile)
            => string.IsNullOrWhiteSpace(profile) ? null : profile.Trim();

        private static bool SameIdentity(string? measuredPath, string? currentPath)
        {
            string? measured = Normalize(measuredPath);
            string? current = Normalize(currentPath);
            return measured == null || current == null ||
                   string.Equals(measured, current, StringComparison.OrdinalIgnoreCase);
        }

        private static string DisplayProfile(string? profile)
            => string.IsNullOrWhiteSpace(profile) ? "none" : profile.Trim();
    }
}
