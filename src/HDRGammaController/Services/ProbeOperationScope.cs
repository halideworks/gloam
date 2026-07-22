using System;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Owns the resources shared by every operation that places a probe on a display:
    /// cancellation, the patch surface, correction bypass, the instrument session, and
    /// restoration. Construction is transactional; a failed startup runs the same cleanup
    /// path as a completed or cancelled sweep.
    /// </summary>
    internal sealed class ProbeOperationScope : IAsyncDisposable
    {
        internal enum SessionKind
        {
            None,
            Measurement,
            Spectral,
        }

        internal sealed record Options(
            MonitorInfo Monitor,
            ColorimeterService Colorimeter,
            string PlacementLabel,
            bool HdrMode = false,
            double PatchSize = 600,
            double PatchOffsetX = 0,
            double PatchOffsetY = 0,
            bool RequestPlacement = true,
            SessionKind Session = SessionKind.Measurement,
            CalibrationStateManager? StateManager = null,
            GammaMode PreviousGammaMode = GammaMode.WindowsDefault,
            CalibrationSettings? PreviousSettings = null,
            bool EnterBypass = false,
            bool ContinueIfBypassFails = true,
            bool DisposeColorimeter = false,
            Action? EnterBusy = null,
            Action? ExitBusy = null,
            Action<PatchDisplayWindow>? ConfigurePatchWindow = null,
            Action<double, double>? PlacementCommitted = null,
            CancellationToken CancellationToken = default);

        private readonly Options _options;
        private readonly CancellationTokenSource _cancellation;
        private bool _busyEntered;
        private bool _bypassEntered;
        private bool _sessionStarted;
        private bool _disposed;

        private ProbeOperationScope(Options options)
        {
            _options = options;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
            PatchWindow = new PatchDisplayWindow(
                options.Monitor,
                options.PatchSize,
                options.PatchOffsetX,
                options.PatchOffsetY);
            PatchWindow.AbortRequested += Cancel;
            PatchWindow.Closed += OnPatchWindowClosed;
        }

        internal PatchDisplayWindow PatchWindow { get; }
        internal CancellationToken Token => _cancellation.Token;

        internal void Cancel() => _cancellation.Cancel();

        internal static async Task<ProbeOperationScope> StartAsync(Options options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.Monitor);
            ArgumentNullException.ThrowIfNull(options.Colorimeter);

            var scope = new ProbeOperationScope(options);
            try
            {
                options.ConfigurePatchWindow?.Invoke(scope.PatchWindow);
                options.EnterBusy?.Invoke();
                scope._busyEntered = true;

                if (options.EnterBypass && options.StateManager != null)
                {
                    try
                    {
                        options.StateManager.EnterBypassMode(
                            options.Monitor,
                            options.PreviousGammaMode,
                            options.PreviousSettings);
                        scope._bypassEntered = true;
                    }
                    catch (Exception ex) when (options.ContinueIfBypassFails)
                    {
                        Log.Info($"ProbeOperationScope: correction bypass failed; continuing without it: {ex.Message}");
                    }
                }

                scope.PatchWindow.Show();
                if (options.RequestPlacement)
                {
                    await scope.PatchWindow.WaitForPlacementAsync(options.PlacementLabel, scope.Token);
                    options.PlacementCommitted?.Invoke(
                        scope.PatchWindow.OffsetX,
                        scope.PatchWindow.OffsetY);
                }

                await scope.StartSessionAsync();
                return scope;
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        private async Task StartSessionAsync()
        {
            switch (_options.Session)
            {
                case SessionKind.Measurement:
                    await _options.Colorimeter.BeginMeasurementSessionAsync(_options.HdrMode, Token);
                    _sessionStarted = true;
                    break;
                case SessionKind.Spectral:
                    await _options.Colorimeter.BeginSpectralSessionAsync(Token);
                    _sessionStarted = true;
                    break;
            }
        }

        /// <summary>
        /// Ends this scope's instrument session early while retaining the placed patch
        /// window. Two-instrument correction capture uses this at the USB hand-off.
        /// </summary>
        internal async Task EndSessionAsync()
        {
            if (!_sessionStarted) return;
            if (_options.Session == SessionKind.Spectral)
                await _options.Colorimeter.EndSpectralSessionAsync();
            else if (_options.Session == SessionKind.Measurement)
                await _options.Colorimeter.EndMeasurementSessionAsync();
            _sessionStarted = false;
        }

        private void OnPatchWindowClosed(object? sender, EventArgs e)
        {
            if (!_disposed)
                Cancel();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            PatchWindow.AbortRequested -= Cancel;
            PatchWindow.Closed -= OnPatchWindowClosed;

            if (_sessionStarted)
            {
                try
                {
                    await EndSessionAsync();
                }
                catch (Exception ex)
                {
                    Log.Info($"ProbeOperationScope: instrument-session cleanup failed: {ex.Message}");
                }
            }

            try
            {
                if (PatchWindow.IsLoaded)
                    PatchWindow.Close();
            }
            catch (Exception ex)
            {
                Log.Info($"ProbeOperationScope: patch-window cleanup failed: {ex.Message}");
            }

            if (_bypassEntered)
            {
                try
                {
                    _options.StateManager!.RestorePreviousState();
                }
                catch (Exception ex)
                {
                    Log.Info($"ProbeOperationScope: correction restore failed: {ex.Message}");
                }
                _bypassEntered = false;
            }

            if (_options.DisposeColorimeter)
                _options.Colorimeter.Dispose();

            _cancellation.Dispose();
            if (_busyEntered)
            {
                _options.ExitBusy?.Invoke();
                _busyEntered = false;
            }
        }
    }
}
