using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Orchestrates the four-patch spectral capture that produces a CCSS for the user's
    /// exact panel: full-drive White, Red, Green and Blue patches, several spectrometer
    /// reads per patch, median per wavelength bin, then luminance normalization so the
    /// white sample integrates to Y = 100 (the relative scale every CCSS consumer uses).
    ///
    /// Hardware-free by construction: the caller supplies "show this patch" and "take one
    /// spectral reading" as delegates, so the whole pipeline is unit-testable and the UI
    /// layer keeps ownership of the patch window and the spotread session lifecycle.
    /// </summary>
    public sealed class SpectralCaptureService
    {
        /// <summary>Patch drive levels in capture order (CCSS convention: white first).</summary>
        public static readonly IReadOnlyList<(string Name, double R, double G, double B)> Patches =
            new (string, double, double, double)[]
            {
                ("White", 1.0, 1.0, 1.0),
                ("Red",   1.0, 0.0, 0.0),
                ("Green", 0.0, 1.0, 0.0),
                ("Blue",  0.0, 0.0, 1.0),
            };

        private readonly Func<(double R, double G, double B), Task> _showPatch;
        private readonly Func<CancellationToken, Task<SpectralReading>> _measure;

        /// <summary>Reads averaged (median per bin) for each patch. Default 3.</summary>
        public int ReadsPerPatch { get; set; } = 3;

        /// <summary>Wait after a patch is shown before the first read, for panel settling.</summary>
        public TimeSpan SettleDelay { get; set; } = TimeSpan.FromMilliseconds(600);

        /// <summary>Progress callback: (completed reads, total reads, current patch name).</summary>
        public Action<int, int, string>? Progress { get; set; }

        /// <param name="showPatch">Displays a full-screen patch at the given 0..1 drive levels
        /// (UI layer's responsibility — e.g. PatchDisplayWindow.SetColor via its dispatcher).</param>
        /// <param name="measure">Takes one spectral reading
        /// (e.g. <see cref="ColorimeterService.MeasureSpectralAsync"/>).</param>
        public SpectralCaptureService(
            Func<(double R, double G, double B), Task> showPatch,
            Func<CancellationToken, Task<SpectralReading>> measure)
        {
            _showPatch = showPatch ?? throw new ArgumentNullException(nameof(showPatch));
            _measure = measure ?? throw new ArgumentNullException(nameof(measure));
        }

        /// <summary>
        /// Runs the full W/R/G/B capture and returns the luminance-normalized spectral set
        /// ready for <see cref="CcssWriter"/>. Throws on unusable readings (mismatched
        /// wavelength grids, non-positive white luminance).
        /// </summary>
        public async Task<CcssWriter.SpectralSet> CaptureAsync(CancellationToken cancellationToken = default)
        {
            int totalReads = Patches.Count * ReadsPerPatch;
            int completedReads = 0;
            var medians = new List<SpectralSample>(Patches.Count);
            double whiteY = 0;

            Log.Info($"SpectralCapture: starting 4-patch capture ({ReadsPerPatch} reads per patch).");

            foreach (var patch in Patches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Progress?.Invoke(completedReads, totalReads, patch.Name);

                await _showPatch((patch.R, patch.G, patch.B));
                await Task.Delay(SettleDelay, cancellationToken);

                var reads = new List<SpectralReading>(ReadsPerPatch);
                for (int i = 0; i < ReadsPerPatch; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reading = await _measure(cancellationToken);
                    reads.Add(reading);
                    completedReads++;
                    Progress?.Invoke(completedReads, totalReads, patch.Name);
                    Log.Info($"SpectralCapture: {patch.Name} read {i + 1}/{ReadsPerPatch}: " +
                             $"Y={reading.Xyz.Y:F3} cd/m2, {reading.Spectrum.Bands} bands " +
                             $"{reading.Spectrum.StartNm:F1}-{reading.Spectrum.EndNm:F1} nm");
                }

                var median = MedianSpectrum(reads.Select(r => r.Spectrum).ToList());
                medians.Add(median);

                if (patch.Name == "White")
                {
                    // Median of the per-read Y values: robust against one glinted reading,
                    // consistent with the per-bin median used for the spectrum itself.
                    whiteY = Median(reads.Select(r => r.Xyz.Y).ToList());
                }
            }

            if (!(whiteY > 0) || !double.IsFinite(whiteY))
                throw new InvalidOperationException(
                    $"White patch measured no luminance (Y={whiteY}). Is the probe on the patch?");

            // Luminance normalization per CCSS convention: scale ALL four spectra by the
            // same factor so the white sample corresponds to Y = 100. Relative radiometry
            // between channels is preserved (that is the information a CCSS carries).
            double scale = 100.0 / whiteY;
            var normalized = medians
                .Select(s => new SpectralSample(s.StartNm, s.EndNm, s.Values.Select(v => v * scale).ToArray()))
                .ToList();

            Log.Info($"SpectralCapture: capture complete. White Y={whiteY:F3} cd/m2, " +
                     $"normalization scale {scale:F6}, grid {normalized[0].Bands} bands " +
                     $"{normalized[0].StartNm:F1}-{normalized[0].EndNm:F1} nm.");

            return new CcssWriter.SpectralSet(
                White: normalized[0], Red: normalized[1], Green: normalized[2], Blue: normalized[3]);
        }

        /// <summary>
        /// Per-bin median of several spectra. All spectra must share one wavelength grid
        /// (they came from the same instrument in one session; a mismatch means something
        /// went badly wrong mid-capture and the result would be garbage).
        /// </summary>
        internal static SpectralSample MedianSpectrum(IReadOnlyList<SpectralSample> spectra)
        {
            if (spectra == null || spectra.Count == 0)
                throw new ArgumentException("At least one spectrum is required.", nameof(spectra));

            var first = spectra[0];
            foreach (var s in spectra)
            {
                if (s.Bands != first.Bands ||
                    Math.Abs(s.StartNm - first.StartNm) > 1e-6 ||
                    Math.Abs(s.EndNm - first.EndNm) > 1e-6)
                {
                    throw new InvalidOperationException(
                        "Spectrometer readings changed wavelength grid mid-capture " +
                        $"({first.Bands} bands {first.StartNm}-{first.EndNm} nm vs {s.Bands} bands {s.StartNm}-{s.EndNm} nm).");
                }
            }

            var values = new double[first.Bands];
            var bin = new double[spectra.Count];
            for (int i = 0; i < first.Bands; i++)
            {
                for (int r = 0; r < spectra.Count; r++)
                    bin[r] = spectra[r].Values[i];
                values[i] = Median(bin.ToList());
            }
            return new SpectralSample(first.StartNm, first.EndNm, values);
        }

        internal static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }
    }
}
