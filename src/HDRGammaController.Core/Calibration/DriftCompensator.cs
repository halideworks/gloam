using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Fits and removes slow multiplicative luminance drift (panel warm-up, OLED ABL
    /// settling) from a calibration run, using the periodic <see cref="PatchCategory.DriftCheck"/>
    /// white re-reads that PatchSetGenerator interleaves into Thorough/Full sequences.
    /// </summary>
    /// <remarks>
    /// Model: display output at time t = (stable display) × f(t), where f is a smooth
    /// luminance-only factor. Each drift-check white gives a sample f(t_i) = Y_i / Y_0
    /// (ratio against the FIRST white, measured at the start of the run before warm-up
    /// develops). f is interpolated piecewise-linearly between samples and every
    /// measurement's XYZ is divided by f(timestamp), normalizing the whole run to its
    /// t0 state. Scaling X, Y and Z by the same factor preserves chromaticity exactly,
    /// so chromaticity drift is deliberately NOT hidden — the validator still sees it.
    ///
    /// Safety: drift beyond <see cref="MaxCorrectableDriftFraction"/> is NOT compensated.
    /// A run that drifted that much is a broken measurement setup (display not warmed up,
    /// dynamic brightness active), and papering over it would silently install a profile
    /// built from bad data. Leaving the measurements raw lets
    /// <see cref="CalibrationMeasurementValidator"/>'s repeated-white gate fail the run
    /// with an actionable message.
    ///
    /// Black re-reads are additive-domain and too noise-dominated for a multiplicative
    /// fit; they are only ANALYZED here (max deviation reported) and gated by the
    /// validator's repeated-black check.
    /// </remarks>
    public static class DriftCompensator
    {
        /// <summary>
        /// Largest white drift (|Y/Y0 - 1|) this class will correct. Above this the run
        /// is left untouched so the measurement validator rejects it instead.
        /// Matches the validator's 8% repeated-white failure threshold.
        /// </summary>
        public const double MaxCorrectableDriftFraction = 0.08;

        /// <summary>Result of a drift analysis/compensation pass.</summary>
        public sealed class DriftAnalysis
        {
            /// <summary>Whether compensation was applied to the returned measurements.</summary>
            public bool Applied { get; init; }

            /// <summary>Why compensation was (not) applied — for logs/report.</summary>
            public required string Summary { get; init; }

            /// <summary>Number of valid drift-check white anchors found.</summary>
            public int WhiteAnchorCount { get; init; }

            /// <summary>Peak |Y/Y0 - 1| observed across the white anchors (0 if &lt;2 anchors).</summary>
            public double MaxWhiteDriftFraction { get; init; }

            /// <summary>Peak |Y_i - Y_0| across drift-check black re-reads, cd/m² (0 if &lt;2).</summary>
            public double MaxBlackDriftY { get; init; }

            /// <summary>
            /// The measurements to hand to model building: drift-normalized when
            /// <see cref="Applied"/>, otherwise the original instances.
            /// </summary>
            public required IReadOnlyList<MeasurementResult> Measurements { get; init; }
        }

        /// <summary>
        /// Analyzes drift-check anchors in <paramref name="measurements"/> and, when there
        /// are enough anchors and the drift is within the correctable range, returns a new
        /// list with every measurement's XYZ divided by the interpolated drift factor.
        /// Measurement order, patches, timestamps and validity flags are preserved.
        /// </summary>
        public static DriftAnalysis Compensate(IReadOnlyList<MeasurementResult> measurements)
        {
            if (measurements == null) throw new ArgumentNullException(nameof(measurements));

            var whites = measurements
                .Where(m => m.IsValid && m.Patch.Nits is null &&
                            m.Patch.Category == PatchCategory.DriftCheck &&
                            m.Patch.DisplayRgb.R >= 0.99 &&
                            m.Patch.DisplayRgb.G >= 0.99 &&
                            m.Patch.DisplayRgb.B >= 0.99)
                .OrderBy(m => m.Timestamp)
                .ToList();

            var blacks = measurements
                .Where(m => m.IsValid && m.Patch.Nits is null &&
                            m.Patch.Category == PatchCategory.DriftCheck &&
                            m.Patch.DisplayRgb.R <= 0.01 &&
                            m.Patch.DisplayRgb.G <= 0.01 &&
                            m.Patch.DisplayRgb.B <= 0.01)
                .OrderBy(m => m.Timestamp)
                .ToList();

            double maxBlackDrift = 0;
            if (blacks.Count > 1)
            {
                double y0 = blacks[0].Xyz.Y;
                maxBlackDrift = blacks.Max(b => Math.Abs(b.Xyz.Y - y0));
            }

            if (whites.Count < 2 || whites[0].Xyz.Y <= 0)
            {
                return new DriftAnalysis
                {
                    Applied = false,
                    Summary = whites.Count < 2
                        ? $"Drift compensation skipped: {whites.Count} drift-check white(s) in the run (need at least 2)."
                        : "Drift compensation skipped: first drift-check white measured no light.",
                    WhiteAnchorCount = whites.Count,
                    MaxBlackDriftY = maxBlackDrift,
                    Measurements = measurements
                };
            }

            double referenceY = whites[0].Xyz.Y;
            var times = new double[whites.Count];
            var ratios = new double[whites.Count];
            DateTime t0 = whites[0].Timestamp;
            for (int i = 0; i < whites.Count; i++)
            {
                times[i] = (whites[i].Timestamp - t0).TotalSeconds;
                ratios[i] = whites[i].Xyz.Y / referenceY;
            }

            double maxDrift = ratios.Max(r => Math.Abs(r - 1.0));

            if (maxDrift > MaxCorrectableDriftFraction)
            {
                // Too large to silently correct — leave raw so the validator fails the run.
                return new DriftAnalysis
                {
                    Applied = false,
                    Summary = $"Drift compensation NOT applied: white drifted {maxDrift * 100:F1}% " +
                              $"(limit {MaxCorrectableDriftFraction * 100:F0}%). The run will fail validation; " +
                              "let the display warm up and disable dynamic brightness, then re-run.",
                    WhiteAnchorCount = whites.Count,
                    MaxWhiteDriftFraction = maxDrift,
                    MaxBlackDriftY = maxBlackDrift,
                    Measurements = measurements
                };
            }

            var compensated = new List<MeasurementResult>(measurements.Count);
            foreach (var m in measurements)
            {
                if (!m.IsValid)
                {
                    compensated.Add(m);
                    continue;
                }

                double factor = InterpolateFactor(times, ratios, (m.Timestamp - t0).TotalSeconds);
                if (factor <= 0 || !double.IsFinite(factor))
                {
                    compensated.Add(m);
                    continue;
                }

                compensated.Add(new MeasurementResult
                {
                    Id = m.Id,
                    Timestamp = m.Timestamp,
                    Patch = m.Patch,
                    Xyz = new CieXyz(m.Xyz.X / factor, m.Xyz.Y / factor, m.Xyz.Z / factor),
                    IntegrationTimeMs = m.IntegrationTimeMs,
                    IsValid = m.IsValid,
                    ErrorMessage = m.ErrorMessage,
                    RawOutput = m.RawOutput,
                    SequenceIndex = m.SequenceIndex
                });
            }

            return new DriftAnalysis
            {
                Applied = true,
                Summary = $"Drift compensation applied: {whites.Count} white anchors, peak drift {maxDrift * 100:F2}%, " +
                          "all measurements normalized to the run's initial state.",
                WhiteAnchorCount = whites.Count,
                MaxWhiteDriftFraction = maxDrift,
                MaxBlackDriftY = maxBlackDrift,
                Measurements = compensated
            };
        }

        /// <summary>
        /// Piecewise-linear interpolation of the drift factor at time t (seconds since the
        /// first white anchor). Clamped to the first/last sample outside the anchored range.
        /// </summary>
        private static double InterpolateFactor(double[] times, double[] ratios, double t)
        {
            if (t <= times[0]) return ratios[0];
            if (t >= times[^1]) return ratios[^1];

            // times is sorted; find the bracketing segment.
            for (int i = 1; i < times.Length; i++)
            {
                if (t <= times[i])
                {
                    double span = times[i] - times[i - 1];
                    if (span <= 0) return ratios[i];
                    double u = (t - times[i - 1]) / span;
                    return ratios[i - 1] + u * (ratios[i] - ratios[i - 1]);
                }
            }
            return ratios[^1];
        }
    }
}
