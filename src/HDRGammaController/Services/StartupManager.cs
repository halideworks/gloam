using Microsoft.Win32;
using System;
using HDRGammaController.Core;
using System.Diagnostics;

namespace HDRGammaController.Services
{
    public static class StartupManager
    {
        private const string AppName = "Gloam";
        // Pre-rebrand value name; pointed at HDRGammaController.exe, which no longer
        // exists after the rebrand, so it is migrated to the new name on sight.
        private const string LegacyAppName = "HDRGammaController";
        private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>
        /// Gets or sets whether the app is configured to start with Windows.
        /// </summary>
        public static bool IsStartupEnabled
        {
            get
            {
                try
                {
                    MigrateLegacyValue();
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
                    return key?.GetValue(AppName) != null;
                }
                catch
                {
                    return false;
                }
            }
            set
            {
                try
                {
                    MigrateLegacyValue();
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
                    if (key == null) return;

                    if (value)
                    {
                        string exePath = GetExePath();
                        key.SetValue(AppName, $"\"{exePath}\"");
                        Log.Info($"StartupManager: Enabled startup with path: {exePath}");
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                        Log.Info("StartupManager: Disabled startup");
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"StartupManager: Error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// One-time migration of the old HDRGammaController Run value: if present,
        /// remove it and (since it indicated startup was enabled) re-register the
        /// current executable under the Gloam name.
        /// </summary>
        private static void MigrateLegacyValue()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
                if (key == null) return;
                if (key.GetValue(LegacyAppName) == null) return;

                key.DeleteValue(LegacyAppName, false);
                if (key.GetValue(AppName) == null)
                {
                    string exePath = GetExePath();
                    key.SetValue(AppName, $"\"{exePath}\"");
                    Log.Info($"StartupManager: Migrated startup registration {LegacyAppName} -> {AppName} ({exePath})");
                }
                else
                {
                    Log.Info($"StartupManager: Removed stale {LegacyAppName} startup registration");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"StartupManager: Legacy startup migration error: {ex.Message}");
            }
        }

        private static string GetExePath()
        {
            // Environment.ProcessPath points at the original host executable for both
            // framework-dependent and self-contained single-file publishes.
            return Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine the application executable path.");
        }
    }
}
