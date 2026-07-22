using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using static HDRGammaController.Core.Calibration.PatchSetGenerator;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Owns one initial calibration session from orchestrator construction through the
    /// generated artifact. The WPF window supplies patch presentation and progress views;
    /// calibration policy, event lifetime, closed-loop setup, and HDR patch selection live
    /// here as one disposable unit.
    /// </summary>
    internal sealed class CalibrationSessionCoordinator : IDisposable
    {
        internal sealed record Config(
            ColorimeterService Colorimeter,
            CalibrationTarget Target,
            CalibrationPreset Preset,
            bool HdrMode,
            MonitorInfo? Monitor,
            CalibrationStateManager? StateManager,
            int RefinementRounds);

        internal sealed record ArtifactProgress(string Phase, double Percent, string Label);

        internal sealed record Result(
            CalibrationResult Calibration,
            Lut3D? Lut,
            DisplayCharacterization? Characterization,
            CalibrationMetrics? Metrics);

        private readonly Config _config;
        private readonly CalibrationOrchestrator _orchestrator;
        private bool _disposed;

        internal CalibrationSessionCoordinator(Config config)
        {
            ArgumentNullException.ThrowIfNull(config);
            _config = config;
            _orchestrator = new CalibrationOrchestrator(
                config.Colorimeter,
                config.Target,
                config.Preset,
                settleTimeMs: 300,
                maxRetries: 3,
                hdrMode: config.HdrMode);

            _orchestrator.DisplayPatchRequested += ForwardDisplayPatchRequested;
            _orchestrator.ProgressChanged += ForwardProgressChanged;
            _orchestrator.StateChanged += ForwardStateChanged;
            _orchestrator.MeasurementTaken += ForwardMeasurementTaken;
            _orchestrator.ErrorOccurred += ForwardErrorOccurred;
            _orchestrator.PhaseChanged += ForwardPhaseChanged;

            ConfigureClosedLoop();
            ConfigureHdrPatches();
        }

        internal event EventHandler<DisplayPatchEventArgs>? DisplayPatchRequested;
        internal event EventHandler<CalibrationProgressEventArgs>? ProgressChanged;
        internal event EventHandler<CalibrationStateEventArgs>? StateChanged;
        internal event EventHandler<MeasurementEventArgs>? MeasurementTaken;
        internal event EventHandler<CalibrationErrorEventArgs>? ErrorOccurred;
        internal event EventHandler<string>? PhaseChanged;

        internal int TotalPatches => _orchestrator.TotalPatches;

        internal void Pause() => _orchestrator.Pause();
        internal void Resume() => _orchestrator.Resume();
        internal void Cancel() => _orchestrator.Cancel();

        internal async Task<Result> RunAsync(
            IProgress<ArtifactProgress>? artifactProgress,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var calibration = await _orchestrator.StartCalibrationAsync(cancellationToken);
            if (!calibration.Success || calibration.Measurements == null)
                return new Result(calibration, null, null, null);

            artifactProgress?.Report(new ArtifactProgress(
                _config.HdrMode ? "Building HDR profile model..." : "Generating LUT...",
                99,
                _config.HdrMode ? "Building model..." : "Generating..."));

            var generator = new Lut3DGenerator(
                _config.Target,
                calibration.Measurements,
                lutSize: 33);

            Lut3D? lut;
            DisplayCharacterization? characterization;
            if (_config.HdrMode)
            {
                lut = null;
                characterization = generator.BuildCharacterizationOnly(hdrMode: true);
                artifactProgress?.Report(new ArtifactProgress(
                    "Building HDR profile model...", 100, "Model built"));
            }
            else
            {
                lut = generator.Generate(_ => artifactProgress?.Report(new ArtifactProgress(
                    "Generating LUT...", 100, "Generating...")));
                characterization = generator.Characterization;
            }

            return new Result(
                calibration,
                lut,
                characterization,
                generator.CalculateMetrics());
        }

        private void ConfigureClosedLoop()
        {
            if (_config.HdrMode)
            {
                Log.Info("CalibrationSessionCoordinator: closed-loop refinement skipped in HDR; report verification measures the installed result.");
                return;
            }

            if (_config.StateManager == null || _config.Monitor == null || _config.RefinementRounds <= 0)
                return;

            var monitor = _config.Monitor;
            var stateManager = _config.StateManager;
            _orchestrator.ClosedLoop = new ClosedLoopConfig
            {
                Corrector = new ClosedLoopCorrector(
                    _config.Target,
                    monitor.SdrWhiteLevel,
                    monitor.IsHdrActive),
                Apply = correction => stateManager.ApplyCorrectionLut(
                    monitor,
                    correction.R,
                    correction.G,
                    correction.B),
                MaxRefinementRounds = _config.RefinementRounds,
                TargetDeltaE = 1.0,
            };
        }

        private void ConfigureHdrPatches()
        {
            if (!_config.HdrMode || _config.Monitor == null) return;
            _orchestrator.AdditionalPatches = HdrWirePatchSet.Build(_config.Monitor.HdrPeakNits);
            Log.Info($"CalibrationSessionCoordinator: appended {_orchestrator.AdditionalPatches.Count} HDR wire-ladder patches " +
                     $"(panel peak {_config.Monitor.HdrPeakNits:F0} nits).");
        }

        private void ForwardDisplayPatchRequested(object? sender, DisplayPatchEventArgs e) =>
            DisplayPatchRequested?.Invoke(this, e);
        private void ForwardProgressChanged(object? sender, CalibrationProgressEventArgs e) =>
            ProgressChanged?.Invoke(this, e);
        private void ForwardStateChanged(object? sender, CalibrationStateEventArgs e) =>
            StateChanged?.Invoke(this, e);
        private void ForwardMeasurementTaken(object? sender, MeasurementEventArgs e) =>
            MeasurementTaken?.Invoke(this, e);
        private void ForwardErrorOccurred(object? sender, CalibrationErrorEventArgs e) =>
            ErrorOccurred?.Invoke(this, e);
        private void ForwardPhaseChanged(object? sender, string e) => PhaseChanged?.Invoke(this, e);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _orchestrator.DisplayPatchRequested -= ForwardDisplayPatchRequested;
            _orchestrator.ProgressChanged -= ForwardProgressChanged;
            _orchestrator.StateChanged -= ForwardStateChanged;
            _orchestrator.MeasurementTaken -= ForwardMeasurementTaken;
            _orchestrator.ErrorOccurred -= ForwardErrorOccurred;
            _orchestrator.PhaseChanged -= ForwardPhaseChanged;
        }
    }
}
