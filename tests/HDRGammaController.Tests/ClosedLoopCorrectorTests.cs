using System;
using System.Collections.Generic;
using Xunit;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Verifies the closed-loop correction converges, using a SIMULATED display so the loop
    /// can be exercised without a colorimeter. The simulated display has a known wrong gamma
    /// and a blue color cast; applying the corrector's LUTs and re-measuring should drive the
    /// grayscale residual down round over round.
    /// </summary>
    public class ClosedLoopCorrectorTests
    {
        // Simulated panel: per-channel power-law response with a channel-gain color cast.
        // sentSignal (after VCGT) -> linear light; then linear RGB -> XYZ via sRGB primaries.
        private sealed class SimDisplay
        {
            public double GammaR = 2.45, GammaG = 2.45, GammaB = 2.45;
            public double GainR = 1.00, GainG = 0.97, GainB = 1.18; // cool/blue cast
            public double PeakNits = 120.0;

            public CieXyz Measure(double sentR, double sentG, double sentB)
            {
                double lr = GainR * Math.Pow(Math.Clamp(sentR, 0, 1), GammaR);
                double lg = GainG * Math.Pow(Math.Clamp(sentG, 0, 1), GammaG);
                double lb = GainB * Math.Pow(Math.Clamp(sentB, 0, 1), GammaB);
                var xyz = ColorMath.LinearSrgbToXyz(new LinearRgb(lr, lg, lb));
                return new CieXyz(xyz.X * PeakNits, xyz.Y * PeakNits, xyz.Z * PeakNits);
            }
        }

        private static double SampleLut(double[] lut, double v)
        {
            double idx = Math.Clamp(v, 0, 1) * (lut.Length - 1);
            int lo = (int)Math.Floor(idx);
            int hi = Math.Min(lo + 1, lut.Length - 1);
            return lut[lo] + (lut[hi] - lut[lo]) * (idx - lo);
        }

        // Measures a neutral grayscale ramp through the (optional) VCGT correction.
        private static List<MeasurementResult> MeasureRamp(
            SimDisplay sim, (double[] R, double[] G, double[] B)? correction, int steps = 16)
        {
            var list = new List<MeasurementResult>();
            for (int i = 0; i <= steps; i++)
            {
                double v = i / (double)steps;
                double sR = correction.HasValue ? SampleLut(correction.Value.R, v) : v;
                double sG = correction.HasValue ? SampleLut(correction.Value.G, v) : v;
                double sB = correction.HasValue ? SampleLut(correction.Value.B, v) : v;
                var xyz = sim.Measure(sR, sG, sB);
                list.Add(new MeasurementResult
                {
                    Patch = new ColorPatch { Name = $"grey{i}", DisplayRgb = new LinearRgb(v, v, v), Category = PatchCategory.Grayscale },
                    Xyz = xyz,
                    IsValid = true
                });
            }
            return list;
        }

        [Fact]
        public void ClosedLoop_DrivesGrayscaleResidualDown()
        {
            var sim = new SimDisplay();
            var corrector = new ClosedLoopCorrector(StandardTargets.SrgbGamma22, sdrWhiteLevel: 120, isHdr: false);

            // Round 0: native + initial correction.
            var native = MeasureRamp(sim, null);
            double nativeResidual = corrector.GrayscaleResidualDeltaE(native);

            var correction = corrector.BuildInitialCorrection(native);
            var afterInitial = MeasureRamp(sim, correction);
            double bestResidual = corrector.GrayscaleResidualDeltaE(afterInitial);
            var best = correction;

            // A few refinement rounds, keeping the best.
            for (int round = 0; round < 4; round++)
            {
                correction = corrector.RefineCorrection(MeasureRamp(sim, correction), correction);
                double residual = corrector.GrayscaleResidualDeltaE(MeasureRamp(sim, correction));
                if (residual < bestResidual) { bestResidual = residual; best = correction; }
            }

            // The corrected grayscale must be substantially better than native, and good in
            // absolute terms (the simulated cast/gamma is fully 1D-correctable).
            Assert.True(bestResidual < nativeResidual,
                $"Correction did not improve residual: native={nativeResidual:F2}, best={bestResidual:F2}");
            Assert.True(bestResidual < 1.5,
                $"Closed loop did not converge to a low residual: best={bestResidual:F2} dE");
        }

        [Fact]
        public void BuildInitialCorrection_ProducesMonotonic1024Luts()
        {
            var sim = new SimDisplay();
            var corrector = new ClosedLoopCorrector(StandardTargets.SrgbGamma22, 120, false);
            var (r, g, b) = corrector.BuildInitialCorrection(MeasureRamp(sim, null));

            Assert.Equal(1024, r.Length);
            foreach (var lut in new[] { r, g, b })
                for (int i = 1; i < lut.Length; i++)
                    Assert.True(lut[i] >= lut[i - 1] - 1e-9, $"correction LUT not monotonic at {i}");
        }

        [Fact]
        public void RefineCorrection_NonMonotonicMeasuredRamp_UsesMonotonicEnvelopeForInverse()
        {
            var target = new CalibrationTarget
            {
                Name = "Linear test",
                RedPrimary = Chromaticity.Rec709Red,
                GreenPrimary = Chromaticity.Rec709Green,
                BluePrimary = Chromaticity.Rec709Blue,
                WhitePoint = Chromaticity.D65,
                TransferFunction = TransferFunctionType.Linear,
            };
            var corrector = new ClosedLoopCorrector(target, 100, isHdr: false, damping: 1.0);
            var identity = IdentityCorrection();
            var achieved = new List<MeasurementResult>
            {
                Gray(0.00, 0.00),
                Gray(0.25, 0.50),
                Gray(0.50, 0.30), // noisy inversion; must not pull inverse lookup forward
                Gray(0.75, 0.75),
                Gray(1.00, 1.00),
            };

            var refined = corrector.RefineCorrection(achieved, identity);
            double corrected55 = SampleLut(refined.R, 0.55);

            Assert.InRange(corrected55, 0.53, 0.57);
        }

        [Fact]
        public void RefineCorrection_NormalizesResponseAgainstEndpointBlackAndWhite()
        {
            var target = new CalibrationTarget
            {
                Name = "Linear test",
                RedPrimary = Chromaticity.Rec709Red,
                GreenPrimary = Chromaticity.Rec709Green,
                BluePrimary = Chromaticity.Rec709Blue,
                WhitePoint = Chromaticity.D65,
                TransferFunction = TransferFunctionType.Linear,
            };
            var corrector = new ClosedLoopCorrector(target, 100, isHdr: false, damping: 1.0);
            var identity = IdentityCorrection();
            var achieved = new List<MeasurementResult>
            {
                Gray(0.00, 0.10), // measured black endpoint
                Gray(0.25, 0.00), // stray lower reading must not redefine black
                Gray(0.50, 0.50),
                Gray(1.00, 1.00),
            };

            var refined = corrector.RefineCorrection(achieved, identity);
            double corrected20 = SampleLut(refined.R, 0.20);

            Assert.InRange(corrected20, 0.35, 0.38);
        }

        [Fact]
        public void RefineCorrection_NonFiniteInputs_ReturnsFiniteMonotonicLuts()
        {
            var target = new CalibrationTarget
            {
                Name = "Linear test",
                RedPrimary = Chromaticity.Rec709Red,
                GreenPrimary = Chromaticity.Rec709Green,
                BluePrimary = Chromaticity.Rec709Blue,
                WhitePoint = Chromaticity.D65,
                TransferFunction = TransferFunctionType.Linear,
            };
            var corrector = new ClosedLoopCorrector(target, double.NaN, isHdr: false, damping: double.NaN);
            var achieved = new List<MeasurementResult>
            {
                Gray(0.00, 0.00),
                Gray(0.50, 0.25),
                Gray(1.00, 1.00),
                new MeasurementResult
                {
                    Patch = new ColorPatch
                    {
                        Name = "corrupt gray",
                        DisplayRgb = new LinearRgb(double.NaN, double.NaN, double.NaN),
                        Category = PatchCategory.Grayscale
                    },
                    Xyz = new CieXyz(double.NaN, double.PositiveInfinity, -1.0),
                    IsValid = true
                }
            };
            var current = (
                R: new[] { 0.0, double.NaN, 1.0 },
                G: Array.Empty<double>(),
                B: new[] { double.PositiveInfinity });

            var refined = corrector.RefineCorrection(achieved, current);

            AssertFiniteMonotonic(refined.R);
            AssertFiniteMonotonic(refined.G);
            AssertFiniteMonotonic(refined.B);
        }

        // ------------------------------------------------------------------ M1: residual objective

        [Fact]
        public void GrayscaleResidualDeltaE_WhitePointOnlyImprovement_StrictlyImprovesScore()
        {
            // M1 regression: the residual used the MEASURED white as its Lab reference, so
            // white always scored dE = 0 and a uniform cast was invisible — keep-best could
            // then revert a round that only improved the white point. Grading against the
            // TARGET white makes a white-only improvement strictly lower the score.
            var corrector = new ClosedLoopCorrector(StandardTargets.SrgbGamma22, 120, isHdr: false);

            var heavyCast = NeutralRamp(blueGain: 1.15);   // tone perfect, white 15% blue
            var lightCast = NeutralRamp(blueGain: 1.05);   // SAME tone, white improved only
            var onTarget = NeutralRamp(blueGain: 1.00);

            double heavy = corrector.GrayscaleResidualDeltaE(heavyCast);
            double light = corrector.GrayscaleResidualDeltaE(lightCast);
            double perfect = corrector.GrayscaleResidualDeltaE(onTarget);

            Assert.True(perfect < 0.3, $"on-target ramp should score ~0, got {perfect:F2}");
            Assert.True(light > perfect, "any cast must score worse than on-target");
            Assert.True(heavy > light + 0.3,
                $"an iteration that ONLY improves the white point must strictly improve the " +
                $"residual: 15% cast {heavy:F2} vs 5% cast {light:F2}");
        }

        // Neutral gamma-2.2 ramp on the target (D65) white, with an optional uniform blue
        // cast; tone tracking is exact so any score difference is white-point only.
        private static List<MeasurementResult> NeutralRamp(double blueGain, int steps = 16)
        {
            var list = new List<MeasurementResult>();
            for (int i = 0; i <= steps; i++)
            {
                double v = i / (double)steps;
                double lin = Math.Pow(v, 2.2);
                list.Add(new MeasurementResult
                {
                    Patch = new ColorPatch
                    {
                        Name = $"grey{i}",
                        DisplayRgb = new LinearRgb(v, v, v),
                        Category = PatchCategory.Grayscale
                    },
                    Xyz = new CieXyz(
                        0.95047 * lin * 120.0,
                        1.0 * lin * 120.0,
                        1.08883 * lin * blueGain * 120.0),
                    IsValid = true
                });
            }
            return list;
        }

        // ------------------------------------------------------------------ M2: gain domain

        [Fact]
        public void RefineCorrection_AppliesWhiteGainInLinearLight_NotOnEncodedSignal()
        {
            // The VCGT output is a gamma-encoded panel signal. A requested linear gain g must
            // therefore become OETF(g * EOTF(signal)); multiplying the encoded value directly
            // (the old bug) applies g^gamma of linear change (~11% for a 5% request at 2.2).
            var neutralPanel = new SimDisplay { GammaR = 2.2, GammaG = 2.2, GammaB = 2.2, GainR = 1, GainG = 1, GainB = 1 };
            var corrector = new ClosedLoopCorrector(StandardTargets.SrgbGamma22, 120, isHdr: false, damping: 1.0);
            corrector.BuildInitialCorrection(MeasureRamp(neutralPanel, null)); // captures the native tone curve

            // Achieved pass: perfect gamma-2.2 tone, red channel 5% low in LINEAR light. The
            // white balance (gains renormalized so max = 1) requests a 1/1.05 linear step on
            // green and blue.
            var redDeficient = new SimDisplay { GammaR = 2.2, GammaG = 2.2, GammaB = 2.2, GainR = 1.0 / 1.05, GainG = 1, GainB = 1 };
            var identity = IdentityCorrection();
            var refined = corrector.RefineCorrection(MeasureRamp(redDeficient, identity), identity);

            double v = 0.6;
            double signalRatio = SampleLut(refined.G, v) / v;
            double linearRatio = Math.Pow(signalRatio, 2.2);
            double requested = 1.0 / 1.05;

            Assert.InRange(linearRatio, requested - 0.012, requested + 0.012);
            Assert.True(linearRatio > 0.925,
                $"linear change {1 - linearRatio:P1} looks like the encoded-domain bug " +
                $"(would be ~{1 - Math.Pow(requested, 2.2):P1}), requested {1 - requested:P1}");
        }

        // ------------------------------------------------------------------ M4: decomposition

        [Fact]
        public void DecomposeCorrection_SplitsNeutralToneAndWhiteGains_AndRecomposes()
        {
            var tone = ToneCurve.CreateGamma(2.2);
            var neutralIn = new double[1024];
            for (int i = 0; i < neutralIn.Length; i++)
                neutralIn[i] = Math.Pow(i / 1023.0, 0.9);

            // Realistic post-white-balance gains (max = 1: channels are only pulled DOWN).
            double gR = 1.0, gG = 0.97, gB = 0.93;
            var correction = (
                R: ClosedLoopCorrector.RecomposeChannel(neutralIn, gR, tone),
                G: ClosedLoopCorrector.RecomposeChannel(neutralIn, gG, tone),
                B: ClosedLoopCorrector.RecomposeChannel(neutralIn, gB, tone));

            var (neutralOut, outR, outG, outB) = ClosedLoopCorrector.DecomposeCorrection(correction, tone);

            // Gains recovered up to the mean-1 normalization: ratios preserved.
            Assert.Equal(gR / gG, outR / outG, 6);
            Assert.Equal(gB / gG, outB / outG, 6);

            // Neutral tone x white gains must recompose to the original channels.
            var rebuilt = (
                R: ClosedLoopCorrector.RecomposeChannel(neutralOut, outR, tone),
                G: ClosedLoopCorrector.RecomposeChannel(neutralOut, outG, tone),
                B: ClosedLoopCorrector.RecomposeChannel(neutralOut, outB, tone));
            for (int i = 0; i < neutralIn.Length; i++)
            {
                Assert.True(Math.Abs(rebuilt.R[i] - correction.R[i]) < 1e-6, $"R diverged at {i}");
                Assert.True(Math.Abs(rebuilt.G[i] - correction.G[i]) < 1e-6, $"G diverged at {i}");
                Assert.True(Math.Abs(rebuilt.B[i] - correction.B[i]) < 1e-6, $"B diverged at {i}");
            }

            // The neutral component is monotonic and spans the full range.
            Assert.Equal(0.0, neutralOut[0], 9);
            for (int i = 1; i < neutralOut.Length; i++)
                Assert.True(neutralOut[i] >= neutralOut[i - 1] - 1e-12);
        }

        // ------------------------------------------------------------------ m4: BT.1886 wiring

        [Fact]
        public void MakeEffectiveTarget_WiresMeasuredContrastIntoBt1886_Only()
        {
            var characterization = new DisplayCharacterization
            {
                BlackLevel = 0.2,
                PeakLuminance = 200.0
            };

            var effective = ClosedLoopCorrector.MakeEffectiveTarget(
                StandardTargets.Rec709Gamma24, characterization);

            // Measured contrast 1000:1 scaled into the target's 100-nit white domain.
            Assert.Equal(0.1, effective.BlackLevel, 10);
            Assert.NotEqual(Math.Pow(0.5, 2.4), effective.ApplyEotf(0.5), 4);

            // Non-BT.1886 targets pass through untouched.
            Assert.Same(StandardTargets.SrgbGamma22,
                ClosedLoopCorrector.MakeEffectiveTarget(StandardTargets.SrgbGamma22, characterization));

            // A zero/implausible measured black leaves the target alone.
            var zeroBlack = new DisplayCharacterization { BlackLevel = 0.0, PeakLuminance = 200.0 };
            Assert.Same(StandardTargets.Rec709Gamma24,
                ClosedLoopCorrector.MakeEffectiveTarget(StandardTargets.Rec709Gamma24, zeroBlack));
        }

        private static MeasurementResult Gray(double signal, double y)
        {
            return new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = $"gray {signal:F2}",
                    DisplayRgb = new LinearRgb(signal, signal, signal),
                    Category = PatchCategory.Grayscale
                },
                Xyz = new CieXyz(0.95047 * y, y, 1.08883 * y),
                IsValid = true
            };
        }

        private static (double[] R, double[] G, double[] B) IdentityCorrection()
        {
            var r = new double[1024];
            var g = new double[1024];
            var b = new double[1024];
            for (int i = 0; i < r.Length; i++)
                r[i] = g[i] = b[i] = i / 1023.0;
            return (r, g, b);
        }

        private static void AssertFiniteMonotonic(double[] lut)
        {
            Assert.Equal(1024, lut.Length);
            for (int i = 0; i < lut.Length; i++)
            {
                Assert.True(double.IsFinite(lut[i]));
                Assert.InRange(lut[i], 0.0, 1.0);
                if (i > 0)
                    Assert.True(lut[i] >= lut[i - 1] - 1e-12, $"LUT not monotonic at {i}");
            }
        }
    }
}
