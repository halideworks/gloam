using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Startup repair for Gloam-owned HDR profiles created before the ICC-safe
    /// characterization fix. It is deliberately conservative: only a profile recorded in
    /// this monitor's settings, with Gloam's monitor-name prefix and a valid MHC2 payload,
    /// can be rewritten. Safe profiles are still checked against Windows' active default so
    /// a profile stranded in an inactive per-user list is restored without recalibration.
    /// </summary>
    public sealed class AdvancedColorProfileMigrationService
    {
        public sealed record Result(int Inspected, int Repaired, int Reactivated, int Deferred, int Failed);

        private readonly SettingsManager _settings;
        private readonly Func<IReadOnlyList<MonitorInfo>> _enumerateMonitors;

        public AdvancedColorProfileMigrationService(SettingsManager settings, MonitorManager monitorManager)
            : this(settings, () => monitorManager.EnumerateMonitors())
        {
        }

        internal AdvancedColorProfileMigrationService(
            SettingsManager settings, Func<IReadOnlyList<MonitorInfo>> enumerateMonitors)
        {
            _settings = settings;
            _enumerateMonitors = enumerateMonitors;
        }

        public Result Run()
        {
            int inspected = 0, repaired = 0, reactivated = 0, deferred = 0, failed = 0;
            string? settingsBackup = null;

            IReadOnlyList<MonitorInfo> monitors;
            try { monitors = _enumerateMonitors(); }
            catch (Exception ex)
            {
                Log.Info($"AdvancedColorProfileMigration: monitor enumeration failed: {ex.Message}");
                return new Result(0, 0, 0, 0, 1);
            }

            foreach (var monitor in monitors)
            {
                var saved = _settings.GetMonitorProfile(monitor.MonitorDevicePath);
                string? profileName = saved?.Mhc2ProfileName;
                if (string.IsNullOrWhiteSpace(profileName)) continue;
                inspected++;

                if (!monitor.IsHdrActive)
                {
                    deferred++;
                    Log.Info($"AdvancedColorProfileMigration: deferred '{profileName}' on {monitor.FriendlyName}; HDR is off.");
                    continue;
                }

                profileName = Path.GetFileName(profileName);
                string prefix = CalibrationProfileInstaller.BuildProfileNamePrefix(monitor);
                if (!profileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    deferred++;
                    Log.Info($"AdvancedColorProfileMigration: skipped non-Gloam profile '{profileName}'.");
                    continue;
                }

                var platform = AdvancedColorProfileAssociation.Platform;
                string profilePath = Path.Combine(platform.ColorStoreDirectory, profileName);
                if (!File.Exists(profilePath))
                {
                    failed++;
                    Log.Error($"AdvancedColorProfileMigration: saved profile is missing from the color store: {profileName}");
                    continue;
                }

                bool needsRepair;
                try { needsRepair = Mhc2ProfileBuilder.NeedsAdvancedColorIccCharacterizationRepair(profilePath); }
                catch (Exception ex)
                {
                    failed++;
                    Log.Error($"AdvancedColorProfileMigration: refused to inspect '{profileName}': {ex.Message}");
                    continue;
                }

                if (!needsRepair)
                {
                    if (AdvancedColorProfileAssociation.TryIsVerifiedCurrentUserDefault(
                            monitor, profileName, out bool isActive, out _) && isActive)
                        continue;

                    if (CalibrationProfileInstaller.Reenable(monitor, profileName, hdrMode: true))
                    {
                        reactivated++;
                        Log.Info($"AdvancedColorProfileMigration: restored active HDR default '{profileName}' on {monitor.FriendlyName}.");
                    }
                    else
                    {
                        failed++;
                        Log.Error($"AdvancedColorProfileMigration: could not reactivate safe profile '{profileName}'.");
                    }
                    continue;
                }

                if (settingsBackup == null)
                {
                    settingsBackup = _settings.TryCreateBackup("pre-1.7.8-profile-migration");
                    if (_settings.LoadedExistingSettingsFile && settingsBackup == null)
                    {
                        failed++;
                        Log.Error("AdvancedColorProfileMigration: settings backup failed; legacy profile repair was not attempted.");
                        continue;
                    }
                }

                string repairedName = BuildRepairedProfileName(profileName);
                var repair = CalibrationProfileInstaller.RepairAdvancedColorProfile(
                    monitor, profileName, repairedName);
                if (!repair.Success)
                {
                    failed++;
                    Log.Error($"AdvancedColorProfileMigration: repair failed for '{profileName}': {repair.Error}");
                    continue;
                }

                if (!_settings.TryReplaceMhc2Calibration(
                        monitor.MonitorDevicePath, profileName, repairedName))
                {
                    // The profile transaction succeeded but settings did not. Put the old
                    // profile back before returning so Windows and settings never disagree.
                    CalibrationProfileInstaller.RestoreDefaultProfile(
                        monitor, profileName, hdrMode: true);
                    CalibrationProfileInstaller.Disable(monitor, repairedName);
                    failed++;
                    Log.Error($"AdvancedColorProfileMigration: settings update failed for '{profileName}'; association rolled back.");
                    continue;
                }

                repaired++;
                Log.Info($"AdvancedColorProfileMigration: repaired '{profileName}' as '{repairedName}' without changing MHC2 calibration data.");
            }

            var result = new Result(inspected, repaired, reactivated, deferred, failed);
            Log.Info($"AdvancedColorProfileMigration: inspected {inspected}, repaired {repaired}, " +
                     $"reactivated {reactivated}, deferred {deferred}, failed {failed}.");
            return result;
        }

        internal static string BuildRepairedProfileName(string existingProfileName)
        {
            string stem = Path.GetFileNameWithoutExtension(existingProfileName);
            const string marker = " ICC-safe ";
            int markerIndex = stem.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0) stem = stem[..markerIndex];
            stem = stem.Trim();
            const string suffix = " ICC-safe 1.7.8.icm";
            int maxStem = Math.Max(1, 180 - suffix.Length);
            if (stem.Length > maxStem) stem = stem[..maxStem].Trim();
            return stem + suffix;
        }
    }
}
