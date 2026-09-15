using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Gloam.Core.Calibration
{
    /// <summary>Outcome of an <see cref="OemCorrectionImporter"/> run.</summary>
    public sealed class OemImportResult
    {
        public OemImportResult(bool success, IReadOnlyList<string> installedFiles, string message, string output)
        {
            Success = success;
            InstalledFiles = installedFiles;
            Message = message;
            Output = output;
        }

        /// <summary>True when oeminst exited cleanly and wrote at least one file.</summary>
        public bool Success { get; }

        /// <summary>Full paths oeminst reported writing.</summary>
        public IReadOnlyList<string> InstalledFiles { get; }

        /// <summary>One or two sentences for the setup screen.</summary>
        public string Message { get; }

        /// <summary>Raw oeminst output, for the log.</summary>
        public string Output { get; }
    }

    /// <summary>
    /// Converts the i1Display EDR calibration files that X-Rite i1Profiler and Calibrite
    /// PROFILER install into ArgyllCMS CCSS corrections, using Argyll's own <c>oeminst</c>.
    /// Once installed, spotread lists them as technology rows (OLED, white LED, RGB LED, ...)
    /// in its <c>-y</c> table, so a display type resolves to a matching correction instead
    /// of the instrument's generic base calibration.
    /// </summary>
    /// <remarks>
    /// The EDR files are the vendor's; nothing is downloaded. They are only converted when
    /// already present on this PC under the user's own software license. oeminst itself
    /// searches one fixed X-Rite path, so the files are enumerated here (Calibrite and
    /// 32-bit install roots included) and handed to it explicitly.
    /// </remarks>
    public static class OemCorrectionImporter
    {
        // Vendor folders under each Program Files root that may hold i1d3 EDR files.
        private static readonly string[] VendorFolders = { "X-Rite", "Calibrite", "I-O DATA" };

        // oeminst -v prints one of these per installed file.
        private static readonly Regex WrotePattern = new(@"^Wrote '([^']+)'", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>The Program Files roots to search on this machine.</summary>
        public static IReadOnlyList<string> DefaultSearchRoots()
        {
            var roots = new List<string>();
            foreach (string variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
            {
                string? value = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(value) && !roots.Contains(value, StringComparer.OrdinalIgnoreCase))
                    roots.Add(value);
            }
            return roots;
        }

        /// <summary>Every <c>.edr</c> file under a known vendor folder of the given roots.</summary>
        public static IReadOnlyList<string> FindEdrFiles(IEnumerable<string>? searchRoots = null)
        {
            var found = new List<string>();
            foreach (string root in searchRoots ?? DefaultSearchRoots())
            {
                foreach (string vendor in VendorFolders)
                {
                    string dir = Path.Combine(root, vendor);
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        found.AddRange(Directory.EnumerateFiles(dir, "*.edr", SearchOption.AllDirectories));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A vendor folder we cannot read contributes nothing.
                    }
                }
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        /// <summary>Path of oeminst next to spotread, or null if the Argyll bundle lacks it.</summary>
        public static string? FindOeminst(string argyllBinPath)
        {
            foreach (string name in new[] { "oeminst.exe", "oeminst" })
            {
                string path = Path.Combine(argyllBinPath, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>Message shown when the search found nothing to import.</summary>
        public const string NothingFoundMessage =
            "No i1Display EDR files were found on this PC. They ship with X-Rite i1Profiler and Calibrite PROFILER; " +
            "install either, then import again. A panel-matched correction from Find / Create is better than any EDR.";

        /// <summary>
        /// Runs <c>oeminst -v</c> on <paramref name="edrFiles"/> (found with
        /// <see cref="FindEdrFiles"/>) and reports what it wrote.
        /// </summary>
        public static async Task<OemImportResult> ImportAsync(
            string argyllBinPath,
            IReadOnlyList<string> edrFiles,
            Action<string>? log,
            CancellationToken cancellationToken)
        {
            if (edrFiles.Count == 0)
                return new OemImportResult(false, Array.Empty<string>(), NothingFoundMessage, "");

            string? oeminst = FindOeminst(argyllBinPath);
            if (oeminst == null)
            {
                return new OemImportResult(false, Array.Empty<string>(),
                    "The bundled ArgyllCMS has no oeminst tool. Click Refresh to reinstall ArgyllCMS, then import again.", "");
            }

            var psi = new ProcessStartInfo(oeminst)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = argyllBinPath
            };
            psi.ArgumentList.Add("-v");
            foreach (string file in edrFiles)
                psi.ArgumentList.Add(file);

            log?.Invoke($"oeminst: importing {edrFiles.Count} EDR file(s) with {oeminst}");

            string output;
            int exitCode;
            try
            {
                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException("oeminst did not start.");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(2));

                var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = process.StandardError.ReadToEndAsync(cts.Token);
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    throw;
                }
                output = await stdout + "\n" + await stderr;
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new OemImportResult(false, Array.Empty<string>(),
                    "oeminst did not finish within two minutes and was stopped.", "");
            }

            log?.Invoke($"oeminst exit {exitCode}:\n{output}");
            return ParseOutput(output, exitCode);
        }

        /// <summary>Turns oeminst's verbose output and exit code into a result.</summary>
        internal static OemImportResult ParseOutput(string output, int exitCode)
        {
            var installed = new List<string>();
            foreach (Match m in WrotePattern.Matches(output))
                installed.Add(m.Groups[1].Value);

            if (exitCode != 0)
            {
                string lastLine = LastNonEmptyLine(output);
                return new OemImportResult(false, installed,
                    $"oeminst failed (exit {exitCode}): {lastLine}", output);
            }
            if (installed.Count == 0)
                return new OemImportResult(false, installed, "oeminst ran but reported no installed files.", output);

            // oeminst also installs Argyll's own ref/CRT.ccss alongside any EDR set; count
            // only the files that came from the EDRs for the message.
            int edrCount = 0;
            foreach (string path in installed)
            {
                if (!Path.GetFileName(path).Equals("CRT.ccss", StringComparison.OrdinalIgnoreCase))
                    edrCount++;
            }
            string dir = Path.GetDirectoryName(installed[0]) ?? "";
            return new OemImportResult(true, installed,
                $"Imported {edrCount} meter correction(s) into {dir}. Re-detecting the meter to pick them up.", output);
        }

        private static string LastNonEmptyLine(string text)
        {
            string last = "";
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) last = t;
            }
            return last;
        }
    }
}
