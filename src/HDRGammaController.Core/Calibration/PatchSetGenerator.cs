using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Generates optimized patch sequences for display calibration.
    /// </summary>
    /// <remarks>
    /// The patch set significantly affects calibration quality and time.
    /// This generator creates patches optimized for:
    /// - Accurate grayscale tracking (gamma and white balance)
    /// - Precise primary and secondary colors
    /// - Good gamut coverage for 3D LUT generation
    /// - Critical patches for perceptual accuracy (near-neutrals, skin tones)
    ///
    /// Patch ordering is optimized to minimize display settling time
    /// (avoiding large luminance jumps) and group related patches.
    /// </remarks>
    public class PatchSetGenerator
    {
        // ------- Drift-anchor scheduling (M7) -------
        // Long runs on OLED/ABL-prone panels drift in luminance as the panel heats and the
        // average picture level wanders. Periodic re-reads of white and black provide the
        // time series DriftCompensator fits its multiplicative correction from, and give
        // the measurement validator real repeated-white data to gate on.

        /// <summary>A drift-check white is re-read after this many ordinary patches.</summary>
        internal const int DriftWhiteIntervalPatches = 25;

        /// <summary>A drift-check black is re-read after this many ordinary patches.</summary>
        internal const int DriftBlackIntervalPatches = 50;

        // ------- Per-channel grayscale-tracking ramps (E4) -------
        // A shared luminance tone curve + 3×3 matrix cannot correct a LEVEL-DEPENDENT gray
        // cast (e.g. green running hot only at mid-levels). Single-channel R/G/B ramps
        // measure each channel's true EOTF so Lut3DGenerator can fit per-channel tone
        // curves and the install path can ship true per-channel correction LUTs
        // (VCGT-style: LUT_c = f_c⁻¹ ∘ target).

        /// <summary>
        /// Signal levels for the single-channel grayscale-tracking ramps. Levels below 0.25
        /// are deliberately skipped: at low drives a single channel emits so little light
        /// that colorimeter chroma noise dominates the reading and would corrupt the fit.
        /// The 1.0 entries collapse onto the existing full-drive primaries during snapping
        /// dedupe, which is intended — the full-drive primary IS the ramp's top anchor.
        /// </summary>
        internal static readonly double[] SingleChannelRampLevels = { 0.25, 0.4, 0.55, 0.7, 0.85, 1.0 };

        /// <summary>
        /// Preset calibration configurations.
        /// </summary>
        public enum CalibrationPreset
        {
            /// <summary>Quick verification (~50 patches, ~3-5 min)</summary>
            Quick,

            /// <summary>Standard calibration (~200 patches, ~15 min)</summary>
            Standard,

            /// <summary>Thorough calibration (~500 patches, ~35 min)</summary>
            Thorough,

            /// <summary>Full profile (~1000+ patches, ~60+ min)</summary>
            Full,

            /// <summary>Grayscale only (~21 patches, ~2 min)</summary>
            GrayscaleOnly,

            /// <summary>Custom patch count</summary>
            Custom,

            /// <summary>
            /// Adaptive placement (roadmap 1.1): a small coarse seed set is measured
            /// first, then <see cref="AdaptivePatchPlanner"/> keeps requesting batches
            /// where the fitted model is most uncertain until the accuracy targets or
            /// the patch budget are hit. <see cref="GeneratePatchSet"/> returns only the
            /// SEED for this preset; the extra rounds are produced during the run.
            /// </summary>
            Adaptive
        }

        /// <summary>
        /// Generates a patch set for the specified target and preset.
        /// </summary>
        public static IReadOnlyList<ColorPatch> GeneratePatchSet(
            CalibrationTarget target,
            CalibrationPreset preset)
        {
            return preset switch
            {
                CalibrationPreset.Quick => GenerateQuickSet(target),
                CalibrationPreset.Standard => GenerateStandardSet(target),
                CalibrationPreset.Thorough => GenerateThoroughSet(target),
                CalibrationPreset.Full => GenerateFullSet(target),
                CalibrationPreset.GrayscaleOnly => GenerateGrayscaleSet(target, 21),
                CalibrationPreset.Adaptive => GenerateAdaptiveSeedSet(target),
                _ => GenerateStandardSet(target)
            };
        }

        /// <summary>
        /// Generates a custom patch set with specified grid size.
        /// </summary>
        /// <param name="target">Calibration target</param>
        /// <param name="gridSize">3D grid size (e.g., 5 = 5x5x5 = 125 patches)</param>
        /// <param name="grayscalePoints">Number of grayscale points (default 21)</param>
        public static IReadOnlyList<ColorPatch> GenerateCustomSet(
            CalibrationTarget target,
            int gridSize,
            int grayscalePoints = 21)
        {
            var patches = new List<ColorPatch>();
            int index = 0;

            // Always start with black and white for reference
            patches.Add(CreatePatch("Black", new LinearRgb(0, 0, 0), PatchCategory.Grayscale, index++, true, target));
            patches.Add(CreatePatch("White", new LinearRgb(1, 1, 1), PatchCategory.Grayscale, index++, true, target));

            // Grayscale ramp
            AddGrayscaleRamp(patches, ref index, grayscalePoints, target);

            // Primaries and secondaries at full saturation
            AddPrimariesAndSecondaries(patches, ref index, target);

            // 3D grid
            AddGrid(patches, ref index, gridSize, target);

            // Optimize ordering for minimal settling time. No drift anchors for custom
            // sets: callers control the patch budget explicitly.
            return FinalizePatchSet(patches, target, insertDriftAnchors: false);
        }

        #region Preset Generators

        private static IReadOnlyList<ColorPatch> GenerateQuickSet(CalibrationTarget target)
        {
            var patches = new List<ColorPatch>();
            int index = 0;

            // Reference patches
            patches.Add(CreatePatch("Black", new LinearRgb(0, 0, 0), PatchCategory.Grayscale, index++, true, target));
            patches.Add(CreatePatch("White", new LinearRgb(1, 1, 1), PatchCategory.Grayscale, index++, true, target));

            // 11-point grayscale
            AddGrayscaleRamp(patches, ref index, 11, target);

            // Primaries and secondaries
            AddPrimariesAndSecondaries(patches, ref index, target);

            // Small grid (3x3x3 minus corners already covered = ~20 patches)
            AddGrid(patches, ref index, 3, target, skipExisting: true);

            // Near-neutral patches (critical for white point accuracy)
            AddNearNeutrals(patches, ref index, target);

            // Quick is a short run (~5 min): drift over that window is small and the
            // anchor overhead would be a large fraction of the patch budget. Skip.
            return FinalizePatchSet(patches, target, insertDriftAnchors: false);
        }

        private static IReadOnlyList<ColorPatch> GenerateStandardSet(CalibrationTarget target)
        {
            var patches = new List<ColorPatch>();
            int index = 0;

            // Reference patches
            patches.Add(CreatePatch("Black", new LinearRgb(0, 0, 0), PatchCategory.Grayscale, index++, true, target));
            patches.Add(CreatePatch("White", new LinearRgb(1, 1, 1), PatchCategory.Grayscale, index++, true, target));

            // 21-point grayscale
            AddGrayscaleRamp(patches, ref index, 21, target);

            // Primaries and secondaries at multiple saturations
            AddPrimariesAndSecondaries(patches, ref index, target);
            AddPrimariesAtSaturation(patches, ref index, 0.5, target);

            // 5x5x5 grid
            AddGrid(patches, ref index, 5, target, skipExisting: true);

            // Near-neutrals
            AddNearNeutrals(patches, ref index, target);

            // Additional critical colors
            AddSkinTones(patches, ref index, target);

            // Standard (~15 min) stays anchor-free to preserve its advertised duration;
            // drift compensation is a Thorough/Full feature where run length warrants it.
            return FinalizePatchSet(patches, target, insertDriftAnchors: false);
        }

        private static IReadOnlyList<ColorPatch> GenerateThoroughSet(CalibrationTarget target)
        {
            var patches = new List<ColorPatch>();
            int index = 0;

            // Reference patches
            patches.Add(CreatePatch("Black", new LinearRgb(0, 0, 0), PatchCategory.Grayscale, index++, true, target));
            patches.Add(CreatePatch("White", new LinearRgb(1, 1, 1), PatchCategory.Grayscale, index++, true, target));

            // 33-point grayscale
            AddGrayscaleRamp(patches, ref index, 33, target);

            // Primaries and secondaries at multiple saturations
            AddPrimariesAndSecondaries(patches, ref index, target);
            AddPrimariesAtSaturation(patches, ref index, 0.75, target);
            AddPrimariesAtSaturation(patches, ref index, 0.5, target);
            AddPrimariesAtSaturation(patches, ref index, 0.25, target);

            // Single-channel ramps for per-channel grayscale-tracking correction (E4).
            // Added BEFORE the grid so a colliding grid axis node dedupes in favor of the
            // named ramp member (dedupe keeps the first occurrence).
            AddSingleChannelRamps(patches, ref index, target);

            // 7x7x7 grid
            AddGrid(patches, ref index, 7, target, skipExisting: true);

            // Near-neutrals at multiple levels
            AddNearNeutrals(patches, ref index, target);

            // Skin tones
            AddSkinTones(patches, ref index, target);

            // Shadow detail
            AddShadowDetail(patches, ref index, target);

            // Long run: interleave periodic white/black re-reads for drift compensation.
            return FinalizePatchSet(patches, target, insertDriftAnchors: true);
        }

        private static IReadOnlyList<ColorPatch> GenerateFullSet(CalibrationTarget target)
        {
            var patches = new List<ColorPatch>();
            int index = 0;

            // Reference patches
            patches.Add(CreatePatch("Black", new LinearRgb(0, 0, 0), PatchCategory.Grayscale, index++, true, target));
            patches.Add(CreatePatch("White", new LinearRgb(1, 1, 1), PatchCategory.Grayscale, index++, true, target));

            // 65-point grayscale (maximum resolution)
            AddGrayscaleRamp(patches, ref index, 65, target);

            // Primaries at many saturation levels
            for (double sat = 1.0; sat >= 0.1; sat -= 0.1)
            {
                AddPrimariesAtSaturation(patches, ref index, sat, target);
            }

            // Single-channel ramps for per-channel grayscale-tracking correction (E4).
            AddSingleChannelRamps(patches, ref index, target);

            // 9x9x9 grid
            AddGrid(patches, ref index, 9, target, skipExisting: true);

            // Near-neutrals
            AddNearNeutrals(patches, ref index, target);

            // Skin tones
            AddSkinTones(patches, ref index, target);

            // Shadow and highlight detail
            AddShadowDetail(patches, ref index, target);
            AddHighlightDetail(patches, ref index, target);

            // Long run: interleave periodic white/black re-reads for drift compensation.
            return FinalizePatchSet(patches, target, insertDriftAnchors: true);
        }

        private static IReadOnlyList<ColorPatch> GenerateGrayscaleSet(CalibrationTarget target, int points)
        {
            var patches = new List<ColorPatch>();
            int index = 0;

            patches.Add(CreatePatch("Black", new LinearRgb(0, 0, 0), PatchCategory.Grayscale, index++, true, target));
            patches.Add(CreatePatch("White", new LinearRgb(1, 1, 1), PatchCategory.Grayscale, index++, true, target));

            AddGrayscaleRamp(patches, ref index, points, target);

            // No reorder needed for a monotone ramp; dedupe in case 8-bit snapping
            // collapsed neighboring levels for very dense ramps.
            var deduped = DeduplicateBySnappedSignal(patches);
            Reindex(deduped);
            return deduped;
        }

        #endregion

        #region Adaptive placement (1.1)

        // ------- Adaptive seed + candidate pool (roadmap 1.1) -------
        // The adaptive preset measures a small coarse SEED first, fits the display model,
        // finds where the model predicts worst, and measures there next. The seed must
        // pin every anchor the model builder and validator need (black, white, full-drive
        // primaries/secondaries) and give each 1D manifold enough spread that the FIRST
        // leave-one-out residual is meaningful, while staying small (~30 patches) so the
        // savings come from the adaptive rounds, not the seed.

        /// <summary>Grayscale seed levels (9 points incl. black/white), spanning shadow to highlight.</summary>
        internal static readonly double[] AdaptiveSeedGrayLevels =
            { 0.0, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875, 1.0 };

        /// <summary>Per-channel seed ramp levels (4 interior points; full drive is the primary anchor).</summary>
        internal static readonly double[] AdaptiveSeedRampLevels = { 0.25, 0.5, 0.75 };

        /// <summary>
        /// Coarse seed set for the adaptive preset (~30 patches, 8-bit snapped): black +
        /// white + a 9-point grayscale, R/G/B ramps at 3 interior levels each, and the
        /// full-drive primaries and secondaries. Drift anchors are interleaved (an
        /// adaptive run can grow long across rounds, so it follows the Thorough/Full drift
        /// rule).
        /// </summary>
        internal static IReadOnlyList<ColorPatch> GenerateAdaptiveSeedSet(CalibrationTarget target)
        {
            var patches = new List<ColorPatch>();
            int index = 0;

            // Grayscale seed (includes black and white).
            foreach (double level in AdaptiveSeedGrayLevels)
            {
                string name = level <= 0 ? "Black" : level >= 1 ? "White" : $"Gray {level * 100:F0}%";
                bool critical = level <= 0 || level >= 1;
                patches.Add(CreatePatch(name, new LinearRgb(level, level, level),
                    PatchCategory.Grayscale, index++, critical, target));
            }

            // Single-channel ramps so the per-channel curves have real seed shape.
            foreach (double level in AdaptiveSeedRampLevels)
            {
                string levelStr = $"{level * 100:F0}%";
                patches.Add(CreatePatch($"Red Ramp {levelStr}", new LinearRgb(level, 0, 0),
                    PatchCategory.Saturated, index++, false, target));
                patches.Add(CreatePatch($"Green Ramp {levelStr}", new LinearRgb(0, level, 0),
                    PatchCategory.Saturated, index++, false, target));
                patches.Add(CreatePatch($"Blue Ramp {levelStr}", new LinearRgb(0, 0, level),
                    PatchCategory.Saturated, index++, false, target));
            }

            // Full-drive primaries and secondaries (model matrix anchors).
            AddPrimariesAndSecondaries(patches, ref index, target);

            return FinalizePatchSet(patches, target, insertDriftAnchors: true);
        }

        /// <summary>
        /// Bounded candidate pool for <see cref="AdaptivePatchPlanner"/> in the adaptive
        /// preset's signal domain. The pool is deliberately restricted so scoring stays
        /// fast and every candidate is a place the 1D/near-neutral model can actually be
        /// refined:
        /// <list type="bullet">
        /// <item><b>Grayscale axis</b> — a dense 1/255-step-family lattice (every 4th
        /// 8-bit code, ~64 levels) where tone-curve curvature is highest and cheapest to
        /// resolve.</item>
        /// <item><b>Per-channel ramps</b> — the same dense lattice on each of R/G/B (from
        /// the 0.2 fit floor up) so a level-dependent single-channel cast can be localized.</item>
        /// <item><b>Near-neutral + primary/secondary cube planes</b> — a coarse lattice of
        /// slightly-tinted neutrals and reduced-saturation primaries/secondaries, the
        /// color regions a shared-matrix model most often mispredicts. The full color cube
        /// is NOT enumerated: it would explode the pool and the SDR correction model is
        /// 1D-tone + 3×3-matrix, so off-neutral interior nodes add little the matrix can
        /// use.</item>
        /// </list>
        /// All candidates are 8-bit snapped and de-duplicated; each is tagged with its
        /// <see cref="SignalManifold"/> via <see cref="AdaptivePatchPlanner.ClassifySignal"/>.
        /// </summary>
        internal static IReadOnlyList<SignalPoint> BuildAdaptiveCandidatePool()
        {
            var pool = new List<SignalPoint>();
            var seen = new HashSet<(int, int, int, SignalManifold)>();

            void Add(double r, double g, double b)
            {
                double rs = Snap8Bit(r), gs = Snap8Bit(g), bs = Snap8Bit(b);
                var point = AdaptivePatchPlanner.ClassifySignal(new LinearRgb(rs, gs, bs));
                var key = ((int)Math.Round(rs * 255), (int)Math.Round(gs * 255),
                           (int)Math.Round(bs * 255), point.Manifold);
                if (seen.Add(key))
                    pool.Add(point);
            }

            // Dense grayscale + per-channel ramp lattice: every 4th 8-bit code.
            for (int code = 0; code <= 255; code += AdaptivePoolGrayStep)
            {
                double v = code / 255.0;
                Add(v, v, v);                 // gray axis
                if (v >= AdaptivePoolRampFloor)
                {
                    Add(v, 0, 0);             // red ramp
                    Add(0, v, 0);             // green ramp
                    Add(0, 0, v);             // blue ramp
                }
            }

            // Near-neutral tinted planes: small tints around a coarse level grid.
            foreach (double level in AdaptivePoolNeutralLevels)
            {
                Add(level + AdaptivePoolTint, level, level - AdaptivePoolTint); // warm
                Add(level - AdaptivePoolTint, level, level + AdaptivePoolTint); // cool
                Add(level - AdaptivePoolTint, level + AdaptivePoolTint, level - AdaptivePoolTint); // green
            }

            // Reduced-saturation primary/secondary planes (mixed toward white).
            foreach (double sat in AdaptivePoolSaturations)
            {
                double bg = 1.0 - sat;
                Add(1, bg, bg); Add(bg, 1, bg); Add(bg, bg, 1);   // primaries
                Add(bg, 1, 1); Add(1, bg, 1); Add(1, 1, bg);      // secondaries
            }

            return pool;
        }

        /// <summary>Grayscale/ramp candidate spacing in 8-bit codes (every 4th code ≈ 1.6% signal).</summary>
        internal const int AdaptivePoolGrayStep = 4;

        /// <summary>Lowest signal admitted to the per-channel ramp candidates (matches the fit floor).</summary>
        internal const double AdaptivePoolRampFloor = 0.2;

        /// <summary>Coarse levels for the near-neutral tinted candidate planes.</summary>
        internal static readonly double[] AdaptivePoolNeutralLevels = { 0.2, 0.35, 0.5, 0.65, 0.8 };

        /// <summary>Tint magnitude for the near-neutral candidate planes.</summary>
        internal const double AdaptivePoolTint = 0.03;

        /// <summary>Saturations for the reduced-saturation primary/secondary candidate planes.</summary>
        internal static readonly double[] AdaptivePoolSaturations = { 0.25, 0.5, 0.75 };

        /// <summary>
        /// Wraps planner-chosen signal points as measurable <see cref="ColorPatch"/>es for
        /// the next adaptive round, computing each patch's target XYZ/Lab through the same
        /// path as the preset generators. Names carry the round number for progress/report.
        /// </summary>
        internal static IReadOnlyList<ColorPatch> BuildAdaptiveRoundPatches(
            IReadOnlyList<SignalPoint> points, CalibrationTarget target, int round, int startIndex)
        {
            var patches = new List<ColorPatch>(points.Count);
            int index = startIndex;
            foreach (var p in points)
            {
                var rgb = new LinearRgb(p.R, p.G, p.B);
                var category = ClassifyAdaptiveCategory(p);
                patches.Add(CreatePatch($"Adaptive R{round} {p.Manifold} {index}", rgb, category, index++, false, target));
            }
            return patches;
        }

        private static PatchCategory ClassifyAdaptiveCategory(SignalPoint p) => p.Manifold switch
        {
            SignalManifold.Gray => PatchCategory.Grayscale,
            SignalManifold.Cube => PatchCategory.General,
            _ => PatchCategory.Saturated // single-channel ramps: sub-Primary to dodge the near-black validator gate
        };

        /// <summary>
        /// Running drift-anchor cadence state for adaptive rounds. Held across rounds by
        /// the orchestrator so drift white/black re-reads keep their fixed periodic spacing
        /// over the whole adaptive tail (same intervals as the preset sequences).
        /// </summary>
        internal sealed class AdaptiveDriftState
        {
            public int SinceWhite;
            public int SinceBlack;
            public int WhiteCount;
            public int BlackCount;
        }

        /// <summary>
        /// Builds one adaptive round's measurable sequence: the planner's chosen points as
        /// patches, with drift-check white/black anchors interleaved at the same cadence
        /// (<see cref="DriftWhiteIntervalPatches"/> / <see cref="DriftBlackIntervalPatches"/>)
        /// as the preset sequences, continued across rounds via <paramref name="drift"/>.
        /// </summary>
        internal static IReadOnlyList<ColorPatch> BuildAdaptiveRoundSequence(
            IReadOnlyList<SignalPoint> points, CalibrationTarget target, int round,
            int startIndex, AdaptiveDriftState drift)
        {
            var seq = new List<ColorPatch>(points.Count + 2);
            int index = startIndex;
            foreach (var p in points)
            {
                var rgb = new LinearRgb(p.R, p.G, p.B);
                seq.Add(CreatePatch($"Adaptive R{round} {p.Manifold} {index}", rgb,
                    ClassifyAdaptiveCategory(p), index++, false, target));

                drift.SinceWhite++;
                drift.SinceBlack++;
                if (drift.SinceWhite >= DriftWhiteIntervalPatches)
                {
                    seq.Add(CreatePatch($"Drift White A{++drift.WhiteCount}", new LinearRgb(1, 1, 1),
                        PatchCategory.DriftCheck, index++, false, target));
                    drift.SinceWhite = 0;
                }
                if (drift.SinceBlack >= DriftBlackIntervalPatches)
                {
                    seq.Add(CreatePatch($"Drift Black A{++drift.BlackCount}", new LinearRgb(0, 0, 0),
                        PatchCategory.DriftCheck, index++, false, target));
                    drift.SinceBlack = 0;
                }
            }
            return seq;
        }

        #endregion

        #region Patch Generators

        private static void AddGrayscaleRamp(List<ColorPatch> patches, ref int index, int points, CalibrationTarget target)
        {
            // Skip 0 and 1 (already added as Black and White)
            for (int i = 1; i < points - 1; i++)
            {
                double level = i / (double)(points - 1);
                string name = $"Gray {level * 100:F0}%";

                patches.Add(CreatePatch(name, new LinearRgb(level, level, level),
                    PatchCategory.Grayscale, index++, i % 5 == 0, target));
            }
        }

        private static void AddPrimariesAndSecondaries(List<ColorPatch> patches, ref int index, CalibrationTarget target)
        {
            // Primary colors
            patches.Add(CreatePatch("Red 100%", new LinearRgb(1, 0, 0), PatchCategory.Primary, index++, true, target));
            patches.Add(CreatePatch("Green 100%", new LinearRgb(0, 1, 0), PatchCategory.Primary, index++, true, target));
            patches.Add(CreatePatch("Blue 100%", new LinearRgb(0, 0, 1), PatchCategory.Primary, index++, true, target));

            // Secondary colors
            patches.Add(CreatePatch("Cyan 100%", new LinearRgb(0, 1, 1), PatchCategory.Secondary, index++, true, target));
            patches.Add(CreatePatch("Magenta 100%", new LinearRgb(1, 0, 1), PatchCategory.Secondary, index++, true, target));
            patches.Add(CreatePatch("Yellow 100%", new LinearRgb(1, 1, 0), PatchCategory.Secondary, index++, true, target));
        }

        private static void AddPrimariesAtSaturation(List<ColorPatch> patches, ref int index, double saturation, CalibrationTarget target)
        {
            string satStr = $"{saturation * 100:F0}%";

            // Primaries at reduced saturation (mixed toward white)
            double bg = 1.0 - saturation;
            patches.Add(CreatePatch($"Red {satStr}", new LinearRgb(1, bg, bg), PatchCategory.Saturated, index++, false, target));
            patches.Add(CreatePatch($"Green {satStr}", new LinearRgb(bg, 1, bg), PatchCategory.Saturated, index++, false, target));
            patches.Add(CreatePatch($"Blue {satStr}", new LinearRgb(bg, bg, 1), PatchCategory.Saturated, index++, false, target));

            // Secondaries
            patches.Add(CreatePatch($"Cyan {satStr}", new LinearRgb(bg, 1, 1), PatchCategory.Saturated, index++, false, target));
            patches.Add(CreatePatch($"Magenta {satStr}", new LinearRgb(1, bg, 1), PatchCategory.Saturated, index++, false, target));
            patches.Add(CreatePatch($"Yellow {satStr}", new LinearRgb(1, 1, bg), PatchCategory.Saturated, index++, false, target));
        }

        /// <summary>
        /// Single-channel R-only/G-only/B-only ramps (E4). Downstream consumers identify
        /// ramp members by DisplayRgb inspection (exactly one nonzero channel), NOT by
        /// category, so no validator/report switch needs a new case.
        ///
        /// Category choice: sub-full levels are <see cref="PatchCategory.Saturated"/>, not
        /// Primary. The measurement validator applies a near-black plausibility gate to
        /// every Primary patch (Y &gt; black + 0.5 cd/m²), which a legitimate Blue-at-25%
        /// reading (~0.4 nits on a 100-nit panel) would false-fail — tagging ramps Primary
        /// would make Thorough/Full runs unvalidatable on dim panels. The 1.0 entries
        /// dedupe onto the existing full-drive PRIMARY patches, so the ramp's top anchor
        /// keeps Primary's median multi-read (M8) and validator gates.
        /// </summary>
        private static void AddSingleChannelRamps(List<ColorPatch> patches, ref int index, CalibrationTarget target)
        {
            foreach (double level in SingleChannelRampLevels)
            {
                var category = level >= 0.99 ? PatchCategory.Primary : PatchCategory.Saturated;
                string levelStr = $"{level * 100:F0}%";
                patches.Add(CreatePatch($"Red Ramp {levelStr}", new LinearRgb(level, 0, 0),
                    category, index++, false, target));
                patches.Add(CreatePatch($"Green Ramp {levelStr}", new LinearRgb(0, level, 0),
                    category, index++, false, target));
                patches.Add(CreatePatch($"Blue Ramp {levelStr}", new LinearRgb(0, 0, level),
                    category, index++, false, target));
            }
        }

        private static void AddGrid(List<ColorPatch> patches, ref int index, int size, CalibrationTarget target, bool skipExisting = false)
        {
            var existing = patches.Select(p => (p.DisplayRgb.R, p.DisplayRgb.G, p.DisplayRgb.B)).ToHashSet();

            for (int ri = 0; ri < size; ri++)
            {
                double r = ri / (double)(size - 1);
                for (int gi = 0; gi < size; gi++)
                {
                    double g = gi / (double)(size - 1);
                    for (int bi = 0; bi < size; bi++)
                    {
                        double b = bi / (double)(size - 1);

                        // Skip if already exists
                        if (skipExisting && existing.Contains((r, g, b)))
                            continue;

                        string name = $"Grid R{ri}G{gi}B{bi}";
                        bool isNeutral = Math.Abs(r - g) < 0.01 && Math.Abs(g - b) < 0.01;
                        var category = isNeutral ? PatchCategory.Grayscale : PatchCategory.General;

                        patches.Add(CreatePatch(name, new LinearRgb(r, g, b), category, index++, false, target));
                    }
                }
            }
        }

        private static void AddNearNeutrals(List<ColorPatch> patches, ref int index, CalibrationTarget target)
        {
            // Near-neutral colors are critical for white point accuracy
            // Small deviations from gray with slight tints
            double[] levels = { 0.2, 0.4, 0.6, 0.8 };
            double tint = 0.02; // Small tint amount

            foreach (double level in levels)
            {
                // Warm neutral (slight red)
                patches.Add(CreatePatch($"Warm Neutral {level * 100:F0}%",
                    new LinearRgb(level + tint, level, level - tint),
                    PatchCategory.NearNeutral, index++, true, target));

                // Cool neutral (slight blue)
                patches.Add(CreatePatch($"Cool Neutral {level * 100:F0}%",
                    new LinearRgb(level - tint, level, level + tint),
                    PatchCategory.NearNeutral, index++, true, target));

                // Green neutral
                patches.Add(CreatePatch($"Green Neutral {level * 100:F0}%",
                    new LinearRgb(level - tint, level + tint, level - tint),
                    PatchCategory.NearNeutral, index++, true, target));
            }
        }

        private static void AddSkinTones(List<ColorPatch> patches, ref int index, CalibrationTarget target)
        {
            // Standard skin tone references (approximate sRGB values)
            // These are widely used reference skin tones for display calibration
            patches.Add(CreatePatch("Skin Light", new LinearRgb(0.85, 0.65, 0.52), PatchCategory.SkinTone, index++, true, target));
            patches.Add(CreatePatch("Skin Medium", new LinearRgb(0.70, 0.45, 0.30), PatchCategory.SkinTone, index++, true, target));
            patches.Add(CreatePatch("Skin Dark", new LinearRgb(0.40, 0.25, 0.18), PatchCategory.SkinTone, index++, true, target));
            patches.Add(CreatePatch("Skin Asian", new LinearRgb(0.80, 0.60, 0.45), PatchCategory.SkinTone, index++, true, target));
        }

        private static void AddShadowDetail(List<ColorPatch> patches, ref int index, CalibrationTarget target)
        {
            // Extra detail in shadow region (0-10%)
            for (double level = 0.01; level <= 0.10; level += 0.01)
            {
                string name = $"Shadow {level * 100:F0}%";
                patches.Add(CreatePatch(name, new LinearRgb(level, level, level),
                    PatchCategory.Shadow, index++, false, target));
            }
        }

        private static void AddHighlightDetail(List<ColorPatch> patches, ref int index, CalibrationTarget target)
        {
            // Extra detail in highlight region (90-99%)
            for (double level = 0.91; level <= 0.99; level += 0.01)
            {
                string name = $"Highlight {level * 100:F0}%";
                patches.Add(CreatePatch(name, new LinearRgb(level, level, level),
                    PatchCategory.Highlight, index++, false, target));
            }
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Snaps a 0-1 signal value onto the 8-bit grid (v = round(v*255)/255).
        /// </summary>
        /// <remarks>
        /// M9 measurement integrity: the patch window renders SDR patches through an 8-bit
        /// swapchain, so the stimulus that physically reaches the panel is quantized to
        /// n/255 regardless of what value we generate. Snapping at GENERATION time makes
        /// the model's input coordinate equal the displayed stimulus exactly, instead of
        /// fitting measured light against a coordinate up to half an 8-bit step away.
        /// HDR wire-ladder patches (ColorPatch.Nits) are NOT snapped — they render through
        /// an FP16 scRGB surface with no 8-bit quantization (and are generated elsewhere).
        /// </remarks>
        internal static double Snap8Bit(double v) =>
            Math.Round(Math.Clamp(v, 0.0, 1.0) * 255.0) / 255.0;

        private static ColorPatch CreatePatch(string name, LinearRgb signalRgb, PatchCategory category,
            int index, bool isCritical, CalibrationTarget target)
        {
            // Snap the requested signal to the displayable 8-bit grid BEFORE computing the
            // target XYZ, so the target corresponds to the stimulus actually shown.
            signalRgb = new LinearRgb(Snap8Bit(signalRgb.R), Snap8Bit(signalRgb.G), Snap8Bit(signalRgb.B));

            // Calculate target XYZ for this patch
            // IMPORTANT: signalRgb contains the gamma-encoded signal values (0-1) that will be sent to the display.
            // To compute the target XYZ (what the color SHOULD look like), we must:
            // 1. Apply the target's EOTF to decode signal to linear light
            // 2. Convert linear RGB to XYZ using the target's color matrix
            // In Windows HDR desktop mode these calibration patches are SDR UI content
            // riding the OS SDR-to-HDR path. They must be graded as sRGB content, not as
            // direct PQ code values; absolute PQ stimuli are represented separately by
            // ColorPatch.Nits via HdrWirePatchSet.
            double linearR = LinearizeSignalForUiPatch(target, signalRgb.R);
            double linearG = LinearizeSignalForUiPatch(target, signalRgb.G);
            double linearB = LinearizeSignalForUiPatch(target, signalRgb.B);
            var linearRgb = new LinearRgb(linearR, linearG, linearB);
            var targetXyz = target.LinearRgbToXyz(linearRgb);

            return new ColorPatch
            {
                Name = name,
                DisplayRgb = signalRgb,
                Category = category,
                Index = index,
                IsCritical = isCritical,
                TargetXyz = targetXyz,
                TargetLab = ColorMath.XyzToLab(targetXyz)
            };
        }

        private static double LinearizeSignalForUiPatch(CalibrationTarget target, double signal) =>
            target.TransferFunction == TransferFunctionType.Pq
                ? ColorMath.SrgbEotf(signal)
                : target.ApplyEotf(signal);

        /// <summary>
        /// Shared tail of every preset generator: dedupe patches that collapsed onto the
        /// same 8-bit signal after snapping, order for minimal settling, optionally
        /// interleave drift anchors, then re-index.
        /// </summary>
        private static IReadOnlyList<ColorPatch> FinalizePatchSet(
            List<ColorPatch> patches, CalibrationTarget target, bool insertDriftAnchors)
        {
            var deduped = DeduplicateBySnappedSignal(patches);
            var ordered = OptimizePatchOrder(deduped);
            if (insertDriftAnchors)
                ordered = InsertDriftAnchors(ordered, target);
            Reindex(ordered);
            return ordered;
        }

        /// <summary>
        /// Removes patches whose (snapped) 8-bit signal triple duplicates an earlier patch,
        /// keeping the first occurrence. Snapping can collapse neighbors (e.g. very dense
        /// ramps or grid nodes that coincide with existing anchors), and duplicate model
        /// inputs waste measurement time without adding information.
        /// </summary>
        private static List<ColorPatch> DeduplicateBySnappedSignal(List<ColorPatch> patches)
        {
            var seen = new HashSet<(int R, int G, int B)>();
            var result = new List<ColorPatch>(patches.Count);
            foreach (var p in patches)
            {
                var key = ((int)Math.Round(p.DisplayRgb.R * 255.0),
                           (int)Math.Round(p.DisplayRgb.G * 255.0),
                           (int)Math.Round(p.DisplayRgb.B * 255.0));
                if (seen.Add(key))
                    result.Add(p);
            }
            return result;
        }

        /// <summary>
        /// Interleaves periodic drift-check anchors into an already-ordered sequence (M7):
        /// a white re-read every <see cref="DriftWhiteIntervalPatches"/> ordinary patches
        /// and a black re-read every <see cref="DriftBlackIntervalPatches"/>, plus a white
        /// at the very start (the t0 reference all later ratios are computed against) and
        /// at the very end (so the drift fit covers the tail of the run).
        ///
        /// Anchors are deliberately inserted AFTER OptimizePatchOrder and are never
        /// reordered: their value is their fixed periodic position in TIME, which is what
        /// lets DriftCompensator interpolate a luminance factor for every timestamp.
        /// </summary>
        private static List<ColorPatch> InsertDriftAnchors(List<ColorPatch> ordered, CalibrationTarget target)
        {
            int whiteN = 0, blackN = 0;
            ColorPatch White() => CreatePatch($"Drift White {++whiteN}", new LinearRgb(1, 1, 1),
                PatchCategory.DriftCheck, 0, false, target);
            ColorPatch Black() => CreatePatch($"Drift Black {++blackN}", new LinearRgb(0, 0, 0),
                PatchCategory.DriftCheck, 0, false, target);

            var result = new List<ColorPatch>(ordered.Count + ordered.Count / DriftWhiteIntervalPatches + 4)
            {
                White() // t0 reference, before warm-up/ABL has developed
            };

            int sinceWhite = 0, sinceBlack = 0;
            foreach (var p in ordered)
            {
                result.Add(p);
                sinceWhite++;
                sinceBlack++;
                if (sinceWhite >= DriftWhiteIntervalPatches)
                {
                    result.Add(White());
                    sinceWhite = 0;
                }
                if (sinceBlack >= DriftBlackIntervalPatches)
                {
                    result.Add(Black());
                    sinceBlack = 0;
                }
            }

            if (sinceWhite > 0)
                result.Add(White()); // closing anchor

            return result;
        }

        private static void Reindex(List<ColorPatch> ordered)
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i] = new ColorPatch
                {
                    Name = ordered[i].Name,
                    DisplayRgb = ordered[i].DisplayRgb,
                    Category = ordered[i].Category,
                    Index = i,
                    IsCritical = ordered[i].IsCritical,
                    TargetXyz = ordered[i].TargetXyz,
                    TargetLab = ordered[i].TargetLab
                };
            }
        }

        /// <summary>
        /// Optimizes patch order to minimize display settling time.
        /// </summary>
        /// <remarks>
        /// Large luminance jumps require longer settling time. This greedy nearest-neighbor
        /// pass minimizes the luminance change between consecutive patches.
        ///
        /// Ordering key: the "luminance" used below is the Rec.709-weighted sum of the
        /// GAMMA-ENCODED signal values, not true linear-light luminance. That is fine —
        /// the key is ordinal only (it decides which patch is "nearest"), and gamma
        /// encoding is monotone per channel, so the produced order is a sensible
        /// low-settle sequence even though the numeric distances are not photometric.
        ///
        /// Tradeoff (m3): step-minimization produces a quasi-monotonic luminance ramp,
        /// which concentrates any panel drift (warm-up, ABL) into a systematic error along
        /// the ramp — the worst case for drift ALIASING into the tone curve. Full
        /// randomization would decorrelate drift from level but multiplies settle time
        /// (every step becomes a large luminance jump) and hammers OLED fall-time
        /// behavior. We keep step-minimization for the time budget and instead measure
        /// the drift directly: DriftCheck anchors are interleaved at fixed periodic
        /// positions AFTER this reordering (see InsertDriftAnchors) and DriftCompensator
        /// removes the fitted drift from all measurements.
        /// </remarks>
        private static List<ColorPatch> OptimizePatchOrder(List<ColorPatch> patches)
        {
            if (patches.Count <= 2) return patches;

            var ordered = new List<ColorPatch>();
            var remaining = new HashSet<ColorPatch>(patches);

            // Start with black
            var black = patches.FirstOrDefault(p => p.DisplayRgb.Max < 0.01);
            if (black != null)
            {
                ordered.Add(black);
                remaining.Remove(black);
            }
            else if (remaining.Count > 0)
            {
                ordered.Add(remaining.First());
                remaining.Remove(ordered[0]);
            }

            // Greedy nearest-neighbor by luminance
            while (remaining.Count > 0)
            {
                var last = ordered[^1];
                double lastLum = 0.2126 * last.DisplayRgb.R + 0.7152 * last.DisplayRgb.G + 0.0722 * last.DisplayRgb.B;

                ColorPatch? nearest = null;
                double nearestDist = double.MaxValue;

                foreach (var patch in remaining)
                {
                    double patchLum = 0.2126 * patch.DisplayRgb.R + 0.7152 * patch.DisplayRgb.G + 0.0722 * patch.DisplayRgb.B;
                    double dist = Math.Abs(patchLum - lastLum);

                    // Slight preference for same category
                    if (patch.Category != last.Category)
                        dist += 0.01;

                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = patch;
                    }
                }

                if (nearest != null)
                {
                    ordered.Add(nearest);
                    remaining.Remove(nearest);
                }
            }

            // Note: re-indexing happens in FinalizePatchSet AFTER drift anchors are
            // interleaved, so Index always reflects the final measurement sequence.
            return ordered;
        }

        #endregion

        /// <summary>
        /// Gets estimated calibration time for a preset.
        /// </summary>
        public static TimeSpan GetEstimatedTime(CalibrationPreset preset)
        {
            return preset switch
            {
                CalibrationPreset.Quick => TimeSpan.FromMinutes(3),
                CalibrationPreset.Standard => TimeSpan.FromMinutes(15),
                CalibrationPreset.Thorough => TimeSpan.FromMinutes(35),
                CalibrationPreset.Full => TimeSpan.FromMinutes(60),
                CalibrationPreset.GrayscaleOnly => TimeSpan.FromMinutes(2),
                // Adaptive: budget-based UPPER bound (usually finishes well before this).
                CalibrationPreset.Adaptive => TimeSpan.FromSeconds(AdaptivePatchPlanner.DefaultPatchBudget * 3),
                _ => TimeSpan.FromMinutes(15)
            };
        }

        /// <summary>
        /// Gets approximate patch count for a preset.
        /// </summary>
        public static int GetApproximatePatchCount(CalibrationPreset preset)
        {
            return preset switch
            {
                CalibrationPreset.Quick => 50,
                CalibrationPreset.Standard => 200,
                CalibrationPreset.Thorough => 500,
                CalibrationPreset.Full => 1000,
                CalibrationPreset.GrayscaleOnly => 21,
                CalibrationPreset.Adaptive => AdaptivePatchPlanner.DefaultPatchBudget,
                _ => 200
            };
        }
    }
}
