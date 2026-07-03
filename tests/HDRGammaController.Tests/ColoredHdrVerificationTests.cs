using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Pins the colored HDR verification math: the Rec.2020-container stimulus set (rung
    /// capping, per-channel nits, container-referred reference XYZ, the scRGB triple the
    /// FP16 renderer presents) and the ΔE ITP grading aggregate (exact match → 0, offsets
    /// grade against the reference, non-physical readings excluded as NaN — the same
    /// NaN-not-sentinel convention as DeltaEItp itself).
    /// </summary>
    public class ColoredHdrVerificationTests
    {
        private const int Hues = 6; // R, G, B, C, M, Y

        // ------------------------------------------------------------------ stimulus set

        [Fact]
        public void Build_PeakAboveAllRungs_EmitsAllRungsForAllHues()
        {
            var stimuli = ColoredHdrVerificationSet.Build(1000);

            Assert.Equal(3 * Hues, stimuli.Count);
            foreach (double rung in new[] { 100.0, 203.0, 400.0 })
                Assert.Equal(Hues, stimuli.Count(s => s.RungNits == rung));
            foreach (string hue in new[] { "Red", "Green", "Blue", "Cyan", "Magenta", "Yellow" })
                Assert.Equal(3, stimuli.Count(s => s.Hue == hue));
        }

        [Fact]
        public void Build_SkipsRungsAboveThePeak()
        {
            var stimuli = ColoredHdrVerificationSet.Build(300);

            Assert.Equal(2 * Hues, stimuli.Count);
            Assert.DoesNotContain(stimuli, s => s.RungNits > 300);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(double.NaN)]
        [InlineData(-5.0)]
        [InlineData(90.0)] // even below 100 nits the 100 rung is always kept
        public void Build_AlwaysKeepsTheHundredNitRung(double peak)
        {
            var stimuli = ColoredHdrVerificationSet.Build(peak);

            Assert.Equal(Hues, stimuli.Count);
            Assert.All(stimuli, s => Assert.Equal(100.0, s.RungNits));
        }

        [Fact]
        public void Build_ReferenceLuminanceEqualsTheRung()
        {
            // "Red 203" is linear Rec.2020 red scaled so its LUMINANCE is 203 nits; the
            // container reference Y must equal the rung exactly for every hue.
            foreach (var s in ColoredHdrVerificationSet.Build(1000))
                Assert.Equal(s.RungNits, s.ReferenceXyz.Y, s.RungNits * 1e-9);
        }

        [Fact]
        public void Build_ReferenceChromaticityMatchesRec2020Primaries()
        {
            var stimuli = ColoredHdrVerificationSet.Build(1000);

            var red = stimuli.Single(s => s.Name == "Red 203");
            var green = stimuli.Single(s => s.Name == "Green 203");
            var blue = stimuli.Single(s => s.Name == "Blue 203");

            Assert.Equal(0.708, red.ReferenceXyz.ToChromaticity().X, 1e-9);
            Assert.Equal(0.292, red.ReferenceXyz.ToChromaticity().Y, 1e-9);
            Assert.Equal(0.170, green.ReferenceXyz.ToChromaticity().X, 1e-9);
            Assert.Equal(0.797, green.ReferenceXyz.ToChromaticity().Y, 1e-9);
            Assert.Equal(0.131, blue.ReferenceXyz.ToChromaticity().X, 1e-9);
            Assert.Equal(0.046, blue.ReferenceXyz.ToChromaticity().Y, 1e-9);
        }

        [Fact]
        public void Build_Rec2020ChannelNitsReproduceTheReferenceXyz()
        {
            // The per-channel nits triple in the container and the reference XYZ are two
            // views of the same stimulus: pushing the triple through the Rec.2020 matrix
            // must land on the reference.
            foreach (var s in ColoredHdrVerificationSet.Build(1000))
            {
                var xyz = ColorMath.LinearRec2020ToXyz(s.Rec2020ChannelNits);
                Assert.Equal(s.ReferenceXyz.X, xyz.X, Math.Max(1e-9, s.ReferenceXyz.X * 1e-9));
                Assert.Equal(s.ReferenceXyz.Y, xyz.Y, Math.Max(1e-9, s.ReferenceXyz.Y * 1e-9));
                Assert.Equal(s.ReferenceXyz.Z, xyz.Z, Math.Max(1e-9, s.ReferenceXyz.Z * 1e-9));
            }
        }

        [Fact]
        public void Build_ActiveChannelsShareOneLevel_InactiveChannelsAreZero()
        {
            foreach (var s in ColoredHdrVerificationSet.Build(1000))
            {
                double[] unit = { s.UnitRgb.R, s.UnitRgb.G, s.UnitRgb.B };
                double[] drive = { s.Rec2020ChannelNits.R, s.Rec2020ChannelNits.G, s.Rec2020ChannelNits.B };
                double active = drive.Where((_, i) => unit[i] > 0).First();

                for (int i = 0; i < 3; i++)
                {
                    if (unit[i] > 0)
                        Assert.Equal(active, drive[i], active * 1e-12);
                    else
                        Assert.Equal(0.0, drive[i]);
                }
                // Equal luminance at fewer active channels needs more drive per channel.
                Assert.True(active > s.RungNits,
                    $"{s.Name}: colored drive {active:F1} should exceed the rung {s.RungNits:F0} (no hue reaches white's luminance efficiency)");
            }
        }

        [Fact]
        public void Build_ScRgbTripleEncodesTheSameColorForTheRenderer()
        {
            // The renderer surface is scRGB (linear Rec.709 primaries, value = nits/80),
            // so the presented triple must round-trip through the sRGB matrix back to the
            // container reference. Wide-gamut hues necessarily carry negative components
            // on inactive channels (scRGB encodes outside-709 colors that way).
            var stimuli = ColoredHdrVerificationSet.Build(1000);
            foreach (var s in stimuli)
            {
                var xyz = ColorMath.LinearSrgbToXyz(s.ScRgbNits);
                Assert.Equal(s.ReferenceXyz.X, xyz.X, Math.Max(1e-6, s.ReferenceXyz.X * 1e-9));
                Assert.Equal(s.ReferenceXyz.Y, xyz.Y, Math.Max(1e-6, s.ReferenceXyz.Y * 1e-9));
                Assert.Equal(s.ReferenceXyz.Z, xyz.Z, Math.Max(1e-6, s.ReferenceXyz.Z * 1e-9));
            }

            var red = stimuli.Single(s => s.Name == "Red 203");
            Assert.True(red.ScRgbNits.R > 0);
            Assert.True(red.ScRgbNits.G < 0, "Rec.2020 red lies outside Rec.709: scRGB green must go negative");
            Assert.True(red.ScRgbNits.B < 0, "Rec.2020 red lies outside Rec.709: scRGB blue must go negative");
        }

        [Fact]
        public void Build_ClassifiesPrimaryAndSecondaryHues()
        {
            var stimuli = ColoredHdrVerificationSet.Build(100);

            Assert.True(stimuli.Single(s => s.Hue == "Red").IsPrimaryHue);
            Assert.True(stimuli.Single(s => s.Hue == "Green").IsPrimaryHue);
            Assert.True(stimuli.Single(s => s.Hue == "Blue").IsPrimaryHue);
            Assert.False(stimuli.Single(s => s.Hue == "Cyan").IsPrimaryHue);
            Assert.False(stimuli.Single(s => s.Hue == "Magenta").IsPrimaryHue);
            Assert.False(stimuli.Single(s => s.Hue == "Yellow").IsPrimaryHue);
        }

        // ------------------------------------------------------------------ grading

        private static IReadOnlyList<ColoredHdrStimulus> Set100() =>
            ColoredHdrVerificationSet.Build(100);

        [Fact]
        public void GradeColoredHdr_ExactMatch_IsZeroEverywhere()
        {
            var readings = Set100().Select(s => (s, s.ReferenceXyz));

            var metrics = CalibrationVerifier.GradeColoredHdr(readings);

            Assert.Equal(Set100().Count, metrics.GradedCount);
            Assert.Equal(0, metrics.ExcludedCount);
            Assert.Equal(0.0, metrics.AverageItpDeltaE, 1e-9);
            Assert.Equal(0.0, metrics.MaxItpDeltaE, 1e-9);
            Assert.Equal(0.0, metrics.AverageAbsLuminanceError, 1e-9);
            Assert.All(metrics.Patches, p =>
            {
                Assert.Equal(0.0, p.DeltaEItp, 1e-9);
                Assert.Equal(0.0, p.LuminanceError, 1e-9);
            });
        }

        [Fact]
        public void GradeColoredHdr_KnownOffset_MatchesDeltaEItpAgainstTheReference()
        {
            // A 20% luminance shortfall on every patch: each grade must equal the BT.2124
            // distance to the container reference (the grader adds no scaling of its own)
            // and the luminance error must read exactly -20%.
            var readings = Set100().Select(s => (s, s.ReferenceXyz * 0.8)).ToList();

            var metrics = CalibrationVerifier.GradeColoredHdr(readings);

            foreach (var (stimulus, measured) in readings)
            {
                var grade = metrics.Patches.Single(p => p.Name == stimulus.Name);
                double expected = CalibrationVerifier.DeltaEItp(measured, stimulus.ReferenceXyz);
                Assert.Equal(expected, grade.DeltaEItp, 1e-12);
                Assert.True(grade.DeltaEItp > 0);
                Assert.Equal(-0.20, grade.LuminanceError, 1e-9);
            }
            Assert.Equal(0.20, metrics.AverageAbsLuminanceError, 1e-9);
            Assert.Equal(metrics.Patches.Max(p => p.DeltaEItp), metrics.MaxItpDeltaE, 1e-12);
        }

        [Fact]
        public void GradeColoredHdr_WorstPatchNameTracksTheMax()
        {
            var stimuli = Set100();
            // Everything perfect except Magenta, which is measurably desaturated.
            var readings = stimuli.Select(s => (s, s.Hue == "Magenta"
                ? new CieXyz(s.ReferenceXyz.X * 0.7, s.ReferenceXyz.Y, s.ReferenceXyz.Z * 0.7)
                : s.ReferenceXyz)).ToList();

            var metrics = CalibrationVerifier.GradeColoredHdr(readings);

            Assert.Equal("Magenta 100", metrics.WorstPatchName);
            Assert.True(metrics.MaxItpDeltaE > 0);
            Assert.True(metrics.MaxItpDeltaE > metrics.AverageItpDeltaE);
        }

        [Fact]
        public void GradeColoredHdr_NonPhysicalReading_IsNaNAndExcludedFromAggregates()
        {
            // Same convention as the PQ sweep: DeltaEItp returns NaN for non-physical
            // XYZ, and one NaN must not poison the aggregate - it is excluded and counted.
            var stimuli = Set100();
            var readings = stimuli.Select((s, i) => (s, i == 0
                ? new CieXyz(double.NaN, -50, 10)
                : s.ReferenceXyz)).ToList();

            var metrics = CalibrationVerifier.GradeColoredHdr(readings);

            var bad = metrics.Patches[0];
            Assert.True(double.IsNaN(bad.DeltaEItp));
            Assert.True(double.IsNaN(bad.LuminanceError));
            Assert.Equal(1, metrics.ExcludedCount);
            Assert.Equal(stimuli.Count - 1, metrics.GradedCount);
            Assert.True(double.IsFinite(metrics.AverageItpDeltaE));
            Assert.Equal(0.0, metrics.AverageItpDeltaE, 1e-9);
        }

        [Fact]
        public void GradeColoredHdr_AllNonPhysical_ReportsNaNAggregatesAndNoWorstPatch()
        {
            var readings = Set100().Select(s => (s, new CieXyz(-1, -1, -1)));

            var metrics = CalibrationVerifier.GradeColoredHdr(readings);

            Assert.Equal(0, metrics.GradedCount);
            Assert.Equal(Set100().Count, metrics.ExcludedCount);
            Assert.True(double.IsNaN(metrics.AverageItpDeltaE));
            Assert.True(double.IsNaN(metrics.MaxItpDeltaE));
            Assert.True(double.IsNaN(metrics.AverageAbsLuminanceError));
            Assert.Null(metrics.WorstPatchName);
        }

        [Fact]
        public void GradeColoredHdr_LargerOffsetGradesWorse()
        {
            var red = Set100().Single(s => s.Name == "Red 100");

            double small = CalibrationVerifier.GradeColoredHdr(
                new[] { (red, red.ReferenceXyz * 0.95) }).MaxItpDeltaE;
            double large = CalibrationVerifier.GradeColoredHdr(
                new[] { (red, red.ReferenceXyz * 0.60) }).MaxItpDeltaE;

            Assert.True(large > small, $"expected {large:F2} > {small:F2}");
        }
    }
}
