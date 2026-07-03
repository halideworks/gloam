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

        private static bool IsSingleChannel(ColorPatch p, int channel)
        {
            double[] v = { p.DisplayRgb.R, p.DisplayRgb.G, p.DisplayRgb.B };
            for (int c = 0; c < 3; c++)
            {
                if (c == channel && v[c] <= 1e-9) return false;
                if (c != channel && v[c] > 1e-9) return false;
            }
            return true;
        }

        [Theory]
        [InlineData(PatchSetGenerator.CalibrationPreset.Thorough)]
        [InlineData(PatchSetGenerator.CalibrationPreset.Full)]
        public void LongPresets_IncludeSingleChannelRampsPerChannel(PatchSetGenerator.CalibrationPreset preset)
        {
            var patches = PatchSetGenerator.GeneratePatchSet(StandardTargets.SrgbGamma22, preset);

            for (int channel = 0; channel < 3; channel++)
            {
                foreach (double level in PatchSetGenerator.SingleChannelRampLevels)
                {
                    double snapped = PatchSetGenerator.Snap8Bit(level);
                    var member = patches.FirstOrDefault(p =>
                        IsSingleChannel(p, channel) &&
                        Math.Abs(Math.Max(p.DisplayRgb.R, Math.Max(p.DisplayRgb.G, p.DisplayRgb.B)) - snapped) < 1e-9);
                    Assert.True(member != null, $"{preset}: missing channel-{channel} ramp member at {level}");

                    // Sub-full members are Saturated so the validator's Primary near-black
                    // gate cannot false-fail a dim panel's blue 25% reading. The 1.0 anchor
                    // dedupes onto the preset's existing full-drive patch: Primary in
                    // Thorough (AddPrimariesAndSecondaries), Saturated in Full (whose
                    // full-drive primaries come from the saturation ladder at 1.0).
                    if (level < 0.99)
                        Assert.Equal(PatchCategory.Saturated, member!.Category);
                    else
                        Assert.True(member!.Category is PatchCategory.Primary or PatchCategory.Saturated,
                            $"{preset}: unexpected category {member.Category} for the full-drive anchor");
                }
            }
        }

        [Fact]
        public void SingleChannelRamps_SkipLevelsBelowChromaNoiseFloor()
        {
            // No sub-25% single-channel ramp members: colorimeter chroma noise dominates
            // single-channel readings at low drive. (Grid axis nodes below that level are
            // General-category and get filtered by the same floor in Lut3DGenerator.)
            foreach (var preset in new[] { PatchSetGenerator.CalibrationPreset.Thorough, PatchSetGenerator.CalibrationPreset.Full })
            {
                var patches = PatchSetGenerator.GeneratePatchSet(StandardTargets.SrgbGamma22, preset);
                foreach (var p in patches.Where(p =>
                             p.Category is PatchCategory.Primary or PatchCategory.Saturated))
                {
                    for (int channel = 0; channel < 3; channel++)
                    {
                        if (!IsSingleChannel(p, channel)) continue;
                        double drive = Math.Max(p.DisplayRgb.R, Math.Max(p.DisplayRgb.G, p.DisplayRgb.B));
                        Assert.True(drive >= PatchSetGenerator.Snap8Bit(0.25) - 1e-9,
                            $"{preset}/{p.Name}: single-channel ramp member below the 25% noise floor");
                    }
                }
            }
        }

        [Theory]
        [InlineData(PatchSetGenerator.CalibrationPreset.Quick)]
        [InlineData(PatchSetGenerator.CalibrationPreset.Standard)]
        public void ShortPresets_HaveNoSingleChannelRampMembers(PatchSetGenerator.CalibrationPreset preset)
        {
            var patches = PatchSetGenerator.GeneratePatchSet(StandardTargets.SrgbGamma22, preset);

            // Among Primary/Saturated patches, only the three full-drive primaries are
            // single-channel in short presets — no dedicated ramp members. (Grid axis
            // nodes are General-category and not ramp members.)
            for (int channel = 0; channel < 3; channel++)
            {
                var singles = patches
                    .Where(p => p.Category is PatchCategory.Primary or PatchCategory.Saturated)
                    .Where(p => IsSingleChannel(p, channel))
                    .ToList();
                Assert.Single(singles);
                Assert.Equal(1.0, Math.Max(singles[0].DisplayRgb.R,
                    Math.Max(singles[0].DisplayRgb.G, singles[0].DisplayRgb.B)), 12);
            }
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
