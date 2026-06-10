using System;
using System.Collections.Generic;
using Xunit;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Regression coverage for the report's headline ΔE figure (Lut3DGenerator.CalculateMetrics).
    /// The colorimeter reports ABSOLUTE luminance (white Y ≈ 100–120 cd/m²) while patch targets
    /// are normalized to white Y = 1. Taking Lab on raw absolute XYZ pushes white to L* ≈ 559 and
    /// makes ΔE2000 explode into the dozens/hundreds — the "ΔE 90+" the user reported. These tests
    /// pin the normalization so a perfect (just-scaled) measurement reads ~0.
    /// </summary>
    public class CalibrationMetricsTests
    {
        private static List<MeasurementResult> BuildGrayscale(CalibrationTarget target, double whiteNits, double extraBlueGain = 1.0)
        {
            var list = new List<MeasurementResult>();
            for (int i = 0; i < 16; i++)
            {
                double lvl = i / 15.0;
                var signal = new LinearRgb(lvl, lvl, lvl);

                double lin = target.ApplyEotf(lvl);
                var txyz = target.LinearRgbToXyz(new LinearRgb(lin, lin, lin));

                var patch = new ColorPatch
                {
                    Name = $"Gray {lvl * 100:F0}%",
                    DisplayRgb = signal,
                    Category = PatchCategory.Grayscale,
                    Index = i,
                    TargetXyz = txyz,
                    TargetLab = ColorMath.XyzToLab(txyz)
                };

                // Measured = target reproduced perfectly, but on the ABSOLUTE colorimeter scale
                // (×whiteNits). extraBlueGain lets a test inject a real white-point error.
                var measured = new CieXyz(txyz.X * whiteNits, txyz.Y * whiteNits, txyz.Z * whiteNits * extraBlueGain);
                list.Add(new MeasurementResult { Patch = patch, Xyz = measured });
            }
            return list;
        }

        [Fact]
        public void CalculateMetrics_PerfectMatchOnAbsoluteScale_IsNearZero()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 120.0);

            var metrics = new Lut3DGenerator(target, measurements, 9).CalculateMetrics();

            // A perfect reproduction (only differing by an absolute luminance scale) must read ~0,
            // NOT the ~90 the un-normalized code produced.
            Assert.True(metrics.AverageDeltaE < 1.0,
                $"perfect match should be ~0 after normalization, got ΔE={metrics.AverageDeltaE:F1}");
            Assert.True(metrics.MaxDeltaE < 2.0, $"max ΔE should be tiny, got {metrics.MaxDeltaE:F1}");
        }

        [Fact]
        public void CalculateMetrics_RealWhitePointError_IsModerateNotHundreds()
        {
            // A genuinely-off white point (15% extra blue) should surface as a believable
            // single/low-double-digit ΔE, not a luminance-blowout in the dozens/hundreds.
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 120.0, extraBlueGain: 1.15);

            var metrics = new Lut3DGenerator(target, measurements, 9).CalculateMetrics();

            Assert.InRange(metrics.AverageDeltaE, 1.0, 25.0);
        }

        [Fact]
        public void GrayscaleDecomposition_BlueCast_IsChromaticNotTonal()
        {
            // A pure white-point error must land in the CHROMATIC component of the grayscale
            // decomposition, with the tone component near zero (luminance is unchanged).
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 120.0, extraBlueGain: 1.15);

            var metrics = new Lut3DGenerator(target, measurements, 9).CalculateMetrics();

            Assert.True(metrics.AverageGrayscaleColorDeltaE > metrics.AverageGrayscaleToneDeltaE * 2,
                $"blue cast should be chromatic: color={metrics.AverageGrayscaleColorDeltaE:F2} tone={metrics.AverageGrayscaleToneDeltaE:F2}");
            Assert.True(metrics.AverageGrayscaleToneDeltaE < 1.0,
                $"tone component should be tiny for a pure cast, got {metrics.AverageGrayscaleToneDeltaE:F2}");
        }

        [Fact]
        public void DeltaEItp_IdenticalColors_IsZero()
        {
            var xyz = new CieXyz(95.0, 100.0, 108.0);
            Assert.Equal(0.0, CalibrationVerifier.DeltaEItp(xyz, xyz), 9);
        }

        [Fact]
        public void DeltaEItp_BehavesSanely()
        {
            // Luminance-only and chroma-only differences both register; the metric grows
            // with error size; values stay in the JND-scaled range, not thousands.
            var white = new CieXyz(95.0, 100.0, 108.0);
            var dimmer = new CieXyz(85.5, 90.0, 97.2);
            var bluer = new CieXyz(95.0, 100.0, 118.0);
            var muchBluer = new CieXyz(95.0, 100.0, 130.0);

            double lum = CalibrationVerifier.DeltaEItp(white, dimmer);
            double chroma = CalibrationVerifier.DeltaEItp(white, bluer);
            double chromaBig = CalibrationVerifier.DeltaEItp(white, muchBluer);

            Assert.True(lum > 0.1 && double.IsFinite(lum));
            Assert.True(chroma > 0.1 && double.IsFinite(chroma));
            Assert.True(chromaBig > chroma, "larger error must produce larger dE ITP");
            Assert.True(chroma < 200, $"dE ITP implausibly large: {chroma:F1}");
        }

        [Fact]
        public void Metrics_PopulateItpValues()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 120.0, extraBlueGain: 1.10);

            var metrics = new Lut3DGenerator(target, measurements, 9).CalculateMetrics();

            Assert.True(metrics.ItpDeltaEs.Count > 0, "ITP values should be computed");
            Assert.True(metrics.AverageItpDeltaE > 0);
            Assert.True(metrics.MaxItpDeltaE >= metrics.AverageItpDeltaE);
        }
    }
}
