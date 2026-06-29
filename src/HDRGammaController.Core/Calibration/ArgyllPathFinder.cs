using System;
using System.Collections.Generic;
using System.IO;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Unified utility class for finding ArgyllCMS installation paths.
    /// Centralizes path detection logic to ensure consistent behavior across the application.
    /// </summary>
    public static class ArgyllPathFinder
    {
        /// <summary>
        /// Finds the ArgyllCMS bin directory containing spotread.exe and dispwin.exe.
        /// </summary>
        /// <returns>Path to the bin directory, or null if not found.</returns>
        public static string? FindArgyllBinPath()
        {
            // PRIORITY 1: Application's argyll_cache (bundled/preferred version - usually newest)
            foreach (var path in SearchApplicationArgyllCache())
            {
                if (IsValidArgyllBinPath(path))
                    return path;
            }

            // PRIORITY 2: Standard installation paths
            foreach (var path in GetStandardSearchPaths())
            {
                if (IsValidArgyllBinPath(path))
                    return path;
            }

            // PRIORITY 3: Search versioned directories in LocalAppData
            foreach (var path in SearchLocalAppDataVersioned())
            {
                if (IsValidArgyllBinPath(path))
                    return path;
            }

            // PRIORITY 4: Search DisplayCAL bundled ArgyllCMS (last resort - may be old).
            // Deliberately do not search PATH: calibration executes spotread.exe, so discovery
            // is restricted to app-managed and known install locations to avoid binary planting.
            foreach (var path in SearchDisplayCalArgyll())
            {
                if (IsValidArgyllBinPath(path))
                    return path;
            }

            return null;
        }

        /// <summary>
        /// Checks if a directory is a valid ArgyllCMS bin directory.
        /// </summary>
        private static bool IsValidArgyllBinPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return false;

            // Check for spotread.exe (primary colorimeter tool)
            return File.Exists(Path.Combine(path, "spotread.exe")) ||
                   File.Exists(Path.Combine(path, "spotread"));
        }

        /// <summary>
        /// Gets standard installation paths to search.
        /// </summary>
        private static IEnumerable<string> GetStandardSearchPaths()
        {
            // User's local app data (auto-downloaded location)
            yield return Path.Combine(AppPaths.DataDir, "Argyll", "bin");

            // Standard ArgyllCMS installations
            yield return @"C:\Program Files\ArgyllCMS\bin";
            yield return @"C:\Program Files (x86)\ArgyllCMS\bin";
            yield return @"C:\Program Files\Argyll\bin";
            yield return @"C:\Program Files (x86)\Argyll\bin";
            yield return @"C:\ArgyllCMS\bin";
            yield return @"C:\Argyll\bin";

            // Version-specific paths (common installation patterns)
            yield return @"C:\Program Files\Argyll_V3.5.0\bin";
            yield return @"C:\Program Files\Argyll_V3.3.0\bin";
            yield return @"C:\Program Files\Argyll_V3.2.0\bin";
            yield return @"C:\Program Files\Argyll_V3.1.0\bin";
            yield return @"C:\Program Files (x86)\Argyll_V3.5.0\bin";
            yield return @"C:\Program Files (x86)\Argyll_V3.3.0\bin";

            // DisplayCAL's bundled Argyll in Program Files
            yield return @"C:\Program Files\DisplayCAL\Argyll\bin";
            yield return @"C:\Program Files (x86)\DisplayCAL\Argyll\bin";

            // DisplayCAL's roaming app data download location. The wildcard search in
            // SearchDisplayCalArgyll() (priority 5) covers arbitrary versions; this is
            // just a fast direct hit for the common ones.
            string dcDl = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DisplayCAL", "dl");
            yield return Path.Combine(dcDl, "Argyll_V3.5.0", "bin");
            yield return Path.Combine(dcDl, "Argyll_V3.3.0", "bin");
        }

        /// <summary>
        /// Searches DisplayCAL installation directories for bundled ArgyllCMS.
        /// </summary>
        private static IEnumerable<string> SearchDisplayCalArgyll()
        {
            string[] programDirs =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (var programDir in programDirs)
            {
                if (string.IsNullOrEmpty(programDir))
                    continue;

                string displayCalDir = Path.Combine(programDir, "DisplayCAL");
                if (!Directory.Exists(displayCalDir))
                    continue;

                // Search for Argyll_* directories
                string[] argyllDirs;
                try
                {
                    argyllDirs = Directory.GetDirectories(displayCalDir, "Argyll_*");
                    // Sort by version descending to prefer newer versions
                    Array.Sort(argyllDirs);
                    Array.Reverse(argyllDirs);
                }
                catch
                {
                    continue;
                }

                foreach (var argyllDir in argyllDirs)
                {
                    yield return Path.Combine(argyllDir, "bin");
                }
            }

            // Also check DisplayCAL's download location in AppData
            var appDataPaths = new List<string>();
            try
            {
                string displayCalAppData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DisplayCAL", "dl");

                if (Directory.Exists(displayCalAppData))
                {
                    var argyllDirs = Directory.GetDirectories(displayCalAppData, "Argyll_*");
                    // Sort by version descending to prefer newer versions
                    Array.Sort(argyllDirs);
                    Array.Reverse(argyllDirs);

                    foreach (var argyllDir in argyllDirs)
                    {
                        appDataPaths.Add(Path.Combine(argyllDir, "bin"));
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            foreach (var path in appDataPaths)
            {
                yield return path;
            }
        }

        /// <summary>
        /// Searches for ArgyllCMS in the application's directory structure.
        /// This handles development scenarios and bundled distributions.
        /// </summary>
        private static IEnumerable<string> SearchApplicationArgyllCache()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;

            // Check argyll_cache folder directly under app directory
            string argyllCacheDir = Path.Combine(appDir, "argyll_cache");
            if (Directory.Exists(argyllCacheDir))
            {
                foreach (var path in SearchVersionedArgyllDirectory(argyllCacheDir))
                {
                    yield return path;
                }
            }

            // Walk up parent directories to find argyll_cache (development scenarios)
            // This handles running from bin/Debug/net8.0-windows/ etc.
            var current = new DirectoryInfo(appDir);
            for (int i = 0; i < 6 && current?.Parent != null; i++)
            {
                current = current.Parent;
                string cacheDir = Path.Combine(current.FullName, "argyll_cache");
                if (Directory.Exists(cacheDir))
                {
                    foreach (var path in SearchVersionedArgyllDirectory(cacheDir))
                    {
                        yield return path;
                    }
                    break; // Found the cache, no need to go further up
                }
            }
        }

        /// <summary>
        /// Searches versioned ArgyllCMS directories in LocalAppData.
        /// </summary>
        private static IEnumerable<string> SearchLocalAppDataVersioned()
        {
            string localArgyllDir = Path.Combine(AppPaths.DataDir, "Argyll");

            if (!Directory.Exists(localArgyllDir))
                yield break;

            foreach (var path in SearchVersionedArgyllDirectory(localArgyllDir))
            {
                yield return path;
            }
        }

        /// <summary>
        /// Searches a directory for Argyll_* versioned subdirectories.
        /// </summary>
        private static IEnumerable<string> SearchVersionedArgyllDirectory(string baseDir)
        {
            string[] versionDirs;
            try
            {
                versionDirs = Directory.GetDirectories(baseDir, "Argyll_*");
            }
            catch
            {
                yield break;
            }

            // Sort by version (descending) to prefer newer versions
            Array.Sort(versionDirs);
            Array.Reverse(versionDirs);

            foreach (var versionDir in versionDirs)
            {
                yield return Path.Combine(versionDir, "bin");
            }
        }

        /// <summary>
        /// Gets all potential ArgyllCMS paths for diagnostic purposes.
        /// </summary>
        /// <returns>List of paths with their existence status.</returns>
        public static IEnumerable<(string Path, bool Exists, bool HasSpotread)> GetDiagnosticPaths()
        {
            var allPaths = new List<string>();

            foreach (var path in GetStandardSearchPaths())
                allPaths.Add(path);

            foreach (var path in SearchDisplayCalArgyll())
                allPaths.Add(path);

            foreach (var path in SearchApplicationArgyllCache())
                allPaths.Add(path);

            foreach (var path in SearchLocalAppDataVersioned())
                allPaths.Add(path);

            foreach (var path in allPaths)
            {
                bool exists = Directory.Exists(path);
                bool hasSpotread = exists && (
                    File.Exists(Path.Combine(path, "spotread.exe")) ||
                    File.Exists(Path.Combine(path, "spotread")));
                yield return (path, exists, hasSpotread);
            }
        }
    }
}
