using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Patch sets for the post-apply verification sweep. The standard quick set lives in
    /// <see cref="CalibrationVerifier.BuildVerificationPatches"/>; this adds the opt-in
    /// "Detailed verification" set: a fine grayscale, per-primary saturation ramps and the
    /// ColorChecker-classic memory colors. Several times more patches than the quick sweep,
    /// so the report can show a ΔE distribution, per-patch errors and a category breakdown.
    /// </summary>
    public static class VerificationPatchSets
    {
        /// <summary>
        /// Patch count of <see cref="Detailed"/>: 21 grayscale + 12 saturation ramp +
        /// 6 memory colors. Also the cap for the per-patch list persisted in
        /// <see cref="CalibrationReportSummary.DetailedPatches"/>.
        /// </summary>
        public const int DetailedPatchCount = 21 + 12 + 6;

        /// <summary>
        /// The detailed verification sweep (39 patches):
        ///  - fine grayscale, 21 steps from white to black every 5% signal;
        ///  - R/G/B saturation ramps at 25/50/75/100% saturation (mixed toward white;
        ///    the 100% steps are the pure primaries);
        ///  - the six ColorChecker-classic memory colors (standard sRGB renditions).
        /// Like the standard verify set these are SDR signal patches; in HDR mode Windows
        /// renders them with the sRGB curve at the SDR white level, and the PQ wire ladder
        /// (FP16, absolute nits) stays a separate sweep exactly as in the standard verify.
        /// </summary>
        /// <param name="target">Target the sweep will be graded against; used to attach the
        /// expected XYZ/Lab to each patch.</param>
        /// <param name="hdrMode">True when the display is in HDR mode (the patches are then
        /// sRGB content on the PQ wire; the expected values use the sRGB content curve).</param>
        public static IReadOnlyList<ColorPatch> Detailed(CalibrationTarget target, bool hdrMode)
        {
            var defs = new List<(string Name, double R, double G, double B, PatchCategory Cat)>();

            // Fine grayscale: white down to black in 5% steps (white first, matching the
            // standard sweep so the meter starts bright and the normalization peak is early).
            for (int i = 20; i >= 0; i--)
            {
                double level = i / 20.0;
                string name = i switch
                {
                    20 => "White",
                    0 => "Black",
                    _ => $"Gray {level * 100:F0}%",
                };
                defs.Add((name, level, level, level, PatchCategory.Grayscale));
            }

            // Per-primary saturation ramps: saturation s mixes the other channels toward
            // white (1 - s), same convention as the calibration patch generator. The 100%
            // steps are the pure primaries and are categorized as such.
            foreach (var (channel, r, g, b) in new[]
                     {
                         ("Red", 1, 0, 0), ("Green", 0, 1, 0), ("Blue", 0, 0, 1),
                     })
            {
                foreach (double sat in new[] { 0.25, 0.50, 0.75, 1.00 })
                {
                    double bg = 1.0 - sat;
                    var cat = sat >= 1.0 ? PatchCategory.Primary : PatchCategory.Saturated;
                    defs.Add(($"{channel} {sat * 100:F0}%",
                        r == 1 ? 1 : bg, g == 1 ? 1 : bg, b == 1 ? 1 : bg, cat));
                }
            }

            // Memory colors: the first six ColorChecker-classic patches, standard sRGB
            // renditions (8-bit sRGB-encoded values / 255, used directly as signal levels).
            var memory = new (string Name, byte R, byte G, byte B)[]
            {
                ("Dark skin",    115,  82,  68),
                ("Light skin",   194, 150, 130),
                ("Blue sky",      98, 122, 157),
                ("Foliage",       87, 108,  67),
                ("Blue flower",  133, 128, 177),
                ("Bluish green", 103, 189, 170),
            };
            foreach (var m in memory)
                defs.Add((m.Name, m.R / 255.0, m.G / 255.0, m.B / 255.0, PatchCategory.MemoryColor));

            // Expected color for each patch: the same content curve the grading uses (sRGB
            // for PQ targets / HDR mode, where SDR patches ride the sRGB curve at SDR white).
            bool srgbContent = hdrMode || target.TransferFunction == TransferFunctionType.Pq;
            double Linearize(double s) => srgbContent ? ColorMath.SrgbEotf(s) : target.ApplyEotf(s);

            return defs.Select((p, i) =>
            {
                var targetXyz = target.LinearRgbToXyz(new LinearRgb(
                    Linearize(p.R), Linearize(p.G), Linearize(p.B)));
                return new ColorPatch
                {
                    Name = p.Name,
                    DisplayRgb = new LinearRgb(p.R, p.G, p.B),
                    Category = p.Cat,
                    Index = i,
                    IsCritical = true,
                    TargetXyz = targetXyz,
                    TargetLab = ColorMath.XyzToLab(targetXyz),
                };
            }).ToList();
        }
    }

    /// <summary>One verified patch: its name, analysis category and measured ΔE2000.</summary>
    public readonly record struct PatchDeltaE(string Name, PatchCategory Category, double DeltaE);

    /// <summary>
    /// Per-category average ΔE2000 for a detailed verification sweep. A null value means the
    /// sweep contained no patches of that category.
    /// </summary>
    public sealed class CategoryBreakdown
    {
        public double? GrayscaleDeltaE { get; init; }
        public double? PrimariesDeltaE { get; init; }
        public double? SaturationDeltaE { get; init; }
        public double? MemoryColorsDeltaE { get; init; }

        /// <summary>Single display line, categories without data omitted.</summary>
        public string ToDisplayText()
        {
            var parts = new List<string>(4);
            void Add(string label, double? v)
            {
                if (v is { } d) parts.Add($"{label} {d:F2}");
            }
            Add("Grayscale", GrayscaleDeltaE);
            Add("Primaries", PrimariesDeltaE);
            Add("Saturation sweeps", SaturationDeltaE);
            Add("Memory colors", MemoryColorsDeltaE);
            return parts.Count == 0
                ? "No category data."
                : "Average ΔE2000 by category: " + string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The detailed-verification math kept WPF-free so it is unit-testable: ΔE histogram
    /// bucketing, worst-patch ranking and the per-category breakdown.
    /// </summary>
    public static class VerificationAnalysis
    {
        // Bucket upper edges; the last bucket is open-ended (5+).
        private static readonly double[] BucketUpperEdges = { 0.5, 1.0, 2.0, 3.0, 5.0 };

        /// <summary>Histogram bucket labels, parallel to <see cref="HistogramCounts"/>.</summary>
        public static readonly IReadOnlyList<string> HistogramBucketLabels =
            new[] { "0-0.5", "0.5-1", "1-2", "2-3", "3-5", "5+" };

        /// <summary>
        /// Buckets ΔE values into [0,0.5), [0.5,1), [1,2), [2,3), [3,5), [5,∞).
        /// Always returns six counts.
        /// </summary>
        public static int[] HistogramCounts(IEnumerable<double> deltaEs)
        {
            var counts = new int[BucketUpperEdges.Length + 1];
            foreach (double de in deltaEs)
            {
                int bucket = 0;
                while (bucket < BucketUpperEdges.Length && de >= BucketUpperEdges[bucket])
                    bucket++;
                counts[bucket]++;
            }
            return counts;
        }

        /// <summary>The worst patches by ΔE, highest first, at most <paramref name="count"/>.</summary>
        public static IReadOnlyList<PatchDeltaE> WorstPatches(
            IEnumerable<PatchDeltaE> patches, int count = 10)
        {
            return patches.OrderByDescending(p => p.DeltaE).Take(count).ToList();
        }

        /// <summary>
        /// Per-category averages. Skin tones count as memory colors (that is what they are);
        /// categories not present in the detailed set (secondaries, grid, ...) are ignored.
        /// </summary>
        public static CategoryBreakdown ComputeCategoryBreakdown(IEnumerable<PatchDeltaE> patches)
        {
            var gray = new List<double>();
            var primary = new List<double>();
            var saturation = new List<double>();
            var memory = new List<double>();
            foreach (var p in patches)
            {
                switch (p.Category)
                {
                    case PatchCategory.Grayscale: gray.Add(p.DeltaE); break;
                    case PatchCategory.Primary: primary.Add(p.DeltaE); break;
                    case PatchCategory.Saturated: saturation.Add(p.DeltaE); break;
                    case PatchCategory.MemoryColor:
                    case PatchCategory.SkinTone: memory.Add(p.DeltaE); break;
                }
            }

            static double? Avg(List<double> values) => values.Count > 0 ? values.Average() : null;
            return new CategoryBreakdown
            {
                GrayscaleDeltaE = Avg(gray),
                PrimariesDeltaE = Avg(primary),
                SaturationDeltaE = Avg(saturation),
                MemoryColorsDeltaE = Avg(memory),
            };
        }
    }
}
