using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
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

        // Maps each applied display's GDI device name (the _lastApplied key) to its
        // CalibrationStateManager.BypassKey, which is what the calibration bypass registry is
        // keyed on. Lets the ramp guard answer "is this display mid-calibration?" — it only has
        // the device name, but IsDeviceInBypass wants the bypass key (device path, else name).
        private readonly ConcurrentDictionary<string, string> _deviceNameToPath = new();

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
            _deviceNameToPath.Clear();
            _unverifiableDisplays.Clear();
            lock (_displayIndexLock) { _displayIndexMap = null; }
        }

        // --- Ramp guard ---------------------------------------------------------
        // Fullscreen exclusive games and some driver events silently reset the VCGT.
        // GetDeviceGammaRamp readback is essentially free, so we can verify each
        // display still holds the ramp we last applied and restore it when stomped.

        // Displays whose drivers transform ramps on write (readback never matches
        // what we set). Guarded once, then excluded — otherwise we'd "restore" on
        // every poll and flash the screen.
        private readonly ConcurrentDictionary<string, bool> _unverifiableDisplays = new();

        // Drivers may quantize ramp entries (e.g. 10-bit hardware); allow ~0.8% of
        // full scale before calling it an external overwrite. Real stomps (identity
        // resets, other apps' night modes) deviate far more than this.
        private const int RampToleranceCounts = 512;

        /// <summary>
        /// Verifies each display still holds the ramp we last applied and restores it
        /// if something overwrote it externally. Returns the number restored.
        /// </summary>
        public int VerifyAndRestoreRamps()
        {
            int restored = 0;
            foreach (var kvp in _lastApplied)
            {
                string device = kvp.Key;
                if (_unverifiableDisplays.ContainsKey(device)) continue;

                // Mid-calibration guard: a calibration has bypassed this display's corrections
                // (and the closed loop may be loading candidate ramps on it). Re-asserting the
                // saved ramp here would clobber an in-flight measurement, so leave it alone.
                if (_deviceNameToPath.TryGetValue(device, out var devicePath) &&
                    CalibrationStateManager.IsDeviceInBypass(devicePath))
                {
                    continue;
                }

                var snapshot = kvp.Value;
                try
                {
                    if (RampMatchesApplied(device, snapshot)) continue;

                    Log.Info($"DispwinRunner: Ramp on {device} was overwritten externally; restoring");
                    if (!NativeGammaRamp.TryApply(device, snapshot.R, snapshot.G, snapshot.B)) continue;
                    restored++;

                    // TOCTOU guard: the coalescer can swap a newer LUT into _lastApplied (and onto
                    // the hardware) between our read of the snapshot and this write, which would
                    // make us clobber the new ramp with the old one. If the live value no longer
                    // matches our snapshot, the newer apply wins — re-assert it so the screen ends
                    // the tick on the current value rather than self-healing (flickering) next tick.
                    if (_lastApplied.TryGetValue(device, out var current) &&
                        (!LutsEqual(current.R, snapshot.R) ||
                         !LutsEqual(current.G, snapshot.G) ||
                         !LutsEqual(current.B, snapshot.B)))
                    {
                        if (!CalibrationStateManager.IsDeviceInBypass(devicePath))
                        {
                            NativeGammaRamp.TryApply(device, current.R, current.G, current.B);
                        }
                        continue;
                    }

                    if (!RampMatchesApplied(device, snapshot))
                    {
                        _unverifiableDisplays[device] = true;
                        Log.Error($"DispwinRunner: {device} readback never matches the written ramp; ramp guard disabled for this display");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"DispwinRunner: Ramp guard error on {device}: {ex.Message}");
                }
            }
            return restored;
        }

        private static bool RampMatchesApplied(string device, (double[] R, double[] G, double[] B) luts)
        {
            // Treat an unreadable ramp as a match — never fight a display we can't see.
            if (!NativeGammaRamp.TryRead(device, out var r, out var g, out var b)) return true;
            return ChannelMatches(r, luts.R) && ChannelMatches(g, luts.G) && ChannelMatches(b, luts.B);
        }

        private static bool ChannelMatches(ushort[] hardware, double[] lut)
        {
            var expected = NativeGammaRamp.BuildRampChannel(lut);
            if (hardware.Length != expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
            {
                if (Math.Abs(hardware[i] - expected[i]) > RampToleranceCounts) return false;
            }
            return true;
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

            // Self-contained Full package: a fixed, versioned path under the application
            // directory. Do not scan the current directory or PATH for an arbitrary exe.
            string bundled = Path.Combine(AppContext.BaseDirectory, "argyll_cache",
                ArgyllDownloader.ArgyllVersion, "bin", "dispwin.exe");
            if (File.Exists(bundled))
            {
                Log.Info($"DispwinRunner: Found bundled dispwin: {bundled}");
                return bundled;
            }

            // 2. Search common Program Files locations (admin-protected)
            var trustedPaths = new[]
            {
                @"C:\Program Files\ArgyllCMS\bin\dispwin.exe",
                @"C:\Program Files (x86)\ArgyllCMS\bin\dispwin.exe",
                @"C:\Program Files\Argyll_V3.5.0\bin\dispwin.exe",
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

        /// <summary>
        /// Raised (once per process) when the dispwin fallback was needed but no
        /// dispwin.exe could be found. The GUI layer decides how to surface this —
        /// Core must not show UI, and this code path runs on background threads.
        /// </summary>
        public event Action? DispwinUnavailable;
        private bool _dispwinMissingNotified;

        public bool EnsureConfigured()
        {
            if (!string.IsNullOrEmpty(_dispwinPath)) return true;

            // Try detection again
            _dispwinPath = FindDispwin();
            if (!string.IsNullOrEmpty(_dispwinPath)) return true;

            if (!_dispwinMissingNotified)
            {
                _dispwinMissingNotified = true;
                Log.Error("DispwinRunner: dispwin.exe not found; fallback unavailable until ArgyllCMS is installed (Calibrate Display can download it).");
                DispwinUnavailable?.Invoke();
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
        public void ApplyGamma(MonitorInfo monitor, GammaMode mode, double whiteLevel, CalibrationSettings calibration, DisplayCalibrationProfile? calibrationProfile, bool skipIfBypassed = false)
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

            // Live/background applies (slider, night mode, app exclusion, resume re-apply) must
            // not stomp a monitor a calibration has put into bypass for measurement. The live
            // path passes skipIfBypassed: true; re-checking here - immediately before the
            // hardware write - closes the window between the caller's earlier check and this
            // syscall. Calibration's own writes (ApplyCorrectionLut) and the on-exit restore
            // (RestorePreviousState calls this with the default false) are never skipped.
            if (skipIfBypassed && CalibrationStateManager.IsDeviceInBypass(monitor))
            {
                Log.Info($"DispwinRunner.ApplyGamma: skipping apply for {monitor.DeviceName} (calibration bypass active)");
                return;
            }

            // Fast path: set the hardware ramp directly via SetDeviceGammaRamp — the
            // same API dispwin ends up calling, minus the ~100-500ms process spawn and
            // the temp-file round trip. Argyll is then only needed for calibration.
            if (NativeGammaRamp.TryApply(monitor.DeviceName, lutR, lutG, lutB))
            {
                Log.Info($"DispwinRunner.ApplyGamma: Applied via native gamma ramp for {applyKey}");
                RecordApplied(monitor, lutR, lutG, lutB);
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
                    RecordApplied(monitor, lutR, lutG, lutB);
                }
                else
                {
                    // Unknown ramp state — make sure the next apply isn't skipped.
                    _lastApplied.TryRemove(applyKey, out _);
                    _deviceNameToPath.TryRemove(applyKey, out _);
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

        /// <summary>
        /// Applies raw per-channel correction LUTs (1024-point, signal→signal in [0,1])
        /// directly to the display's hardware gamma ramp, bypassing gamma-mode/calibration
        /// generation. Closed-loop calibration uses this to load a candidate correction and
        /// re-measure it. The applied LUTs are recorded so the ramp guard preserves them
        /// for the duration of the measurement loop instead of restoring a stale profile.
        /// </summary>
        public bool ApplyCorrectionLut(MonitorInfo monitor, double[] lutR, double[] lutG, double[] lutB)
        {
            bool ok = NativeGammaRamp.TryApply(monitor.DeviceName, lutR, lutG, lutB);
            if (ok)
            {
                RecordApplied(monitor, lutR, lutG, lutB);
            }
            else
            {
                _lastApplied.TryRemove(monitor.DeviceName, out _);
                _deviceNameToPath.TryRemove(monitor.DeviceName, out _);
            }
            return ok;
        }

        // Records a successful apply: the LUT for the dedupe/ramp-guard cache, and the
        // device-name -> device-path mapping the ramp guard needs to skip bypassed displays.
        private void RecordApplied(MonitorInfo monitor, double[] lutR, double[] lutG, double[] lutB)
        {
            _lastApplied[monitor.DeviceName] = (lutR, lutG, lutB);
            // Store the SAME key the bypass registry is keyed on (CalibrationStateManager.BypassKey:
            // device path, else GDI device name) so the ramp guard's IsDeviceInBypass lookup agrees
            // with EnterBypassMode's registration even when MonitorDevicePath is empty.
            _deviceNameToPath[monitor.DeviceName] = CalibrationStateManager.BypassKey(monitor);
        }

        public void ClearGamma(MonitorInfo monitor)
        {
             // The ramp is reset (or in an unknown state on failure) either way.
             _lastApplied.TryRemove(monitor.DeviceName, out _);
             _deviceNameToPath.TryRemove(monitor.DeviceName, out _);

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
            sb.AppendLine("DESCRIPTOR \"Gloam Generated Calibration\"");
            sb.AppendLine("ORIGINATOR \"Gloam\"");
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
