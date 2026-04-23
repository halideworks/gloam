using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// A long-running spotread.exe session used to drive a whole calibration from a single
    /// process. This replaces the old per-patch <c>spotread -O</c> pattern which spawned a
    /// fresh process for every measurement — that pattern was fragile (USB re-enumeration
    /// between patches, silent exit-zero on connect failure, no way to distinguish
    /// "couldn't connect" from "measurement returned nothing") and produced the "gets to
    /// the patches then doesn't read" symptom observed on i1 Display Plus hardware.
    ///
    /// DisplayCAL uses the same pattern this class implements: start spotread in
    /// interactive mode with ARGYLL_NOT_INTERACTIVE=1, wait for the ready prompt, then
    /// send a single character + newline per measurement and parse the XYZ line from
    /// stdout.
    /// </summary>
    internal sealed class SpotreadSession : IAsyncDisposable
    {
        // "Result is XYZ: X Y Z, D50 Lab: ..." — we also accept bare "XYZ:" lines some
        // builds emit in non-interactive mode.
        private static readonly Regex XyzPattern = new(
            @"XYZ:\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s+([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s+([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Fragments that indicate spotread is ready to accept the next measurement trigger.
        // Exact wording varies across Argyll versions, so we match fragments not whole lines.
        private static readonly string[] ReadyFragments =
        {
            "any other key to take",
            "to take a reading",
            "to commence",
            "hit esc or q",
            "place instrument",
            "instrument is ready"
        };

        // Fragments that indicate a hard failure — no point waiting for measurement to arrive.
        private static readonly string[] FatalFragments =
        {
            "failed to open",
            "not connected",
            "instrument did not",
            "unable to initialise",
            "no instrument found",
            "couldn't find an instrument",
            "error: ",
            "error-"
        };

        private readonly Process _process;
        private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _stateLock = new();
        private readonly StringBuilder _recentLog = new();
        private TaskCompletionSource<CieXyz>? _pendingMeasurement;
        private volatile string? _fatalError;
        private volatile bool _disposed;
        private readonly Action<string> _log;

        private SpotreadSession(Process process, Action<string> log)
        {
            _process = process;
            _log = log;
            process.OutputDataReceived += (_, e) => OnLine(e.Data, isError: false);
            process.ErrorDataReceived  += (_, e) => OnLine(e.Data, isError: true);
            process.Exited += OnExited;
        }

        /// <summary>
        /// Spawns a spotread.exe in interactive mode and waits for the ready prompt.
        /// Throws if spotread can't talk to the instrument, with a diagnostic that
        /// includes the last few lines of spotread output (not just "no color data").
        /// </summary>
        public static async Task<SpotreadSession> StartAsync(
            string spotreadPath,
            int instrumentIndex,
            DisplayType displayType,
            bool hdrMode,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            string displayFlag = displayType.ToSpotreadFlag();
            // Deliberately NO -O: that flag is broken for our use case — it exits before
            // emitting a usable prompt when stdin is redirected and stdout is piped, so
            // our original "wait for prompt, then send input" loop never fired.
            //
            // -N disables the colorimeter's initial dark calibration. i1 Display Plus
            // doesn't need a lens cap calibration, so this is safe and avoids a ~5s stall.
            string args = $"-N -c {instrumentIndex} -e -y {displayFlag}";
            if (hdrMode) args += " -H";

            var psi = new ProcessStartInfo(spotreadPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(spotreadPath)
            };

            // Puts spotread into line-at-a-time mode so WriteAsync(" \r\n") triggers a
            // measurement rather than being swallowed by its raw-key reader.
            psi.Environment["ARGYLL_NOT_INTERACTIVE"] = "1";

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var session = new SpotreadSession(process, log);

            log($"SpotreadSession starting: {spotreadPath} {args}");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Connection-phase timeout. On healthy hardware spotread takes 1–3s to open
            // the HID, initialize the i1D3 firmware, and emit the ready prompt.
            // 15s gives plenty of margin without making a stuck process feel like the
            // app has hung.
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(TimeSpan.FromSeconds(15));

            try
            {
                await session._readyTcs.Task.WaitAsync(startupCts.Token);
                log("SpotreadSession ready.");
                return session;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await session.DisposeAsync();
                throw;
            }
            catch (OperationCanceledException)
            {
                string tail = session.SnapshotRecentLog();
                await session.DisposeAsync();
                throw new InvalidOperationException(
                    "Colorimeter did not become ready within 15 seconds. " +
                    "Another application (DisplayCAL's profile loader, i1Profiler) may be " +
                    "holding the device, or the USB driver needs to be installed.\n\n" +
                    "Recent spotread output:\n" + tail);
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// Triggers a single measurement and returns the XYZ result.
        /// Call serially — only one measurement may be in flight per session.
        /// </summary>
        public async Task<CieXyz> MeasureAsync(CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SpotreadSession));
            if (_fatalError != null)
                throw new InvalidOperationException($"spotread session failed: {_fatalError}");
            if (_process.HasExited)
                throw new InvalidOperationException(
                    $"spotread exited unexpectedly (code {_process.ExitCode}).\nRecent output:\n{SnapshotRecentLog()}");

            var tcs = new TaskCompletionSource<CieXyz>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateLock)
            {
                if (_pendingMeasurement != null)
                    throw new InvalidOperationException("Another measurement is already in progress on this session.");
                _pendingMeasurement = tcs;
            }

            try
            {
                // Space + CRLF. Space = "take reading" under ARGYLL_NOT_INTERACTIVE.
                // CRLF (not just LF) — Windows-line-ending quirk in spotread's scripted mode.
                await _process.StandardInput.WriteAsync(" \r\n");
                await _process.StandardInput.FlushAsync();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Measurement timed out after 30s. Recent spotread output:\n{SnapshotRecentLog()}");
            }
            finally
            {
                lock (_stateLock) _pendingMeasurement = null;
            }
        }

        private void OnLine(string? line, bool isError)
        {
            if (string.IsNullOrEmpty(line)) return;

            _log((isError ? "stderr> " : "stdout> ") + line);
            lock (_stateLock)
            {
                _recentLog.Append(isError ? "[e] " : "[o] ").AppendLine(line);
                // Keep the recent-log window bounded so a very chatty spotread doesn't
                // let it grow without bound over a long calibration.
                if (_recentLog.Length > 4000) _recentLog.Remove(0, _recentLog.Length - 2000);
            }

            // 1. Did a measurement just complete?
            var xyzMatch = XyzPattern.Match(line);
            if (xyzMatch.Success &&
                double.TryParse(xyzMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(xyzMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
                double.TryParse(xyzMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
            {
                TaskCompletionSource<CieXyz>? pending;
                lock (_stateLock) pending = _pendingMeasurement;
                pending?.TrySetResult(new CieXyz(x, y, z));
                return;
            }

            string lower = line.ToLowerInvariant();

            // 2. Are we ready to accept the next measurement?
            if (!_readyTcs.Task.IsCompleted)
            {
                foreach (var fragment in ReadyFragments)
                {
                    if (lower.Contains(fragment))
                    {
                        _readyTcs.TrySetResult(true);
                        break;
                    }
                }
            }

            // 3. Did something go wrong at the spotread level?
            foreach (var fragment in FatalFragments)
            {
                if (lower.Contains(fragment))
                {
                    _fatalError = line;
                    var ex = new InvalidOperationException($"spotread reported: {line.Trim()}");
                    _readyTcs.TrySetException(ex);
                    lock (_stateLock) _pendingMeasurement?.TrySetException(ex);
                    return;
                }
            }
        }

        private void OnExited(object? sender, EventArgs e)
        {
            // Unblock anyone waiting — they need to know the process is gone.
            int code;
            try { code = _process.ExitCode; } catch { code = -1; }
            string msg = _fatalError
                ?? $"spotread exited (code {code}) before producing a measurement. " +
                   $"Last output:\n{SnapshotRecentLog()}";
            var ex = new InvalidOperationException(msg);
            _readyTcs.TrySetException(ex);
            lock (_stateLock) _pendingMeasurement?.TrySetException(ex);
        }

        private string SnapshotRecentLog()
        {
            lock (_stateLock) return _recentLog.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (!_process.HasExited)
                {
                    // Polite exit: 'q' is spotread's quit command under ARGYLL_NOT_INTERACTIVE.
                    try
                    {
                        await _process.StandardInput.WriteAsync("q\r\n");
                        await _process.StandardInput.FlushAsync();
                        _process.StandardInput.Close();
                    }
                    catch { /* pipe may already be broken */ }

                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await _process.WaitForExitAsync(cts.Token);
                    }
                    catch
                    {
                        // Didn't quit cleanly; force it.
                        try { _process.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
