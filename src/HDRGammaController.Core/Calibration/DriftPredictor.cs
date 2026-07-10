using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Drift prediction from the trust-check trend store (the feasible core of roadmap 4.2):
    /// fits a least-squares trend to the avg-ΔE history under the current profile and
    /// answers "how far has this panel likely drifted, and when will it cross the
    /// recalibration threshold?" — with a predictive interval, so recalibration becomes
    /// data-driven instead of superstition.
    /// </summary>
    /// <remarks>
    /// Honesty gates: no prediction below 3 points or under 7 days of span (a slope fit on
    /// less is noise-reading); the predictive interval combines the regression's residual
    /// standard error with the measurements' own U95; and a slope statistically
    /// indistinguishable from zero reports "stable", never a fabricated crossing date.
    /// The full 4.2 (Bayesian priors from a golden-panel library, acquisition, thermal
    /// models) stays research — it needs a panel library we don't have yet.
    /// </remarks>
    public static class DriftPredictor
    {
        public const int MinPoints = 3;
        public const double MinSpanDays = 7.0;

        /// <summary>Recalibrate when predicted drift since baseline exceeds this (matches
        /// TrustCheckHistory's alert floor).</summary>
        public const double RecalibrateDriftThreshold = TrustCheckHistory.DeltaEDriftFloor;

        public sealed record Prediction(
            double SlopeDeltaEPerDay,
            double SlopeStandardError,
            bool SlopeSignificant,
            double PredictedCurrentDeltaE,
            double PredictedCurrentU95,
            double DriftSinceBaseline,
            DateTime? PredictedThresholdCrossingUtc,
            int PointCount,
            double SpanDays,
            string Summary);

        /// <summary>
        /// Fits the trend over the latest profile's entries. Returns null when the history
        /// cannot honestly support a prediction (too few points / too short a span).
        /// </summary>
        public static Prediction? Predict(IReadOnlyList<TrustCheckEntry> history, DateTime nowUtc)
        {
            if (history == null || history.Count == 0) return null;

            var latest = history[^1];
            var series = history
                .Where(e => e.ProfileId == latest.ProfileId && e.HdrMode == latest.HdrMode)
                .OrderBy(e => e.TimestampUtc)
                .ToList();
            if (series.Count < MinPoints) return null;

            DateTime t0 = series[0].TimestampUtc;
            double spanDays = (series[^1].TimestampUtc - t0).TotalDays;
            if (spanDays < MinSpanDays) return null;

            // Ordinary least squares of avg ΔE vs days.
            var xs = series.Select(e => (e.TimestampUtc - t0).TotalDays).ToList();
            var ys = series.Select(e => e.AvgDeltaE2000).ToList();
            int n = xs.Count;
            double xMean = xs.Average(), yMean = ys.Average();
            double sxx = xs.Sum(x => (x - xMean) * (x - xMean));
            if (sxx < 1e-9) return null;
            double sxy = 0;
            for (int i = 0; i < n; i++) sxy += (xs[i] - xMean) * (ys[i] - yMean);
            double slope = sxy / sxx;
            double intercept = yMean - slope * xMean;

            // Residual standard error and slope SE.
            double sse = 0;
            for (int i = 0; i < n; i++)
            {
                double resid = ys[i] - (intercept + slope * xs[i]);
                sse += resid * resid;
            }
            double residSe = n > 2 ? Math.Sqrt(sse / (n - 2)) : 0.0;
            double slopeSe = residSe / Math.Sqrt(sxx);
            // A ~95% two-sided gate; small-n crudeness is acceptable because failing the
            // gate only means we SAY LESS, never more. A zero slope SE (perfectly linear
            // history) makes any nonzero slope significant, not insignificant.
            bool significant = Math.Abs(slope) > Math.Max(2.0 * slopeSe, 1e-9);

            double nowDays = (nowUtc - t0).TotalDays;
            double predictedNow = intercept + slope * nowDays;

            // Predictive interval at "now": regression prediction SE (with extrapolation
            // growth) RSS-combined with the measurement uncertainty of the checks themselves.
            double predictionSe = residSe * Math.Sqrt(
                1.0 + 1.0 / n + (nowDays - xMean) * (nowDays - xMean) / sxx);
            double measurementStdU = series
                .Select(e => (e.U95DeltaE ?? 0.0) / UncertaintyBudget.CoverageFactorK)
                .DefaultIfEmpty(0.0)
                .Average();
            double predictedU95 = UncertaintyBudget.CoverageFactorK *
                UncertaintyBudget.Rss(predictionSe, measurementStdU);

            double baseline = ys[0];
            double driftNow = predictedNow - baseline;

            DateTime? crossing = null;
            if (significant && slope > 0)
            {
                double thresholdValue = baseline + RecalibrateDriftThreshold;
                if (predictedNow < thresholdValue)
                {
                    double daysToCross = (thresholdValue - intercept) / slope;
                    if (daysToCross > nowDays && daysToCross < nowDays + 3650)
                        crossing = t0.AddDays(daysToCross);
                }
                else
                {
                    crossing = nowUtc; // already past the threshold per the trend
                }
            }

            string summary;
            if (!significant)
            {
                summary = $"Stable: no statistically significant drift over {spanDays:F0} days " +
                          $"({series.Count} checks; slope {slope * 30:+0.00;-0.00} ΔE/month ± {slopeSe * 30 * 2:F2}).";
            }
            else if (slope > 0)
            {
                summary = $"Drifting {slope * 30:F2} ΔE/month: estimated {Math.Max(driftNow, 0):F2} ΔE above " +
                          $"baseline today (± {predictedU95:F2})" +
                          (crossing is { } c
                              ? c <= nowUtc
                                  ? " — past the recalibration threshold; re-verify or recalibrate."
                                  : $" — projected to cross the recalibration threshold around {c:yyyy-MM-dd}."
                              : ".");
            }
            else
            {
                summary = $"Improving trend ({slope * 30:F2} ΔE/month) — likely warm-up or environment " +
                          "effects in the early checks; keep trending.";
            }

            return new Prediction(
                slope, slopeSe, significant, predictedNow, predictedU95,
                driftNow, crossing, series.Count, spanDays, summary);
        }
    }
}
