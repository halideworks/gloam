using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Orchestrates the two-instrument capture that feeds a CCMX correction matrix:
    /// phase 1 measures the W/R/G/B patches with the first instrument, the user is
    /// prompted to swap probes, phase 2 measures the SAME patches with the second
    /// instrument. Per patch, several XYZ reads are taken and the per-component median
    /// used (robust against a single glinted reading, consistent with
    /// <see cref="SpectralCaptureService"/>).
    ///
    /// Stability guard: white is measured at the start of each phase (as the White data
    /// point) and re-measured at the end of the phase; if the white luminance drifted by
    /// more than <see cref="MaxWhiteDriftFraction"/>, the capture refuses. A correction
    /// matrix transfers one instrument's readings onto another's, so the panel must emit
    /// the same light in both phases — a drifting (cold, ABL-limited, auto-dimming) panel
    /// would bake the drift into the matrix as a fake instrument difference.
    ///
    /// Hardware-free by construction: "show this patch", "take one reading" (per
    /// instrument) and "prompt the swap" are all delegates, so the pipeline is fully
    /// unit-testable and the UI layer keeps ownership of the patch window, message boxes
    /// and the spotread session lifecycles.
    /// </summary>
    public sealed class MeterOffsetCaptureService
    {
        /// <summary>Patch drive levels in capture order (white first, matching the CCSS/CCMX convention).</summary>
        public static IReadOnlyList<(string Name, double R, double G, double B)> Patches => SpectralCaptureService.Patches;

        private readonly Func<(double R, double G, double B), Task> _showPatch;
        private readonly Func<CancellationToken, Task<CieXyz>> _measureFirst;
        private readonly Func<CancellationToken, Task<bool>> _promptSwap;
        private readonly Func<CancellationToken, Task<CieXyz>> _measureSecond;

        private int _completedReads;

        /// <summary>Reads medianed per patch. Default 3.</summary>
        public int ReadsPerPatch { get; set; } = 3;

        /// <summary>Wait after a patch is shown before the first read, for panel settling.</summary>
        public TimeSpan SettleDelay { get; set; } = TimeSpan.FromMilliseconds(600);

        /// <summary>
        /// Maximum tolerated relative white-luminance drift within one phase (start vs end
        /// of phase). Default 0.03 (3%): beyond that the panel is not stable enough for a
        /// cross-instrument transfer and the capture refuses.
        /// </summary>
        public double MaxWhiteDriftFraction { get; set; } = 0.03;

        /// <summary>Progress callback: (completed reads, total reads, current patch name, phase description).</summary>
        public Action<int, int, string, string>? Progress { get; set; }

        /// <summary>Readings from the two phases, both in <see cref="Patches"/> order (W, R, G, B).</summary>
        public sealed record TwoInstrumentReadings(
            IReadOnlyList<CieXyz> FirstInstrument,
            IReadOnlyList<CieXyz> SecondInstrument);

        /// <param name="showPatch">Displays a full-screen patch at the given 0..1 drive levels
        /// (UI layer's responsibility — e.g. PatchDisplayWindow.SetColor via its dispatcher).</param>
        /// <param name="measureFirstInstrument">Takes one XYZ reading with the phase-1 instrument.</param>
        /// <param name="promptSwap">Asks the user to swap probes and prepares the phase-2
        /// instrument (detect/connect). Returns false to cancel the capture. The phase-2
        /// measure delegate is only invoked after this returns true.</param>
        /// <param name="measureSecondInstrument">Takes one XYZ reading with the phase-2 instrument.</param>
        public MeterOffsetCaptureService(
            Func<(double R, double G, double B), Task> showPatch,
            Func<CancellationToken, Task<CieXyz>> measureFirstInstrument,
            Func<CancellationToken, Task<bool>> promptSwap,
            Func<CancellationToken, Task<CieXyz>> measureSecondInstrument)
        {
            _showPatch = showPatch ?? throw new ArgumentNullException(nameof(showPatch));
            _measureFirst = measureFirstInstrument ?? throw new ArgumentNullException(nameof(measureFirstInstrument));
            _promptSwap = promptSwap ?? throw new ArgumentNullException(nameof(promptSwap));
            _measureSecond = measureSecondInstrument ?? throw new ArgumentNullException(nameof(measureSecondInstrument));
        }

        /// <summary>Total reads across both phases (4 patches + 1 white stability recheck, per phase).</summary>
        public int TotalReads => 2 * (Patches.Count + 1) * ReadsPerPatch;

        /// <summary>
        /// Runs both capture phases (with the swap prompt in between) and returns the
        /// medianed XYZ readings of each instrument in patch order W/R/G/B, ready for
        /// <see cref="CcmxWriter.SolveCorrectionMatrix"/>. Throws
        /// <see cref="OperationCanceledException"/> when the swap prompt is declined, and
        /// <see cref="InvalidOperationException"/> on unusable readings or panel drift.
        /// </summary>
        public async Task<TwoInstrumentReadings> CaptureAsync(CancellationToken cancellationToken = default)
        {
            _completedReads = 0;
            Log.Info($"MeterOffsetCapture: starting two-instrument W/R/G/B capture " +
                     $"({ReadsPerPatch} reads per patch, white drift limit {MaxWhiteDriftFraction * 100:F0}%).");

            var first = await CapturePhaseAsync("phase 1", _measureFirst, cancellationToken);

            Log.Info("MeterOffsetCapture: phase 1 complete - prompting instrument swap.");
            if (!await _promptSwap(cancellationToken))
            {
                Log.Info("MeterOffsetCapture: instrument swap declined - capture cancelled.");
                throw new OperationCanceledException("The instrument swap was cancelled.");
            }

            var second = await CapturePhaseAsync("phase 2", _measureSecond, cancellationToken);

            Log.Info("MeterOffsetCapture: both phases complete.");
            return new TwoInstrumentReadings(first, second);
        }

        private async Task<IReadOnlyList<CieXyz>> CapturePhaseAsync(
            string phaseName,
            Func<CancellationToken, Task<CieXyz>> measure,
            CancellationToken cancellationToken)
        {
            var results = new List<CieXyz>(Patches.Count);
            CieXyz whiteStart = default;

            foreach (var patch in Patches)
            {
                var median = await MeasurePatchAsync(patch, phaseName, measure, cancellationToken);
                results.Add(median);

                if (patch.Name == "White")
                {
                    whiteStart = median;
                    if (!(whiteStart.Y > 0) || !double.IsFinite(whiteStart.Y))
                        throw new InvalidOperationException(
                            $"White patch measured no luminance (Y={whiteStart.Y:F4}) in {phaseName}. Is the probe on the patch?");
                }
            }

            // Stability recheck: re-show and re-measure white at the end of the phase.
            var whitePatch = Patches[0];
            var whiteEnd = await MeasurePatchAsync(whitePatch, $"{phaseName} white recheck", measure, cancellationToken);
            double drift = Math.Abs(whiteEnd.Y - whiteStart.Y) / whiteStart.Y;
            Log.Info($"MeterOffsetCapture: {phaseName} white stability: start Y={whiteStart.Y:F3}, " +
                     $"end Y={whiteEnd.Y:F3}, drift {drift * 100:F2}%.");

            if (drift > MaxWhiteDriftFraction)
                throw new InvalidOperationException(
                    $"White luminance drifted {drift * 100:F1}% during {phaseName} " +
                    $"(start Y={whiteStart.Y:F2}, end Y={whiteEnd.Y:F2} cd/m2), above the {MaxWhiteDriftFraction * 100:F0}% limit. " +
                    "A cross-instrument transfer needs a stable panel: let the display warm up for 30+ minutes, " +
                    "disable auto-brightness / power-saving dimming, then retry.");

            return results;
        }

        private async Task<CieXyz> MeasurePatchAsync(
            (string Name, double R, double G, double B) patch,
            string phaseName,
            Func<CancellationToken, Task<CieXyz>> measure,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Progress?.Invoke(_completedReads, TotalReads, patch.Name, phaseName);

            await _showPatch((patch.R, patch.G, patch.B));
            await Task.Delay(SettleDelay, cancellationToken);

            var xs = new List<double>(ReadsPerPatch);
            var ys = new List<double>(ReadsPerPatch);
            var zs = new List<double>(ReadsPerPatch);
            for (int i = 0; i < ReadsPerPatch; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var xyz = await measure(cancellationToken);

                if (!SpotreadSession.TryAcceptMeasuredXyz(xyz, out string? error))
                    throw new InvalidOperationException(
                        $"{phaseName}: {patch.Name} read {i + 1}/{ReadsPerPatch} is unusable: {error}");

                xs.Add(xyz.X);
                ys.Add(xyz.Y);
                zs.Add(xyz.Z);
                _completedReads++;
                Progress?.Invoke(_completedReads, TotalReads, patch.Name, phaseName);
                Log.Info($"MeterOffsetCapture: {phaseName} {patch.Name} read {i + 1}/{ReadsPerPatch}: " +
                         $"XYZ = {xyz.X:F3} {xyz.Y:F3} {xyz.Z:F3}");
            }

            return new CieXyz(
                SpectralCaptureService.Median(xs),
                SpectralCaptureService.Median(ys),
                SpectralCaptureService.Median(zs));
        }
    }
}
