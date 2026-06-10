using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HDRGammaController.Interop;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Generates a calibrated MHC2 ICC profile (gamut matrix + per-channel tone LUTs) and
    /// installs it as a monitor's default Windows color profile, so the Desktop Window
    /// Manager applies the correction natively and persistently — across reboots and in HDR —
    /// with no .cube export and no per-frame work from this app. The GPU gamma ramp is then
    /// free to layer night-mode/gamma on top (see the apply path's calibration-aware mode).
    /// </summary>
    public static class CalibrationProfileInstaller
    {
        // White levels for which an MHC2 template is shipped/extracted.
        private static readonly int[] TemplateWhiteLevels = { 100, 200, 300, 400 };

        public sealed record InstallResult(bool Success, string ProfileName, string? Error);

        /// <summary>
        /// Builds and installs the calibrated profile for <paramref name="monitor"/>. Returns
        /// the installed profile filename so callers can record it (and later revert).
        /// </summary>
        public static InstallResult Install(
            MonitorInfo monitor,
            DisplayCharacterization characterization,
            CalibrationTarget target,
            double[] lutR, double[] lutG, double[] lutB,
            double whiteLevel)
        {
            if (string.IsNullOrEmpty(monitor.MonitorDevicePath))
                return new InstallResult(false, "", "Monitor has no device path; cannot associate a profile.");

            string? template = FindTemplate(whiteLevel);
            if (template == null)
                return new InstallResult(false, "", "No MHC2 template found to base the profile on.");

            double[,] matrix;
            try { matrix = Mhc2ProfileBuilder.BuildGamutMatrix(characterization, target); }
            catch (Exception ex) { return new InstallResult(false, "", $"Gamut matrix failed: {ex.Message}"); }

            string profileName = $"HDRCal_{ShortHash(monitor.MonitorDevicePath)}.icm";
            string srcPath = Path.Combine(Path.GetTempPath(), profileName);

            try
            {
                Mhc2ProfileBuilder.Build(template, srcPath, matrix, lutR, lutG, lutB);

                // Copy into the system color store. Returns false if an identical name already
                // exists — harmless, we re-associate below regardless.
                if (!Wcs.InstallColorProfile(null, srcPath))
                    Log.Info($"CalibrationProfileInstaller: InstallColorProfile returned false for {profileName} (may already exist).");

                if (!Wcs.AssociateColorProfileWithDevice(null, srcPath, monitor.MonitorDevicePath))
                    return new InstallResult(false, profileName,
                        "Windows refused to associate the profile with the display. Make sure the monitor is active.");

                if (!Wcs.WcsSetDefaultColorProfile(
                        Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                        monitor.MonitorDevicePath, Wcs.CPT_ICC, Wcs.CPST_PERCEPTUAL, 0, profileName))
                    Log.Info($"CalibrationProfileInstaller: WcsSetDefaultColorProfile returned false for {profileName}.");

                Log.Info($"CalibrationProfileInstaller: Installed + set default '{profileName}' for {monitor.FriendlyName}.");
                return new InstallResult(true, profileName, null);
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationProfileInstaller: Install failed: {ex.Message}");
                return new InstallResult(false, profileName, ex.Message);
            }
            finally
            {
                try { if (File.Exists(srcPath)) File.Delete(srcPath); } catch { }
            }
        }

        /// <summary>Removes a previously-installed calibration profile from a monitor.</summary>
        public static void Uninstall(MonitorInfo monitor, string profileName)
        {
            if (string.IsNullOrEmpty(monitor.MonitorDevicePath) || string.IsNullOrEmpty(profileName)) return;
            try
            {
                Wcs.DisassociateColorProfileFromDevice(null, profileName, monitor.MonitorDevicePath);
                Wcs.UninstallColorProfile(null, profileName, true);
                Log.Info($"CalibrationProfileInstaller: Removed '{profileName}' from {monitor.FriendlyName}.");
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationProfileInstaller: Uninstall failed: {ex.Message}");
            }
        }

        private static string? FindTemplate(double whiteLevel)
        {
            int nearest = TemplateWhiteLevels.OrderBy(w => Math.Abs(w - whiteLevel)).First();
            string fileName = $"srgb_to_gamma2p2_{nearest}_mhc2.icm";
            foreach (var dir in CandidateDirectories())
            {
                string p = Path.Combine(dir, fileName);
                if (File.Exists(p)) return p;
            }
            Log.Error($"CalibrationProfileInstaller: template {fileName} not found near {AppContext.BaseDirectory}.");
            return null;
        }

        private static System.Collections.Generic.IEnumerable<string> CandidateDirectories()
        {
            // ResourceExtractor drops the templates next to the executable; walk up too so dev
            // runs (bin/Debug/...) find the repo-root copies.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                yield return dir.FullName;
        }

        private static string ShortHash(string input)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash)[..8];
        }
    }
}
