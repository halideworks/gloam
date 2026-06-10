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

            // SAFETY GATE: the shipped MHC2 templates are SDR (gamma 2.2). Installing an SDR
            // profile while the display is in HDR mode produces a washed-out, highlight-crushed
            // image (learned the hard way). Refuse the mismatch instead of wrecking the display.
            // HDR-native MHC2 generation is a separate, validated path.
            if (monitor.IsHdrActive)
                return new InstallResult(false, "",
                    "This display is currently in HDR mode, and only SDR calibration profiles are " +
                    "available to install. Applying an SDR profile in HDR would wash out the image.\n\n" +
                    "Switch the display to SDR to apply this calibration, or wait for the HDR-native " +
                    "calibration path. (Your measurements and the report are still valid.)");

            string? template = FindTemplate(whiteLevel);
            if (template == null)
                return new InstallResult(false, "", "No MHC2 template found to base the profile on.");

            double[,] matrix;
            try { matrix = Mhc2ProfileBuilder.BuildGamutMatrix(characterization, target); }
            catch (Exception ex) { return new InstallResult(false, "", $"Gamut matrix failed: {ex.Message}"); }

            // GAMUT GUARD: block only when the target gamut is wider than the panel can EMIT
            // (e.g. an sRGB-class display calibrated to Rec.2020). There the matrix has to
            // synthesize primaries the display can't produce, so a target primary maps to a
            // display drive value well above full-scale → clipping and a heavy cast (the magenta
            // we hit). NARROWING a slightly-wide panel to sRGB/Rec.709 is perfectly reachable
            // (drive values stay <= 1) and must NOT be blocked — that wrongly rejected a good
            // Gamma-2.4 calibration. A small overshoot from white-point correction is fine.
            double maxDrive = MaxTargetDrive(matrix);
            if (maxDrive > 1.3) // keep in sync with the setup-time EDID filter
                return new InstallResult(false, "",
                    $"The chosen target ('{target.Name}') needs primaries about {maxDrive:P0} of this " +
                    "display's maximum — i.e. a wider gamut than the panel can physically produce, so the " +
                    "correction would clip and cast color.\n\nCalibrate to a target the panel can reach — " +
                    "for an SDR display that's usually \"sRGB (Gamma 2.2)\" or Rec.709. Re-run with that selected.");

            string profileName = BuildProfileName(monitor, target);
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

        /// <summary>
        /// Largest display drive value the matrix demands for the target's primaries and white
        /// (content R/G/B = (1,0,0),(0,1,0),(0,0,1),(1,1,1)). &lt;= ~1 means every target color is
        /// reachable (including narrowing a wider panel); well above 1 means the target asks for
        /// primaries the display can't emit → it would clip and cast.
        /// </summary>
        private static double MaxTargetDrive(double[,] m)
        {
            double max = 0;
            (double, double, double)[] contents = { (1, 0, 0), (0, 1, 0), (0, 0, 1), (1, 1, 1) };
            foreach (var (a, b, c) in contents)
                for (int r = 0; r < 3; r++)
                    max = Math.Max(max, m[r, 0] * a + m[r, 1] * b + m[r, 2] * c);
            return max;
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

        /// <summary>
        /// Human-readable, explanatory, dated profile filename, e.g.
        /// "M27Q P - sRGB G2.2 - 2026-06-09 2245.icm". Sanitized for the file system; the
        /// trailing timestamp keeps each calibration distinct in Color Management.
        /// </summary>
        private static string BuildProfileName(MonitorInfo monitor, CalibrationTarget target)
        {
            string monitorName = Sanitize(string.IsNullOrWhiteSpace(monitor.FriendlyName) ? "Display" : monitor.FriendlyName);
            string targetName = Sanitize(ShortTargetName(target));
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HHmm");
            return $"{monitorName} - {targetName} - {stamp}.icm";
        }

        private static string ShortTargetName(CalibrationTarget t)
        {
            // Compact, recognizable label rather than the long descriptive Name.
            string n = t.Name ?? "Custom";
            return n.Replace("(", "").Replace(")", "").Replace("Gamma ", "G").Trim();
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
            s = s.Replace("  ", " ").Trim();
            return s.Length > 40 ? s[..40].Trim() : s;
        }

        private static string ShortHash(string input)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash)[..8];
        }
    }
}
