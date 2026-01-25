using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private CalibrationState _state = CalibrationState.Idle;
        private int _currentPatchIndex;
        private bool _isPaused;
        private int _retryCount;

        // Configuration
        private readonly int _settleTimeMs;
        private readonly int _maxRetries;
        private readonly bool _hdrMode;

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
                if (_currentPatchIndex <= 0 || TotalPatches <= 0)
                    return TimeSpan.FromSeconds(TotalPatches * 3); // ~3 sec per patch estimate

                var avgTimePerPatch = _elapsedTimer.Elapsed.TotalSeconds / _currentPatchIndex;
                var remainingPatches = TotalPatches - _currentPatchIndex;
                return TimeSpan.FromSeconds(avgTimePerPatch * remainingPatches);
            }
        }

        /// <summary>
        /// Gets the progress percentage (0-100).
        /// </summary>
        public double ProgressPercent => TotalPatches > 0 ? (_currentPatchIndex * 100.0 / TotalPatches) : 0;

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
                _patches = PatchSetGenerator.GeneratePatchSet(_target, _preset);
                _measurements = new List<MeasurementResult>(_patches.Count);
                _currentPatchIndex = 0;
                _elapsedTimer.Restart();

                RaiseProgressChanged();

                // Run measurement loop
                SetState(CalibrationState.Running);
                await RunMeasurementLoopAsync(_cancellationTokenSource.Token);

                // Complete
                _elapsedTimer.Stop();

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

                SetState(CalibrationState.Completed);

                var result = new CalibrationResult
                {
                    Success = true,
                    Measurements = _measurements,
                    Target = _target,
                    TotalTime = _elapsedTimer.Elapsed,
                    Message = $"Calibration completed successfully. {_measurements.Count} patches measured in {FormatTime(_elapsedTimer.Elapsed)}."
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
            if (_state == CalibrationState.Running)
            {
                _isPaused = true;
                SetState(CalibrationState.Paused);
            }
        }

        /// <summary>
        /// Resumes a paused calibration.
        /// </summary>
        public void Resume()
        {
            if (_state == CalibrationState.Paused)
            {
                _isPaused = false;
                SetState(CalibrationState.Running);
            }
        }

        /// <summary>
        /// Cancels the calibration.
        /// </summary>
        public void Cancel()
        {
            _cancellationTokenSource?.Cancel();
            SetState(CalibrationState.Cancelled);
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
                _retryCount = 0;

                // Request patch display
                DisplayPatchRequested?.Invoke(this, new DisplayPatchEventArgs(patch, _currentPatchIndex, _patches.Count));

                // Wait for display to settle
                await Task.Delay(_settleTimeMs, cancellationToken);

                // Take measurement with retry
                MeasurementResult? measurement = null;
                while (_retryCount < _maxRetries)
                {
                    try
                    {
                        measurement = await _colorimeterService.MeasureAsync(patch, _hdrMode, cancellationToken);

                        if (measurement.IsValid)
                            break;

                        _retryCount++;
                        if (_retryCount < _maxRetries)
                        {
                            ErrorOccurred?.Invoke(this, new CalibrationErrorEventArgs(
                                $"Measurement invalid: {measurement.ErrorMessage}. Retrying ({_retryCount}/{_maxRetries})...",
                                patch, false));
                            await Task.Delay(_settleTimeMs, cancellationToken); // Extra settle time on retry
                        }
                    }
                    catch (Exception ex)
                    {
                        _retryCount++;
                        if (_retryCount >= _maxRetries)
                            throw;

                        ErrorOccurred?.Invoke(this, new CalibrationErrorEventArgs(
                            $"Measurement error: {ex.Message}. Retrying ({_retryCount}/{_maxRetries})...",
                            patch, false));
                        await Task.Delay(_settleTimeMs * 2, cancellationToken); // Longer wait on error
                    }
                }

                if (measurement == null || !measurement.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Failed to get valid measurement for patch {patch.Name} after {_maxRetries} attempts");
                }

                // Store measurement
                _measurements!.Add(measurement);
                MeasurementTaken?.Invoke(this, new MeasurementEventArgs(measurement));

                // Update progress
                RaiseProgressChanged();
            }
        }

        private void SetState(CalibrationState newState)
        {
            if (_state != newState)
            {
                var oldState = _state;
                _state = newState;
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
