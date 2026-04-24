using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Helper class for managing ArgyllCMS USB driver installation.
    /// </summary>
    public static class UsbDriverHelper
    {
        /// <summary>
        /// Gets the path to the ArgyllCMS USB driver installer if available.
        /// </summary>
        public static string? GetDriverInstallerPath()
        {
            // Check our downloaded ArgyllCMS first
            string localArgyllDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HDRGammaController", "Argyll");

            string installerPath = Path.Combine(localArgyllDir, "usb", "ArgyllCMS_install_USB.exe");
            if (File.Exists(installerPath))
                return installerPath;

            // Check standard installation paths
            var searchPaths = new[]
            {
                @"C:\Program Files\ArgyllCMS",
                @"C:\Program Files (x86)\ArgyllCMS",
                @"C:\Program Files\Argyll_V3.3.0",
                @"C:\ArgyllCMS"
            };

            foreach (var basePath in searchPaths)
            {
                installerPath = Path.Combine(basePath, "usb", "ArgyllCMS_install_USB.exe");
                if (File.Exists(installerPath))
                    return installerPath;
            }

            return null;
        }

        /// <summary>
        /// Gets the path to the ArgyllCMS USB driver uninstaller if available.
        /// </summary>
        public static string? GetDriverUninstallerPath()
        {
            string? installerPath = GetDriverInstallerPath();
            if (installerPath == null)
                return null;

            string uninstallerPath = Path.Combine(
                Path.GetDirectoryName(installerPath)!,
                "ArgyllCMS_uninstall_USB.exe");

            return File.Exists(uninstallerPath) ? uninstallerPath : null;
        }

        /// <summary>
        /// Checks if the driver installer is available.
        /// </summary>
        public static bool IsDriverInstallerAvailable() => GetDriverInstallerPath() != null;

        /// <summary>
        /// Launches the ArgyllCMS USB driver installer.
        /// </summary>
        /// <param name="waitForExit">If true, waits for the installer to complete.</param>
        /// <returns>True if the installer was launched successfully.</returns>
        public static bool LaunchDriverInstaller(bool waitForExit = true)
        {
            string? installerPath = GetDriverInstallerPath();
            if (installerPath == null)
                return false;

            try
            {
                var psi = new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                    Verb = "runas" // Request admin elevation
                };

                var process = Process.Start(psi);
                if (process == null)
                    return false;

                if (waitForExit)
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to launch driver installer: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Launches the driver installer asynchronously.
        /// </summary>
        /// <returns>True if the installer completed successfully.</returns>
        public static async Task<bool> LaunchDriverInstallerAsync()
        {
            string? installerPath = GetDriverInstallerPath();
            if (installerPath == null)
                return false;

            try
            {
                var psi = new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                    Verb = "runas" // Request admin elevation
                };

                var process = Process.Start(psi);
                if (process == null)
                    return false;

                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to launch driver installer: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks whether an error really indicates the ArgyllCMS USB driver is missing.
        /// Must be narrow: false positives here cause the app to prompt for reinstalling a
        /// driver that is already installed, which is confusing and unhelpful.
        /// </summary>
        /// <remarks>
        /// Things that are NOT driver errors even though they appear in spotread output:
        ///   - "SetCommState failed" / "LastError 31": benign serial-port enumeration
        ///     noise printed on every spotread invocation regardless of colorimeter state.
        ///   - "err 32" / "LastError 32" / "sharing violation": ERROR_SHARING_VIOLATION —
        ///     another process holds the HID handle. Reinstalling the driver doesn't help;
        ///     the user needs to close the holder or unplug/replug.
        /// </remarks>
        public static bool IsDriverError(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
                return false;

            // Genuine driver-missing signals from ArgyllCMS / libusb:
            //  - "Couldn't open USB device" (libusb_open failed without the Argyll driver)
            //  - "Driver not found" / "No driver installed"
            //  - libusb0/winusb specific failures
            return errorMessage.Contains("libusb", StringComparison.OrdinalIgnoreCase) ||
                   errorMessage.Contains("winusb", StringComparison.OrdinalIgnoreCase) ||
                   errorMessage.Contains("driver not found", StringComparison.OrdinalIgnoreCase) ||
                   errorMessage.Contains("no driver installed", StringComparison.OrdinalIgnoreCase) ||
                   errorMessage.Contains("could not open USB", StringComparison.OrdinalIgnoreCase) ||
                   errorMessage.Contains("couldn't open USB", StringComparison.OrdinalIgnoreCase);
        }
    }
}
