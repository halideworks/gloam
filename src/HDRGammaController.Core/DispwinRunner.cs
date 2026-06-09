using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Windows;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    public class DispwinRunner
    {
        private string _dispwinPath;

        // Last LUT successfully applied per display (keyed by \\.\DISPLAYn device name).
        // Re-applying an identical LUT is not a no-op on screen: each dispwin spawn
        // rewrites the GPU gamma ramp, which the user can see as a flicker — so we skip
        // the spawn entirely when nothing changed. The arrays are the shared read-only
        // references handed out by LutGenerator's cache; they are never mutated.
        private readonly ConcurrentDictionary<string, (double[] R, double[] G, double[] B)> _lastApplied = new();

        // dispwin numbers displays by its own (GDI) enumeration, which is not
        // guaranteed to match DXGI adapter/output order — and the old OutputId+1
        // heuristic breaks outright on multi-adapter systems. We parse the display
        // list from dispwin's usage output once and match by GDI device name
        // (\\.\DISPLAYn), falling back to OutputId+1 if anything fails.
        private Dictionary<string, int>? _displayIndexMap;
        private readonly object _displayIndexLock = new object();

        /// <summary>
        /// Forgets all previously-applied LUTs so the next apply always runs dispwin,
        /// and re-reads dispwin's display numbering. Call after events that may have
        /// changed the display topology or reset the GPU gamma ramp behind our back
        /// (display configuration changes, resume from sleep).
        /// </summary>
        public void InvalidateAppliedState()
        {
            _lastApplied.Clear();
            lock (_displayIndexLock) { _displayIndexMap = null; }
        }

        private int ResolveDisplayIndex(MonitorInfo monitor)
        {
            // MonitorInfo.DeviceName is the GDI name, e.g. @"\\.\DISPLAY1".
            string gdiName = (monitor.DeviceName ?? string.Empty).TrimStart('\\', '.');
            var map = GetDisplayIndexMap();
            if (gdiName.Length > 0 && map != null && map.TryGetValue(gdiName, out int index))
            {
                return index;
            }
            return (int)monitor.OutputId + 1;
        }

        private Dictionary<string, int>? GetDisplayIndexMap()
        {
            lock (_displayIndexLock)
            {
                if (_displayIndexMap != null) return _displayIndexMap;
                if (string.IsNullOrEmpty(_dispwinPath)) return null;

                // dispwin's usage text (exit code 1, output on stderr) lists displays as:
                //     1 = 'DISPLAY1, at 0, 0, width 2560, height 1440 (Primary Display)'
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var psi = new ProcessStartInfo(_dispwinPath, "-?")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        var stdoutTask = p.StandardOutput.ReadToEndAsync();
                        var stderrTask = p.StandardError.ReadToEndAsync();
                        if (p.WaitForExit(5000))
                        {
                            string usage = stdoutTask.GetAwaiter().GetResult() + "\n" +
                                           stderrTask.GetAwaiter().GetResult();
                            foreach (Match m in Regex.Matches(usage, @"^\s*(\d+)\s*=\s*'([^,']+),", RegexOptions.Multiline))
                            {
                                map[m.Groups[2].Value.Trim()] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                            }
                        }
                        else
                        {
                            try { p.Kill(entireProcessTree: true); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"DispwinRunner: Failed to query display list: {ex.Message}");
                }

                if (map.Count > 0)
                {
                    Log.Info($"DispwinRunner: Display map: {string.Join(", ", map)}");
                }
                // Cache even when empty so a broken dispwin doesn't get re-queried on
                // every apply; InvalidateAppliedState() clears it for a fresh attempt.
                _displayIndexMap = map;
                return map;
            }
        }

        public DispwinRunner()
        {
             _dispwinPath = FindDispwin();
             Log.Info($"DispwinRunner: Initialized with path='{_dispwinPath}'");
        }
        
        private string FindDispwin()
        {
            // SECURITY: Do NOT search current directory first (DLL/EXE planting risk)
            // Only search controlled/admin-protected locations

            // 1. Search in our local app data directory (controlled by this app)
            string localAppData = Path.Combine(ArgyllDownloader.LocalArgyllBinDir, "dispwin.exe");
            if (File.Exists(localAppData))
            {
                Log.Info($"DispwinRunner: Found dispwin in LocalAppData: {localAppData}");
                return localAppData;
            }

            // 2. Search common Program Files locations (admin-protected)
            var trustedPaths = new[]
            {
                @"C:\Program Files\ArgyllCMS\bin\dispwin.exe",
                @"C:\Program Files (x86)\ArgyllCMS\bin\dispwin.exe",
                @"C:\Program Files\Argyll_V3.3.0\bin\dispwin.exe",
                @"C:\Program Files\DisplayCAL\Argyll\bin\dispwin.exe",
                @"C:\Program Files (x86)\DisplayCAL\Argyll\bin\dispwin.exe",
                @"C:\Program Files\Argyll\bin\dispwin.exe",
                @"C:\Program Files (x86)\Argyll\bin\dispwin.exe",
                @"C:\Argyll\bin\dispwin.exe",
                @"C:\ArgyllCMS\bin\dispwin.exe"
            };

            foreach (var path in trustedPaths)
            {
                if (File.Exists(path))
                {
                    Log.Info($"DispwinRunner: Found dispwin at trusted path: {path}");
                    return path;
                }
            }

            // 3. Search DisplayCAL's AppData download location (user-controlled but common)
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string displayCalDir = Path.Combine(appData, "DisplayCAL", "dl");
                if (Directory.Exists(displayCalDir))
                {
                    // Look for Argyll_V* directories
                    foreach (var argyllDir in Directory.GetDirectories(displayCalDir, "Argyll_*"))
                    {
                        string dispwinPath = Path.Combine(argyllDir, "bin", "dispwin.exe");
                        if (File.Exists(dispwinPath))
                        {
                            Log.Info($"DispwinRunner: Found dispwin in DisplayCAL dir: {dispwinPath}");
                            return dispwinPath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"DispwinRunner: Error searching AppData: {ex.Message}");
            }

            // 4. Search in PATH (last resort, validates full path before returning)
            string pathResult = FindInPath("dispwin.exe");
            if (!string.IsNullOrEmpty(pathResult))
            {
                Log.Info($"DispwinRunner: Found dispwin in PATH: {pathResult}");
                return pathResult;
            }

            return string.Empty;
        }
        
        /// <summary>
        /// Searches PATH for a file and returns its full path, or empty string if not found.
        /// </summary>
        private string FindInPath(string fileName)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv)) return string.Empty;

            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string fullPath = Path.Combine(dir, fileName);
                    if (File.Exists(fullPath))
                    {
                        // Return full resolved path, not just the filename
                        return Path.GetFullPath(fullPath);
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"DispwinRunner: Error checking PATH entry '{dir}': {ex.Message}");
                }
            }
            return string.Empty;
        }

        public bool EnsureConfigured()
        {
            if (!string.IsNullOrEmpty(_dispwinPath)) return true;

            // Try detection again
            _dispwinPath = FindDispwin();
            if (!string.IsNullOrEmpty(_dispwinPath)) return true;

            // Offer to auto-download using shared ArgyllDownloader
            var result = MessageBox.Show(
                "ArgyllCMS 'dispwin.exe' not found.\n\n" +
                "This application requires ArgyllCMS to apply gamma tables.\n" +
                $"Would you like to download ArgyllCMS {ArgyllDownloader.ArgyllVersion} automatically?\n\n" +
                "(This will download ~15MB from argyllcms.com)",
                "HDR Gamma Controller - Missing Dependency",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                bool success = false;
                try
                {
                    // 5-minute ceiling protects the UI from hanging on a stalled mirror.
                    // Wait() with a timeout returns false if the task didn't complete in time.
                    var downloadTask = ArgyllDownloader.DownloadAsync();
                    if (!downloadTask.Wait(TimeSpan.FromMinutes(5)))
                    {
                        MessageBox.Show(
                            "ArgyllCMS download timed out.\nPlease check your connection and try again.",
                            "Download timeout", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        success = downloadTask.Result;
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"DispwinRunner: Download failed: {ex.Message}");
                    MessageBox.Show($"Download failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                if (success)
                {
                    _dispwinPath = FindDispwin();
                    if (!string.IsNullOrEmpty(_dispwinPath))
                    {
                        MessageBox.Show("ArgyllCMS downloaded successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        return true;
                    }
                }
            }

            return false;
        }

        public void ApplyGamma(MonitorInfo monitor, GammaMode mode, double whiteLevel)
        {
            ApplyGamma(monitor, mode, whiteLevel, CalibrationSettings.Default);
        }
        
        public void ApplyGamma(MonitorInfo monitor, GammaMode mode, double whiteLevel, CalibrationSettings calibration)
        {
            ApplyGamma(monitor, mode, whiteLevel, calibration, null);
        }

        /// <summary>
        /// Applies gamma correction using optional calibration profile for accurate color reproduction.
        /// </summary>
        /// <param name="monitor">Target monitor.</param>
        /// <param name="mode">Gamma mode to apply.</param>
        /// <param name="whiteLevel">SDR white level in nits.</param>
        /// <param name="calibration">User calibration settings (brightness, temperature, etc.).</param>
        /// <param name="calibrationProfile">Optional display calibration profile for color-accurate correction.</param>
        public void ApplyGamma(MonitorInfo monitor, GammaMode mode, double whiteLevel, CalibrationSettings calibration, DisplayCalibrationProfile? calibrationProfile)
        {
            bool hasProfile = calibrationProfile != null;
            Log.Info($"DispwinRunner.ApplyGamma: monitor={monitor.DeviceName}, mode={mode}, whiteLevel={whiteLevel}, hasCalibration={calibration.HasAdjustments}, hasProfile={hasProfile}");

            // Generate LUTs - use calibrated version if profile is available
            double[] lutR, lutG, lutB;

            if (calibrationProfile != null)
            {
                // Calibrated mode: Use measured display characteristics for accurate gamma compensation
                double targetGamma = GammaModeToValue(mode);
                var characterization = calibrationProfile.ToCharacterization();

                Log.Info($"DispwinRunner.ApplyGamma: Using calibration profile '{calibrationProfile.Name}' (mode={calibrationProfile.Mode})");

                (lutR, lutG, lutB, _) = LutGenerator.GenerateCalibratedLut(
                    targetGamma,
                    characterization,
                    calibration,
                    whiteLevel,
                    monitor.IsHdrActive);
            }
            else
            {
                // Standard mode: Use gamma curve without measured display response
                (lutR, lutG, lutB, _) = LutGenerator.GenerateLut(mode, whiteLevel, calibration, monitor.IsHdrActive);
            }

            Log.Info($"DispwinRunner.ApplyGamma: Generated LUTs with {lutR.Length} entries");

            // Skip the ramp rewrite entirely when this display already has exactly
            // this LUT loaded.
            string applyKey = monitor.DeviceName;
            if (_lastApplied.TryGetValue(applyKey, out var last) &&
                LutsEqual(last.R, lutR) && LutsEqual(last.G, lutG) && LutsEqual(last.B, lutB))
            {
                Log.Info($"DispwinRunner.ApplyGamma: LUT unchanged for {applyKey}, skipping");
                return;
            }

            // Fast path: set the hardware ramp directly via SetDeviceGammaRamp — the
            // same API dispwin ends up calling, minus the ~100-500ms process spawn and
            // the temp-file round trip. Argyll is then only needed for calibration.
            if (NativeGammaRamp.TryApply(monitor.DeviceName, lutR, lutG, lutB))
            {
                Log.Info($"DispwinRunner.ApplyGamma: Applied via native gamma ramp for {applyKey}");
                _lastApplied[applyKey] = (lutR, lutG, lutB);
                return;
            }

            // Fallback: external dispwin (requires an Argyll install).
            Log.Info("DispwinRunner.ApplyGamma: Native apply failed, falling back to dispwin");
            if (!EnsureConfigured())
            {
                Log.Info("DispwinRunner.ApplyGamma: dispwin not configured, aborting.");
                return;
            }

            // Create .cal file content
            // SECURITY: Use GUID-based filename to prevent race conditions
            // (GetTempFileName + ChangeExtension creates a race between file creation and use)
            string calContent = GenerateCalContent(lutR, lutG, lutB);
            string calFile = Path.Combine(Path.GetTempPath(), $"HDRGamma_{Guid.NewGuid():N}.cal");
            Log.Info($"DispwinRunner.ApplyGamma: Created temp file={calFile}");

            try
            {
                // Write with exclusive access to prevent tampering
                using (var fs = new FileStream(calFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs))
                {
                    writer.Write(calContent);
                }

                int argIndex = ResolveDisplayIndex(monitor);
                string args = $"-d {argIndex} \"{calFile}\"";
                Log.Info($"DispwinRunner.ApplyGamma: Running dispwin with args: {args}");
                if (RunDispwin(args))
                {
                    _lastApplied[applyKey] = (lutR, lutG, lutB);
                }
                else
                {
                    // Unknown ramp state — make sure the next apply isn't skipped.
                    _lastApplied.TryRemove(applyKey, out _);
                }
            }
            catch (Exception ex)
            {
                Log.Info($"DispwinRunner.ApplyGamma: Exception: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(calFile)) File.Delete(calFile);
                }
                catch (Exception ex)
                {
                    Log.Info($"DispwinRunner.ApplyGamma: Failed to cleanup temp file: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Converts GammaMode enum to numeric gamma value.
        /// </summary>
        private static double GammaModeToValue(GammaMode mode)
        {
            return mode switch
            {
                GammaMode.WindowsDefault => 1.0,  // Linear / bypass
                GammaMode.Gamma22 => 2.2,
                GammaMode.Gamma24 => 2.4,
                _ => 2.2
            };
        }

        public void ClearGamma(MonitorInfo monitor)
        {
             // The ramp is reset (or in an unknown state on failure) either way.
             _lastApplied.TryRemove(monitor.DeviceName, out _);

             if (NativeGammaRamp.TryClear(monitor.DeviceName))
             {
                 Log.Info($"DispwinRunner.ClearGamma: Cleared via native gamma ramp for {monitor.DeviceName}");
                 return;
             }

             if (!EnsureConfigured()) return;
             int argIndex = ResolveDisplayIndex(monitor);
             RunDispwin($"-d {argIndex} -c");
        }

        private static bool LutsEqual(double[] a, double[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <returns>True if dispwin ran to completion with exit code 0.</returns>
        private bool RunDispwin(string args)
        {
            Log.Info($"DispwinRunner.RunDispwin: Executing '{_dispwinPath}' with args '{args}'");
            try
            {
                var psi = new ProcessStartInfo(_dispwinPath, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    Log.Info("DispwinRunner.RunDispwin: Process.Start returned null");
                    return false;
                }

                // Drain both streams concurrently — sequential ReadToEnd can deadlock if
                // the process fills the other pipe's buffer first.
                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();

                if (!p.WaitForExit(5000))
                {
                    Log.Info("DispwinRunner.RunDispwin: Timed out after 5s, killing process");
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return false;
                }

                string stdout = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();
                Log.Info($"DispwinRunner.RunDispwin: Exit code={p.ExitCode}");
                if (!string.IsNullOrWhiteSpace(stdout))
                    Log.Info($"DispwinRunner.RunDispwin: stdout={stdout}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    Log.Info($"DispwinRunner.RunDispwin: stderr={stderr}");
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Log.Info($"DispwinRunner.RunDispwin: Exception: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        private string GenerateCalContent(double[] lutR, double[] lutG, double[] lutB)
        {
            var sb = new StringBuilder();
            // Argyll CAL format - must start with "CAL" identifier
            sb.AppendLine("CAL    ");
            sb.AppendLine();
            sb.AppendLine("DESCRIPTOR \"HDRGammaController Generated Calibration\"");
            sb.AppendLine("ORIGINATOR \"HDRGammaController\"");
            sb.AppendLine($"CREATED \"{DateTime.Now:ddd MMM dd HH:mm:ss yyyy}\"");
            sb.AppendLine("DEVICE_CLASS \"DISPLAY\"");
            sb.AppendLine("COLOR_REP \"RGB\"");
            sb.AppendLine();
            sb.AppendLine("NUMBER_OF_FIELDS 4");
            sb.AppendLine("BEGIN_DATA_FORMAT");
            sb.AppendLine("RGB_I RGB_R RGB_G RGB_B");
            sb.AppendLine("END_DATA_FORMAT");
            sb.AppendLine();
            sb.AppendLine($"NUMBER_OF_SETS {lutR.Length}");
            sb.AppendLine("BEGIN_DATA");
            
            for(int i=0; i<lutR.Length; i++)
            {
                double input = i / (double)(lutR.Length - 1);
                // Per-channel RGB values
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, 
                    "{0:G6} {1:G6} {2:G6} {3:G6}", input, lutR[i], lutG[i], lutB[i]));
            }
            
            sb.AppendLine("END_DATA");
            return sb.ToString();
        }
    }
}
