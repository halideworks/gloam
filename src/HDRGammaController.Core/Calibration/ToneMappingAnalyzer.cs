using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>One dense-ladder point: requested content nits vs measured panel nits.</summary>
    public sealed record ToneMapLadderPoint(double RequestedNits, double MeasuredNits);

    /// <summary>One APL point: white window covering the given percent of the screen area.</summary>
    public sealed record AplPoint(double WindowPercent, double MeasuredNits);

    /// <summary>
    /// HDR tone-mapping characterization (roadmap 2.3): what the panel ACTUALLY does near
    /// peak versus what its DXGI/EDID metadata claims — the single biggest source of HDR
    /// user confusion. JSON-serializable so it persists on the report summary.
    /// </summary>
    public sealed record ToneMappingCharacterization
    {
        public int SchemaVersion { get; init; } = 1;
        public double ClaimedPeakNits { get; init; }
        public double ClaimedMaxFullFrameNits { get; init; }

        /// <summary>True reachable peak at the measurement window size (max measured Y).</summary>
        public double MeasuredPeakNits { get; init; }

        /// <summary>Measured 100%-window (full-frame) white, when the APL sweep ran.</summary>
        public double? MeasuredFullFramePeakNits { get; init; }

        /// <summary>Content nits where measured tracking first falls below 95% of the
        /// request — the true tone-mapping knee. Equal to the top measured rung when no
        /// roll-off was observed inside the measured range.</summary>
        public double KneeNits { get; init; }

        /// <summary>False when the ladder never left the 95% tracking band (knee is a
        /// lower bound, not an observed departure).</summary>
        public bool KneeObserved { get; init; }

        public IReadOnlyList<ToneMapLadderPoint> Ladder { get; init; } = Array.Empty<ToneMapLadderPoint>();
        public IReadOnlyList<AplPoint> AplSweep { get; init; } = Array.Empty<AplPoint>();

        /// <summary>HGIG-style values for game HDR calibration menus.</summary>
        public double HgigPeakNits { get; init; }
        public double SuggestedMaxCllNits { get; init; }
        public double? SuggestedMaxFallNits { get; init; }
    }

    /// <summary>
    /// Pure analysis over the dense near-peak ladder and the APL window sweep. The knee
    /// threshold is 95% tracking (−5%): tighter would flag measurement noise, looser would
    /// miss real roll-off starts.
    /// </summary>
    public static class ToneMappingAnalyzer
    {
        public const double KneeTrackingThreshold = 0.95;

        /// <summary>Ladder fractions of the CLAIMED peak, dense where roll-off lives, with
        /// two above-claim probes to catch under-claiming panels.</summary>
        public static readonly IReadOnlyList<double> LadderFractionsOfClaimedPeak =
            new[] { 0.40, 0.50, 0.60, 0.70, 0.80, 0.85, 0.90, 0.95, 1.00, 1.10, 1.25 };

        /// <summary>APL window sizes as percent of screen AREA.</summary>
        public static readonly IReadOnlyList<double> AplWindowPercents =
            new[] { 1.0, 4.0, 10.0, 25.0, 50.0, 100.0 };

        public static ToneMappingCharacterization Analyze(
            double claimedPeakNits,
            double claimedMaxFullFrameNits,
            IReadOnlyList<ToneMapLadderPoint> ladder,
            IReadOnlyList<AplPoint>? aplSweep = null)
        {
            ArgumentNullException.ThrowIfNull(ladder);
            var points = ladder
                .Where(p => double.IsFinite(p.RequestedNits) && double.IsFinite(p.MeasuredNits) &&
                            p.RequestedNits > 0 && p.MeasuredNits >= 0)
                .OrderBy(p => p.RequestedNits)
                .ToList();
            if (points.Count < 3)
                throw new ArgumentException("Tone-mapping analysis needs at least 3 valid ladder points.", nameof(ladder));

            double measuredPeak = points.Max(p => p.MeasuredNits);

            // Knee: first requested level whose tracking ratio drops below the threshold,
            // linearly interpolated against the previous (still-tracking) rung for a
            // sub-rung estimate.
            double knee = points[^1].RequestedNits;
            bool observed = false;
            for (int i = 0; i < points.Count; i++)
            {
                double ratio = points[i].MeasuredNits / points[i].RequestedNits;
                if (ratio >= KneeTrackingThreshold) continue;

                if (i == 0)
                {
                    knee = points[0].RequestedNits;
                }
                else
                {
                    double prevRatio = points[i - 1].MeasuredNits / points[i - 1].RequestedNits;
                    double t = prevRatio <= ratio
                        ? 0.0
                        : (prevRatio - KneeTrackingThreshold) / (prevRatio - ratio);
                    knee = points[i - 1].RequestedNits +
                           Math.Clamp(t, 0, 1) * (points[i].RequestedNits - points[i - 1].RequestedNits);
                }
                observed = true;
                break;
            }

            var apl = (aplSweep ?? Array.Empty<AplPoint>())
                .Where(p => double.IsFinite(p.MeasuredNits) && p.MeasuredNits >= 0)
                .OrderBy(p => p.WindowPercent)
                .ToList();
            double? fullFrame = apl.Count > 0 && Math.Abs(apl[^1].WindowPercent - 100.0) < 1e-6
                ? apl[^1].MeasuredNits
                : null;

            return new ToneMappingCharacterization
            {
                ClaimedPeakNits = claimedPeakNits,
                ClaimedMaxFullFrameNits = claimedMaxFullFrameNits,
                MeasuredPeakNits = measuredPeak,
                MeasuredFullFramePeakNits = fullFrame,
                KneeNits = knee,
                KneeObserved = observed,
                Ladder = points,
                AplSweep = apl,
                // HGIG: games should tone-map to the panel's REAL reachable peak, not the
                // metadata claim; "peak white" in HGIG menus = the measured window peak.
                HgigPeakNits = measuredPeak,
                SuggestedMaxCllNits = measuredPeak,
                SuggestedMaxFallNits = fullFrame,
            };
        }

        /// <summary>Human-readable summary for the report window / persistence.</summary>
        public static string Describe(ToneMappingCharacterization c)
        {
            ArgumentNullException.ThrowIfNull(c);
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            double claimError = c.ClaimedPeakNits > 0
                ? (c.MeasuredPeakNits - c.ClaimedPeakNits) / c.ClaimedPeakNits
                : double.NaN;
            sb.Append(string.Format(inv,
                "Tone mapping: measured peak {0:F0} nits vs claimed {1:F0}",
                c.MeasuredPeakNits, c.ClaimedPeakNits));
            if (double.IsFinite(claimError))
                sb.Append(string.Format(inv, " ({0:+0%;-0%})", claimError));
            sb.Append('.');

            sb.Append(c.KneeObserved
                ? string.Format(inv, " Roll-off knee at ~{0:F0} nits (tracking falls below 95%).", c.KneeNits)
                : string.Format(inv, " No roll-off inside the measured range (tracks to {0:F0} nits).", c.KneeNits));

            if (c.MeasuredFullFramePeakNits is { } ff)
            {
                sb.Append(string.Format(inv, " Full-frame white {0:F0} nits", ff));
                if (c.ClaimedMaxFullFrameNits > 0)
                    sb.Append(string.Format(inv, " (claimed {0:F0})", c.ClaimedMaxFullFrameNits));
                sb.Append('.');
                if (c.AplSweep.Count >= 2)
                {
                    double small = c.AplSweep[0].MeasuredNits;
                    if (small > 0 && ff < small * 0.85)
                        sb.Append(string.Format(inv,
                            " ABL: brightness falls {0:P0} from {1:F0}% to full-frame windows.",
                            1 - ff / small, c.AplSweep[0].WindowPercent));
                }
            }

            sb.Append(string.Format(inv,
                " Game HDR (HGIG) suggestion: peak/MaxTML {0:F0} nits; metadata MaxCLL {1:F0}",
                c.HgigPeakNits, c.SuggestedMaxCllNits));
            if (c.SuggestedMaxFallNits is { } fall)
                sb.Append(string.Format(inv, ", MaxFALL {0:F0}", fall));
            sb.Append(" nits.");

            return sb.ToString();
        }
    }
}
