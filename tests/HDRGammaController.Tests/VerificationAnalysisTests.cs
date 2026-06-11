using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Pins the detailed-verification math: histogram bucketing (boundary values must land
    /// in the right bucket), worst-patch ranking, the per-category breakdown, and the
    /// composition of the detailed patch set the sweep measures.
    /// </summary>
    public class VerificationAnalysisTests
    {
        // ------------------------------------------------------------------ histogram

        [Fact]
        public void HistogramCounts_BucketsBoundariesCorrectly()
        {
            // Lower edges are inclusive, upper edges exclusive: [0,0.5) [0.5,1) [1,2) [2,3) [3,5) [5,inf).
            var deltaEs = new[] { 0.0, 0.49, 0.5, 0.99, 1.0, 1.99, 2.0, 2.99, 3.0, 4.99, 5.0, 12.0 };

            int[] counts = VerificationAnalysis.HistogramCounts(deltaEs);

            Assert.Equal(new[] { 2, 2, 2, 2, 2, 2 }, counts);
        }

        [Fact]
        public void HistogramCounts_Empty_ReturnsSixZeroBuckets()
        {
            int[] counts = VerificationAnalysis.HistogramCounts(Array.Empty<double>());

            Assert.Equal(6, counts.Length);
            Assert.All(counts, c => Assert.Equal(0, c));
            Assert.Equal(6, VerificationAnalysis.HistogramBucketLabels.Count);
        }

        [Fact]
        public void HistogramCounts_TotalEqualsInputCount()
        {
            var rng = new Random(42);
            var deltaEs = Enumerable.Range(0, 100).Select(_ => rng.NextDouble() * 8).ToList();

            Assert.Equal(100, VerificationAnalysis.HistogramCounts(deltaEs).Sum());
        }

        // ------------------------------------------------------------------ worst patches

        [Fact]
        public void WorstPatches_SortsDescendingAndCapsAtTen()
        {
            var patches = Enumerable.Range(0, 15)
                .Select(i => new PatchDeltaE($"Patch {i}", PatchCategory.General, i * 0.5))
                .ToList();

            var worst = VerificationAnalysis.WorstPatches(patches);

            Assert.Equal(10, worst.Count);
            Assert.Equal("Patch 14", worst[0].Name);            // largest dE first
            Assert.Equal(14 * 0.5, worst[0].DeltaE, 9);
            for (int i = 1; i < worst.Count; i++)
                Assert.True(worst[i].DeltaE <= worst[i - 1].DeltaE, "must be sorted descending");
        }

        [Fact]
        public void WorstPatches_FewerThanCap_ReturnsAll()
        {
            var patches = new[]
            {
                new PatchDeltaE("A", PatchCategory.Grayscale, 1.0),
                new PatchDeltaE("B", PatchCategory.Primary, 3.0),
            };

            var worst = VerificationAnalysis.WorstPatches(patches);

            Assert.Equal(2, worst.Count);
            Assert.Equal("B", worst[0].Name);
        }

        // ------------------------------------------------------------------ category breakdown

        [Fact]
        public void CategoryBreakdown_AveragesPerCategory()
        {
            var patches = new[]
            {
                new PatchDeltaE("Gray 50%", PatchCategory.Grayscale, 1.0),
                new PatchDeltaE("Gray 25%", PatchCategory.Grayscale, 3.0),
                new PatchDeltaE("Red 100%", PatchCategory.Primary, 2.0),
                new PatchDeltaE("Red 50%", PatchCategory.Saturated, 4.0),
                new PatchDeltaE("Red 25%", PatchCategory.Saturated, 2.0),
                new PatchDeltaE("Blue sky", PatchCategory.MemoryColor, 5.0),
            };

            var breakdown = VerificationAnalysis.ComputeCategoryBreakdown(patches);

            Assert.NotNull(breakdown.GrayscaleDeltaE);
            Assert.Equal(2.0, breakdown.GrayscaleDeltaE!.Value, 9);
            Assert.Equal(2.0, breakdown.PrimariesDeltaE!.Value, 9);
            Assert.Equal(3.0, breakdown.SaturationDeltaE!.Value, 9);
            Assert.Equal(5.0, breakdown.MemoryColorsDeltaE!.Value, 9);
        }

        [Fact]
        public void CategoryBreakdown_MissingCategory_IsNull()
        {
            var patches = new[]
            {
                new PatchDeltaE("Gray 50%", PatchCategory.Grayscale, 1.0),
            };

            var breakdown = VerificationAnalysis.ComputeCategoryBreakdown(patches);

            Assert.NotNull(breakdown.GrayscaleDeltaE);
            Assert.Null(breakdown.PrimariesDeltaE);
            Assert.Null(breakdown.SaturationDeltaE);
            Assert.Null(breakdown.MemoryColorsDeltaE);
        }

        [Fact]
        public void CategoryBreakdown_SkinTonesCountAsMemoryColors()
        {
            var patches = new[]
            {
                new PatchDeltaE("Skin tone", PatchCategory.SkinTone, 2.0),
                new PatchDeltaE("Blue sky", PatchCategory.MemoryColor, 4.0),
            };

            var breakdown = VerificationAnalysis.ComputeCategoryBreakdown(patches);

            Assert.Equal(3.0, breakdown.MemoryColorsDeltaE!.Value, 9);
        }

        [Fact]
        public void CategoryBreakdown_DisplayText_OmitsMissingCategories()
        {
            var breakdown = VerificationAnalysis.ComputeCategoryBreakdown(new[]
            {
                new PatchDeltaE("Gray 50%", PatchCategory.Grayscale, 1.25),
            });

            string text = breakdown.ToDisplayText();

            Assert.Contains("Grayscale 1.25", text);
            Assert.DoesNotContain("Primaries", text);
            Assert.DoesNotContain("Memory colors", text);
        }

        // ------------------------------------------------------------------ detailed patch set

        [Fact]
        public void Detailed_HasExpectedComposition()
        {
            var patches = VerificationPatchSets.Detailed(StandardTargets.SrgbGamma22, hdrMode: false);

            Assert.Equal(VerificationPatchSets.DetailedPatchCount, patches.Count);
            Assert.Equal(39, patches.Count);
            Assert.Equal(21, patches.Count(p => p.Category == PatchCategory.Grayscale));
            Assert.Equal(3, patches.Count(p => p.Category == PatchCategory.Primary));
            Assert.Equal(9, patches.Count(p => p.Category == PatchCategory.Saturated));
            Assert.Equal(6, patches.Count(p => p.Category == PatchCategory.MemoryColor));
            Assert.Equal(patches.Count, patches.Select(p => p.Name).Distinct().Count());
        }

        [Fact]
        public void Detailed_GrayscaleCoversFivePercentSteps_WhiteFirst()
        {
            var patches = VerificationPatchSets.Detailed(StandardTargets.SrgbGamma22, hdrMode: false);

            // Starts bright (meter behavior + peak normalization), ends at black.
            Assert.Equal("White", patches[0].Name);
            Assert.Equal(1.0, patches[0].DisplayRgb.R, 9);

            var grayLevels = patches
                .Where(p => p.Category == PatchCategory.Grayscale)
                .Select(p => p.DisplayRgb.R)
                .OrderBy(v => v)
                .ToList();
            for (int i = 0; i < 21; i++)
                Assert.Equal(i * 0.05, grayLevels[i], 9);

            // Grayscale patches are neutral by definition.
            Assert.All(patches.Where(p => p.Category == PatchCategory.Grayscale),
                p => Assert.Equal(p.DisplayRgb.R, p.DisplayRgb.G, 9));
        }

        [Fact]
        public void Detailed_SaturationRamps_MixTowardWhite()
        {
            var patches = VerificationPatchSets.Detailed(StandardTargets.SrgbGamma22, hdrMode: false);

            var red50 = patches.Single(p => p.Name == "Red 50%");
            Assert.Equal(1.0, red50.DisplayRgb.R, 9);
            Assert.Equal(0.5, red50.DisplayRgb.G, 9);
            Assert.Equal(0.5, red50.DisplayRgb.B, 9);
            Assert.Equal(PatchCategory.Saturated, red50.Category);

            // 100% saturation is the pure primary.
            var blue100 = patches.Single(p => p.Name == "Blue 100%");
            Assert.Equal(new LinearRgb(0, 0, 1), blue100.DisplayRgb);
            Assert.Equal(PatchCategory.Primary, blue100.Category);
        }

        [Fact]
        public void Detailed_MemoryColors_UseColorCheckerSrgbValues()
        {
            var patches = VerificationPatchSets.Detailed(StandardTargets.SrgbGamma22, hdrMode: false);

            var darkSkin = patches.Single(p => p.Name == "Dark skin");
            Assert.Equal(115 / 255.0, darkSkin.DisplayRgb.R, 9);
            Assert.Equal(82 / 255.0, darkSkin.DisplayRgb.G, 9);
            Assert.Equal(68 / 255.0, darkSkin.DisplayRgb.B, 9);
            Assert.Equal(PatchCategory.MemoryColor, darkSkin.Category);

            var bluishGreen = patches.Single(p => p.Name == "Bluish green");
            Assert.Equal(103 / 255.0, bluishGreen.DisplayRgb.R, 9);
            Assert.Equal(189 / 255.0, bluishGreen.DisplayRgb.G, 9);
            Assert.Equal(170 / 255.0, bluishGreen.DisplayRgb.B, 9);
        }

        [Fact]
        public void Detailed_AttachesTargetValues_InBothModes()
        {
            foreach (bool hdr in new[] { false, true })
            {
                var patches = VerificationPatchSets.Detailed(StandardTargets.SrgbGamma22, hdr);
                Assert.All(patches, p =>
                {
                    Assert.NotNull(p.TargetXyz);
                    Assert.NotNull(p.TargetLab);
                    Assert.Null(p.Nits); // SDR signal patches, never wire-ladder patches
                });
            }
        }

        [Fact]
        public void ComputeMetrics_PopulatesPerPatchResults_InMeasurementOrder()
        {
            var target = StandardTargets.SrgbGamma22;
            var patches = VerificationPatchSets.Detailed(target, hdrMode: false);

            // Perfect reproduction on the absolute colorimeter scale.
            var measurements = patches.Select(p => new MeasurementResult
            {
                Patch = p,
                Xyz = new CieXyz(
                    p.TargetXyz!.Value.X * 120, p.TargetXyz.Value.Y * 120, p.TargetXyz.Value.Z * 120),
            }).ToList();

            var metrics = CalibrationVerifier.ComputeMetrics(measurements, target);

            Assert.Equal(patches.Count, metrics.PatchResults.Count);
            Assert.Equal(patches.Select(p => p.Name), metrics.PatchResults.Select(r => r.Name));
            Assert.All(metrics.PatchResults, r => Assert.True(r.DeltaE < 0.5,
                $"perfect match should be ~0, got {r.DeltaE:F2} for {r.Name}"));
        }
    }
}
