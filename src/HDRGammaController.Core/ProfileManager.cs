using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HDRGammaController.Interop;

namespace HDRGammaController.Core
{
    public class ProfileManager
    {
        private string _templatePath;
        
        public ProfileManager(string templatePath)
        {
            _templatePath = templatePath;
        }

        /// <summary>
        /// Generates an MHC2 profile and persists it using WCS.
        /// </summary>
        public void ApplyProfile(MonitorInfo monitor, GammaMode mode, double whiteLevel)
        {
            if (!File.Exists(_templatePath)) throw new FileNotFoundException("MHC2 template not found", _templatePath);
            if (string.IsNullOrEmpty(monitor.MonitorDevicePath)) throw new InvalidOperationException("Monitor device path is missing");

            // 1. Generate new LUT
            double[] lut = LutGenerator.GenerateLut(mode, whiteLevel);

            // 2. Generate Unique Filename
            // We use a hash of the MonitorID to keep it unique per monitor but consistent.
            string monitorHash = ComputeHash(monitor.MonitorDevicePath);
            string profileName = $"HDRGamma_{monitorHash}_{mode}_{whiteLevel:F0}.icm";

            string tempDir = Path.GetTempPath();
            string sourcePath = Path.Combine(tempDir, profileName);

            try
            {
                // 3. Patch Template
                ProfileTemplatePatching.PatchProfile(_templatePath, sourcePath, lut);

                // 4. Install Profile (copies to spool\drivers\color)
                // Note: If profile with same name exists, this might fail or overwrite.
                // Documentation says: "installs ... for use by the current user".
                // We verify result.
                if (!Wcs.InstallColorProfile(null, sourcePath))
                {
                    // If it fails, it might be in use or access denied.
                    // We try to associate anyway if it's already there.
                    // But usually we want to ensure our new version is used.
                    // Ideally we uninstall old one first?
                    // For now, assume success or harmless failure if identical.
                    Log.Info($"ProfileManager: InstallColorProfile returned false for {profileName}");
                }

                // 5. Associate Profile with Monitor
                // Requires Device ID (\\?\DISPLAY#...)
                if (!Wcs.AssociateColorProfileWithDevice(null, sourcePath, monitor.MonitorDevicePath))
                {
                    // Failed to associate
                    throw new Exception("Failed to associate profile with device. Ensure monitor is active.");
                }

                // 6. Set as Default
                // Use WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER
                if (!Wcs.WcsSetDefaultColorProfile(
                    Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                    monitor.MonitorDevicePath,
                    Wcs.CPT_ICC,
                    Wcs.CPST_PERCEPTUAL,
                    0,
                    profileName // Just the filename, not path
                    ))
                {
                    Log.Info($"ProfileManager: WcsSetDefaultColorProfile returned false for {profileName}");
                }
            }
            finally
            {
                // SECURITY: Always cleanup temp file, even on error
                if (File.Exists(sourcePath))
                {
                    try { File.Delete(sourcePath); }
                    catch (Exception ex) { Log.Info($"ProfileManager: Failed to cleanup temp file: {ex.Message}"); }
                }
            }
        }

        private string ComputeHash(string input)
        {
            using (var md5 = MD5.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(input);
                byte[] hash = md5.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 8);
            }
        }
    }
}
