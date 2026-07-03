using System;
using System.Collections.Generic;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class DriftCompensatorTests
    {
        private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        private static MeasurementResult Meas(
            double seconds, double r, double g, double b, double y, PatchCategory cat, string? name = null)
        {
            return new MeasurementResult
            {
                Timestamp = T0.AddSeconds(seconds),
                Patch = new ColorPatch
                {
                    Name = name ?? $"{cat} {seconds:F0}s",
                    DisplayRgb = new LinearRgb(r, g, b),
                    Category = cat
                },
                Xyz = new CieXyz(0.95047 * y, y, 1.08883 * y),
                IsValid = true
            };
        }

        private static MeasurementResult DriftWhite(double seconds, double y) =>
            Meas(seconds, 1, 1, 1, y, PatchCategory.DriftCheck, $"Drift White @{seconds:F0}s");

        private static MeasurementResult DriftBlack(double seconds, double y) =>
            Meas(seconds, 0, 0, 0, y, PatchCategory.DriftCheck, $"Drift Black @{seconds:F0}s");

        [Fact]
        public void FivePercentLinearDrift_IsCorrectedToUnderOnePercentResidual()
        {
            // Display brightens linearly by 5% over 100 seconds (warm-up).
            static double F(double sec) => 1.0 + 0.05 * sec / 100.0;

            var run = new List<MeasurementResult>();
            var trueY = new Dictionary<Guid, double>();

            // White anchors every 25 s (as the generator interleaves them).
            for (int k = 0; k <= 4; k++)
                run.Add(DriftWhite(25 * k, 100.0 * F(25 * k)));

            // Ordinary patches between anchors, each with a known true (drift-free) luminance.
            for (int i = 0; i < 20; i++)
            {
                double sec = 2.5 + i * 4.9;
                double truth = 5.0 + i * 5.0;
                var m = Meas(sec, i / 20.0, i / 20.0, i / 20.0, truth * F(sec), PatchCategory.Grayscale);
                trueY[m.Id] = truth;
                run.Add(m);
            }

            var analysis = DriftCompensator.Compensate(run);

            Assert.True(analysis.Applied, analysis.Summary);
            Assert.Equal(5, analysis.WhiteAnchorCount);
            Assert.InRange(analysis.MaxWhiteDriftFraction, 0.045, 0.055);

            foreach (var m in analysis.Measurements.Where(m => m.Patch.Category == PatchCategory.Grayscale))
            {
                double truth = trueY[m.Id];
                double residual = Math.Abs(m.Xyz.Y - truth) / truth;
                Assert.True(residual < 0.01,
                    $"{m.Patch.Name}: residual {residual * 100:F2}% after compensation (Y={m.Xyz.Y:F3}, true={truth:F3})");
            }
        }

        [Fact]
        public void Compensation_PreservesChromaticity()
        {
            static double F(double sec) => 1.0 + 0.04 * sec / 100.0;
            var run = new List<MeasurementResult>
            {
                DriftWhite(0, 100.0),
                Meas(50, 0.5, 0.5, 0.5, 20.0 * F(50), PatchCategory.Grayscale),
                DriftWhite(100, 100.0 * F(100))
            };

            var analysis = DriftCompensator.Compensate(run);
            Assert.True(analysis.Applied);

            var original = run[1].Chromaticity;
            var compensated = analysis.Measurements[1].Chromaticity;
            Assert.Equal(original.X, compensated.X, 12);
            Assert.Equal(original.Y, compensated.Y, 12);
        }

        [Fact]
        public void ExcessiveDrift_IsNotCompensated_SoValidatorCanReject()
        {
            var run = new List<MeasurementResult>
            {
                DriftWhite(0, 100.0),
                Meas(50, 0.5, 0.5, 0.5, 21.0, PatchCategory.Grayscale),
                DriftWhite(100, 110.0) // 10% — beyond the 8% correctable limit
            };

            var analysis = DriftCompensator.Compensate(run);

            Assert.False(analysis.Applied);
            Assert.InRange(analysis.MaxWhiteDriftFraction, 0.095, 0.105);
            // Measurements are handed back untouched (raw drift left for the validator).
            Assert.Same(run[1], analysis.Measurements[1]);
        }

        [Fact]
        public void FewerThanTwoWhiteAnchors_IsANoOp()
        {
            var run = new List<MeasurementResult>
            {
                Meas(0, 1, 1, 1, 100.0, PatchCategory.Grayscale, "White"),
                Meas(10, 0.5, 0.5, 0.5, 21.0, PatchCategory.Grayscale)
            };

            var analysis = DriftCompensator.Compensate(run);

            Assert.False(analysis.Applied);
            Assert.Equal(0, analysis.WhiteAnchorCount); // ordinary whites are not drift anchors
            Assert.Same(run[0], analysis.Measurements[0]);
        }

        [Fact]
        public void BlackDrift_IsReportedForValidatorGating()
        {
            static double F(double sec) => 1.0 + 0.03 * sec / 100.0;
            var run = new List<MeasurementResult>
            {
                DriftWhite(0, 100.0),
                DriftBlack(1, 0.10),
                DriftBlack(99, 0.35),
                DriftWhite(100, 100.0 * F(100))
            };

            var analysis = DriftCompensator.Compensate(run);

            Assert.True(analysis.Applied);
            Assert.InRange(analysis.MaxBlackDriftY, 0.24, 0.26);
        }
    }
}
