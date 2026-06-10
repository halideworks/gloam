using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// The HDR MHC2 LUTs live in PQ wire-signal domain. These tests simulate panels with
    /// known PQ responses (as measured through Windows' SDR-in-HDR mapping:
    /// nits = panelResponse(PQ(sdrWhite · srgbEotf(v)))) and assert the generated LUTs
    /// correct toward ST.2084 tracking without inventing data beyond the measured range.
    /// </summary>
    public class HdrMhc2LutBuilderTests
    {
        private const double SdrWhite = 200.0;
        private static readonly double[] GrayLevels =
            { 0.0, 0.05, 0.10, 0.15, 0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90, 1.0 };

        /// <summary>Simulates the measured grayscale: panelNits(wirePq) defines the panel.</summary>
        private static List<MeasurementResult> SimulatePanel(Func<double, double> panelNitsOfPq)
        {
            return GrayLevels.Select((v, i) =>
            {
                double wirePq = TransferFunctions.PqInverseEotf(SdrWhite * TransferFunctions.SrgbEotf(v));
                double nits = panelNitsOfPq(wirePq);
                return new MeasurementResult
                {
                    Patch = new ColorPatch
                    {
                        Name = $"Gray {v:P0}",
                        DisplayRgb = new LinearRgb(v, v, v),
                        Category = PatchCategory.Grayscale,
                        Index = i,
                    },
                    Xyz = new CieXyz(nits * 0.95, nits, nits * 1.08),
                };
            }).ToList();
        }

        [Fact]
        public void PerfectPqPanel_YieldsIdentityLut_InMeasuredRange()
        {
            // Panel that tracks ST.2084 exactly: wire p produces ST2084(p) nits (plus a tiny
            // black floor so the range checks pass).
            var measurements = SimulatePanel(p => Math.Max(TransferFunctions.PqEotf(p), 0.05));
            var result = HdrMhc2LutBuilder.Build(measurements, SdrWhite);

            // Within the solidly-measured range (above black noise, below the blend window)
            // the LUT must be identity: the panel needs no correction.
            for (int i = 0; i < 1024; i++)
            {
                double p = i / 1023.0;
                double nits = TransferFunctions.PqEotf(p);
                if (nits < 1.0 || nits > result.MeasuredPeakNits * 0.8) continue;
                Assert.True(Math.Abs(result.LutR[i] - p) < 0.01,
                    $"identity expected at p={p:F3} (nits {nits:F1}), got {result.LutR[i]:F3}");
            }
        }

        [Fact]
        public void DimmedMidtones_AreDrivenHarder()
        {
            // Panel renders midtones too dark (a gamma-like skew in its PQ tracking): the
            // LUT must drive the wire signal HIGHER to land on ST.2084.
            var measurements = SimulatePanel(p =>
            {
                double ideal = TransferFunctions.PqEotf(p);
                double norm = ideal / SdrWhite;
                return Math.Max(SdrWhite * Math.Pow(norm, 1.2), 0.05); // darker midtones
            });
            var result = HdrMhc2LutBuilder.Build(measurements, SdrWhite);

            // Only the fully-corrected region (below the 50%-of-range blend start) — above it
            // the LUT deliberately fades to identity to stay out of the panel's HDR knee.
            int checkedCount = 0;
            for (int i = 0; i < 1024; i++)
            {
                double p = i / 1023.0;
                double nits = TransferFunctions.PqEotf(p);
                if (nits < 5.0 || nits > result.MeasuredPeakNits * 0.45) continue;
                Assert.True(result.LutR[i] > p,
                    $"expected boost at p={p:F3} (panel too dark), got {result.LutR[i]:F3}");
                checkedCount++;
            }
            Assert.True(checkedCount > 20, "test did not cover the midtone range");
        }

        [Fact]
        public void Luts_AreNeutral_IdenticalAcrossChannels()
        {
            // White-point correction lives in the (uniformly scaled) matrix; per-channel LUT
            // differences would re-tint saturated colors, so the LUTs must be identical.
            var measurements = SimulatePanel(p => Math.Max(TransferFunctions.PqEotf(p), 0.05));
            var result = HdrMhc2LutBuilder.Build(measurements, SdrWhite);
            Assert.Equal(result.LutR, result.LutG);
            Assert.Equal(result.LutR, result.LutB);
        }

        [Fact]
        public void AboveMeasuredRange_ContinuesAnalytically_AndStaysMonotonic()
        {
            var measurements = SimulatePanel(p => Math.Max(TransferFunctions.PqEotf(p), 0.05));
            var result = HdrMhc2LutBuilder.Build(measurements, SdrWhite);

            // Above the blend window (80% of the measured range and beyond — including all
            // true HDR highlights) the LUT must be the identity passthrough: never corrected
            // panel-knee territory, never extrapolated measurement data.
            for (int i = 0; i < 1024; i++)
            {
                double p = i / 1023.0;
                if (TransferFunctions.PqEotf(p) < result.MeasuredPeakNits * 0.85) continue;
                Assert.True(Math.Abs(result.LutR[i] - p) < 0.002,
                    $"identity passthrough expected at p={p:F3}, got {result.LutR[i]:F3}");
            }

            foreach (var lut in new[] { result.LutR, result.LutG, result.LutB })
            {
                for (int i = 1; i < lut.Length; i++)
                    Assert.True(lut[i] >= lut[i - 1], $"LUT not monotonic at {i}");
                Assert.InRange(lut[0], 0.0, 1.0);
                Assert.InRange(lut[^1], 0.0, 1.0);
            }
        }

        [Fact]
        public void TooFewGrayPoints_Throws()
        {
            var measurements = SimulatePanel(p => Math.Max(TransferFunctions.PqEotf(p), 0.05))
                .Take(3).ToList();
            Assert.Throws<InvalidOperationException>(() =>
                HdrMhc2LutBuilder.Build(measurements, SdrWhite));
        }
    }
}
