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
            Custom
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
                _ => 200
            };
        }
    }
}
