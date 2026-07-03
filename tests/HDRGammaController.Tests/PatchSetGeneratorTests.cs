using System;
using System.Collections.Generic;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class PatchSetGeneratorTests
    {
        private static readonly PatchSetGenerator.CalibrationPreset[] AllPresets =
        {
            PatchSetGenerator.CalibrationPreset.Quick,
            PatchSetGenerator.CalibrationPreset.Standard,
            PatchSetGenerator.CalibrationPreset.Thorough,
            PatchSetGenerator.CalibrationPreset.Full,
            PatchSetGenerator.CalibrationPreset.GrayscaleOnly
        };

        private static bool IsOn8BitGrid(double v) =>
            Math.Abs(v * 255.0 - Math.Round(v * 255.0)) < 1e-9;

        [Fact]
        public void AllPresets_SdrSignals_AreSnappedTo8BitGrid()
        {
            foreach (var preset in AllPresets)
            {
                var patches = PatchSetGenerator.GeneratePatchSet(StandardTargets.SrgbGamma22, preset);
                foreach (var p in patches)
                {
                    Assert.True(IsOn8BitGrid(p.DisplayRgb.R), $"{preset}/{p.Name}: R={p.DisplayRgb.R} off-grid");
                    Assert.True(IsOn8BitGrid(p.DisplayRgb.G), $"{preset}/{p.Name}: G={p.DisplayRgb.G} off-grid");
                    Assert.True(IsOn8BitGrid(p.DisplayRgb.B), $"{preset}/{p.Name}: B={p.DisplayRgb.B} off-grid");
                }
            }
        }

        [Fact]
        public void AllPresets_HaveNoDuplicateSignalTriples_AmongModelPatches()
        {
            foreach (var preset in AllPresets)
            {
                var patches = PatchSetGenerator.GeneratePatchSet(StandardTargets.SrgbGamma22, preset);
                var seen = new HashSet<(int, int, int)>();
                foreach (var p in patches.Where(p => p.Category != PatchCategory.DriftCheck))
                {
                    var key = ((int)Math.Round(p.DisplayRgb.R * 255),
                               (int)Math.Round(p.DisplayRgb.G * 255),
                               (int)Math.Round(p.DisplayRgb.B * 255));
                    Assert.True(seen.Add(key), $"{preset}: duplicate signal {key} ({p.Name})");
                }
            }
        }

        [Fact]
        public void AllPresets_IndicesMatchSequencePositions()
        {
            foreach (var preset in AllPresets)
            {
                var patches = PatchSetGenerator.GeneratePatchSet(StandardTargets.SrgbGamma22, preset);
                for (int i = 0; i < patches.Count; i++)
                    Assert.Equal(i, patches[i].Index);
            }
        }

        [Theory]
        [InlineData(PatchSetGenerator.CalibrationPreset.Thorough)]
        [InlineData(PatchSetGenerator.CalibrationPreset.Full)]
        public void LongPresets_InterleavePeriodicDriftAnchors(PatchSetGenerator.CalibrationPreset preset)
        {
            var patches = PatchSetGenerator.GeneratePatchSet(StandardTargets.SrgbGamma22, preset);

            var whites = patches
                .Select((p, i) => (p, i))
                .Where(t => t.p.Category == PatchCategory.DriftCheck && t.p.DisplayRgb.R >= 0.99)
                .ToList();
            var blacks = patches
                .Where(p => p.Category == PatchCategory.DriftCheck && p.DisplayRgb.R <= 0.01)
                .ToList();

            int ordinary = patches.Count(p => p.Category != PatchCategory.DriftCheck);

            // A leading white (t0 reference), a trailing one, and roughly one per interval.
            Assert.True(whites.Count >= ordinary / PatchSetGenerator.DriftWhiteIntervalPatches,
                $"{preset}: only {whites.Count} drift whites for {ordinary} patches");
            Assert.Equal(PatchCategory.DriftCheck, patches[0].Category);
            Assert.True(patches[0].DisplayRgb.R >= 0.99, "first patch must be the t0 drift white");
            Assert.True(blacks.Count >= ordinary / PatchSetGenerator.DriftBlackIntervalPatches - 1,
                $"{preset}: only {blacks.Count} drift blacks for {ordinary} patches");

            // Anchors sit at their periodic positions: never more than the interval's worth
            // of ordinary patches between consecutive drift whites.
            for (int w = 1; w < whites.Count; w++)
            {
                int between = 0;
                for (int i = whites[w - 1].i + 1; i < whites[w].i; i++)
                    if (patches[i].Category != PatchCategory.DriftCheck)
                        between++;
                Assert.True(between <= PatchSetGenerator.DriftWhiteIntervalPatches,
                    $"{preset}: {between} ordinary patches between drift whites #{w - 1} and #{w}");
            }
        }

        [Theory]
        [InlineData(PatchSetGenerator.CalibrationPreset.Quick)]
        [InlineData(PatchSetGenerator.CalibrationPreset.Standard)]
        [InlineData(PatchSetGenerator.CalibrationPreset.GrayscaleOnly)]
        public void ShortPresets_HaveNoDriftAnchors(PatchSetGenerator.CalibrationPreset preset)
        {
            var patches = PatchSetGenerator.GeneratePatchSet(StandardTargets.SrgbGamma22, preset);
            Assert.DoesNotContain(patches, p => p.Category == PatchCategory.DriftCheck);
        }

        [Fact]
        public void Snap8Bit_RoundsToNearestCode()
        {
            Assert.Equal(128.0 / 255.0, PatchSetGenerator.Snap8Bit(0.5), 12);
            Assert.Equal(0.0, PatchSetGenerator.Snap8Bit(0.0), 12);
            Assert.Equal(1.0, PatchSetGenerator.Snap8Bit(1.0), 12);
            Assert.Equal(3.0 / 255.0, PatchSetGenerator.Snap8Bit(0.01), 12);
        }
    }
}
