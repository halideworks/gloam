using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Service for communicating with colorimeters via ArgyllCMS spotread.
    /// Supports i1 Display Plus and other ArgyllCMS-compatible instruments.
    /// </summary>
    /// <remarks>
    /// This service uses ArgyllCMS's spotread utility for measurements.
    /// spotread is a command-line tool that reads a single color from a display
    /// using a connected colorimeter.
    ///
    /// Supported instruments (via ArgyllCMS):
    /// - X-Rite i1 Display Pro/Plus
    /// - X-Rite i1 Pro/Pro2
    /// - X-Rite ColorMunki
    /// - Datacolor Spyder series
    /// - And many others
    /// </remarks>
    public class ColorimeterService : IDisposable
    {
        private readonly string _argyllBinPath;
        private string? _spotreadPath;
        private bool _isInitialized;
        private bool _isInitializing; // Guards against concurrent InitializeAsync calls
        private ColorimeterInfo? _connectedColorimeter;
        private int _displayIndex = 1;
        private DisplayType _displayType = DisplayType.LcdLed;
        private readonly object _lock = new();

        // Persistent spotread session. Created by BeginMeasurementSessionAsync at the
        // start of a calibration and disposed by EndMeasurementSessionAsync. While it's
        // active every MeasureAsync call reuses the same spotread process instead of
        // spawning a fresh one per patch — which is what DisplayCAL does and is what
        // fixed the "starts patches but doesn't read" failure on i1 Display Plus.
        private SpotreadSession? _session;
        private bool _sessionHdrMode;

        // Log file for debugging spotread communication. Resolved dynamically so isolated
        // smoke/test data roots do not accidentally write to the user's real data folder.
        private static string LogFilePath => Path.Combine(AppPaths.DataDir, "colorimeter.log");

        // Serialize appends: spotread emits on stdout/stderr, and the persistent session plus
        // any transient one-shot measurement can both be logging concurrently.
        private static readonly object _logLock = new();
        private const long MaxLogBytes = 5_000_000;
        private const int MaxLogArchives = 3;

        private static void Log(string message)
        {
            try
            {
                string logDir = Path.GetDirectoryName(LogFilePath)!;
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                // AppendAllText itself isn't atomic across concurrent callers; a short lock keeps
                // lines intact and ordered (the file is append-only and tiny).
                lock (_logLock)
                {
                    LogFileRotator.RotateIfNeeded(LogFilePath, MaxLogBytes, MaxLogArchives);
                    File.AppendAllText(LogFilePath, $"[{timestamp}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Ignore logging errors
            }
        }

        /// <summary>
        /// Gets the path to the log file for debugging.
        /// </summary>
        public static string GetLogFilePath() => LogFilePath;

        /// <summary>
        /// Event raised when a measurement completes.
        /// </summary>
        public event EventHandler<MeasurementEventArgs>? MeasurementCompleted;

        /// <summary>
        /// Event raised when there's an error during measurement.
        /// </summary>
        public event EventHandler<MeasurementErrorEventArgs>? MeasurementError;

        /// <summary>
        /// Event raised when colorimeter status changes.
        /// </summary>
        public event EventHandler<ColorimeterStatusEventArgs>? StatusChanged;

        /// <summary>
        /// Gets the currently connected colorimeter info.
        /// </summary>
        public ColorimeterInfo? ConnectedColorimeter => _connectedColorimeter;

        /// <summary>
        /// Gets whether a colorimeter is connected and ready.
        /// Virtual so orchestrator-level tests can substitute a fake service.
        /// </summary>
        public virtual bool IsReady => _isInitialized && _connectedColorimeter != null;

        /// <summary>
        /// Creates a new ColorimeterService.
        /// </summary>
        /// <param name="argyllBinPath">Path to ArgyllCMS bin directory (containing spotread.exe)</param>
        public ColorimeterService(string argyllBinPath)
        {
            _argyllBinPath = argyllBinPath;
        }

        /// <summary>
        /// Initializes the service and detects connected colorimeters.
        /// </summary>
        /// <remarks>
        /// Thread-safe: Multiple concurrent calls will only perform initialization once.
        /// Subsequent calls after successful initialization return immediately.
        /// Failed initialization can be retried by calling again.
        /// </remarks>
        public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            // Fast path: already initialized successfully
            lock (_lock)
            {
                if (_isInitialized && _connectedColorimeter != null)
                    return true;
            }

            // Slow path: need to initialize (but only one thread at a time)
            // Note: We can't hold the lock across await, so we use a flag pattern
            bool shouldInitialize;
            lock (_lock)
            {
                // Double-check after acquiring lock
                if (_isInitialized && _connectedColorimeter != null)
                    return true;

                // Mark that we're about to initialize (prevent concurrent attempts)
                // _isInitialized remains false until we successfully complete
                shouldInitialize = !_isInitializing;
                if (shouldInitialize)
                    _isInitializing = true;
            }

            // If another thread is already initializing, wait for it
            if (!shouldInitialize)
            {
                // Poll until initialization completes, with a maximum timeout of 60 seconds
                const int maxWaitMs = 60000;
                const int pollIntervalMs = 50;
                int elapsed = 0;

                while (elapsed < maxWaitMs)
                {
                    await Task.Delay(pollIntervalMs, cancellationToken);
                    elapsed += pollIntervalMs;
                    lock (_lock)
                    {
                        if (!_isInitializing)
                            return _isInitialized && _connectedColorimeter != null;
                    }
                }

                // Timeout waiting for another thread to complete initialization
                throw new TimeoutException("Timeout waiting for colorimeter initialization to complete");
            }

            try
            {
                // Find spotread executable
                _spotreadPath = FindSpotread();
                if (string.IsNullOrEmpty(_spotreadPath))
                {
                    RaiseStatusChanged(ColorimeterStatus.NotFound,
                        "ArgyllCMS spotread not found. Please install ArgyllCMS.");
                    return false;
                }

                RaiseStatusChanged(ColorimeterStatus.Searching, "Searching for colorimeter...");

                // Detect connected colorimeter
                _connectedColorimeter = await DetectColorimeterAsync(cancellationToken);

                if (_connectedColorimeter != null)
                {
                    // Give the USB HID device time to fully release after detection
                    // This prevents "sharing violation" errors when measurement starts
                    await Task.Delay(500, cancellationToken);

                    if (_connectedColorimeter.InstrumentIndex != null)
                    {
                        Log($"Instrument index: {_connectedColorimeter.InstrumentIndex} ({_connectedColorimeter.InstrumentDescriptor ?? "no descriptor"})");
                    }

                    lock (_lock)
                    {
                        _isInitialized = true;
                    }
                    RaiseStatusChanged(ColorimeterStatus.Ready,
                        $"Connected: {_connectedColorimeter.Model}");
                    return true;
                }
                else
                {
                    RaiseStatusChanged(ColorimeterStatus.NotConnected,
                        "No colorimeter detected. Please connect your i1 Display Plus.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                RaiseStatusChanged(ColorimeterStatus.Error, $"Initialization failed: {ex.Message}");
                return false;
            }
            finally
            {
                lock (_lock)
                {
                    _isInitializing = false;
                }
            }
        }

        /// <summary>
        /// Sets the display index for measurements (1-based).
        /// </summary>
        public void SetDisplayIndex(int index)
        {
            if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
            _displayIndex = index;
        }

        /// <summary>
        /// Sets the display type for measurements (affects colorimeter compensation).
        /// </summary>
        public void SetDisplayType(DisplayType type)
        {
            _displayType = type;
            Log($"Display type set to: {type} (flag: -{type.ToSpotreadFlag()})");
        }

        /// <summary>
        /// Gets the current display type setting.
        /// </summary>
        public DisplayType DisplayType => _displayType;

        private string? _correctionFilePath;

        /// <summary>
        /// Sets the spectral correction file (.ccss/.ccmx) passed to spotread via -X.
        /// Null clears it (generic display-type correction is used instead).
        /// </summary>
        public void SetCorrectionFile(string? path)
        {
            _correctionFilePath = string.IsNullOrWhiteSpace(path) ? null : path;
            Log($"Meter correction file set to: {_correctionFilePath ?? "(built-in for display type)"}");
        }

        /// <summary>
        /// Takes a single color measurement.
        /// </summary>
        /// <param name="patch">The patch being measured (for context)</param>
        /// <param name="hdrMode">Whether to use HDR measurement mode</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The measurement result</returns>
        /// <remarks>Virtual so orchestrator-level tests can substitute a fake service.</remarks>
        public virtual async Task<MeasurementResult> MeasureAsync(
            ColorPatch patch,
            bool hdrMode = false,
            CancellationToken cancellationToken = default)
        {
            if (!IsReady)
                throw new InvalidOperationException("Colorimeter not initialized. Call InitializeAsync first.");

            Log($"=== Starting measurement for patch: {patch.Name} (RGB: {patch.DisplayRgb.R:F3},{patch.DisplayRgb.G:F3},{patch.DisplayRgb.B:F3}) HDR={hdrMode} ===");

            try
            {
                var xyz = await TakeMeasurementAsync(hdrMode, cancellationToken);

                var result = new MeasurementResult
                {
                    Patch = patch,
                    Xyz = xyz,
                    IsValid = true
                };

                MeasurementCompleted?.Invoke(this, new MeasurementEventArgs(result));
                return result;
            }
            catch (UsbDriverException ex)
            {
                MeasurementError?.Invoke(this, new MeasurementErrorEventArgs(ex.Message, patch));
                throw;
            }
            catch (Exception ex)
            {
                var result = new MeasurementResult
                {
                    Patch = patch,
                    Xyz = new CieXyz(0, 0, 0),
                    IsValid = false,
                    ErrorMessage = ex.Message
                };

                MeasurementError?.Invoke(this, new MeasurementErrorEventArgs(ex.Message, patch));
                return result;
            }
        }

        /// <summary>
        /// Opens a persistent spotread session for a calibration run. Call once at the
        /// start of a calibration, then take however many measurements are needed, then
        /// call <see cref="EndMeasurementSessionAsync"/>. Surfaces connection failures
        /// here rather than partway through the patch loop.
        /// </summary>
        public virtual async Task BeginMeasurementSessionAsync(bool hdrMode, CancellationToken cancellationToken = default)
        {
            if (!IsReady)
                throw new InvalidOperationException("Colorimeter not initialized. Call InitializeAsync first.");
            if (string.IsNullOrEmpty(_spotreadPath))
                throw new InvalidOperationException("spotread path not configured.");

            // If a session already exists for the right mode, reuse it. If HDR mode changed,
            // close and reopen: no spotread flag differs between the modes (-H is high-res
            // SPECTRAL mode for spectrometers, not an HDR switch — see SpotreadSession), but
            // the OS display mode flips between the SDR and HDR phases and a fresh instrument
            // initialization across that transition is deliberate.
            if (_session != null)
            {
                if (_sessionHdrMode == hdrMode) return;
                await EndMeasurementSessionAsync();
            }

            int instrumentIndex = _connectedColorimeter?.InstrumentIndex ?? 1;
            Log($"Opening persistent spotread session (instrument {instrumentIndex}, HDR={hdrMode})");
            try
            {
                _session = await SpotreadSession.StartAsync(
                    _spotreadPath, instrumentIndex, _displayType, hdrMode, Log, cancellationToken,
                    _correctionFilePath);
                _sessionHdrMode = hdrMode;
                RaiseStatusChanged(ColorimeterStatus.Ready, "Spotread session ready");
            }
            catch (Exception ex)
            {
                Log($"Session startup failed: {ex.Message}");
                _session = null;
                // Translate common failures into a clearer UsbDriverException when warranted,
                // so the UI's existing driver-install offer flow still fires.
                if (UsbDriverHelper.IsDriverError(ex.Message))
                    throw new UsbDriverException(
                        "Colorimeter communication failed - the ArgyllCMS USB driver may be missing or another application holds the device.\n" + ex.Message, ex);
                throw;
            }
        }

        /// <summary>
        /// Closes the persistent spotread session. Safe to call if no session is active.
        /// </summary>
        public virtual async Task EndMeasurementSessionAsync()
        {
            if (_session == null) return;
            Log("Closing spotread session");
            try { await _session.DisposeAsync(); }
            catch (Exception ex) { Log($"Session close error (non-fatal): {ex.Message}"); }
            _session = null;
        }

        /// <summary>
        /// Takes a raw XYZ measurement. Uses the persistent session if one is open;
        /// otherwise opens a transient single-shot session for this one measurement
        /// (which still uses the session code path — no more broken <c>-O</c>).
        /// </summary>
        private async Task<CieXyz> TakeMeasurementAsync(bool hdrMode, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_spotreadPath))
                throw new InvalidOperationException("spotread path not configured");

            // Happy path: an orchestrated calibration has already opened a session.
            if (_session != null && _sessionHdrMode == hdrMode)
            {
                // C1 stale-reading protection: a session that hit a per-measurement timeout
                // is poisoned — its timed-out trigger is still queued inside spotread, and
                // reusing the process would pair that late reading with the NEXT patch
                // (silent off-by-one for the rest of the run). Kill it and start fresh
                // before measuring. The retry loop in the orchestrator lands here on its
                // next attempt, so a single timeout costs one session restart, not the run.
                if (_session.IsPoisoned)
                {
                    Log("spotread session is poisoned (measurement timeout) - restarting it before the next measurement");
                    await EndMeasurementSessionAsync();
                    await BeginMeasurementSessionAsync(hdrMode, cancellationToken);
                }

                try
                {
                    return await _session!.MeasureAsync(cancellationToken);
                }
                catch (InvalidOperationException ex) when (UsbDriverHelper.IsDriverError(ex.Message))
                {
                    throw new UsbDriverException(ex.Message, ex);
                }
            }

            // Transient path: callers who skip the Begin/End lifecycle (e.g. ad-hoc one-off
            // measurements from tooling) still get correct behavior, just with the extra
            // cost of spotread startup/shutdown per call.
            Log($"No active session - opening transient one (HDR={hdrMode})");
            int instrumentIndex = _connectedColorimeter?.InstrumentIndex ?? 1;
            SpotreadSession transient;
            try
            {
                transient = await SpotreadSession.StartAsync(
                    _spotreadPath, instrumentIndex, _displayType, hdrMode, Log, cancellationToken,
                    _correctionFilePath);
            }
            catch (InvalidOperationException ex) when (UsbDriverHelper.IsDriverError(ex.Message))
            {
                throw new UsbDriverException(ex.Message, ex);
            }

            await using (transient)
            {
                return await transient.MeasureAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Detects connected colorimeters using spotread.
        /// </summary>
        private async Task<ColorimeterInfo?> DetectColorimeterAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_spotreadPath))
                return null;

            Log("=== Starting colorimeter detection ===");

            // Use help output to get the instrument list (spotread doesn't support -l)
            var listResult = await RunSpotreadCommandAsync(TimeSpan.FromSeconds(10), cancellationToken, "-?");
            if (listResult != null)
            {
                Log($"spotread -? output: {listResult}");

                // Parse actual connected device from list
                var deviceInfo = ParseDeviceListOutput(listResult);
                if (deviceInfo != null)
                {
                    Log($"Detected device from -?: {deviceInfo.Model}");
                    return deviceInfo;
                }
            }
            // If no errors, assume device is present
            if (listResult != null &&
                !listResult.Contains("No colorimeter", StringComparison.OrdinalIgnoreCase) &&
                !listResult.Contains("Error", StringComparison.OrdinalIgnoreCase) &&
                !listResult.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                Log("No explicit errors - assuming device present");
                return new ColorimeterInfo
                {
                    Model = "Colorimeter Detected",
                    IsHdrCapable = true // Assume capable, actual capability tested during measurement
                };
            }

            Log("No colorimeter detected");
            return null;
        }

        /// <summary>
        /// Runs a spotread command and returns the combined output.
        /// </summary>
        private async Task<string?> RunSpotreadCommandAsync(TimeSpan timeout, CancellationToken cancellationToken, params string[] args)
        {
            if (string.IsNullOrEmpty(_spotreadPath))
                return null;

            var psi = new ProcessStartInfo(_spotreadPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return null;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);

                var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                string output, error;
                try
                {
                    output = await stdoutTask;
                    error = await stderrTask;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                return output + "\n" + error;
            }
            catch (Exception ex)
            {
                Log($"Error running spotread {args}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parses device information from spotread output.
        /// </summary>
        private static ColorimeterInfo? ParseDeviceListOutput(string output)
        {
            // Common colorimeter patterns in ArgyllCMS output
            var devicePatterns = new[]
            {
                // X-Rite devices
                (@"i1\s*Display\s*(?:Pro|Plus|3)?", true),
                (@"i1\s*Pro\s*\d*", true),
                (@"ColorMunki\s*(?:Display|Design|Photo)?", true),
                // Datacolor devices
                (@"Spyder\s*(?:\d+|X|X2)?(?:\s*(?:Pro|Express|Elite))?", false),
                (@"Spyder\s*\w+", false),
                // Others
                (@"Huey\s*(?:Pro)?", false),
                (@"DTP94", false),
                (@"Eye-One", true),
                (@"i1 Studio", true),
            };

            // Try to parse explicit instrument list lines with indices.
            // Example: "1 = 'hid:/10 (X-Rite i1 DisplayPro, ColorMunki Display)'"
            var listMatches = Regex.Matches(output, @"(?m)^\s*(\d+)\s*=\s*'([^']+)'");
            foreach (Match entry in listMatches)
            {
                if (!int.TryParse(entry.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                    continue;

                string descriptor = entry.Groups[2].Value;
                foreach (var (pattern, isHdrCapable) in devicePatterns)
                {
                    var match = Regex.Match(descriptor, pattern, RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        return new ColorimeterInfo
                        {
                            Model = match.Value.Trim(),
                            IsHdrCapable = isHdrCapable,
                            InstrumentIndex = index,
                            InstrumentDescriptor = descriptor
                        };
                    }
                }
            }

            if (listMatches.Count > 0)
            {
                var firstEntry = listMatches[0];
                if (int.TryParse(firstEntry.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                {
                    string descriptor = firstEntry.Groups[2].Value;
                    return new ColorimeterInfo
                    {
                        Model = "Colorimeter Detected",
                        IsHdrCapable = true,
                        InstrumentIndex = index,
                        InstrumentDescriptor = descriptor
                    };
                }
            }

            foreach (var (pattern, isHdrCapable) in devicePatterns)
            {
                var match = Regex.Match(output, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return new ColorimeterInfo
                    {
                        Model = match.Value.Trim(),
                        IsHdrCapable = isHdrCapable
                    };
                }
            }

            // Also look for USB device strings that indicate a colorimeter is present
            if (output.Contains("USB", StringComparison.OrdinalIgnoreCase) &&
                (output.Contains("HID", StringComparison.OrdinalIgnoreCase) ||
                 output.Contains("Instrument", StringComparison.OrdinalIgnoreCase)))
            {
                return new ColorimeterInfo
                {
                    Model = "USB Colorimeter",
                    IsHdrCapable = false
                };
            }

            return null;
        }

        /// <summary>
        /// Finds the spotread executable.
        /// </summary>
        private string? FindSpotread()
        {
            // Check configured path first
            string spotreadPath = Path.Combine(_argyllBinPath, "spotread.exe");
            if (File.Exists(spotreadPath))
                return spotreadPath;

            // Check without .exe (Unix compatibility)
            spotreadPath = Path.Combine(_argyllBinPath, "spotread");
            if (File.Exists(spotreadPath))
                return spotreadPath;

            // Use unified path finder for comprehensive search
            string? binPath = ArgyllPathFinder.FindArgyllBinPath();
            if (binPath != null)
            {
                spotreadPath = Path.Combine(binPath, "spotread.exe");
                if (File.Exists(spotreadPath))
                    return spotreadPath;

                // Unix compatibility
                spotreadPath = Path.Combine(binPath, "spotread");
                if (File.Exists(spotreadPath))
                    return spotreadPath;
            }

            return null;
        }

        private void RaiseStatusChanged(ColorimeterStatus status, string message)
        {
            StatusChanged?.Invoke(this, new ColorimeterStatusEventArgs(status, message));
        }

        /// <summary>
        /// Gets the path to the ArgyllCMS USB driver installer if available.
        /// </summary>
        private static string GetUsbDriverInstallerPath()
        {
            string? discoveredBin = ArgyllPathFinder.FindArgyllBinPath();
            if (!string.IsNullOrEmpty(discoveredBin))
            {
                string? root = Directory.GetParent(discoveredBin)?.FullName;
                if (root != null)
                {
                    string discovered = Path.Combine(root, "usb", "ArgyllCMS_install_USB.exe");
                    if (File.Exists(discovered)) return discovered;
                }
            }

            // Check our downloaded ArgyllCMS first
            string localArgyllDir = Path.Combine(AppPaths.DataDir, "Argyll");

            string installerPath = Path.Combine(localArgyllDir, "usb", "ArgyllCMS_install_USB.exe");
            if (File.Exists(installerPath))
                return installerPath;

            // Check standard installation paths
            var searchPaths = new[]
            {
                @"C:\Program Files\ArgyllCMS",
                @"C:\Program Files (x86)\ArgyllCMS",
                @"C:\Program Files\Argyll_V3.5.0",
                @"C:\Program Files\Argyll_V3.3.0",
                @"C:\ArgyllCMS"
            };

            foreach (var basePath in searchPaths)
            {
                installerPath = Path.Combine(basePath, "usb", "ArgyllCMS_install_USB.exe");
                if (File.Exists(installerPath))
                    return installerPath;
            }

            return string.Empty;
        }

        public void Dispose()
        {
            // Close any dangling spotread session. We use fire-and-forget here because
            // Dispose() can't be async; the session's DisposeAsync kills the process on
            // a 3-second timeout so this won't block long in practice.
            var session = _session;
            _session = null;
            if (session != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await session.DisposeAsync(); }
                    catch { /* already disposed or process gone */ }
                });
            }
        }
    }

    /// <summary>
    /// Information about a connected colorimeter.
    /// </summary>
    public class ColorimeterInfo
    {
        /// <summary>
        /// Colorimeter model name (e.g., "i1 Display Plus").
        /// </summary>
        public required string Model { get; init; }

        /// <summary>
        /// Instrument list index from spotread (used with -c).
        /// </summary>
        public int? InstrumentIndex { get; init; }

        /// <summary>
        /// Raw instrument descriptor from spotread (e.g., "hid:/10 (X-Rite i1 DisplayPro, ColorMunki Display)").
        /// </summary>
        public string? InstrumentDescriptor { get; init; }

        /// <summary>
        /// Whether this colorimeter supports HDR measurement modes.
        /// </summary>
        public bool IsHdrCapable { get; init; }

        /// <summary>
        /// Serial number if available.
        /// </summary>
        public string? SerialNumber { get; init; }

        /// <summary>
        /// Firmware version if available.
        /// </summary>
        public string? FirmwareVersion { get; init; }

        public override string ToString() => Model;
    }

    /// <summary>
    /// Colorimeter connection status.
    /// </summary>
    public enum ColorimeterStatus
    {
        /// <summary>Status not yet determined.</summary>
        Unknown,

        /// <summary>ArgyllCMS/spotread not found.</summary>
        NotFound,

        /// <summary>Searching for connected colorimeter.</summary>
        Searching,

        /// <summary>No colorimeter connected.</summary>
        NotConnected,

        /// <summary>Colorimeter connected and ready.</summary>
        Ready,

        /// <summary>Currently taking a measurement.</summary>
        Measuring,

        /// <summary>Error occurred.</summary>
        Error
    }

    /// <summary>
    /// Event args for measurement completion.
    /// </summary>
    public class MeasurementEventArgs : EventArgs
    {
        public MeasurementResult Result { get; }

        public MeasurementEventArgs(MeasurementResult result)
        {
            Result = result;
        }
    }

    /// <summary>
    /// Event args for measurement errors.
    /// </summary>
    public class MeasurementErrorEventArgs : EventArgs
    {
        public string Message { get; }
        public ColorPatch? Patch { get; }

        public MeasurementErrorEventArgs(string message, ColorPatch? patch = null)
        {
            Message = message;
            Patch = patch;
        }
    }

    /// <summary>
    /// Event args for colorimeter status changes.
    /// </summary>
    public class ColorimeterStatusEventArgs : EventArgs
    {
        public ColorimeterStatus Status { get; }
        public string Message { get; }

        public ColorimeterStatusEventArgs(ColorimeterStatus status, string message)
        {
            Status = status;
            Message = message;
        }
    }

    /// <summary>
    /// Exception thrown when colorimeter communication fails due to USB driver issues.
    /// This indicates the ArgyllCMS USB drivers need to be installed.
    /// </summary>
    public class UsbDriverException : Exception
    {
        public UsbDriverException(string message) : base(message) { }
        public UsbDriverException(string message, Exception innerException) : base(message, innerException) { }
    }
}
