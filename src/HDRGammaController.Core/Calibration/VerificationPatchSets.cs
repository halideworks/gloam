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

            // Expected color for each patch: delegated to the ONE authoritative rule in
            // CalibrationVerifier.LinearizePatchSignal, which ComputeMetrics (the grader)
            // also uses — the expectation and the grade cannot drift apart. (hdrMode is
            // retained as a parameter for the docs above; target selection already enforces
            // hdrMode <=> PQ target, and the rule keys off the target.)
            return defs.Select((p, i) =>
            {
                var targetXyz = target.LinearRgbToXyz(new LinearRgb(
                    CalibrationVerifier.LinearizePatchSignal(target, p.R),
                    CalibrationVerifier.LinearizePatchSignal(target, p.G),
                    CalibrationVerifier.LinearizePatchSignal(target, p.B)));
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

    /// <summary>
    /// One colored HDR verification stimulus: a Rec.2020-container hue at an absolute PQ
    /// luminance rung. Stimuli are CONTAINER-REFERRED — the reference is what an ideal
    /// HDR10 (Rec.2020 + ST.2084) mastering display would emit for this wire signal — and
    /// they are graded ABSOLUTELY against <see cref="ReferenceXyz"/>. A real panel that
    /// falls short of Rec.2020 (or cannot reach the rung luminance on a saturated hue)
    /// shows its actual gamut mapping / tone mapping here; that shortfall is exactly the
    /// above-SDR-white color error this set characterizes.
    /// </summary>
    /// <param name="Name">Display name, e.g. "Red 203".</param>
    /// <param name="Hue">Hue name only ("Red" … "Yellow").</param>
    /// <param name="RungNits">The stimulus luminance in cd/m²; ReferenceXyz.Y equals this.</param>
    /// <param name="UnitRgb">The unit hue in the Rec.2020 container (active channels = 1).</param>
    /// <param name="Rec2020ChannelNits">Per-channel drive in the Rec.2020 container,
    /// expressed in white-equivalent nits (a channel value of n contributes what that
    /// channel contributes to an n-nit D65 white). Active channels carry
    /// RungNits / Y(unit hue); inactive channels are 0.</param>
    /// <param name="ReferenceXyz">Absolute reference XYZ in cd/m² from the Rec.2020
    /// primaries; Y == RungNits exactly.</param>
    /// <param name="ScRgbNits">The same color converted to scRGB (linear Rec.709/sRGB
    /// primaries) per-channel absolute nits — the triple to hand to the FP16 renderer's
    /// PresentNits, whose surface is scRGB (value = nits / 80). Wide-gamut hues carry
    /// NEGATIVE inactive components here; scRGB FP16 encodes them and the compositor's
    /// 709→2020 wire conversion restores the in-container positive color.</param>
    public sealed record ColoredHdrStimulus(
        string Name,
        string Hue,
        double RungNits,
        LinearRgb UnitRgb,
        LinearRgb Rec2020ChannelNits,
        CieXyz ReferenceXyz,
        LinearRgb ScRgbNits)
    {
        /// <summary>True for R/G/B (one active channel); false for C/M/Y.</summary>
        public bool IsPrimaryHue =>
            (UnitRgb.R > 0 ? 1 : 0) + (UnitRgb.G > 0 ? 1 : 0) + (UnitRgb.B > 0 ? 1 : 0) == 1;
    }

    /// <summary>
    /// Builds the colored HDR verification set: R, G, B, C, M, Y at absolute PQ luminance
    /// rungs {100, 203, 400} nits (each stimulus's LUMINANCE equals its rung — "Red 203"
    /// is linear Rec.2020 red scaled so Y = 203 cd/m²). Rungs above the display's
    /// reachable/reported peak are skipped; the 100-nit rung is always kept so every HDR
    /// verify grades at least one colored rung.
    /// </summary>
    public static class ColoredHdrVerificationSet
    {
        /// <summary>Absolute luminance rungs in cd/m²: diffuse-white-ish anchors 100 and
        /// 203 (BT.2408 reference white) plus a 400-nit highlight rung.</summary>
        public static readonly IReadOnlyList<double> RungNits = new[] { 100.0, 203.0, 400.0 };

        private static readonly (string Name, double R, double G, double B)[] Hues =
        {
            ("Red", 1, 0, 0), ("Green", 0, 1, 0), ("Blue", 0, 0, 1),
            ("Cyan", 0, 1, 1), ("Magenta", 1, 0, 1), ("Yellow", 1, 1, 0),
        };

        /// <summary>
        /// Builds the stimuli for a display whose peak luminance is
        /// <paramref name="displayPeakNits"/> (measured wire peak when available, else the
        /// DXGI-reported panel peak). Rungs above the peak are skipped — grading a 400-nit
        /// stimulus on a 300-nit panel would grade the panel's clip, not its color — but
        /// the 100-nit rung always survives (and when the peak is unknown or non-physical
        /// only the 100-nit rung is emitted).
        /// </summary>
        public static IReadOnlyList<ColoredHdrStimulus> Build(double displayPeakNits)
        {
            bool peakKnown = double.IsFinite(displayPeakNits) && displayPeakNits > 0;
            var stimuli = new List<ColoredHdrStimulus>();
            foreach (double rung in RungNits)
            {
                if (rung > 100.0 && (!peakKnown || rung > displayPeakNits))
                    continue;
                foreach (var (name, r, g, b) in Hues)
                {
                    var unit = new LinearRgb(r, g, b);
                    // Luminance of the unit hue in the container: the Y row of the
                    // Rec.2020 RGB→XYZ matrix (matrix is D65-normalized: (1,1,1) → Y=1).
                    var unitXyz = ColorMath.LinearRec2020ToXyz(unit);
                    double scale = rung / unitXyz.Y; // white-equivalent nits per channel
                    var referenceXyz = unitXyz * scale; // Y == rung exactly
                    var scRgb = ColorMath.XyzToLinearSrgb(referenceXyz);
                    stimuli.Add(new ColoredHdrStimulus(
                        $"{name} {rung:F0}", name, rung, unit,
                        unit.Scale(scale), referenceXyz, scRgb));
                }
            }
            return stimuli;
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

    public enum ProfileActivationStatus
    {
        Passed,
        InsufficientSignal,
        Warning
    }

    /// <summary>
    /// Result of the post-install activation sentinel: compare native-vs-verified patch
    /// errors for the same measured patches. A warning does not prove Windows ignored the
    /// profile, but it flags the professional failure mode we care about: after installing,
    /// a clearly inaccurate native panel did not measurably move toward the target.
    /// </summary>
    public sealed record ProfileActivationCheck(
        ProfileActivationStatus Status,
        int ComparedPatchCount,
        double NativeAverageDeltaE,
        double VerifiedAverageDeltaE,
        string Message)
    {
        public bool ShouldWarn => Status == ProfileActivationStatus.Warning;
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
            foreach (double de in deltaEs.Where(double.IsFinite))
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
            return patches
                .Where(p => double.IsFinite(p.DeltaE))
                .OrderByDescending(p => p.DeltaE)
                .Take(count)
                .ToList();
        }

        /// <summary>The best patches by ΔE, lowest first, at most <paramref name="count"/>.</summary>
        public static IReadOnlyList<PatchDeltaE> BestPatches(
            IEnumerable<PatchDeltaE> patches, int count = 10)
        {
            return patches
                .Where(p => double.IsFinite(p.DeltaE))
                .OrderBy(p => p.DeltaE)
                .Take(count)
                .ToList();
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
            foreach (var p in patches.Where(p => double.IsFinite(p.DeltaE)))
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

        /// <summary>
        /// Activation sentinel for the installed profile. It reuses patches already measured
        /// by the verify sweep, so it adds no extra meter time: native and verified ΔE are
        /// compared only for matching grayscale patches (and primaries for full correction).
        /// When native error is already tiny, the check is intentionally non-actionable
        /// because a no-op and a successful subtle correction are indistinguishable.
        /// </summary>
        public static ProfileActivationCheck AnalyzeProfileActivation(
            IEnumerable<PatchDeltaE>? nativePatches,
            IEnumerable<PatchDeltaE>? verifiedPatches,
            bool whitePointOnly)
        {
            var nativeByKey = Eligible(nativePatches, whitePointOnly)
                .GroupBy(Key)
                .ToDictionary(g => g.Key, g => g.First().DeltaE, StringComparer.OrdinalIgnoreCase);

            var pairs = Eligible(verifiedPatches, whitePointOnly)
                .Select(p => (Patch: p, Key: Key(p)))
                .Where(p => nativeByKey.ContainsKey(p.Key))
                .Select(p => (Native: nativeByKey[p.Key], Verified: p.Patch.DeltaE))
                .ToList();

            if (pairs.Count < 3)
            {
                return new ProfileActivationCheck(
                    ProfileActivationStatus.InsufficientSignal,
                    pairs.Count,
                    0,
                    0,
                    "Profile activation sentinel: not enough matching native and verified patches to confirm profile movement.");
            }

            double nativeAverage = pairs.Average(p => p.Native);
            double verifiedAverage = pairs.Average(p => p.Verified);
            double improvement = nativeAverage - verifiedAverage;
            double relative = nativeAverage > 0 ? improvement / nativeAverage : 0;

            if (nativeAverage < 2.0)
            {
                return new ProfileActivationCheck(
                    ProfileActivationStatus.InsufficientSignal,
                    pairs.Count,
                    nativeAverage,
                    verifiedAverage,
                    $"Profile activation sentinel: native error on comparable patches was already low (avg ΔE {nativeAverage:F2}), so no large movement is expected.");
            }

            if (improvement >= 0.5 || relative >= 0.20 || verifiedAverage <= 1.5)
            {
                return new ProfileActivationCheck(
                    ProfileActivationStatus.Passed,
                    pairs.Count,
                    nativeAverage,
                    verifiedAverage,
                    $"Profile activation sentinel passed: comparable patches moved from avg ΔE {nativeAverage:F2} native to {verifiedAverage:F2} verified.");
            }

            return new ProfileActivationCheck(
                ProfileActivationStatus.Warning,
                pairs.Count,
                nativeAverage,
                verifiedAverage,
                $"Profile activation sentinel warning: comparable patches were avg ΔE {nativeAverage:F2} native and {verifiedAverage:F2} after install, so the verification did not detect the expected movement toward target. Confirm the Windows color profile is active for this display and that verification ran on the same HDR/SDR mode used during measurement.");
        }

        private static IEnumerable<PatchDeltaE> Eligible(IEnumerable<PatchDeltaE>? patches, bool whitePointOnly)
        {
            if (patches == null) yield break;
            foreach (var p in patches)
            {
                if (!double.IsFinite(p.DeltaE)) continue;
                if (p.Name.Equals("Black", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Category == PatchCategory.Grayscale ||
                    (!whitePointOnly && p.Category == PatchCategory.Primary))
                {
                    yield return p;
                }
            }
        }

        private static string Key(PatchDeltaE patch)
            => $"{patch.Category}:{patch.Name}".ToLowerInvariant();

        /// <summary>
        /// Optional caveat line shown under the category breakdown so the saturation and
        /// memory-color averages are not misread. In HDR mode those patches are SDR content
        /// measured through the Windows SDR-to-HDR mapping and the panel's own HDR color
        /// processing; the installed correction is built from neutral (gray-ladder) and
        /// primary measurements - and in white-point-only mode it deliberately leaves the
        /// panel's color rendering untouched - so elevated values in those categories
        /// describe the panel's HDR color handling, not the grayscale/white point
        /// calibration. Returns null when no caveat applies (SDR sweeps).
        /// </summary>
        public static string? CategoryCaveat(bool hdrMode, bool whitePointOnly)
        {
            if (!hdrMode) return null;
            return whitePointOnly
                ? "Note: saturation and memory color patches travel the Windows SDR-to-HDR " +
                  "pipeline and the panel's own HDR color processing. This white-point-only " +
                  "calibration corrects gray tone and white point and intentionally leaves " +
                  "color rendering to the panel, so those categories show the panel's native " +
                  "HDR color accuracy rather than a calibration error."
                : "Note: saturation and memory color patches travel the Windows SDR-to-HDR " +
                  "pipeline and the panel's HDR color processing. The correction is built " +
                  "from neutral and primary measurements, so elevated values in those " +
                  "categories largely reflect the panel's HDR rendering of mixed colors " +
                  "rather than the grayscale or white point calibration.";
        }
    }
}
