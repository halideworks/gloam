using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Utility class for downloading and managing ArgyllCMS binaries.
    /// Provides shared download functionality for both dispwin (gamma) and spotread (calibration).
    ///
    /// Integrity: the versioned archive is fetched over HTTPS and must match the pinned
    /// SHA-256 before any executable is extracted or installed.
    /// </summary>
    public static class ArgyllDownloader
    {
        // Version is centralized here. The extraction logic strips a leading
        // "{ArgyllVersion}/" directory, and the path finders search for this version,
        // so bumping these three lines is all that's needed to move versions.
        //
        // V3.3.0 had a bug where spotread, on failing to open the colorimeter's HID
        // handle, exited silently with code 0 instead of reporting the error — the
        // "silent HID fail" the session code works around. V3.5.0 (Feb 2026) also
        // carries i1d3 measurement fixes, so it is the better baseline.
        private const string ArgyllVersionNumber = "3.5.0";

        /// <summary>URL for ArgyllCMS Windows binaries.</summary>
        public const string ArgyllDownloadUrl = "https://www.argyllcms.com/Argyll_V" + ArgyllVersionNumber + "_win64_exe.zip";

        /// <summary>Current ArgyllCMS version being downloaded (directory-name form).</summary>
        public const string ArgyllVersion = "Argyll_V" + ArgyllVersionNumber;

        public const string ArgyllArchiveSha256 = "C80F78BA30E715079A00A14C2E7C9F533C58E6C8FDBC27F2D32B3D7813F9D132";

        /// <summary>
        /// Minimum required version string (used for version comparison).
        /// </summary>
        public const string MinimumVersion = "3.0.0";

        private const long MaxDownloadBytes = 128L * 1024 * 1024;
        private const long MaxExtractedBytes = 512L * 1024 * 1024;
        private const int MaxArchiveEntries = 10_000;

        /// <summary>
        /// Gets the local directory where ArgyllCMS is installed.
        /// </summary>
        public static string LocalArgyllDir => Path.Combine(AppPaths.DataDir, "Argyll");

        /// <summary>
        /// Gets the bin directory path.
        /// </summary>
        public static string LocalArgyllBinDir => Path.Combine(LocalArgyllDir, "bin");

        /// <summary>
        /// Event raised to report download progress.
        /// </summary>
        public static event EventHandler<DownloadProgressEventArgs>? ProgressChanged;

        /// <summary>
        /// Checks if ArgyllCMS is already installed locally with the required binaries.
        /// </summary>
        /// <returns>True if spotread.exe and dispwin.exe are available.</returns>
        public static bool IsInstalled()
        {
            string binDir = LocalArgyllBinDir;
            if (!Directory.Exists(binDir))
                return false;

            return File.Exists(Path.Combine(binDir, "spotread.exe")) &&
                   File.Exists(Path.Combine(binDir, "dispwin.exe"));
        }

        /// <summary>
        /// Gets the version of the installed ArgyllCMS, or null if not installed.
        /// </summary>
        public static string? GetInstalledVersion()
        {
            // Check for version marker file
            string versionFile = Path.Combine(LocalArgyllDir, "version.txt");
            if (File.Exists(versionFile))
            {
                try
                {
                    return File.ReadAllText(versionFile).Trim();
                }
                catch
                {
                    // Fall through
                }
            }

            // If installed but no version file, assume old version
            if (IsInstalled())
                return "unknown";

            return null;
        }

        /// <summary>
        /// Checks if an update is needed (newer version available than installed).
        /// </summary>
        public static bool IsUpdateNeeded()
        {
            if (!IsInstalled())
                return true;

            string? installed = GetInstalledVersion();
            if (installed == null || installed == "unknown")
                return true;

            // Numeric comparison, not lexicographic. A bare string.Compare would sort "3.10.0"
            // BELOW "3.5.0" (because '1' < '5'), suppressing a real future update.
            return IsNewerVersion(ArgyllVersion, installed);
        }

        /// <summary>
        /// True if <paramref name="candidate"/> is a newer version than
        /// <paramref name="installed"/>. Both may carry a leading "Argyll_V" prefix and/or
        /// trailing qualifiers; only the leading numeric components are compared.
        /// </summary>
        private static bool IsNewerVersion(string candidate, string installed)
        {
            if (Version.TryParse(StripVersionPrefix(candidate), out var c) &&
                Version.TryParse(StripVersionPrefix(installed), out var i))
            {
                return c > i;
            }
            // Fall back to lexicographic only when either side isn't a clean X.Y[.Z] — which
            // shouldn't happen for our own version constants, but keeps a one-off build from
            // blocking updates forever.
            return string.Compare(candidate, installed, StringComparison.OrdinalIgnoreCase) > 0;
        }

        /// <summary>Strips a leading "Argyll_V"/"V" prefix and any whitespace, leaving the numeric core.</summary>
        private static string StripVersionPrefix(string s)
        {
            string t = s.Trim();
            const string prefix = "Argyll_V";
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                t = t.Substring(prefix.Length);
            else if (t.Length > 0 && (t[0] == 'V' || t[0] == 'v'))
                t = t.Substring(1);
            return t.Trim();
        }

        /// <summary>
        /// Downloads and extracts ArgyllCMS binaries to LocalApplicationData.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="progress">Optional progress reporter (0-100).</param>
        /// <returns>True if download and extraction succeeded.</returns>
        public static async Task<bool> DownloadAsync(
            CancellationToken cancellationToken = default,
            IProgress<int>? progress = null)
        {
            string? tempDir = null;
            string? stagingDir = null;
            string? backupDir = null;
            try
            {
                ReportProgress("Starting download...", 0);

                // Create temp directory with unique name
                tempDir = Path.Combine(Path.GetTempPath(), $"HDRGammaController_ArgyllDownload_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                string zipPath = Path.Combine(tempDir, "argyll.zip");

                // Download with progress
                ReportProgress($"Downloading from {ArgyllDownloadUrl}...", 5);
                progress?.Report(5);

                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(10);

                    // Set browser-like headers to avoid 418 "reauthentication required" error
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
                    client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");

                    using var response = await client.GetAsync(ArgyllDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? -1;
                    if (totalBytes > MaxDownloadBytes)
                        throw new InvalidDataException($"ArgyllCMS download is unexpectedly large ({totalBytes} bytes).");
                    var downloadedBytes = 0L;

                    using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                    var buffer = new byte[8192];
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        downloadedBytes += bytesRead;
                        if (downloadedBytes > MaxDownloadBytes)
                            throw new InvalidDataException("ArgyllCMS download exceeded the size limit.");

                        if (totalBytes > 0)
                        {
                            // Download is 5-70% of progress
                            int downloadProgress = (int)(5 + (downloadedBytes * 65 / totalBytes));
                            progress?.Report(downloadProgress);
                            ReportProgress($"Downloading... {downloadedBytes / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB", downloadProgress);
                        }
                    }
                }

                ReportProgress("Download complete", 75);
                progress?.Report(75);

                await using (var archiveStream = File.OpenRead(zipPath))
                {
                    byte[] digest = await SHA256.HashDataAsync(archiveStream, cancellationToken);
                    string actualHash = Convert.ToHexString(digest);
                    if (!actualHash.Equals(ArgyllArchiveSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"ArgyllCMS archive integrity check failed (SHA-256 {actualHash}).");
                }

                // Extract
                ReportProgress("Extracting files...", 80);
                progress?.Report(80);

                // Extract and validate beside the live install. The old working copy is
                // retained until the replacement is complete, so cancellation/corruption
                // cannot leave calibration without its tools.
                stagingDir = LocalArgyllDir + $".staging-{Guid.NewGuid():N}";
                Directory.CreateDirectory(stagingDir);

                // The ZIP contains Argyll_V3.3.0/bin/dispwin.exe etc.
                // We strip the version prefix and extract to LocalArgyllDir
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    int totalEntries = archive.Entries.Count;
                    int extracted = 0;
                    long extractedBytes = 0;

                    if (totalEntries > MaxArchiveEntries)
                        throw new InvalidDataException("ArgyllCMS archive contains too many entries.");

                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Strip the first directory component (Argyll_V3.3.0/)
                        string entryPath = entry.FullName;
                        if (entryPath.StartsWith(ArgyllVersion + "/") || entryPath.StartsWith(ArgyllVersion + "\\"))
                        {
                            entryPath = entryPath.Substring(ArgyllVersion.Length + 1);
                        }

                        if (string.IsNullOrEmpty(entryPath)) continue;

                        // SECURITY: Zip-slip guard. Entry names like "..\..\evil.exe" or
                        // absolute paths must not escape the install directory.
                        string destPath = Path.GetFullPath(Path.Combine(stagingDir, entryPath));
                        string rootPrefix = Path.GetFullPath(stagingDir) + Path.DirectorySeparatorChar;
                        if (!destPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                $"Archive entry '{entry.FullName}' resolves outside the install directory.");
                        }

                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                        {
                            // Directory
                            Directory.CreateDirectory(destPath);
                        }
                        else
                        {
                            if (entry.Length < 0 || entry.Length > MaxExtractedBytes)
                                throw new InvalidDataException($"Archive entry '{entry.FullName}' is unexpectedly large.");
                            extractedBytes = checked(extractedBytes + entry.Length);
                            if (extractedBytes > MaxExtractedBytes)
                                throw new InvalidDataException("ArgyllCMS archive exceeds the extracted-size limit.");

                            // File
                            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                            entry.ExtractToFile(destPath, overwrite: true);
                        }

                        extracted++;
                        // Extraction is 80-95% of progress
                        int extractProgress = 80 + (extracted * 15 / totalEntries);
                        progress?.Report(extractProgress);
                    }
                }

                // Write version marker
                File.WriteAllText(Path.Combine(stagingDir, "version.txt"), ArgyllVersion);

                if (!File.Exists(Path.Combine(stagingDir, "bin", "spotread.exe")) ||
                    !File.Exists(Path.Combine(stagingDir, "bin", "dispwin.exe")))
                    throw new InvalidDataException("ArgyllCMS archive is missing required executables.");

                backupDir = LocalArgyllDir + $".backup-{Guid.NewGuid():N}";
                if (Directory.Exists(LocalArgyllDir))
                    Directory.Move(LocalArgyllDir, backupDir);
                try
                {
                    Directory.Move(stagingDir, LocalArgyllDir);
                    stagingDir = null;
                }
                catch
                {
                    if (Directory.Exists(backupDir) && !Directory.Exists(LocalArgyllDir))
                        Directory.Move(backupDir, LocalArgyllDir);
                    throw;
                }

                if (Directory.Exists(backupDir))
                {
                    try { Directory.Delete(backupDir, recursive: true); }
                    catch (Exception ex) { Log.Info($"ArgyllDownloader: old-version cleanup failed: {ex.Message}"); }
                }
                backupDir = null;

                ReportProgress("Installation complete!", 100);
                progress?.Report(100);

                return true;
            }
            catch (OperationCanceledException)
            {
                ReportProgress("Download cancelled", 0);
                throw;
            }
            catch (Exception ex)
            {
                ReportProgress($"Download failed: {ex.Message}", 0);
                throw;
            }
            finally
            {
                // Cleanup temp directory
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); }
                    catch { /* Ignore cleanup errors */ }
                }
                if (stagingDir != null && Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, true); }
                    catch { /* Ignore cleanup errors */ }
                }
            }
        }

        private static void ReportProgress(string message, int percent)
        {
            ProgressChanged?.Invoke(null, new DownloadProgressEventArgs(message, percent));
        }
    }

    /// <summary>
    /// Event args for download progress.
    /// </summary>
    public class DownloadProgressEventArgs : EventArgs
    {
        public string Message { get; }
        public int PercentComplete { get; }

        public DownloadProgressEventArgs(string message, int percentComplete)
        {
            Message = message;
            PercentComplete = percentComplete;
        }
    }
}
