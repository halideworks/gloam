using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Coordinates the shared verification patch loop and its objective analysis. Rendering
    /// and instrument access arrive as narrow delegates, leaving the report window to present
    /// the result instead of owning timing, measurement sequencing, and activation analysis.
    /// </summary>
    internal static class VerificationCoordinator
    {
        internal sealed record Config(
            IReadOnlyList<ColorPatch> Patches,
            CalibrationTarget Target,
            IReadOnlyList<MeasurementResult>? NativeMeasurements,
            Action<ColorPatch> ShowPatch,
            Action<int, int, ColorPatch, ColorPatch?> ReportProgress,
            Func<ColorPatch, CancellationToken, Task<MeasurementResult>> MeasureAsync,
            Action? MeasurementCaptured = null,
            Func<TimeSpan, CancellationToken, Task>? DelayAsync = null);

        internal sealed record Result(
            IReadOnlyList<MeasurementResult> Measurements,
            CalibrationMetrics Metrics,
            ProfileActivationCheck? Activation);

        internal static async Task<Result> RunAsync(Config config, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(config.Patches);
            ArgumentNullException.ThrowIfNull(config.Target);
            ArgumentNullException.ThrowIfNull(config.ShowPatch);
            ArgumentNullException.ThrowIfNull(config.ReportProgress);
            ArgumentNullException.ThrowIfNull(config.MeasureAsync);

            var delay = config.DelayAsync ?? ((duration, token) => Task.Delay(duration, token));
            var results = new List<MeasurementResult>(config.Patches.Count);
            for (int i = 0; i < config.Patches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var patch = config.Patches[i];
                var next = i + 1 < config.Patches.Count ? config.Patches[i + 1] : null;
                config.ReportProgress(i + 1, config.Patches.Count, patch, next);
                config.ShowPatch(patch);
                await delay(TimeSpan.FromMilliseconds(i == 0 ? 1200 : 500), cancellationToken);
                results.Add(await config.MeasureAsync(patch, cancellationToken));
                config.MeasurementCaptured?.Invoke();
            }

            var metrics = CalibrationVerifier.ComputeMetrics(results, config.Target);
            var activation = config.NativeMeasurements != null
                ? VerificationAnalysis.AnalyzeProfileActivation(
                    CalibrationVerifier.ComputeMetrics(config.NativeMeasurements, config.Target).PatchResults,
                    metrics.PatchResults,
                    config.Target.WhitePointOnly)
                : null;
            return new Result(results, metrics, activation);
        }
    }
}
