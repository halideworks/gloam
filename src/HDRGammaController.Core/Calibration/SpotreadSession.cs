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
        // These phrasings are specific enough to match anywhere in a line.
        private static readonly string[] FatalFragments =
        {
            "failed to open",
            "not connected",
            "instrument did not",
            "unable to initialise",
            "no instrument found",
            "couldn't find an instrument"
        };

        // Generic error tokens are only fatal when they START the message. Matching
        // "error: "/"error-" anywhere in the line (as this class used to) aborts runs on
        // harmless instrument-info output that merely mentions the word (e.g. correction
        // descriptions or verbose status lines containing "... error compensation ...").
        // Argyll prefixes its real failures as either "Error - ..." / "error: ..." at the
        // start of the line, or "spotread: Error - ..."; both shapes are handled here.
        private static readonly string[] FatalLinePrefixes =
        {
            "error: ",
            "error - ",
            "error-"
        };

        private static bool IsFatalErrorLine(string lowerLine)
        {
            string t = lowerLine.TrimStart();
            const string toolPrefix = "spotread:";
            if (t.StartsWith(toolPrefix, StringComparison.Ordinal))
                t = t.Substring(toolPrefix.Length).TrimStart();

            foreach (var prefix in FatalLinePrefixes)
            {
                if (t.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // When spotread hit ERROR_SHARING_VIOLATION (err 32) opening the HID handle, it
        // means another process has the device open. V2.3.1 reports this clearly; V3.3.0
        // silently swallows the error and exits 0. See the `OpenedHidOk` marker below for
        // how we detect the V3.3.0 variant.
        private static readonly string[] SharingViolationFragments =
        {
            "err 32",
            "error 32",
            "lasterror 32",
            "sharing violation"
        };

        // Markers that mean "spotread has started trying to connect to the instrument."
        // If any of these appear and the process later exits 0 without a ready prompt,
        // the HID open silently failed — almost always a sharing violation.
        //
        // "connecting to the instrument" is printed with plain -v, which is what we use.
        // "about to open HID port" only shows with -D 9 full debug; kept as a fallback
        // in case we ever turn debug up.
        private static readonly string[] HidOpenAttemptMarkers =
        {
            "connecting to the instrument",
            "about to open hid port"
        };

        private readonly Process? _process;
        private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _stateLock = new();
        private readonly StringBuilder _recentLog = new();
        private TaskCompletionSource<CieXyz>? _pendingMeasurement;
        private volatile string? _fatalError;
        private volatile bool _disposed;
        private volatile bool _attemptedHidOpen;
        private volatile bool _sharingViolationSeen;
        private volatile bool _poisoned;
        private readonly Action<string> _log;

        // Process interaction seams. Production wires these to the real spotread process;
        // the internal test constructor substitutes fakes so the trigger/timeout/poison
        // protocol can be exercised without spawning an executable.
        private readonly Func<string, Task> _writeInput;
        private readonly Func<bool> _hasExited;
        private readonly Action _killProcess;

        // Per-measurement timeout. Internal-settable so tests don't wait 30 real seconds.
        internal TimeSpan MeasureTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// True once a per-measurement timeout has occurred. A timed-out trigger is still
        /// queued inside spotread: when its XYZ line eventually arrives it would complete
        /// the NEXT measurement's wait, silently attributing a stale reading to the wrong
        /// patch (off-by-one for the rest of the run). A poisoned session refuses further
        /// measurements; the owner must kill it and start a fresh session.
        /// </summary>
        public bool IsPoisoned => _poisoned;

        private SpotreadSession(Process process, Action<string> log)
        {
            _process = process;
            _log = log;
            _writeInput = async text =>
            {
                await process.StandardInput.WriteAsync(text);
                await process.StandardInput.FlushAsync();
            };
            _hasExited = () => process.HasExited;
            _killProcess = () =>
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            };
            process.OutputDataReceived += (_, e) => OnLine(e.Data, isError: false);
            process.ErrorDataReceived  += (_, e) => OnLine(e.Data, isError: true);
            process.Exited += OnExited;
        }

        /// <summary>
        /// Test-only constructor: runs the session protocol against fake process hooks.
        /// Output lines are injected via <see cref="SimulateOutputLine"/>.
        /// </summary>
        internal SpotreadSession(Func<string, Task> writeInput, Action killProcess, Action<string> log)
        {
            _process = null;
            _log = log;
            _writeInput = writeInput;
            _hasExited = () => false;
            _killProcess = killProcess;
        }

        /// <summary>Test-only: feeds a line as if spotread had printed it.</summary>
        internal void SimulateOutputLine(string line, bool isError = false) => OnLine(line, isError);

        /// <summary>Test-only: the fatal error recorded from output parsing, if any.</summary>
        internal string? FatalErrorForTest => _fatalError;

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
            CancellationToken cancellationToken,
            string? correctionFilePath = null)
        {
            string displayFlag = displayType.ToSpotreadFlag();
            // -v (verbose): makes Argyll 3.x print the hid_open_port lines we need to
            // distinguish "silently failed to open HID" (V3.3.0 bug, exits 0 with no
            // error) from "connecting normally". Without -v we lose the diagnostic.
            //
            // Deliberately NO -O: that flag is broken for our use case — it exits before
            // emitting a usable prompt when stdin is redirected and stdout is piped.
            //
            // -N disables the colorimeter's initial dark calibration. i1 Display Plus
            // doesn't need a lens cap calibration, so this is safe and avoids a ~5s stall.
            var args = new System.Collections.Generic.List<string>
            {
                "-v",
                "-N",
                "-c",
                instrumentIndex.ToString(CultureInfo.InvariantCulture),
                "-e",
                "-y",
                displayFlag
            };
            // NOTE (flag hygiene): spotread's -H flag is HIGH-RESOLUTION SPECTRAL mode and
            // only applies to spectrometers (i1 Pro etc.) — it is NOT an "HDR mode". This
            // session used to pass -H when hdrMode was true; on colorimeters (i1 Display
            // Plus and friends) that is at best ignored and at worst rejected. HDR vs SDR
            // needs no spotread flag at all: the instrument reports absolute cd/m² either
            // way. The hdrMode parameter is retained because the SDR and HDR measurement
            // phases still deliberately run in SEPARATE sessions (ColorimeterService keys
            // its persistent session on it), so the instrument re-initializes across the
            // OS display-mode flip.
            _ = hdrMode;
            // Spectral correction sample (.ccss) or matrix (.ccmx): replaces the generic
            // display-type calibration with one matched to the actual panel spectrum.
            // Essential on narrow-primary panels (QD-OLED) where the generic corrections
            // misplace the white point by several ΔE.
            if (!string.IsNullOrEmpty(correctionFilePath) && File.Exists(correctionFilePath))
            {
                // Defense-in-depth: never pass a malformed correction file to spotread — it
                // could come from a hand-edited path or a stale/older DB entry. CcssDatabaseClient
                // validates on save, but a file that landed here any other way is still checked.
                if (!CgatsValidator.IsValidFile(correctionFilePath))
                    throw new InvalidOperationException(
                        $"The meter correction file is not a valid CGATS correction:\n{correctionFilePath}\n" +
                        "Re-download it from the corrections database, or clear it to use the built-in display-type correction.");
                args.Add("-X");
                args.Add(correctionFilePath);
            }

            var psi = new ProcessStartInfo(spotreadPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(spotreadPath)
            };
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            // Puts spotread into line-at-a-time mode so WriteAsync(" \r\n") triggers a
            // measurement rather than being swallowed by its raw-key reader.
            psi.Environment["ARGYLL_NOT_INTERACTIVE"] = "1";

            // Kill any orphaned spotread from a previous run before starting. If the app was
            // closed mid-calibration, its spotread can outlive it and keep the colorimeter's
            // USB handle open, which makes the next calibration fail to connect. Only one
            // spotread should ever be talking to the probe at a time.
            KillStraySpotread(log);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var session = new SpotreadSession(process, log);

            log($"SpotreadSession starting: {spotreadPath} {string.Join(" ", args)}");
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
            if (_poisoned)
                throw new InvalidOperationException(
                    "spotread session was poisoned by a measurement timeout and must be restarted. " +
                    "The timed-out trigger is still queued inside spotread; reusing this session " +
                    "would attribute its late reading to the wrong patch.");
            if (_fatalError != null)
                throw new InvalidOperationException($"spotread session failed: {_fatalError}");
            if (_hasExited())
                throw new InvalidOperationException(
                    $"spotread exited unexpectedly (code {TryGetExitCode()}).\nRecent output:\n{SnapshotRecentLog()}");

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
                await _writeInput(" \r\n");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(MeasureTimeout);
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // CRITICAL: the trigger we just sent is still queued inside spotread. If we
                // simply retried on this session, its late XYZ line would complete the NEXT
                // measurement's wait — every subsequent reading would be off-by-one against
                // the displayed patch, silently. Poison the session and kill the process so
                // the owner is forced to start a fresh one before the next measurement.
                _poisoned = true;
                _log($"Measurement timed out after {MeasureTimeout.TotalSeconds:F0}s - poisoning session and killing spotread.");
                _killProcess();
                throw new TimeoutException(
                    $"Measurement timed out after {MeasureTimeout.TotalSeconds:F0}s. The spotread session will be " +
                    $"restarted before the next measurement. Recent spotread output:\n{SnapshotRecentLog()}");
            }
            finally
            {
                lock (_stateLock) _pendingMeasurement = null;
            }
        }

        private int TryGetExitCode()
        {
            try { return _process?.ExitCode ?? -1; } catch { return -1; }
        }

        /// <summary>
        /// Terminates a lingering spotread.exe spawned by THIS app (e.g. orphaned by a crash
        /// or by the app being closed mid-calibration) so it releases the colorimeter before
        /// we connect.
        ///
        /// Scoped: only spotread processes whose executable resolves to our own downloaded
        /// Argyll bin directory are touched. A system-wide <c>GetProcessesByName("spotread")</c>
        /// would also kill another user's calibration tool or a system-installed Argyll on a
        /// shared/multi-session machine. If we cannot resolve a process's path (access denied,
        /// 32/64-bit cross-resolution), we err on the side of NOT killing it.
        /// </summary>
        private static void KillStraySpotread(Action<string> log)
        {
            try
            {
                // Normalize once (lowercase, trailing separator) for prefix comparison.
                string ourBinDir;
                try
                {
                    ourBinDir = Path.GetFullPath(ArgyllDownloader.LocalArgyllBinDir)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .ToLowerInvariant();
                }
                catch
                {
                    // If we can't even resolve our own bin dir, don't kill anything — we'd be
                    // guessing. The HID sharing-violation path still surfaces a clear message.
                    return;
                }

                foreach (var p in Process.GetProcessesByName("spotread"))
                {
                    using (p)
                    {
                        string? exePath;
                        try { exePath = TryGetProcessImagePath(p); }
                        catch { continue; } // can't resolve -> leave it alone

                        if (string.IsNullOrEmpty(exePath)) continue;

                        string dir;
                        try { dir = Path.GetDirectoryName(exePath) ?? ""; }
                        catch { continue; }

                        dir = Path.GetFullPath(dir)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .ToLowerInvariant();

                        if (!dir.Equals(ourBinDir, StringComparison.Ordinal))
                        {
                            // Not ours — a system/other-user spotread. Leave it alone.
                            continue;
                        }

                        try
                        {
                            log($"Killing stray spotread (PID {p.Id}) from our Argyll bin dir holding the colorimeter");
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(2000);
                        }
                        catch { /* already gone / access denied — best effort */ }
                    }
                }
                // Give Windows a moment to tear down the USB handle.
                System.Threading.Thread.Sleep(300);
            }
            catch { /* enumeration can fail on locked-down machines; non-fatal */ }
        }

        /// <summary>
        /// Resolves a process's main executable path. Uses the modern MainModule where
        /// available, which can throw on cross-bitness access; callers treat that as
        /// "unknown" and skip the process rather than kill it.
        /// </summary>
        private static string? TryGetProcessImagePath(Process p)
        {
            try { return p.MainModule?.FileName; }
            catch { return null; }
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

                // Defense-in-depth against stale-reading attribution: an XYZ line with no
                // measurement pending is a late arrival from a timed-out (or otherwise
                // abandoned) trigger. Consuming it later would pair a stale reading with
                // the wrong patch, so drop it loudly instead.
                if (pending == null || _poisoned)
                {
                    _log($"Ignoring stale spotread XYZ line (no measurement pending{(_poisoned ? ", session poisoned" : "")}): {line.Trim()}");
                    return;
                }

                if (!TryAcceptMeasuredXyz(new CieXyz(x, y, z), out string? error))
                {
                    pending.TrySetException(new InvalidOperationException(error));
                    return;
                }

                pending.TrySetResult(new CieXyz(x, y, z));
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

            // 3. Did spotread attempt to open the HID port? Needed so we can detect
            //    the V3.3.0 silent-exit-after-HID-open bug in OnExited.
            foreach (var marker in HidOpenAttemptMarkers)
            {
                if (lower.Contains(marker))
                {
                    _attemptedHidOpen = true;
                    break;
                }
            }

            // 4. Sharing violation (someone else holds the device). V2.3.1 reports this
            //    line explicitly; V3.3.0 doesn't — OnExited handles the silent case.
            foreach (var fragment in SharingViolationFragments)
            {
                if (lower.Contains(fragment))
                {
                    _sharingViolationSeen = true;
                    break;
                }
            }

            // 5. Did something go wrong at the spotread level? Specific fragments match
            //    anywhere; generic "error"-token lines must be anchored to the start of
            //    the message (see FatalLinePrefixes) so instrument-info lines that merely
            //    contain the word don't abort a healthy run.
            bool fatal = IsFatalErrorLine(lower);
            if (!fatal)
            {
                foreach (var fragment in FatalFragments)
                {
                    if (lower.Contains(fragment))
                    {
                        fatal = true;
                        break;
                    }
                }
            }

            if (fatal)
            {
                _fatalError = _sharingViolationSeen
                    ? BuildSharingViolationMessage(line)
                    : line;
                var ex = new InvalidOperationException(
                    _sharingViolationSeen
                        ? _fatalError
                        : $"spotread reported: {line.Trim()}");
                _readyTcs.TrySetException(ex);
                lock (_stateLock) _pendingMeasurement?.TrySetException(ex);
            }
        }

        private static string BuildSharingViolationMessage(string rawLine)
        {
            var sb = new StringBuilder();
            sb.AppendLine("The colorimeter was found, but its HID handle couldn't be opened for");
            sb.AppendLine("measurement — Windows sharing violation (err 32). spotread can enumerate");
            sb.AppendLine("the device but something is holding it, or it's bound to a driver that");
            sb.AppendLine("doesn't allow the exclusive access spotread needs.\n");

            var suspects = FindSuspectProcessesOnCurrentMachine();
            if (suspects.Count > 0)
            {
                sb.AppendLine("Currently-running programs that may be holding the device:");
                foreach (var p in suspects)
                    sb.AppendLine($"  • {p}");
                sb.AppendLine("Close these first, then retry.\n");
            }
            else
            {
                sb.AppendLine("No obvious color-software holder is running, which usually means the");
                sb.AppendLine("device is bound to the wrong driver for spotread (see step 1), or a");
                sb.AppendLine("background process is holding the HID handle (see steps 2–4).\n");
            }

            sb.AppendLine("Fixes, most effective first:\n");

            sb.AppendLine("  1. BIND THE PROBE TO THE ARGYLLCMS USB DRIVER. This is what DisplayCAL");
            sb.AppendLine("     does, and it bypasses the HID sharing conflict entirely by giving");
            sb.AppendLine("     spotread exclusive libusb access. Run ArgyllCMS_install_USB.exe");
            sb.AppendLine("     (in the Argyll 'usb' folder), pick your colorimeter, and install the");
            sb.AppendLine("     driver. If calibration then works in DisplayCAL but not here, this is");
            sb.AppendLine("     almost certainly the cause — the probe is on the native Windows HID");
            sb.AppendLine("     driver, which is refusing the exclusive open.\n");

            sb.AppendLine("  2. UNPLUG the colorimeter USB cable for 10+ seconds, then plug it into a");
            sb.AppendLine("     different port. The long pause lets Windows fully tear down the device");
            sb.AppendLine("     stack and release a stuck handle.\n");

            sb.AppendLine("  3. Disable DisplayCAL's auto-relaunch tasks (if DisplayCAL is installed):");
            sb.AppendLine("     • Win+R -> taskschd.msc -> Task Scheduler Library");
            sb.AppendLine("     • Disable 'DisplayCAL Profile Loader Launcher' and its Daily Restart.");
            sb.AppendLine("     These respawn the profile loader, which can re-grab the device.\n");

            sb.AppendLine("  4. Only for i1 Display PLUS (not Pro): that model prefers the native HID");
            sb.AppendLine("     driver, so if the ArgyllCMS USB driver was bound to it, run");
            sb.AppendLine("     ArgyllCMS_uninstall_USB.exe instead. Reboot if a handle stays stuck.\n");

            sb.AppendLine("Underlying spotread line: " + rawLine.Trim());
            return sb.ToString();
        }

        /// <summary>
        /// Lists the processes on this machine whose names commonly show up as HID-device
        /// holders on colorimeter systems. Best-effort — no admin required, so we can't
        /// query actual handle tables, just report who's plausibly involved.
        /// </summary>
        private static System.Collections.Generic.List<string> FindSuspectProcessesOnCurrentMachine()
        {
            // Fragment-based match, case insensitive. Kept narrow to avoid false positives.
            string[] suspectFragments =
            {
                "displaycal", "i1profiler", "i1 profiler", "xrite", "x-rite",
                "colormunki", "basiccolor", "calman"
            };

            var found = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    string name;
                    try { name = p.ProcessName; }
                    catch { continue; }

                    string lower = name.ToLowerInvariant();
                    foreach (var frag in suspectFragments)
                    {
                        if (lower.Contains(frag))
                        {
                            found.Add($"{name}.exe (PID {p.Id})");
                            break;
                        }
                    }
                }
            }
            catch { /* Process enumeration can fail on locked-down machines; don't crash the error report */ }
            return found;
        }

        private void OnExited(object? sender, EventArgs e)
        {
            // Unblock anyone waiting — they need to know the process is gone.
            int code = TryGetExitCode();

            // V3.3.0 silent-exit-after-HID-open bug: spotread attempted HID open, never
            // emitted a success OR failure message, and exited with 0. We can't tell from
            // the output alone — we only know something held the device. Surface the
            // likely cause instead of the cryptic "exited before producing a measurement".
            string msg;
            if (_fatalError != null)
            {
                msg = _fatalError;
            }
            else if (_sharingViolationSeen)
            {
                msg = BuildSharingViolationMessage("(spotread exited silently after sharing violation)");
            }
            else if (_attemptedHidOpen && code == 0 && !_readyTcs.Task.IsCompleted)
            {
                // V3.3.0 silent-fail after HID-open. Empirically this is always either a
                // sharing violation or a stale Windows HID handle. Same advice applies.
                msg = BuildSharingViolationMessage(
                    "(spotread V3.3.0 exited silently after HID open — error code suppressed by Argyll)");
            }
            else
            {
                msg = $"spotread exited (code {code}) before producing a measurement.\n" +
                      $"Last output:\n{SnapshotRecentLog()}";
            }

            var ex = new InvalidOperationException(msg);
            _readyTcs.TrySetException(ex);
            lock (_stateLock) _pendingMeasurement?.TrySetException(ex);
        }

        private string SnapshotRecentLog()
        {
            lock (_stateLock) return _recentLog.ToString();
        }

        internal static bool TryAcceptMeasuredXyz(CieXyz xyz, out string? error)
        {
            if (!double.IsFinite(xyz.X) || !double.IsFinite(xyz.Y) || !double.IsFinite(xyz.Z))
            {
                error = $"spotread produced non-finite XYZ values ({xyz.X}, {xyz.Y}, {xyz.Z}).";
                return false;
            }

            const double tolerance = -1e-6;
            if (xyz.X < tolerance || xyz.Y < tolerance || xyz.Z < tolerance)
            {
                error = $"spotread produced non-physical negative XYZ values ({xyz.X}, {xyz.Y}, {xyz.Z}).";
                return false;
            }

            error = null;
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (_process == null) return; // test instance — nothing to tear down

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
