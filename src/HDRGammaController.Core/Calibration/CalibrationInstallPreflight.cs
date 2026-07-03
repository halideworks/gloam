using System;
using System.Collections.Generic;
using System.IO;
using HDRGammaController.Interop;

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
            string? currentDefaultProfile,
            CalibrationTarget? target = null)
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

                AddHdrLuminanceMessages(messages, measuredMonitor, currentMonitor, target);
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

        private static void AddHdrLuminanceMessages(
            List<(string Severity, string Message)> messages,
            MonitorInfo measuredMonitor,
            MonitorInfo currentMonitor,
            CalibrationTarget? target)
        {
            if (currentMonitor.HdrPeakNits <= 0)
            {
                messages.Add((Warn,
                    "HDR peak luminance metadata is unavailable at install time. Re-measuring after Windows display detection settles is safer."));
                return;
            }

            if (currentMonitor.HdrMinNits < 0 ||
                (currentMonitor.HdrMinNits > 0 && currentMonitor.HdrMinNits >= currentMonitor.HdrPeakNits))
            {
                messages.Add((Warn,
                    $"Current HDR luminance metadata looks inconsistent ({currentMonitor.HdrMinNits:F3}-{currentMonitor.HdrPeakNits:F0} nits). " +
                    "Install can continue, but verify HDR tone tracking carefully."));
            }

            if (currentMonitor.HdrMaxFullFrameNits > 0 &&
                currentMonitor.HdrMaxFullFrameNits > currentMonitor.HdrPeakNits * 1.05)
            {
                messages.Add((Warn,
                    $"Current HDR full-frame metadata ({currentMonitor.HdrMaxFullFrameNits:F0} nits) exceeds peak metadata ({currentMonitor.HdrPeakNits:F0} nits). " +
                    "Confirm the display is reporting HDR data correctly before trusting highlight results."));
            }

            if (measuredMonitor.HdrPeakNits > 0)
            {
                double peakDelta = Math.Abs(currentMonitor.HdrPeakNits - measuredMonitor.HdrPeakNits);
                double threshold = Math.Max(50.0, measuredMonitor.HdrPeakNits * 0.10);
                if (peakDelta > threshold)
                {
                    messages.Add((Warn,
                        $"HDR peak metadata changed from {measuredMonitor.HdrPeakNits:F0} to {currentMonitor.HdrPeakNits:F0} nits since measurement. " +
                        "Install can continue, but re-measuring against the current HDR state is more accurate."));
                }
            }

            if (target?.TransferFunction != TransferFunctionType.Pq)
                return;

            if (target.PeakLuminance is { } targetPeak && targetPeak > currentMonitor.HdrPeakNits * 1.10)
            {
                messages.Add((Warn,
                    $"The measured HDR target peaks at {targetPeak:F0} nits, above the display's current reported {currentMonitor.HdrPeakNits:F0}-nit peak. " +
                    "Highlights above the panel limit will be preserved or roll off rather than fully corrected."));
            }

            if (target.ReferenceWhite is { } referenceWhite && referenceWhite > currentMonitor.HdrPeakNits * 0.90)
            {
                messages.Add((Warn,
                    $"The measured HDR reference white ({referenceWhite:F0} nits) is close to the display's current reported peak ({currentMonitor.HdrPeakNits:F0} nits). " +
                    "Re-measure with lower Windows SDR content brightness or a brighter HDR display for reliable desktop calibration."));
            }
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

        #region Foreign-correction preflight (pre-measurement)

        /// <summary>
        /// A non-Gloam Windows default color profile found on the target display before the
        /// native characterization pass. When it carries an MHC2 tag, the compositor is
        /// applying someone else's correction and "native" measurements would be taken
        /// THROUGH it — the resulting profile would then double-correct the panel.
        /// </summary>
        public sealed record ForeignDefaultProfile(
            string ProfileName,
            bool IsAdvancedColor,
            bool HasMhc2Tag);

        /// <summary>
        /// Pure decision logic: which current default profiles (SDR association and
        /// Advanced-Color association) are foreign, i.e. not produced by this app for the
        /// monitor. Gloam's own profiles are excluded — the measurement path already
        /// disables those separately. <paramref name="hasMhc2Tag"/> is injected so the
        /// decision is unit-testable without a color store.
        /// </summary>
        public static IReadOnlyList<ForeignDefaultProfile> AssessForeignDefaults(
            string? sdrDefaultProfile,
            string? advancedColorDefaultProfile,
            string? gloamProfilePrefix,
            Func<string, bool> hasMhc2Tag)
        {
            var result = new List<ForeignDefaultProfile>();

            void Consider(string? name, bool isAdvancedColor)
            {
                string? trimmed = Normalize(name);
                if (trimmed == null) return;
                if (!string.IsNullOrWhiteSpace(gloamProfilePrefix) &&
                    trimmed.StartsWith(gloamProfilePrefix, StringComparison.OrdinalIgnoreCase))
                    return; // ours; the Gloam disable path owns it
                if (result.Exists(r =>
                        string.Equals(r.ProfileName, trimmed, StringComparison.OrdinalIgnoreCase) &&
                        r.IsAdvancedColor == isAdvancedColor))
                    return;

                bool mhc2 = false;
                try { mhc2 = hasMhc2Tag(trimmed); }
                catch { /* cannot inspect -> treat as no tag; warn-only path */ }
                result.Add(new ForeignDefaultProfile(trimmed, isAdvancedColor, mhc2));
            }

            Consider(sdrDefaultProfile, isAdvancedColor: false);
            Consider(advancedColorDefaultProfile, isAdvancedColor: true);
            return result;
        }

        /// <summary>
        /// Cheap ICC tag-table scan for an 'MHC2' tag. ICC layout: 128-byte header, then a
        /// big-endian uint32 tag count, then tagCount 12-byte entries of
        /// [signature u32][offset u32][size u32]. Only the signatures are inspected — the
        /// question here is "does Windows apply a compositor correction from this profile",
        /// not whether the tag content is valid. Returns false for malformed/short data.
        /// </summary>
        public static bool ContainsMhc2Tag(byte[]? profileBytes)
        {
            const int iccHeaderSize = 128;
            if (profileBytes == null || profileBytes.Length < iccHeaderSize + 4) return false;

            uint tagCount = ReadU32BE(profileBytes, iccHeaderSize);
            if (tagCount == 0 || tagCount > 4096) return false;

            for (int i = 0; i < tagCount; i++)
            {
                int entry = iccHeaderSize + 4 + i * 12;
                if (entry + 4 > profileBytes.Length) return false;
                if (profileBytes[entry] == (byte)'M' &&
                    profileBytes[entry + 1] == (byte)'H' &&
                    profileBytes[entry + 2] == (byte)'C' &&
                    profileBytes[entry + 3] == (byte)'2')
                    return true;
            }
            return false;
        }

        private static uint ReadU32BE(byte[] data, int offset) =>
            (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

        /// <summary>
        /// Reads a profile from the Windows color store and scans it for an MHC2 tag.
        /// Any failure (missing file, unreadable store, malformed profile) returns false —
        /// detection must never block calibration.
        /// </summary>
        public static bool ProfileHasMhc2Tag(string profileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(profileName)) return false;
                string? path = ResolveColorStorePath(profileName.Trim());
                if (path == null) return false;
                return ContainsMhc2Tag(File.ReadAllBytes(path));
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationInstallPreflight: MHC2 scan of '{profileName}' failed: {ex.Message}");
                return false;
            }
        }

        private static string? ResolveColorStorePath(string profileName)
        {
            // Association lists store bare filenames; resolve against the color store.
            if (Path.IsPathRooted(profileName) && File.Exists(profileName))
                return profileName;

            try
            {
                uint size = 1024; // bytes
                var sb = new System.Text.StringBuilder((int)size / 2);
                if (Wcs.GetColorDirectory(null, sb, ref size))
                {
                    string candidate = Path.Combine(sb.ToString(), profileName);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { /* fall through to the well-known location */ }

            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "spool", "drivers", "color", profileName);
            return File.Exists(fallback) ? fallback : null;
        }

        #endregion

        #region Night Light detection

        /// <summary>Registry key (under HKCU) holding the Night Light state blob.</summary>
        public const string NightLightStateKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current" +
            @"\default$windows.data.bluelightreduction.bluelightreductionstate" +
            @"\windows.data.bluelightreduction.bluelightreductionstate";

        /// <summary>Hard warning shown when Night Light is confidently detected as active.</summary>
        public const string NightLightWarning =
            "Windows Night Light is active and will corrupt every measurement — disable it before calibrating.";

        /// <summary>
        /// Pure parse of the CloudStore "Data" blob for the Night Light state.
        ///
        /// FORMAT ASSUMPTION (undocumented, reverse-engineered — see the community
        /// night-light togglers, e.g. Rafael Rivera's gist and superuser.com/a/1209192):
        /// the blob is a CloudStore record with a fixed preamble and a varint timestamp;
        /// the byte at index 18 encodes the on/off state field header. Observed across
        /// Windows 10 1903 – Windows 11 23H2 builds:
        ///   data[18] == 0x15 -> Night Light ON  (the ON blob is also 2 bytes longer:
        ///                       an extra 0x10 0x00 field is inserted after the state)
        ///   data[18] == 0x13 -> Night Light OFF
        /// Because this is a heuristic over an undocumented format, anything else — other
        /// byte values, short blobs, missing value — returns null ("cannot determine"),
        /// and callers only warn when the answer is confidently true.
        /// </summary>
        public static bool? IsNightLightActiveBlob(byte[]? data)
        {
            if (data == null || data.Length < 24) return null;
            return data[18] switch
            {
                0x15 => true,
                0x13 => false,
                _ => null,
            };
        }

        /// <summary>
        /// Reads the Night Light state from the registry. Null = unknown (missing key,
        /// unexpected blob layout, or any read failure) — never blocks calibration.
        /// </summary>
        public static bool? DetectNightLightActive()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NightLightStateKeyPath);
                return IsNightLightActiveBlob(key?.GetValue("Data") as byte[]);
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationInstallPreflight: Night Light detection failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region SDR Auto Color Management detection

        /// <summary>
        /// Detects Windows 11 SDR "Auto Color Management" (ACM) on the display. ACM
        /// re-renders SDR through a color pipeline, so measuring without knowing means the
        /// characterization has the wrong basis. DisplayConfig reports
        /// advancedColorEnabled for BOTH real HDR and SDR ACM; when the display is in HDR
        /// this returns false (HDR is handled by the HDR-mode paths, not an ACM concern).
        /// Null = cannot determine.
        /// </summary>
        public static bool? DetectSdrAutoColorManagement(string? gdiDeviceName, bool hdrActive)
        {
            if (hdrActive) return false;
            if (string.IsNullOrEmpty(gdiDeviceName)) return null;
            try
            {
                if (!DisplayConfig.TryGetAdvancedColorInfo(
                        gdiDeviceName, out _, out bool enabled, out bool forceDisabled))
                    return null;
                if (forceDisabled) return false;
                return enabled;
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationInstallPreflight: ACM detection failed: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
