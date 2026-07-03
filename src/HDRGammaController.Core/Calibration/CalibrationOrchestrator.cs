using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Orchestrates the calibration measurement workflow, coordinating between
    /// the colorimeter service, patch generation, and result collection.
    /// </summary>
    /// <remarks>
    /// The orchestrator manages the complete calibration process:
    /// 1. Generates the patch sequence based on preset
    /// 2. Iterates through patches, requesting display and measurement
    /// 3. Collects and validates measurements
    /// 4. Provides progress updates and supports pause/resume/cancel
    /// 5. Returns the complete measurement data for LUT generation
    /// </remarks>
    public class CalibrationOrchestrator
    {
        #region Private Fields

        private readonly ColorimeterService _colorimeterService;
        private readonly CalibrationTarget _target;
        private readonly PatchSetGenerator.CalibrationPreset _preset;

        private IReadOnlyList<ColorPatch>? _patches;
        private List<MeasurementResult>? _measurements;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly Stopwatch _elapsedTimer = new();

        // Thread synchronization
        private readonly object _stateLock = new();
        private volatile CalibrationState _state = CalibrationState.Idle;
        private volatile int _currentPatchIndex;
        private volatile bool _isPaused;
        private int _retryCount;

        // Configuration
        private readonly int _settleTimeMs;
        private readonly int _maxRetries;
        private readonly bool _hdrMode;

        // ------- Adaptive settle (m2) -------
        // A fixed settle underwaits large luminance transitions (OLED/VA pixel fall time,
        // backlight modulation) and overwaits small ones. Settle is scaled with the
        // signal-luminance step between consecutive patches instead. Internal setters let
        // tests shrink the delays; production uses the defaults.

        /// <summary>Base settle applied to every patch regardless of step size.</summary>
        internal int SettleBaseMs { get; set; } = 200;

        /// <summary>Extra settle per unit of |Δsignal-luminance| (full-scale swing adds this much).</summary>
        internal int SettleScaleFullSwingMs { get; set; } = 600;

        /// <summary>
        /// Floor after a LARGE DOWNWARD luminance step (&gt;50% of full scale): OLED/VA
        /// panels decay toward dark much slower than they rise, and measuring too early
        /// reads residual glow into the dark patch.
        /// </summary>
        internal int LargeFallSettleFloorMs { get; set; } = 500;

        /// <summary>
        /// Settle ceiling. 1200ms matches the first-patch settle used by the PQ verify
        /// sweep and the report verification pass — the longest settle used anywhere.
        /// </summary>
        internal int SettleMaxMs { get; set; } = 1200;

        /// <summary>Downward signal step treated as a "large fall" (fraction of full scale).</summary>
        private const double LargeFallThresholdFraction = 0.5;

        /// <summary>Signal-luminance of the previously displayed patch (null before the first).</summary>
        private double? _lastPatchKeyLuminance;

        // ------- Multi-read averaging (M8) -------
        // Near-black patches are meter-noise dominated and white/primary patches anchor
        // the whole characterization, so those take the per-component median of several
        // readings (mirroring the report window's 3× ReanchorWhite pattern). Ordinary
        // mid-tone patches keep single reads for the time budget.

        /// <summary>Readings taken for patches that qualify for multi-read.</summary>
        internal const int MultiReadCount = 3;

        /// <summary>One extra reading is taken when the Y spread across the reads exceeds the noise gate.</summary>
        internal const int MaxReadsPerPatch = 4;

        /// <summary>Signal level at/below which a patch counts as near-black (multi-read).</summary>
        internal const double LowSignalThreshold = 0.10;

        /// <summary>Re-read once more when Y spread exceeds this fraction of the mean…</summary>
        internal const double SpreadReReadFraction = 0.05;

        /// <summary>…or this absolute spread in cd/m² for near-black readings.</summary>
        internal const double SpreadReReadAbsoluteY = 0.02;

        /// <summary>Pause between repeated reads of the same patch (no display change to settle).</summary>
        internal int InterReadDelayMs { get; set; } = 150;

        // ------- Variance-adaptive integration (1.4) -------
        // The M8 multi-read set is fixed a priori (near-black/white/primaries). This
        // extends it with a LIVE noise model: every multi-read burst records its observed
        // Y spread into a per-luminance-decade EWMA (LuminanceNoiseModel). Once a decade
        // proves noisy (relative spread > LuminanceNoiseModel.NoisyRelativeSpread),
        // subsequent SINGLE-read patches landing there are escalated to median-of-3 and
        // their settle is lengthened (bounded below); quiet decades keep fast single
        // reads. Dark-panel VA/OLED runs therefore slow down only where the data is
        // actually noisy.

        /// <summary>
        /// Live per-luminance-decade noise model for this run. Fed by every multi-read
        /// burst; drives single-read escalation and noisy-regime settle lengthening.
        /// </summary>
        public LuminanceNoiseModel NoiseModel { get; } = new();

        /// <summary>
        /// Settle multiplier for patches predicted to land in a noisy luminance decade
        /// (up to 2×, applied on top of the adaptive settle and bounded at
        /// <see cref="NoisySettleMultiplier"/> × <see cref="SettleMaxMs"/>).
        /// </summary>
        internal const int NoisySettleMultiplier = 2;

        /// <summary>Assumed peak white (cd/m²) for bin prediction before any reading exists.</summary>
        internal const double DefaultAssumedPeakY = 100.0;

        /// <summary>Display gamma assumed when predicting a patch's absolute Y for bin lookup only.</summary>
        internal const double PredictionGamma = 2.2;

        /// <summary>Highest Y measured so far (non-wire patches); prediction scale for bin lookup.</summary>
        private double? _observedPeakY;

        /// <summary>Drift analysis from the last completed run (M7), if any.</summary>
        public DriftCompensator.DriftAnalysis? LastDriftAnalysis { get; private set; }

        /// <summary>
        /// When set, the orchestrator runs an in-session apply→verify→refine loop after the
        /// native measurement pass: it builds a correction, loads it on the display, and
        /// re-measures to confirm (and optionally refine) the result. Left null for a plain
        /// measure-only calibration.
        /// </summary>
        public ClosedLoopConfig? ClosedLoop { get; set; }

        /// <summary>
        /// Extra patches appended after the generated set - used for the HDR wire ladder
        /// (ColorPatch.Nits patches the display layer renders via FP16 scRGB). Set before
        /// StartCalibrationAsync.
        /// </summary>
        public IReadOnlyList<ColorPatch>? AdditionalPatches { get; set; }

        #endregion

        #region Events

        /// <summary>
        /// Raised when a patch should be displayed.
        /// </summary>
        public event EventHandler<DisplayPatchEventArgs>? DisplayPatchRequested;

        /// <summary>
        /// Raised when progress updates.
        /// </summary>
        public event EventHandler<CalibrationProgressEventArgs>? ProgressChanged;

        /// <summary>
        /// Raised when the calibration state changes.
        /// </summary>
        public event EventHandler<CalibrationStateEventArgs>? StateChanged;

        /// <summary>
        /// Raised when a measurement completes.
        /// </summary>
        public event EventHandler<MeasurementEventArgs>? MeasurementTaken;

        /// <summary>
        /// Raised when an error occurs during calibration.
        /// </summary>
        public event EventHandler<CalibrationErrorEventArgs>? ErrorOccurred;

        /// <summary>
        /// Raised when calibration completes (success or failure).
        /// </summary>
        public event EventHandler<CalibrationResultEventArgs>? CalibrationCompleted;

        /// <summary>
        /// Raised with a short phase label during the closed-loop apply/verify/refine passes
        /// (e.g. "Verifying: 4/16", "Refining (round 2): 1/16") for the UI to surface.
        /// </summary>
        public event EventHandler<string>? PhaseChanged;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the current calibration state.
        /// </summary>
        public CalibrationState State => _state;

        /// <summary>
        /// Gets whether calibration is currently running.
        /// </summary>
        public bool IsRunning => _state == CalibrationState.Running || _state == CalibrationState.Paused;

        /// <summary>
        /// Gets the current patch index (0-based).
        /// </summary>
        public int CurrentPatchIndex => _currentPatchIndex;

        /// <summary>
        /// Gets the total number of patches.
        /// </summary>
        public int TotalPatches => _patches?.Count ?? 0;

        /// <summary>
        /// Gets the elapsed calibration time.
        /// </summary>
        public TimeSpan ElapsedTime => _elapsedTimer.Elapsed;

        /// <summary>
        /// Gets the estimated remaining time based on current progress.
        /// </summary>
        public TimeSpan EstimatedRemainingTime
        {
            get
            {
                int total = TotalPatches;
                int current = _currentPatchIndex;

                // Before any measurements complete, estimate ~3 sec per patch
                if (current <= 0 || total <= 0)
                    return TimeSpan.FromSeconds(Math.Max(total, 0) * 3);

                // After at least one measurement, calculate based on actual timing
                double elapsedSeconds = _elapsedTimer.Elapsed.TotalSeconds;
                double avgTimePerPatch = elapsedSeconds / current;
                int remainingPatches = Math.Max(0, total - current);
                return TimeSpan.FromSeconds(avgTimePerPatch * remainingPatches);
            }
        }

        /// <summary>
        /// Gets the progress percentage (0-100).
        /// </summary>
        public double ProgressPercent
        {
            get
            {
                int total = TotalPatches;
                return total > 0 ? (_currentPatchIndex * 100.0 / total) : 0;
            }
        }

        /// <summary>
        /// Gets the current patch being measured.
        /// </summary>
        public ColorPatch? CurrentPatch => _patches != null && _currentPatchIndex < _patches.Count
            ? _patches[_currentPatchIndex]
            : null;

        /// <summary>
        /// Gets all completed measurements.
        /// </summary>
        public IReadOnlyList<MeasurementResult> Measurements => _measurements?.AsReadOnly() ?? new List<MeasurementResult>().AsReadOnly();

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new CalibrationOrchestrator.
        /// </summary>
        /// <param name="colorimeterService">The colorimeter service for measurements</param>
        /// <param name="target">The calibration target color space</param>
        /// <param name="preset">The patch set preset to use</param>
        /// <param name="settleTimeMs">Time to wait after displaying a patch before measuring (default 300ms)</param>
        /// <param name="maxRetries">Maximum retry attempts for failed measurements (default 3)</param>
        /// <param name="hdrMode">Whether to use HDR measurement mode</param>
        public CalibrationOrchestrator(
            ColorimeterService colorimeterService,
            CalibrationTarget target,
            PatchSetGenerator.CalibrationPreset preset,
            int settleTimeMs = 300,
            int maxRetries = 3,
            bool hdrMode = false)
        {
            _colorimeterService = colorimeterService ?? throw new ArgumentNullException(nameof(colorimeterService));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _preset = preset;
            _settleTimeMs = Math.Max(100, settleTimeMs);
            _maxRetries = Math.Max(1, maxRetries);
            _hdrMode = hdrMode;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Starts the calibration process.
        /// </summary>
        public async Task<CalibrationResult> StartCalibrationAsync(CancellationToken cancellationToken = default)
        {
            if (IsRunning)
                throw new InvalidOperationException("Calibration is already running");

            if (!_colorimeterService.IsReady)
                throw new InvalidOperationException("Colorimeter is not ready. Call InitializeAsync first.");

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                // Initialize
                SetState(CalibrationState.Initializing);
                var generated = PatchSetGenerator.GeneratePatchSet(_target, _preset);
                if (AdditionalPatches is { Count: > 0 })
                {
                    var combined = new List<ColorPatch>(generated.Count + AdditionalPatches.Count);
                    combined.AddRange(generated);
                    combined.AddRange(AdditionalPatches);
                    _patches = combined;
                }
                else
                {
                    _patches = generated;
                }
                _measurements = new List<MeasurementResult>(_patches.Count);
                _currentPatchIndex = 0;
                _elapsedTimer.Restart();

                RaiseProgressChanged();

                // Open a single persistent spotread session for the whole measurement loop.
                // This is where we discover "spotread can't talk to the instrument" problems —
                // any USB/driver/conflict issue surfaces as an exception here instead of as
                // a cryptic "no color data" error three patches in. The session is torn down
                // in finally regardless of how the loop terminates.
                await _colorimeterService.BeginMeasurementSessionAsync(_hdrMode, _cancellationTokenSource.Token);

                ClosedLoopOutcome? closedLoop = null;
                try
                {
                    SetState(CalibrationState.Running);
                    await RunMeasurementLoopAsync(_cancellationTokenSource.Token);

                    // M7: fit the drift curve from the interleaved DriftCheck whites and
                    // normalize every measurement to the run's initial state BEFORE the
                    // measurements feed model building or the closed loop. When the patch
                    // set carries no drift anchors (Quick/Standard/GrayscaleOnly) this is
                    // a no-op. Excessive drift (>8%) is deliberately NOT corrected so the
                    // measurement validator rejects the run instead.
                    if (_measurements is { Count: > 0 } && _state != CalibrationState.Cancelled)
                    {
                        LastDriftAnalysis = DriftCompensator.Compensate(_measurements);
                        PhaseChanged?.Invoke(this, LastDriftAnalysis.Summary);
                        if (LastDriftAnalysis.Applied)
                        {
                            var compensated = new List<MeasurementResult>(LastDriftAnalysis.Measurements);
                            // DriftCompensator rebuilds MeasurementResult instances and
                            // (deliberately — it owns no knowledge of read bursts) drops the
                            // multi-read metadata. Order is preserved, so restore
                            // ReadingCount/ReadingSpreadY for the uncertainty budget (1.3).
                            for (int i = 0; i < compensated.Count && i < _measurements.Count; i++)
                            {
                                compensated[i].ReadingCount = _measurements[i].ReadingCount;
                                compensated[i].ReadingSpreadY = _measurements[i].ReadingSpreadY;
                            }
                            _measurements = compensated;
                        }
                    }

                    // In-session apply → verify → refine, while spotread is still connected.
                    if (ClosedLoop != null && _state != CalibrationState.Cancelled && _measurements != null)
                    {
                        PhaseChanged?.Invoke(this, "Applying correction…");
                        closedLoop = await RunClosedLoopAsync(_measurements, _cancellationTokenSource.Token);
                    }
                }
                finally
                {
                    await _colorimeterService.EndMeasurementSessionAsync();
                }

                // Complete
                _elapsedTimer.Stop();

                // Atomically decide success vs. cancellation. Cancel() flips the state to
                // Cancelled (and trips the token) under _stateLock from another thread, so the
                // cancellation check and the success transition MUST happen under the same lock -
                // otherwise a Cancel() landing between the check and the transition would be
                // overwritten and a cancelled run reported as Success (installing a bad profile).
                // Any non-cancelled state here completes: that is Running normally, but a Pause()
                // can land during the EndMeasurementSessionAsync await above, leaving _state ==
                // Paused - that must still complete (the measurements are done), or the orchestrator
                // is left stuck Paused with IsRunning == true and wedges the next run. The
                // StateChanged event is raised after releasing the lock to match SetState and avoid
                // handler-reentrancy deadlocks.
                CalibrationState? completedFrom = null;
                lock (_stateLock)
                {
                    if (_cancellationTokenSource!.IsCancellationRequested || _state == CalibrationState.Cancelled)
                    {
                        // Ensure state reflects the cancellation (Cancel() may have already done so).
                        _state = CalibrationState.Cancelled;
                    }
                    else
                    {
                        // Capture the real prior state (Running or Paused) for the event.
                        completedFrom = _state;
                        _state = CalibrationState.Completed;
                    }
                }

                if (_state == CalibrationState.Cancelled)
                {
                    return new CalibrationResult
                    {
                        Success = false,
                        WasCancelled = true,
                        Message = "Calibration was cancelled by user",
                        Measurements = _measurements,
                        TotalTime = _elapsedTimer.Elapsed
                    };
                }

                if (completedFrom.HasValue)
                {
                    StateChanged?.Invoke(this, new CalibrationStateEventArgs(completedFrom.Value, CalibrationState.Completed));
                }

                string msg = $"Calibration completed successfully. {_measurements.Count} patches measured in {FormatTime(_elapsedTimer.Elapsed)}.";
                if (closedLoop.HasValue)
                    msg += $" Grayscale dE {closedLoop.Value.NativeResidual:F2} → {closedLoop.Value.CorrectedResidual:F2} after {closedLoop.Value.Rounds} pass(es).";

                var result = new CalibrationResult
                {
                    Success = true,
                    Measurements = _measurements,
                    Target = _target,
                    TotalTime = _elapsedTimer.Elapsed,
                    Message = msg,
                    ClosedLoopRan = closedLoop.HasValue,
                    CorrectedMeasurements = closedLoop?.AfterMeasurements,
                    FinalCorrection = closedLoop?.Correction,
                    NativeResidualDeltaE = closedLoop?.NativeResidual,
                    CorrectedResidualDeltaE = closedLoop?.CorrectedResidual,
                    RefinementRounds = closedLoop?.Rounds ?? 0,
                    DriftCompensationApplied = LastDriftAnalysis?.Applied ?? false,
                    PeakWhiteDriftFraction = LastDriftAnalysis?.MaxWhiteDriftFraction,
                    DriftSummary = LastDriftAnalysis?.Summary
                };

                CalibrationCompleted?.Invoke(this, new CalibrationResultEventArgs(result));
                return result;
            }
            catch (OperationCanceledException)
            {
                SetState(CalibrationState.Cancelled);
                var result = new CalibrationResult
                {
                    Success = false,
                    WasCancelled = true,
                    Message = "Calibration was cancelled",
                    Measurements = _measurements,
                    TotalTime = _elapsedTimer.Elapsed
                };
                CalibrationCompleted?.Invoke(this, new CalibrationResultEventArgs(result));
                return result;
            }
            catch (UsbDriverException)
            {
                // USB driver errors need user action - rethrow so UI can handle
                SetState(CalibrationState.Failed);
                throw;
            }
            catch (Exception ex)
            {
                SetState(CalibrationState.Failed);
                ErrorOccurred?.Invoke(this, new CalibrationErrorEventArgs(ex.Message, CurrentPatch, true));

                var result = new CalibrationResult
                {
                    Success = false,
                    Message = $"Calibration failed: {ex.Message}",
                    ErrorException = ex,
                    Measurements = _measurements,
                    TotalTime = _elapsedTimer.Elapsed
                };
                CalibrationCompleted?.Invoke(this, new CalibrationResultEventArgs(result));
                return result;
            }
        }

        /// <summary>
        /// Pauses the calibration after the current measurement completes.
        /// </summary>
        public void Pause()
        {
            lock (_stateLock)
            {
                if (_state == CalibrationState.Running)
                {
                    _isPaused = true;
                    SetStateLocked(CalibrationState.Paused);
                }
            }
        }

        /// <summary>
        /// Resumes a paused calibration.
        /// </summary>
        public void Resume()
        {
            lock (_stateLock)
            {
                if (_state == CalibrationState.Paused)
                {
                    _isPaused = false;
                    SetStateLocked(CalibrationState.Running);
                }
            }
        }

        /// <summary>
        /// Cancels the calibration.
        /// </summary>
        public void Cancel()
        {
            lock (_stateLock)
            {
                _cancellationTokenSource?.Cancel();
                SetStateLocked(CalibrationState.Cancelled);
            }
        }

        #endregion

        #region Private Methods

        private async Task RunMeasurementLoopAsync(CancellationToken cancellationToken)
        {
            if (_patches == null) return;

            for (_currentPatchIndex = 0; _currentPatchIndex < _patches.Count; _currentPatchIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Wait while paused
                while (_isPaused)
                {
                    await Task.Delay(100, cancellationToken);
                }

                var patch = _patches[_currentPatchIndex];

                // Request patch display
                DisplayPatchRequested?.Invoke(this, new DisplayPatchEventArgs(patch, _currentPatchIndex, _patches.Count));

                // Update progress now that this patch is on screen and being measured, so the
                // "Current"/"Next" labels track what's actually displayed. (A second update
                // fires after the measurement completes to advance the percentage.)
                RaiseProgressChanged();

                var measurement = await MeasurePatchAsync(patch, cancellationToken);

                // If the user paused while this patch was in flight, the reading is suspect
                // (the probe or panel may have been disturbed mid-pause) - and recording it
                // also played the capture sound during the pause/resume countdown. Discard
                // it, wait out the pause (top of loop), and re-measure the same patch.
                if (_isPaused)
                {
                    _currentPatchIndex--;
                    continue;
                }

                // Store measurement
                _measurements!.Add(measurement);
                MeasurementTaken?.Invoke(this, new MeasurementEventArgs(measurement));

                // Update progress
                RaiseProgressChanged();
            }
        }

        /// <summary>
        /// Full per-patch measurement: adaptive settle (m2) scaled with the luminance step
        /// from the previous patch, then a single read for ordinary mid-tones or a
        /// median-of-3 (median-of-4 when noisy) for near-black/white/primary patches (M8).
        /// Shared by the main measurement loop and the closed-loop verification passes.
        /// Progress is reported per PATCH by the callers, so multi-reads never double-count.
        /// </summary>
        private async Task<MeasurementResult> MeasurePatchAsync(ColorPatch patch, CancellationToken cancellationToken)
        {
            await Task.Delay(ComputeSettleDelayMs(patch), cancellationToken);

            var first = await MeasurePatchWithRetryAsync(patch, cancellationToken);
            TrackObservedPeak(patch, first);

            // 1.4: escalate an ordinary single-read patch to a multi-read burst when the
            // luminance decade its FIRST reading landed in has proven noisy this run.
            // Wire-ladder patches stay single-read regardless (own validation, tight
            // time budget — same exclusion as NeedsMultiRead).
            bool escalated = patch.Nits is null &&
                             !NeedsMultiRead(patch) &&
                             NoiseModel.IsNoisy(first.Xyz.Y);
            if (!NeedsMultiRead(patch) && !escalated)
                return first;

            var reads = new List<MeasurementResult>(MaxReadsPerPatch) { first };
            for (int i = 1; i < MultiReadCount; i++)
            {
                // Same stimulus, no display change: only a short inter-read pause
                // (the probe's own integration time dominates).
                await Task.Delay(InterReadDelayMs, cancellationToken);
                reads.Add(await MeasurePatchWithRetryAsync(patch, cancellationToken));
            }

            // Noise gate: if the readings disagree by more than ~5% of the mean
            // (or 0.02 cd/m² absolute near black), take one extra reading so a single
            // outlier can't tie the median.
            double meanY = reads.Average(r => r.Xyz.Y);
            double spreadY = reads.Max(r => r.Xyz.Y) - reads.Min(r => r.Xyz.Y);
            if (spreadY > Math.Max(meanY * SpreadReReadFraction, SpreadReReadAbsoluteY) &&
                reads.Count < MaxReadsPerPatch)
            {
                await Task.Delay(InterReadDelayMs, cancellationToken);
                reads.Add(await MeasurePatchWithRetryAsync(patch, cancellationToken));
            }

            // 1.4: feed the noise model with the burst's final spread so later patches
            // in this luminance decade adapt (escalation + settle).
            NoiseModel.Record(
                reads.Average(r => r.Xyz.Y),
                reads.Max(r => r.Xyz.Y) - reads.Min(r => r.Xyz.Y));

            return MedianMeasurement(patch, reads);
        }

        /// <summary>Tracks the run's peak measured luminance for bin PREDICTION only (settle heuristic).</summary>
        private void TrackObservedPeak(ColorPatch patch, MeasurementResult measurement)
        {
            if (patch.Nits is not null) return; // wire ladder exceeds SDR white by design
            double y = measurement.Xyz.Y;
            if (double.IsFinite(y) && y > (_observedPeakY ?? 0))
                _observedPeakY = y;
        }

        /// <summary>
        /// M8: which patches warrant multiple readings. Near-black patches (signal ≤ 10%,
        /// expected luminance well under ~1 cd/m² on any plausible display) are meter-noise
        /// dominated; white and 100% primaries anchor the white point, peak luminance and
        /// gamut matrix, so an outlier there skews the whole profile. HDR wire-ladder
        /// patches keep single reads: the PQ sweep has its own monotonicity validation and
        /// a tight time budget.
        /// </summary>
        internal static bool NeedsMultiRead(ColorPatch patch)
        {
            if (patch.Nits is not null) return false;
            var rgb = patch.DisplayRgb;
            bool nearBlack = rgb.Max <= LowSignalThreshold;
            bool isWhite = rgb.R >= 0.99 && rgb.G >= 0.99 && rgb.B >= 0.99;
            bool isPrimary = patch.Category == PatchCategory.Primary;
            return nearBlack || isWhite || isPrimary;
        }

        /// <summary>
        /// Per-component median of several readings of the same patch. The median (unlike
        /// the mean) fully rejects a single glitched reading. Even counts average the two
        /// middle values. Timestamp is taken from the middle reading so drift fitting sees
        /// the temporal center of the read burst.
        /// </summary>
        internal static MeasurementResult MedianMeasurement(ColorPatch patch, IReadOnlyList<MeasurementResult> reads)
        {
            if (reads.Count == 1) return reads[0];

            static double Median(IEnumerable<double> values)
            {
                var v = values.OrderBy(x => x).ToList();
                int n = v.Count;
                return n % 2 == 1 ? v[n / 2] : 0.5 * (v[n / 2 - 1] + v[n / 2]);
            }

            var byTime = reads.OrderBy(r => r.Timestamp).ToList();
            return new MeasurementResult
            {
                Patch = patch,
                Timestamp = byTime[byTime.Count / 2].Timestamp,
                Xyz = new CieXyz(
                    Median(reads.Select(r => r.Xyz.X)),
                    Median(reads.Select(r => r.Xyz.Y)),
                    Median(reads.Select(r => r.Xyz.Z))),
                IsValid = true,
                SequenceIndex = reads[0].SequenceIndex,
                // 1.3/1.4: record the burst size and its observed Y spread so the noise
                // model can be rebuilt offline and the uncertainty budget can derive the
                // per-patch repeatability term.
                ReadingCount = reads.Count,
                ReadingSpreadY = reads.Max(r => r.Xyz.Y) - reads.Min(r => r.Xyz.Y)
            };
        }

        /// <summary>
        /// m2: settle time scaled with the signal step from the previously displayed patch:
        /// base + scale·|Δ|, with a floor after large downward steps (slow pixel decay on
        /// OLED/VA) and a cap consistent with the PQ verify sweep's 1200ms first-patch
        /// settle. Never less than the constructor-configured fixed settle, so this only
        /// ever adds margin relative to the old fixed behavior.
        /// The luminance key is the gamma-encoded Rec.709-weighted signal — ordinal only,
        /// which is all a settle heuristic needs.
        /// </summary>
        internal int ComputeSettleDelayMs(ColorPatch patch)
        {
            double key = PatchKeyLuminance(patch);
            // Before the first patch assume a bright screen (setup UI), which conservatively
            // treats the usual black first patch as a large downward step.
            double prev = _lastPatchKeyLuminance ?? 1.0;
            _lastPatchKeyLuminance = key;

            double delta = Math.Abs(key - prev);
            int settle = SettleBaseMs + (int)(delta * SettleScaleFullSwingMs);
            if (prev - key > LargeFallThresholdFraction)
                settle = Math.Max(settle, LargeFallSettleFloorMs);
            settle = Math.Max(settle, Math.Min(_settleTimeMs, SettleMaxMs));
            settle = Math.Min(settle, SettleMaxMs);

            // 1.4: when this patch is predicted to land in a luminance decade the run has
            // proven noisy, lengthen the settle up to NoisySettleMultiplier× (residual
            // panel transients matter most exactly where the meter is already fighting
            // noise), bounded at NoisySettleMultiplier × SettleMaxMs.
            if (NoiseModel.IsNoisy(PredictPatchY(patch, key)))
                settle = Math.Min(settle * NoisySettleMultiplier, SettleMaxMs * NoisySettleMultiplier);

            return settle;
        }

        /// <summary>
        /// Coarse absolute-luminance prediction for the incoming patch, used ONLY to pick
        /// the noise-model decade before the patch is measured: wire patches use their
        /// requested nits; SDR patches assume gamma-<see cref="PredictionGamma"/> output
        /// scaled by the run's observed peak (or <see cref="DefaultAssumedPeakY"/> before
        /// any reading exists).
        /// </summary>
        private double PredictPatchY(ColorPatch patch, double keyLuminance)
        {
            if (patch.Nits is double nits && double.IsFinite(nits))
                return Math.Max(nits, 0.0);
            double peak = _observedPeakY ?? DefaultAssumedPeakY;
            return Math.Pow(Math.Clamp(keyLuminance, 0.0, 1.0), PredictionGamma) * peak;
        }

        private static double PatchKeyLuminance(ColorPatch patch)
        {
            if (patch.Nits is double nits && double.IsFinite(nits))
                return Math.Clamp(nits / 1000.0, 0.0, 1.0); // coarse: wire ladder tops out ~1000 nits
            var rgb = patch.DisplayRgb;
            return 0.2126 * rgb.R + 0.7152 * rgb.G + 0.0722 * rgb.B;
        }

        /// <summary>
        /// Single reading with the retry loop. Settle is handled by <see cref="MeasurePatchAsync"/>;
        /// this method only waits between RETRY attempts.
        /// </summary>
        private async Task<MeasurementResult> MeasurePatchWithRetryAsync(ColorPatch patch, CancellationToken cancellationToken)
        {
            _retryCount = 0;

            MeasurementResult? measurement = null;
            string? lastError = null;
            while (_retryCount < _maxRetries)
            {
                try
                {
                    measurement = await _colorimeterService.MeasureAsync(patch, _hdrMode, cancellationToken);
                    if (measurement.IsValid) break;

                    lastError = measurement.ErrorMessage ?? "Unknown measurement error";
                    _retryCount++;
                    if (_retryCount < _maxRetries)
                    {
                        ErrorOccurred?.Invoke(this, new CalibrationErrorEventArgs(
                            $"Measurement invalid: {lastError}. Retrying ({_retryCount}/{_maxRetries})...", patch, false));
                        await Task.Delay(_settleTimeMs, cancellationToken);
                    }
                }
                catch (UsbDriverException)
                {
                    throw; // never resolves without user action
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    _retryCount++;
                    if (_retryCount >= _maxRetries)
                        throw new InvalidOperationException(
                            $"Failed to measure patch {patch.Name} after {_maxRetries} attempts. Last error: {lastError}", ex);

                    ErrorOccurred?.Invoke(this, new CalibrationErrorEventArgs(
                        $"Measurement error: {ex.Message}. Retrying ({_retryCount}/{_maxRetries})...", patch, false));
                    await Task.Delay(_settleTimeMs * 2, cancellationToken);
                }
            }

            if (measurement == null || !measurement.IsValid)
                throw new InvalidOperationException(
                    $"Failed to get valid measurement for patch {patch.Name} after {_maxRetries} attempts. " +
                    $"Last error: {lastError ?? "No error details available"}");

            return measurement;
        }

        /// <summary>
        /// Measures a patch list (e.g. a grayscale verification ramp) within the open session,
        /// displaying each patch and reporting progress against a phase label.
        /// </summary>
        private async Task<List<MeasurementResult>> MeasurePatchListAsync(
            IReadOnlyList<ColorPatch> patches, string phaseLabel, CancellationToken cancellationToken)
        {
            var results = new List<MeasurementResult>(patches.Count);
            for (int i = 0; i < patches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var patch = patches[i];
                DisplayPatchRequested?.Invoke(this, new DisplayPatchEventArgs(patch, i, patches.Count));
                PhaseChanged?.Invoke(this, $"{phaseLabel}: {i + 1}/{patches.Count}");
                results.Add(await MeasurePatchAsync(patch, cancellationToken));
            }
            return results;
        }

        /// <summary>
        /// Runs the apply→verify→refine loop after the native pass. Returns the final
        /// correction plus before/after grayscale measurements for the report. Keeps the
        /// best-scoring round so refinement can never produce a worse result than the initial.
        /// </summary>
        private async Task<ClosedLoopOutcome> RunClosedLoopAsync(
            IReadOnlyList<MeasurementResult> nativeMeasurements, CancellationToken cancellationToken)
        {
            var cfg = ClosedLoop!;
            var verifyPatches = cfg.VerificationPatches ?? GrayscalePatches();

            double nativeResidual = cfg.Corrector.GrayscaleResidualDeltaE(nativeMeasurements);

            var correction = cfg.Corrector.BuildInitialCorrection(nativeMeasurements);
            cfg.Apply(correction);

            var bestCorrection = correction;
            var afterMeasurements = await MeasurePatchListAsync(verifyPatches, "Verifying", cancellationToken);
            double bestResidual = cfg.Corrector.GrayscaleResidualDeltaE(afterMeasurements);
            var bestAfter = afterMeasurements;
            int roundsDone = 1;

            for (int round = 1; round < Math.Max(1, cfg.MaxRefinementRounds) && bestResidual > cfg.TargetDeltaE; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                correction = cfg.Corrector.RefineCorrection(afterMeasurements, correction);
                cfg.Apply(correction);
                afterMeasurements = await MeasurePatchListAsync(verifyPatches, $"Refining (round {round + 1})", cancellationToken);
                roundsDone++;

                double residual = cfg.Corrector.GrayscaleResidualDeltaE(afterMeasurements);
                if (residual < bestResidual)
                {
                    bestResidual = residual;
                    bestCorrection = correction;
                    bestAfter = afterMeasurements;
                }
            }

            // Make sure the display ends on the best correction, not the last one tried.
            cfg.Apply(bestCorrection);

            return new ClosedLoopOutcome(bestCorrection, bestAfter, nativeResidual, bestResidual, roundsDone);
        }

        private IReadOnlyList<ColorPatch> GrayscalePatches()
        {
            var list = new List<ColorPatch>();
            if (_patches != null)
                foreach (var p in _patches)
                    if (p.Category == PatchCategory.Grayscale) list.Add(p);
            return list;
        }

        private readonly record struct ClosedLoopOutcome(
            (double[] R, double[] G, double[] B) Correction,
            List<MeasurementResult> AfterMeasurements,
            double NativeResidual,
            double CorrectedResidual,
            int Rounds);

        /// <summary>
        /// Sets state with automatic locking. Use from methods that don't already hold _stateLock.
        /// Events are raised AFTER releasing the lock to prevent potential deadlocks.
        /// </summary>
        private void SetState(CalibrationState newState)
        {
            CalibrationState? oldState = null;

            lock (_stateLock)
            {
                if (_state != newState)
                {
                    oldState = _state;
                    _state = newState;
                }
            }

            // Raise event outside of lock to prevent deadlocks
            if (oldState.HasValue)
            {
                StateChanged?.Invoke(this, new CalibrationStateEventArgs(oldState.Value, newState));
            }
        }

        /// <summary>
        /// Sets state without locking. Caller MUST hold _stateLock.
        /// WARNING: This method raises events while potentially under lock.
        /// Only use when you need atomic state changes (e.g., Pause/Resume/Cancel).
        /// Event handlers must be lightweight and must not call back into the orchestrator.
        /// </summary>
        private void SetStateLocked(CalibrationState newState)
        {
            if (_state != newState)
            {
                var oldState = _state;
                _state = newState;
                // Note: Event raised while holding lock - handlers must be lightweight
                // and must NOT call back into this orchestrator to avoid deadlocks
                StateChanged?.Invoke(this, new CalibrationStateEventArgs(oldState, newState));
            }
        }

        private void RaiseProgressChanged()
        {
            var nextPatch = _patches != null && _currentPatchIndex + 1 < _patches.Count
                ? _patches[_currentPatchIndex + 1]
                : null;

            ProgressChanged?.Invoke(this, new CalibrationProgressEventArgs(
                _currentPatchIndex,
                TotalPatches,
                CurrentPatch,
                nextPatch,
                _elapsedTimer.Elapsed,
                EstimatedRemainingTime,
                ProgressPercent));
        }

        private static string FormatTime(TimeSpan time)
        {
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
                : $"{time.Minutes}:{time.Seconds:D2}";
        }

        #endregion
    }

    #region Calibration State

    /// <summary>
    /// States of the calibration process.
    /// </summary>
    public enum CalibrationState
    {
        /// <summary>Calibration not started.</summary>
        Idle,

        /// <summary>Initializing patches and colorimeter.</summary>
        Initializing,

        /// <summary>Calibration running, taking measurements.</summary>
        Running,

        /// <summary>Calibration paused by user.</summary>
        Paused,

        /// <summary>Calibration completed successfully.</summary>
        Completed,

        /// <summary>Calibration cancelled by user.</summary>
        Cancelled,

        /// <summary>Calibration failed due to error.</summary>
        Failed
    }

    #endregion

    #region Result Classes

    /// <summary>
    /// Result of a calibration run.
    /// </summary>
    /// <summary>
    /// Configuration for the in-session apply→verify→refine closed loop. The caller supplies
    /// the corrector (color math), the display-apply action (VCGT), and the round budget.
    /// </summary>
    public sealed class ClosedLoopConfig
    {
        /// <summary>Builds and refines the correction from measurements.</summary>
        public required ClosedLoopCorrector Corrector { get; init; }

        /// <summary>Loads a candidate per-channel correction onto the display under test.</summary>
        public required Action<(double[] R, double[] G, double[] B)> Apply { get; init; }

        /// <summary>Max apply/verify passes (1 = apply + verify once; &gt;1 enables refinement).</summary>
        public int MaxRefinementRounds { get; init; } = 1;

        /// <summary>Stop refining once the grayscale residual drops to/below this dE.</summary>
        public double TargetDeltaE { get; init; } = 1.0;

        /// <summary>Patches to re-measure each verification pass; defaults to the grayscale subset.</summary>
        public IReadOnlyList<ColorPatch>? VerificationPatches { get; init; }
    }

    public class CalibrationResult
    {
        /// <summary>Whether calibration completed successfully.</summary>
        public bool Success { get; init; }

        /// <summary>Whether calibration was cancelled by user.</summary>
        public bool WasCancelled { get; init; }

        /// <summary>Human-readable result message.</summary>
        public string? Message { get; init; }

        /// <summary>Exception if calibration failed.</summary>
        public Exception? ErrorException { get; init; }

        /// <summary>All measurements taken (may be partial if cancelled/failed).</summary>
        public IReadOnlyList<MeasurementResult>? Measurements { get; init; }

        /// <summary>The calibration target used.</summary>
        public CalibrationTarget? Target { get; init; }

        /// <summary>Total calibration time.</summary>
        public TimeSpan TotalTime { get; init; }

        // --- Closed-loop (apply + verify) results, present only when closed-loop ran ---

        /// <summary>Whether the apply-and-verify closed loop ran.</summary>
        public bool ClosedLoopRan { get; init; }

        /// <summary>Grayscale verification measured WITH the final correction applied.</summary>
        public IReadOnlyList<MeasurementResult>? CorrectedMeasurements { get; init; }

        /// <summary>The final per-channel VCGT correction (1024-point signal→signal LUTs).</summary>
        public (double[] R, double[] G, double[] B)? FinalCorrection { get; init; }

        /// <summary>Mean grayscale dE before correction (native display).</summary>
        public double? NativeResidualDeltaE { get; init; }

        /// <summary>Mean grayscale dE after the final correction (the real "result").</summary>
        public double? CorrectedResidualDeltaE { get; init; }

        /// <summary>Number of refinement rounds actually performed.</summary>
        public int RefinementRounds { get; init; }

        // --- Drift compensation (M7), present when the patch set carried drift anchors ---

        /// <summary>Whether multiplicative luminance drift compensation was applied.</summary>
        public bool DriftCompensationApplied { get; init; }

        /// <summary>Peak white drift observed across the run (|Y/Y0 - 1|), if analyzed.</summary>
        public double? PeakWhiteDriftFraction { get; init; }

        /// <summary>Human-readable drift analysis summary for logs/report.</summary>
        public string? DriftSummary { get; init; }
    }

    #endregion

    #region Event Args Classes

    /// <summary>
    /// Event args for patch display requests.
    /// </summary>
    public class DisplayPatchEventArgs : EventArgs
    {
        public ColorPatch Patch { get; }
        public int PatchIndex { get; }
        public int TotalPatches { get; }

        public DisplayPatchEventArgs(ColorPatch patch, int patchIndex, int totalPatches)
        {
            Patch = patch;
            PatchIndex = patchIndex;
            TotalPatches = totalPatches;
        }
    }

    /// <summary>
    /// Event args for progress updates.
    /// </summary>
    public class CalibrationProgressEventArgs : EventArgs
    {
        public int CurrentIndex { get; }
        public int TotalPatches { get; }
        public ColorPatch? CurrentPatch { get; }
        public ColorPatch? NextPatch { get; }
        public TimeSpan Elapsed { get; }
        public TimeSpan EstimatedRemaining { get; }
        public double ProgressPercent { get; }

        public CalibrationProgressEventArgs(
            int currentIndex,
            int totalPatches,
            ColorPatch? currentPatch,
            ColorPatch? nextPatch,
            TimeSpan elapsed,
            TimeSpan estimatedRemaining,
            double progressPercent)
        {
            CurrentIndex = currentIndex;
            TotalPatches = totalPatches;
            CurrentPatch = currentPatch;
            NextPatch = nextPatch;
            Elapsed = elapsed;
            EstimatedRemaining = estimatedRemaining;
            ProgressPercent = progressPercent;
        }
    }

    /// <summary>
    /// Event args for state changes.
    /// </summary>
    public class CalibrationStateEventArgs : EventArgs
    {
        public CalibrationState OldState { get; }
        public CalibrationState NewState { get; }

        public CalibrationStateEventArgs(CalibrationState oldState, CalibrationState newState)
        {
            OldState = oldState;
            NewState = newState;
        }
    }

    /// <summary>
    /// Event args for errors during calibration.
    /// </summary>
    public class CalibrationErrorEventArgs : EventArgs
    {
        public string Message { get; }
        public ColorPatch? Patch { get; }
        public bool IsFatal { get; }

        public CalibrationErrorEventArgs(string message, ColorPatch? patch, bool isFatal)
        {
            Message = message;
            Patch = patch;
            IsFatal = isFatal;
        }
    }

    /// <summary>
    /// Event args for calibration result.
    /// </summary>
    public class CalibrationResultEventArgs : EventArgs
    {
        public CalibrationResult Result { get; }

        public CalibrationResultEventArgs(CalibrationResult result)
        {
            Result = result;
        }
    }

    #endregion
}
