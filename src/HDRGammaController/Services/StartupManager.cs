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
                TrySetStartupEnabled(value);
            }
        }

        public static bool TrySetStartupEnabled(bool value)
        {
            try
            {
                MigrateLegacyValue();
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
                if (key == null) return false;

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

                return true;
            }
            catch (Exception ex)
            {
                Log.Info($"StartupManager: Error: {ex.Message}");
                return false;
            }
        }

        public static void EnableByDefaultForFreshInstall(SettingsManager settings, bool isInstalled)
        {
            if (settings == null) return;
            if (!isInstalled) return;
            if (settings.LoadedExistingSettingsFile) return;
            if (settings.StartupDefaultApplied) return;

            if (TrySetStartupEnabled(true))
            {
                settings.MarkStartupDefaultApplied();
                Log.Info("StartupManager: Applied fresh-install default startup registration.");
            }
        }

        /// <summary>
        /// Self-heal for a stale Run value: if the registered "Gloam" startup path points
        /// anywhere other than the current executable (e.g. a legacy pre-Velopack install
        /// under Program Files, or a since-deleted exe), rewrite it to the current exe.
        /// Only an INSTALLED build may do this — a portable/dev run must never steal the
        /// startup registration from the real install (mirrors the isInstalled gate on
        /// <see cref="EnableByDefaultForFreshInstall"/>).
        /// </summary>
        public static void RepairIfStale(bool isInstalled)
        {
            if (!isInstalled) return;

            try
            {
                MigrateLegacyValue();
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
                if (key == null) return;

                var registered = key.GetValue(AppName) as string;
                if (registered == null) return; // startup not enabled; nothing to repair

                string currentExePath = GetExePath();
                if (!IsRegisteredPathStale(registered, currentExePath)) return;

                key.SetValue(AppName, $"\"{currentExePath}\"");
                Log.Info($"StartupManager: Repaired stale startup registration {registered} -> \"{currentExePath}\"");
            }
            catch (Exception ex)
            {
                Log.Info($"StartupManager: Startup repair error: {ex.Message}");
            }
        }

        /// <summary>
        /// Pure decision function for <see cref="RepairIfStale"/> (registry access is not
        /// unit-testable). True when the registered Run value does not resolve to the
        /// current executable path. Comparison is case-insensitive and tolerates
        /// surrounding quotes on either side.
        /// </summary>
        internal static bool IsRegisteredPathStale(string? registeredValue, string? currentExePath)
        {
            if (string.IsNullOrWhiteSpace(registeredValue)) return false;
            if (string.IsNullOrWhiteSpace(currentExePath)) return false;

            string registeredPath = ExtractExePath(registeredValue);
            if (string.IsNullOrWhiteSpace(registeredPath)) return false;

            string currentPath = ExtractExePath(currentExePath);
            return !string.Equals(registeredPath, currentPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts the executable path from a Run-key value: strips surrounding quotes
        /// (returning only the quoted segment, so trailing arguments are ignored) and trims
        /// whitespace. An unquoted value is returned trimmed as-is.
        /// </summary>
        internal static string ExtractExePath(string registryValue)
        {
            string trimmed = registryValue.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"')
            {
                int closing = trimmed.IndexOf('"', 1);
                if (closing > 1)
                    return trimmed.Substring(1, closing - 1).Trim();
                return trimmed.Trim('"').Trim();
            }

            return trimmed;
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
