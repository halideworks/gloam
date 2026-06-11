using System;
using HDRGammaController.Core;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Extracts embedded ICM profile resources to the application directory.
    /// Only extracts if files are missing or have changed (for update distribution).
    /// </summary>
    public static class ResourceExtractor
    {
        private static readonly string[] IcmProfiles = new[]
        {
            "srgb_to_gamma2p2_100_mhc2.icm",
            "srgb_to_gamma2p2_200_mhc2.icm",
            "srgb_to_gamma2p2_300_mhc2.icm",
            "srgb_to_gamma2p2_400_mhc2.icm",
            "srgb_to_gamma2p2_sdr.icm",
            "srgb_to_gamma2p2_unspecified.icm"
        };

        /// <summary>
        /// Extracts all embedded ICM profiles to the application directory.
        /// Only extracts if the file is missing or differs from the embedded version.
        /// </summary>
        /// <returns>Number of files extracted or updated.</returns>
        public static int ExtractIcmProfiles()
        {
            var assembly = Assembly.GetExecutingAssembly();
            // Prefer the app directory, but it is read-only for an unelevated process under
            // Program Files - fall back to LocalAppData, which the profile installer also
            // searches. Without the fallback a wiped app dir (e.g. a mirroring deploy)
            // leaves NO templates anywhere and calibration apply fails.
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!IsWritable(appDir))
            {
                appDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HDRGammaController");
                Directory.CreateDirectory(appDir);
                Log.Info($"ResourceExtractor: app dir not writable; extracting to {appDir}");
            }
            int extractedCount = 0;

            foreach (var fileName in IcmProfiles)
            {
                try
                {
                    var resourceName = FindResourceName(assembly, fileName);
                    if (resourceName == null)
                    {
                        Log.Info($"ResourceExtractor: Embedded resource not found for {fileName}");
                        continue;
                    }

                    var targetPath = Path.Combine(appDir, fileName);

                    using var resourceStream = assembly.GetManifestResourceStream(resourceName);
                    if (resourceStream == null)
                    {
                        Log.Info($"ResourceExtractor: Failed to open resource stream for {resourceName}");
                        continue;
                    }

                    // Read embedded resource into memory for comparison
                    var embeddedData = ReadAllBytes(resourceStream);
                    var embeddedHash = ComputeHash(embeddedData);

                    // Check if file exists and compare hashes
                    if (File.Exists(targetPath))
                    {
                        var existingHash = ComputeFileHash(targetPath);
                        if (embeddedHash.SequenceEqual(existingHash))
                        {
                            // File exists and matches - no action needed
                            continue;
                        }
                        Log.Info($"ResourceExtractor: Updating {fileName} (hash mismatch)");
                    }
                    else
                    {
                        Log.Info($"ResourceExtractor: Extracting {fileName} (not found)");
                    }

                    // Extract or update the file
                    File.WriteAllBytes(targetPath, embeddedData);
                    extractedCount++;
                }
                catch (Exception ex)
                {
                    Log.Info($"ResourceExtractor: Error processing {fileName}: {ex.Message}");
                }
            }

            return extractedCount;
        }

        /// <summary>
        /// Finds the full resource name for a given file name.
        /// Resource names include namespace prefixes which vary by project structure.
        /// </summary>
        private static string? FindResourceName(Assembly assembly, string fileName)
        {
            var resourceNames = assembly.GetManifestResourceNames();

            // Look for exact match at end of resource name
            return resourceNames.FirstOrDefault(rn =>
                rn.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase) ||
                rn.EndsWith("." + fileName.Replace(".icm", "_icm"), StringComparison.OrdinalIgnoreCase) ||
                rn.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, $".write_probe_{Guid.NewGuid():N}");
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }

        private static byte[] ComputeHash(byte[] data)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(data);
        }

        private static byte[] ComputeFileHash(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return sha256.ComputeHash(stream);
        }

        /// <summary>
        /// Lists all embedded resources (for debugging).
        /// </summary>
        public static void DebugListResources()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var names = assembly.GetManifestResourceNames();
            Log.Info($"ResourceExtractor: Found {names.Length} embedded resources:");
            foreach (var name in names)
            {
                Log.Info($"  - {name}");
            }
        }
    }
}
