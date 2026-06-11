using Microsoft.Win32;
using System;
using HDRGammaController.Core;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace HDRGammaController.Services
{
    public static class StartupManager
    {
        private const string AppName = "HDRGammaController";
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
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
                    if (key == null) return;

                    if (value)
                    {
                        // Get the path to the current executable
                        string exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
                        
                        // Handle single-file apps (which extract to temp) - use the original path
                        if (exePath.Contains("\\Temp\\") || exePath.EndsWith(".dll"))
                        {
                            // Fallback to the entry assembly location
                            exePath = Process.GetCurrentProcess().MainModule?.FileName ?? exePath;
                        }
                        
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
    }
}
