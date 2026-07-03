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

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(0.0)]
        [InlineData(5000.0)]
        public void InvalidSdrWhite_Throws(double sdrWhite)
        {
            var measurements = SimulatePanel(p => Math.Max(TransferFunctions.PqEotf(p), 0.05));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HdrMhc2LutBuilder.Build(measurements, sdrWhite));
        }

        [Fact]
        public void NonFiniteMeasurementRows_AreIgnored()
        {
            var measurements = SimulatePanel(p => Math.Max(TransferFunctions.PqEotf(p), 0.05));
            measurements.Add(new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "Bad grayscale",
                    DisplayRgb = new LinearRgb(double.NaN, double.NaN, double.NaN),
                    Category = PatchCategory.Grayscale
                },
                Xyz = new CieXyz(0, double.NaN, 0)
            });
            measurements.Add(new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "Bad wire",
                    DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                    Nits = double.PositiveInfinity,
                    Category = PatchCategory.General
                },
                Xyz = new CieXyz(0, double.PositiveInfinity, 0)
            });

            var result = HdrMhc2LutBuilder.Build(measurements, SdrWhite);

            Assert.False(result.WireExact);
            Assert.All(result.LutR, value => Assert.True(double.IsFinite(value)));
            Assert.True(result.MeasuredPeakNits > 100);
        }

        // ---- HDR wire-ladder (FP16 exact wire positions) ------------------------------

        private static readonly double[] Ladder = { 0, 2, 4, 8, 16, 32, 64, 100, 150, 220, 320, 450, 650, 1000 };

        /// <summary>Wire-ladder measurements: requested nits sets the exact wire position.</summary>
        private static List<MeasurementResult> SimulateWireLadder(Func<double, double> panelNitsOfPq)
        {
            return Ladder.Select((n, i) =>
            {
                double nits = panelNitsOfPq(TransferFunctions.PqInverseEotf(n));
                return new MeasurementResult
                {
                    Patch = new ColorPatch
                    {
                        Name = $"HDR wire {n:F0} nits",
                        DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                        Nits = n,
                        Category = PatchCategory.General,
                        Index = i,
                    },
                    Xyz = new CieXyz(nits * 0.95, nits, nits * 1.08),
                };
            }).ToList();
        }

        [Fact]
        public void WireLadder_FlatGainPanel_IsInvertedAcrossFullRange()
        {
            // The June 2026 probe result on the MAG 271QPX: panel outputs a uniform 86.7%
            // of the PQ spec from 56 to 446 nits. With wire-exact data the LUT must drive
            // the wire higher so output lands on spec - across the WHOLE measured range,
            // not just below the old SDR-range knee.
            const double gain = 0.867;
            var measurements = SimulateWireLadder(p => Math.Max(TransferFunctions.PqEotf(p) * gain, 0.02));
            var result = HdrMhc2LutBuilder.Build(measurements, SdrWhite);

            Assert.True(result.WireExact);
            Assert.True(Math.Abs(result.MeasuredPeakNits - 867.0) < 1.0);

            foreach (double desired in new[] { 10.0, 50.0, 100.0, 200.0, 400.0, 600.0 })
            {
                double p = TransferFunctions.PqInverseEotf(desired);
                int i = (int)Math.Round(p * 1023);
                double expected = TransferFunctions.PqInverseEotf(desired / gain);
                Assert.True(Math.Abs(result.LutR[i] - expected) < 0.015,
                    $"at {desired:F0} nits expected wire {expected:F3}, got {result.LutR[i]:F3}");
            }
        }

        [Fact]
        public void WireLadder_AboveReachablePeak_IsIdentity()
        {
            const double gain = 0.867;
            var measurements = SimulateWireLadder(p => Math.Max(TransferFunctions.PqEotf(p) * gain, 0.02));
            var result = HdrMhc2LutBuilder.Build(measurements, SdrWhite);

            // The panel tops out at 867 nits: the LUT cannot create luminance the panel
            // doesn't have, so above the reachable peak it must pass the wire through and
            // leave the panel's own rolloff alone.
            for (int i = 0; i < 1024; i++)
            {
                double p = i / 1023.0;
                if (TransferFunctions.PqEotf(p) < result.MeasuredPeakNits * 1.05) continue;
                Assert.True(Math.Abs(result.LutR[i] - p) < 0.002,
                    $"identity expected above reachable peak at p={p:F3}, got {result.LutR[i]:F3}");
            }
        }

        [Fact]
        public void WireLadder_IsPreferredOverSdrMappedGrayscale()
        {
            // Grayscale says the panel is perfect; the wire ladder says it is 13% dim. The
            // builder must trust the wire ladder (exact positions, no mapping assumption):
            // a boosting LUT proves it did.
            const double gain = 0.867;
            var measurements = SimulatePanel(p => Math.Max(TransferFunctions.PqEotf(p), 0.05));
            measurements.AddRange(SimulateWireLadder(p => Math.Max(TransferFunctions.PqEotf(p) * gain, 0.02)));
            var result = HdrMhc2LutBuilder.Build(measurements, SdrWhite);

            Assert.True(result.WireExact);
            int idx = (int)Math.Round(TransferFunctions.PqInverseEotf(100.0) * 1023);
            Assert.True(result.LutR[idx] > TransferFunctions.PqInverseEotf(100.0) + 0.005,
                "LUT should boost (wire ladder data), not stay identity (grayscale data)");
        }

        [Fact]
        public void SparseLowWireRows_DoNotOverrideSdrMappedGrayscaleFallback()
        {
            // Five low wire rows are enough to satisfy a naive count check, but they do not
            // characterize HDR highlights. The builder must ignore them as a wire-exact
            // source and fall back to the complete SDR-mapped grayscale data.
            var measurements = SimulatePanel(p => Math.Max(TransferFunctions.PqEotf(p), 0.05));
            foreach (double nits in new[] { 0.0, 2.0, 4.0, 8.0, 16.0 })
            {
                measurements.Add(new MeasurementResult
                {
                    Patch = new ColorPatch
                    {
                        Name = $"HDR wire {nits:F0} nits",
                        DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                        Nits = nits,
                        Category = PatchCategory.General,
                    },
                    Xyz = new CieXyz(nits * 0.95, Math.Max(nits * 0.5, 0.02), nits * 1.08),
                });
            }

            var result = HdrMhc2LutBuilder.Build(measurements, SdrWhite);

            Assert.False(result.WireExact);
            Assert.True(result.MeasuredPeakNits > 150);
        }

        [Fact]
        public void SparseLowWireRowsWithoutGrayscale_DoNotBuildWireExactLut()
        {
            var measurements = new List<MeasurementResult>();
            foreach (double nits in new[] { 0.0, 2.0, 4.0, 8.0, 16.0 })
            {
                measurements.Add(new MeasurementResult
                {
                    Patch = new ColorPatch
                    {
                        Name = $"HDR wire {nits:F0} nits",
                        DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                        Nits = nits,
                        Category = PatchCategory.General,
                    },
                    Xyz = new CieXyz(nits * 0.95, Math.Max(nits * 0.5, 0.02), nits * 1.08),
                });
            }

            var ex = Assert.Throws<InvalidOperationException>(() =>
                HdrMhc2LutBuilder.Build(measurements, SdrWhite));
            Assert.Contains("grayscale", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DuplicateWireRows_DoNotCountAsDistinctHdrCoverage()
        {
            var measurements = new List<MeasurementResult>();
            foreach (double nits in new[] { 0.0, 100.0, 100.0, 100.0, 100.0 })
            {
                measurements.Add(new MeasurementResult
                {
                    Patch = new ColorPatch
                    {
                        Name = $"HDR wire {nits:F0} nits",
                        DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                        Nits = nits,
                        Category = PatchCategory.General,
                    },
                    Xyz = new CieXyz(nits * 0.95, Math.Max(nits * 0.9, 0.02), nits * 1.08),
                });
            }

            var ex = Assert.Throws<InvalidOperationException>(() =>
                HdrMhc2LutBuilder.Build(measurements, SdrWhite));
            Assert.Contains("grayscale", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NoWirePatches_FallsBackToSdrMappedGrayscale()
        {
            var measurements = SimulatePanel(p => Math.Max(TransferFunctions.PqEotf(p), 0.05));
            var result = HdrMhc2LutBuilder.Build(measurements, SdrWhite);
            Assert.False(result.WireExact);
        }

        // ---- Gamut-matrix composition (M5) ---------------------------------------------

        /// <summary>Linear interpolation of the 1024-sample LUT at wire position p.</summary>
        private static double SampleLut(double[] lut, double p)
        {
            double x = Math.Clamp(p, 0.0, 1.0) * (lut.Length - 1);
            int i = (int)Math.Floor(x);
            if (i >= lut.Length - 1) return lut[^1];
            double t = x - i;
            return lut[i] + (lut[i + 1] - lut[i]) * t;
        }

        [Fact]
        public void MatrixScaleOne_IsBitIdentical_ToDefaultBuild()
        {
            // Regression guard: composing an identity/absent matrix (s = 1) must not change
            // a single LUT bit — the division by 1.0 is exact in IEEE arithmetic. Checked on
            // both measurement sources (wire ladder and SDR-mapped grayscale fallback).
            const double gain = 0.867;
            var wire = SimulateWireLadder(p => Math.Max(TransferFunctions.PqEotf(p) * gain, 0.02));
            var wireBaseline = HdrMhc2LutBuilder.Build(wire, SdrWhite);
            var wireScaled = HdrMhc2LutBuilder.Build(wire, SdrWhite, matrixNeutralScale: 1.0);
            Assert.Equal(wireBaseline.LutR, wireScaled.LutR);
            Assert.Equal(wireBaseline.LutG, wireScaled.LutG);
            Assert.Equal(wireBaseline.LutB, wireScaled.LutB);

            var gray = SimulatePanel(p => Math.Max(TransferFunctions.PqEotf(p), 0.05));
            var grayBaseline = HdrMhc2LutBuilder.Build(gray, SdrWhite);
            var grayScaled = HdrMhc2LutBuilder.Build(gray, SdrWhite, matrixNeutralScale: 1.0);
            Assert.Equal(grayBaseline.LutR, grayScaled.LutR);
        }

        [Fact]
        public void ComposedLut_HitsTargetNits_ThroughScaledMatrix_WhereOldBehaviorMisses()
        {
            // The M5 scenario: a panel that renders a flat 86.7% of the PQ spec, calibrated
            // with a gamut matrix carrying uniform scale s = 0.8. In the DWM pipeline the
            // matrix runs BEFORE the LUT, so on the neutral axis content at d nits reaches
            // the LUT at wire position PQ⁻¹(s·d). A LUT built against RAW wire positions
            // (old behavior) lands the chain at ≈ s·d — a 20% luminance miss; the composed
            // LUT must land on d.
            const double gain = 0.867;
            const double s = 0.8;
            var measurements = SimulateWireLadder(p => Math.Max(TransferFunctions.PqEotf(p) * gain, 0.02));

            var composed = HdrMhc2LutBuilder.Build(measurements, SdrWhite, matrixNeutralScale: s);
            var old = HdrMhc2LutBuilder.Build(measurements, SdrWhite);
            Assert.True(composed.WireExact);

            // Targets on the measured ladder knots (gain × ladder nits) to keep the
            // piecewise-linear response inversion exact at the probe points. All sit well
            // below the wire-exact blend window (0.9 × 867-nit measured peak).
            foreach (double ladderNits in new[] { 16.0, 64.0, 150.0, 320.0, 650.0 })
            {
                double target = gain * ladderNits;
                double wPrime = TransferFunctions.PqInverseEotf(s * target);

                double composedOut = gain * TransferFunctions.PqEotf(SampleLut(composed.LutR, wPrime));
                double oldOut = gain * TransferFunctions.PqEotf(SampleLut(old.LutR, wPrime));

                Assert.True(Math.Abs(composedOut - target) / target < 0.02,
                    $"composed chain should hit {target:F1} nits, got {composedOut:F1}");
                Assert.True(Math.Abs(oldOut - target) / target > 0.15,
                    $"old behavior should miss {target:F1} nits by ~{1 - s:P0}, got {oldOut:F1}");
                // Looser tolerance: s·d falls BETWEEN ladder knots, where the piecewise-linear
                // response inversion carries a few percent of interpolation error (largest at
                // the sparse low end of the ladder). Still far from the 25% gap up to d.
                Assert.True(Math.Abs(oldOut - s * target) / (s * target) < 0.08,
                    $"old behavior lands at s·d = {s * target:F1} nits, got {oldOut:F1}");
            }
        }

        [Fact]
        public void ComposedLut_GoesIdentity_AboveTheReachableRange()
        {
            // The corrected branch tops out at the panel's TOP MEASURED wire position (the
            // inversion clamps there for any unreachable target), and the monotonic cleanup
            // holds that value through the blend window — same near-peak hold the uncomposed
            // build has, just anchored at post-matrix positions. Above the top measured wire
            // position (1000-nit request on this panel) the LUT must be pure identity: the
            // composition must never "correct" wire signals beyond the measured range.
            const double gain = 0.867;
            const double s = 0.8;
            var measurements = SimulateWireLadder(p => Math.Max(TransferFunctions.PqEotf(p) * gain, 0.02));
            var composed = HdrMhc2LutBuilder.Build(measurements, SdrWhite, matrixNeutralScale: s);

            double topMeasuredWire = TransferFunctions.PqInverseEotf(1000.0); // top ladder request
            int identityChecked = 0;
            for (int i = 1; i < 1024; i++)
            {
                double p = i / 1023.0;
                if (p <= topMeasuredWire + 0.01) continue;
                Assert.True(Math.Abs(composed.LutR[i] - p) < 0.002,
                    $"identity expected above the measured wire range at p={p:F3}, got {composed.LutR[i]:F3}");
                identityChecked++;

                // And monotonic throughout — the hold region must never invert.
                Assert.True(composed.LutR[i] >= composed.LutR[i - 1], $"LUT not monotonic at {i}");
            }
            Assert.True(identityChecked > 100, "test did not cover the identity region");
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.5)]
        [InlineData(1.5)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void InvalidMatrixScale_Throws(double scale)
        {
            var measurements = SimulatePanel(p => Math.Max(TransferFunctions.PqEotf(p), 0.05));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HdrMhc2LutBuilder.Build(measurements, SdrWhite, matrixNeutralScale: scale));
        }
    }
}
