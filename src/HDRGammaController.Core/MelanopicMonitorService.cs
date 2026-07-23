using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Timers;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// A live melanopic evaluation for one monitor: the reading, its honest uncertainty, and
    /// where the spectra came from.
    /// </summary>
    public sealed record MelanopicMonitorState(
        string MonitorDevicePath,
        int Kelvin,
        MelanopicReading Reading,
        UncertaintyBudget.MelanopicUncertainty Uncertainty,
        string SpectraSourceName,
        bool IsHdrActive,
        DateTime TimestampUtc);

    /// <summary>
    /// The live melanopic dashboard's engine (roadmap 3.1): listens to the apply pipeline's
    /// per-monitor state snapshots, evaluates melanopic EDI / % reduction from the loaded
    /// CCSS spectra (or the generic fallback, honestly labeled), and appends samples to the
    /// nightly dose store. A slow keepalive timer re-samples the last state so the dose
    /// curve advances through long static periods — sampling is driven by state CHANGES and
    /// this timer, never by ramp writes.
    /// </summary>
    public sealed class MelanopicMonitorService : IDisposable
    {
        private readonly GammaApplyService _applyService;
        private readonly System.Timers.Timer _keepalive;
        private readonly object _lock = new();
        private readonly Dictionary<string, GammaApplyService.AppliedStateSnapshot> _lastSnapshots = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MelanopicMonitorState> _lastStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastAppendUtc = new(StringComparer.OrdinalIgnoreCase);

        // Background worker: the apply-path handler NEVER computes, does I/O, or raises
        // events on the caller's thread (that thread is the UI thread during a night-mode
        // fade — doing melanopic math + a JSONL append per fade tick there froze the whole
        // system). It only stashes the latest snapshot per monitor and pulses the worker,
        // which coalesces to the newest pending snapshot.
        private readonly Dictionary<string, GammaApplyService.AppliedStateSnapshot> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _pendingLock = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _worker;
        private volatile bool _disposed;

        /// <summary>Assumed viewing solid angle Ω_eff in steradian (see MelanopicCalculator).</summary>
        public double ViewingSolidAngleSr { get; set; } = MelanopicCalculator.DefaultViewingSolidAngleSr;

        public const double KeepaliveMinutes = 5.0;

        /// <summary>Minimum spacing between DISK samples per monitor. A live state change
        /// still updates the number instantly, but the dose store is appended at most this
        /// often per monitor — so a 30-minute fade writes ~30 lines, not thousands, and the
        /// dashboard's read-back stays cheap.</summary>
        public const double MinAppendIntervalSeconds = 60.0;

        /// <summary>Raised (on the worker thread) whenever any monitor's melanopic state was
        /// re-evaluated. UI subscribers marshal and throttle themselves.</summary>
        public event Action<MelanopicMonitorState>? MelanopicUpdated;

        public MelanopicMonitorService(GammaApplyService applyService)
        {
            _applyService = applyService ?? throw new ArgumentNullException(nameof(applyService));
            _applyService.StateApplied += OnStateApplied;

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "MelanopicWorker",
                Priority = ThreadPriority.BelowNormal,
            };
            _worker.Start();

            _keepalive = new System.Timers.Timer(TimeSpan.FromMinutes(KeepaliveMinutes).TotalMilliseconds) { AutoReset = true };
            _keepalive.Elapsed += (_, _) => QueueKeepalive();
            _keepalive.Start();

            MelanopicDoseStore.RotateRetention();
        }

        /// <summary>Latest evaluated states, one per monitor.</summary>
        public IReadOnlyList<MelanopicMonitorState> CurrentStates
        {
            get { lock (_lock) { return _lastStates.Values.ToList(); } }
        }

        // Apply-path handler: MUST stay trivial (no compute, no I/O, no event). Runs on the
        // UI thread during fades.
        private void OnStateApplied(GammaApplyService.AppliedStateSnapshot snapshot)
        {
            if (_disposed || string.IsNullOrEmpty(snapshot.MonitorDevicePath)) return;
            lock (_pendingLock)
            {
                if (_disposed) return;
                _pending[snapshot.MonitorDevicePath] = snapshot;
                _wake.Set();
            }
        }

        private void QueueKeepalive()
        {
            if (_disposed) return;
            // Re-queue the last snapshot per monitor at the current wall clock so the dose
            // integral advances while nothing changes on screen.
            List<GammaApplyService.AppliedStateSnapshot> snapshots;
            lock (_lock) { snapshots = _lastSnapshots.Values.ToList(); }
            if (snapshots.Count == 0) return;
            lock (_pendingLock)
            {
                if (_disposed) return;
                foreach (var s in snapshots)
                    _pending[s.MonitorDevicePath] = s with { TimestampUtc = DateTime.UtcNow };
                _wake.Set();
            }
        }

        private void WorkerLoop()
        {
            while (!_disposed)
            {
                _wake.WaitOne();
                if (_disposed) return;

                List<GammaApplyService.AppliedStateSnapshot> batch;
                lock (_pendingLock)
                {
                    if (_pending.Count == 0) continue;
                    batch = _pending.Values.ToList();
                    _pending.Clear();
                }

                foreach (var snapshot in batch)
                {
                    if (_disposed) return;
                    Evaluate(snapshot);
                }
            }
        }

        private void Evaluate(GammaApplyService.AppliedStateSnapshot snapshot)
        {
            try
            {
                // Skip while a calibration owns the display: measurements through a bypassed
                // ramp are not the user's viewing state and would poison the dose curve.
                if (CalibrationStateManager.IsDeviceInBypass(snapshot.MonitorDevicePath)) return;

                var (spectra, provenance, hasSpectra) = ResolveSpectra(snapshot.CcssPath);

                double whiteNits = ColorAdjustments.ApplyDimmingNits(
                    snapshot.SdrWhiteLevel, snapshot.Brightness, snapshot.SdrWhiteLevel,
                    snapshot.UseLinearBrightness);

                var reading = MelanopicCalculator.Compute(
                    spectra,
                    (snapshot.GainR, snapshot.GainG, snapshot.GainB),
                    whiteNits,
                    ViewingSolidAngleSr,
                    hasSpectra);

                var uncertainty = UncertaintyBudget.CombineMelanopic(
                    reading, provenance, spectra.WhiteResidualFraction);

                var state = new MelanopicMonitorState(
                    snapshot.MonitorDevicePath,
                    snapshot.EffectiveKelvin,
                    reading,
                    uncertainty,
                    spectra.SourceName,
                    snapshot.IsHdrActive,
                    snapshot.TimestampUtc);

                lock (_lock) { _lastStates[snapshot.MonitorDevicePath] = state; }

                // Throttle DISK appends per monitor: the live number updated above is
                // instant; the dose curve only needs a point every minute or so.
                if (double.IsFinite(reading.MelanopicEdiLux))
                {
                    bool doAppend;
                    lock (_lock)
                    {
                        doAppend = !_lastAppendUtc.TryGetValue(snapshot.MonitorDevicePath, out var last) ||
                                   (snapshot.TimestampUtc - last).TotalSeconds >= MinAppendIntervalSeconds;
                        if (doAppend) _lastAppendUtc[snapshot.MonitorDevicePath] = snapshot.TimestampUtc;
                    }
                    if (doAppend)
                    {
                        MelanopicDoseStore.Append(new MelanopicDoseSample
                        {
                            TimestampUtc = snapshot.TimestampUtc,
                            MonitorDevicePath = snapshot.MonitorDevicePath,
                            MelanopicEdiLux = reading.MelanopicEdiLux,
                            EdiExpandedU = uncertainty.EdiExpandedU,
                            ReductionFraction = reading.ReductionFraction,
                            Kelvin = snapshot.EffectiveKelvin,
                            HasSpectra = hasSpectra,
                        });
                    }
                }

                MelanopicUpdated?.Invoke(state);
            }
            catch (Exception ex)
            {
                Log.Info($"MelanopicMonitorService: evaluation failed for {snapshot.MonitorDevicePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Spectra source resolution with honest provenance: a Gloam-captured CCSS (this
        /// panel) beats a user-loaded community CCSS (assumed same-model), beats the generic
        /// primaries fallback. Detection of a Gloam capture is by the file's ORIGINATOR tag.
        /// </summary>
        private static (CcssMelanopicEstimator.CcssSpectra Spectra,
            UncertaintyBudget.CcssProvenance Provenance, bool HasSpectra) ResolveSpectra(string? ccssPath)
        {
            var spectra = CcssMelanopicEstimator.TryLoadSpectra(ccssPath);
            if (spectra == null)
                return (MelanopicCalculator.GenericPrimaries(),
                    UncertaintyBudget.CcssProvenance.GenericOrOtherPanel, false);

            var provenance = UncertaintyBudget.CcssProvenance.DbMatchedSameModel;
            try
            {
                if (ccssPath != null)
                {
                    // Gloam's own SpectralCaptureService writes ORIGINATOR "Gloam" — that
                    // file measured THIS unit's spectra.
                    using var reader = new StreamReader(ccssPath);
                    for (int i = 0; i < 12 && reader.ReadLine() is { } line; i++)
                    {
                        if (line.Contains("ORIGINATOR", StringComparison.OrdinalIgnoreCase) &&
                            line.Contains("Gloam", StringComparison.OrdinalIgnoreCase))
                        {
                            provenance = UncertaintyBudget.CcssProvenance.UserCapturedThisPanel;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Provenance stays at the conservative DB classification.
            }

            return (spectra, provenance, true);
        }

        public void Dispose()
        {
            lock (_pendingLock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _applyService.StateApplied -= OnStateApplied;
            _keepalive.Stop();
            _keepalive.Dispose();
            _wake.Set(); // release the worker from WaitOne
            bool workerStopped = false;
            try
            {
                workerStopped = _worker.Join(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Log.DebugRateLimited(
                    "melanopic-worker-shutdown",
                    $"Melanopic worker shutdown wait failed: {ex.Message}",
                    TimeSpan.FromMinutes(10));
            }
            if (workerStopped)
            {
                _wake.Dispose();
            }
            else
            {
                // The background worker may still be unwinding an in-flight file read. Do
                // not dispose its wait handle underneath it; process shutdown will reclaim
                // the handle once this rare timeout path completes.
                Log.DebugRateLimited(
                    "melanopic-worker-shutdown-timeout",
                    "Melanopic worker did not stop within two seconds; leaving its wait handle alive for safe unwind.",
                    TimeSpan.FromMinutes(10));
            }
        }
    }
}
