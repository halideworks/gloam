using System;
using System.Collections.Generic;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// E4 per-channel grayscale-tracking correction: single-channel ramps →
    /// per-channel tone curves in the characterization → per-channel MHC2 tone LUTs
    /// via <see cref="Lut3DGenerator.ComposePerChannelToneLuts"/>.
    /// </summary>
    public class PerChannelGrayTrackingTests
    {
        // Synthetic panel: Rec.709 luminance shares, gamma-2.2 red/blue, and a green
        // channel that can run hot at MID-LEVELS ONLY (bump vanishes at 0 and 1, so the
        // white point is untouched — the level-dependent cast a shared curve + matrix
        // cannot fix).
        private const double Wr = 0.2126, Wg = 0.7152, Wb = 0.0722;
        private const double PeakNits = 100.0;
        private const double BlackNits = 0.05;

        private static double Fr(double v) => Math.Pow(v, 2.2);
        private static double Fb(double v) => Math.Pow(v, 2.2);

        private static double Fg(double v, double bumpAmp)
        {
            double s = Math.Sin(Math.PI * v);
            return Math.Pow(v, 2.2) * (1.0 + bumpAmp * s * s);
        }

        private static MeasurementResult NeutralMeas(double v, double y) => new()
        {
            Patch = new ColorPatch
            {
                Name = $"Gray {v * 100:F0}%",
                DisplayRgb = new LinearRgb(v, v, v),
                Category = PatchCategory.Grayscale
            },
            Xyz = new CieXyz(0.95047 * y, y, 1.08883 * y),
            IsValid = true
        };

        private static MeasurementResult ChannelMeas(
            string name, int channel, double v, double y, PatchCategory category, Chromaticity chroma) => new()
        {
            Patch = new ColorPatch
            {
                Name = name,
                DisplayRgb = new LinearRgb(
                    channel == 0 ? v : 0, channel == 1 ? v : 0, channel == 2 ? v : 0),
                Category = category
            },
            Xyz = chroma.ToXyz(y),
            IsValid = true
        };

        private static readonly double[] RampLevels = { 0.25, 0.4, 0.55, 0.7, 0.85 };

        private static List<MeasurementResult> SyntheticPanel(double greenBumpAmp, bool includeRamps = true)
        {
            var list = new List<MeasurementResult>();

            for (int i = 0; i <= 20; i++)
            {
                double v = i / 20.0;
                double y = BlackNits + PeakNits * (Wr * Fr(v) + Wg * Fg(v, greenBumpAmp) + Wb * Fb(v));
                list.Add(NeutralMeas(v, y));
            }

            // Full-drive primaries (ramp top anchors). Fg(1) == 1 regardless of the bump.
            list.Add(ChannelMeas("Red 100%", 0, 1.0, BlackNits + PeakNits * Wr, PatchCategory.Primary, Chromaticity.Rec709Red));
            list.Add(ChannelMeas("Green 100%", 1, 1.0, BlackNits + PeakNits * Wg, PatchCategory.Primary, Chromaticity.Rec709Green));
            list.Add(ChannelMeas("Blue 100%", 2, 1.0, BlackNits + PeakNits * Wb, PatchCategory.Primary, Chromaticity.Rec709Blue));

            if (includeRamps)
            {
                foreach (double v in RampLevels)
                {
                    list.Add(ChannelMeas($"Red Ramp {v * 100:F0}%", 0, v,
                        BlackNits + PeakNits * Wr * Fr(v), PatchCategory.Saturated, Chromaticity.Rec709Red));
                    list.Add(ChannelMeas($"Green Ramp {v * 100:F0}%", 1, v,
                        BlackNits + PeakNits * Wg * Fg(v, greenBumpAmp), PatchCategory.Saturated, Chromaticity.Rec709Green));
                    list.Add(ChannelMeas($"Blue Ramp {v * 100:F0}%", 2, v,
                        BlackNits + PeakNits * Wb * Fb(v), PatchCategory.Saturated, Chromaticity.Rec709Blue));
                }
            }

            return list;
        }

        private static DisplayCharacterization Characterize(List<MeasurementResult> measurements)
        {
            var gen = new Lut3DGenerator(StandardTargets.SrgbGamma22, measurements);
            return gen.BuildCharacterizationOnly();
        }

        // ----- characterization: learning per-channel curves from the ramps -----------

        [Fact]
        public void Characterization_WithRamps_LearnsTruePerChannelCurves()
        {
            var c = Characterize(SyntheticPanel(greenBumpAmp: 0.02));

            Assert.True(c.HasPerChannelToneCurves);
            Assert.NotNull(c.NeutralToneCurve);
            Assert.NotSame(c.NeutralToneCurve, c.RedToneCurve);
            Assert.NotSame(c.NeutralToneCurve, c.GreenToneCurve);
            Assert.NotSame(c.NeutralToneCurve, c.BlueToneCurve);

            // Red/blue track pure gamma 2.2; green carries the 2% mid-level excess.
            // Absolute tolerances allow for PCHIP reconstruction between the ramp knots;
            // the red/green SEPARATION is the robust signal (fit errors are common-mode).
            Assert.InRange(c.RedToneCurve.Lookup(0.5), Fr(0.5) - 0.01, Fr(0.5) + 0.01);
            Assert.InRange(c.BlueToneCurve.Lookup(0.5), Fb(0.5) - 0.01, Fb(0.5) + 0.01);
            Assert.InRange(c.GreenToneCurve.Lookup(0.5), Fg(0.5, 0.02) - 0.01, Fg(0.5, 0.02) + 0.01);
            Assert.True(c.GreenToneCurve.Lookup(0.5) - c.RedToneCurve.Lookup(0.5) > 0.0015,
                "green mid-level excess must be visible in the fitted curves");
            // At a ramp knot the fits are exact: the full 2% excess shows.
            Assert.InRange(c.GreenToneCurve.Lookup(0.55), Fg(0.55, 0.02) - 0.001, Fg(0.55, 0.02) + 0.001);
            Assert.InRange(c.RedToneCurve.Lookup(0.55), Fr(0.55) - 0.001, Fr(0.55) + 0.001);

            // Per-channel curves are normalized to their own full drive: they agree at 1.0,
            // so per-channel corrections cannot re-balance white.
            Assert.InRange(c.RedToneCurve.Lookup(1.0), 0.999, 1.0);
            Assert.InRange(c.GreenToneCurve.Lookup(1.0), 0.999, 1.0);
            Assert.InRange(c.BlueToneCurve.Lookup(1.0), 0.999, 1.0);
        }

        [Fact]
        public void Characterization_WithoutRamps_FallsBackToSharedNeutralCurve()
        {
            var c = Characterize(SyntheticPanel(greenBumpAmp: 0.02, includeRamps: false));

            Assert.False(c.HasPerChannelToneCurves);
            Assert.NotNull(c.NeutralToneCurve);
            Assert.Same(c.NeutralToneCurve, c.RedToneCurve);
            Assert.Same(c.NeutralToneCurve, c.GreenToneCurve);
            Assert.Same(c.NeutralToneCurve, c.BlueToneCurve);
        }

        [Fact]
        public void Characterization_DiscardsChromaticityOutlierRampSamples()
        {
            var list = SyntheticPanel(greenBumpAmp: 0.0);

            // Corrupt the green 55% ramp reading: wildly wrong chromaticity AND luminance
            // (stray light hit the probe). The chroma gate must discard it so the bogus Y
            // never enters the fit.
            int idx = list.FindIndex(m => m.Patch.Name == "Green Ramp 55%");
            list[idx] = ChannelMeas("Green Ramp 55%", 1, 0.55,
                BlackNits + PeakNits * Wg * 3.0, PatchCategory.Saturated,
                new Chromaticity(0.35, 0.25)); // magenta-ish, far off the green primary

            var c = Characterize(list);

            Assert.True(c.HasPerChannelToneCurves);
            // Green still fits from the remaining 4 ramp samples + full drive; the value at
            // 0.55 stays near the true response instead of the ~3x contaminated reading.
            Assert.InRange(c.GreenToneCurve.Lookup(0.55), Fg(0.55, 0) - 0.01, Fg(0.55, 0) + 0.01);
        }

        [Fact]
        public void Characterization_GridAxisNodesAlone_AreNotEnoughForPerChannelFit()
        {
            // A Standard-preset-like set: full-drive primaries plus only three single-read
            // grid axis nodes per channel (0.25/0.5/0.75). Below the 5-sample bar → the
            // channel must fall back to the shared curve rather than fit noisy data.
            var list = SyntheticPanel(greenBumpAmp: 0.0, includeRamps: false);
            foreach (double v in new[] { 0.25, 0.5, 0.75 })
            {
                list.Add(ChannelMeas($"Grid R{v}", 0, v, BlackNits + PeakNits * Wr * Fr(v),
                    PatchCategory.General, Chromaticity.Rec709Red));
                list.Add(ChannelMeas($"Grid G{v}", 1, v, BlackNits + PeakNits * Wg * Fg(v, 0),
                    PatchCategory.General, Chromaticity.Rec709Green));
                list.Add(ChannelMeas($"Grid B{v}", 2, v, BlackNits + PeakNits * Wb * Fb(v),
                    PatchCategory.General, Chromaticity.Rec709Blue));
            }

            var c = Characterize(list);

            Assert.False(c.HasPerChannelToneCurves);
            Assert.Same(c.NeutralToneCurve, c.GreenToneCurve);
        }

        // ----- composition: shared/neutral LUT → per-channel correction LUTs ----------

        private static double[] SharedInverseLut(ToneCurve neutral, int size = 1024)
        {
            // What an open-loop shared-curve build produces for a pure gamma-2.2 target:
            // lut(v) = f_neutral⁻¹(v^2.2).
            var lut = new double[size];
            for (int i = 0; i < size; i++)
                lut[i] = neutral.InverseLookup(Math.Pow(i / (double)(size - 1), 2.2));
            return lut;
        }

        [Fact]
        public void Compose_PerfectPanel_ReproducesSharedCurveResult()
        {
            // All channels identical to the neutral response (equal curves, distinct
            // instances so the identity short-circuit is NOT taken): the per-channel
            // delta must be the identity and the output must match the shared result.
            var points = Enumerable.Range(0, 257)
                .Select(i => { double v = i / 256.0; return (input: v, output: Math.Pow(v, 2.2)); })
                .ToList();
            var c = new DisplayCharacterization
            {
                NeutralToneCurve = ToneCurve.CreateFromPoints(points),
                RedToneCurve = ToneCurve.CreateFromPoints(points),
                GreenToneCurve = ToneCurve.CreateFromPoints(points),
                BlueToneCurve = ToneCurve.CreateFromPoints(points),
                HasPerChannelToneCurves = true
            };
            Assert.NotSame(c.NeutralToneCurve, c.RedToneCurve);

            var shared = SharedInverseLut(c.NeutralToneCurve!);
            var (r, g, b) = Lut3DGenerator.ComposePerChannelToneLuts(
                c, shared, (double[])shared.Clone(), (double[])shared.Clone());

            for (int i = 0; i < shared.Length; i++)
            {
                Assert.True(Math.Abs(r[i] - shared[i]) < 1e-3, $"R[{i}]: {r[i]} vs {shared[i]}");
                Assert.True(Math.Abs(g[i] - shared[i]) < 1e-3, $"G[{i}]: {g[i]} vs {shared[i]}");
                Assert.True(Math.Abs(b[i] - shared[i]) < 1e-3, $"B[{i}]: {b[i]} vs {shared[i]}");
            }

            // Endpoints preserved exactly.
            Assert.Equal(0.0, r[0], 9);
            Assert.Equal(0.0, g[0], 9);
            Assert.InRange(r[^1], 0.999, 1.0);
            Assert.InRange(g[^1], 0.999, 1.0);
        }

        [Fact]
        public void Compose_GreenHotAtMidLevels_FixesGrayTrackingAndLeavesWhiteAlone()
        {
            const double bump = 0.02;
            var c = Characterize(SyntheticPanel(bump));
            Assert.True(c.HasPerChannelToneCurves);

            var shared = SharedInverseLut(c.NeutralToneCurve!);
            var (lr, lg, lb) = Lut3DGenerator.ComposePerChannelToneLuts(
                c, shared, (double[])shared.Clone(), (double[])shared.Clone());

            // Simulate the TRUE panel: relative per-channel emission for a gray input,
            // through a given set of per-channel signal LUTs. Equal channel emissions ⇒
            // the gray sits exactly at the panel's white point (zero cast).
            static double Deviation(double sR, double sG, double sB, double bumpAmp)
            {
                double or_ = Fr(sR), og = Fg(sG, bumpAmp), ob = Fb(sB);
                double mean = (or_ + og + ob) / 3.0;
                return Math.Max(Math.Abs(or_ - mean), Math.Max(Math.Abs(og - mean), Math.Abs(ob - mean))) / mean;
            }

            // Average over the mid-gray region (signal 0.35–0.65) where the cast peaks.
            int lo = (int)(0.35 * (shared.Length - 1));
            int hi = (int)(0.65 * (shared.Length - 1));
            double before = 0, after = 0;
            int n = 0;
            for (int i = lo; i <= hi; i++, n++)
            {
                before += Deviation(shared[i], shared[i], shared[i], bump);
                after += Deviation(lr[i], lg[i], lb[i], bump);
            }
            before /= n;
            after /= n;

            Assert.True(before > 0.008, $"synthetic cast too small to be meaningful: {before}");
            Assert.True(after * 5.0 < before,
                $"mid-gray channel deviation must shrink >5x: before={before:F5}, after={after:F5}");
            Assert.True(after < 0.004, $"residual mid-gray deviation too large: {after:F5}");

            // White untouched: the per-channel curves agree at full drive, so the deltas
            // are identity at the top of the range.
            Assert.InRange(lr[^1], 0.999, 1.0);
            Assert.InRange(lg[^1], 0.999, 1.0);
            Assert.InRange(lb[^1], 0.999, 1.0);
            Assert.Equal(0.0, lr[0], 9);
            Assert.Equal(0.0, lg[0], 9);
            Assert.Equal(0.0, lb[0], 9);

            // Monotonic — the MHC2 writer's validation gate requires it.
            for (int i = 1; i < lr.Length; i++)
            {
                Assert.True(lr[i] >= lr[i - 1] - 1e-12, $"R non-monotonic at {i}");
                Assert.True(lg[i] >= lg[i - 1] - 1e-12, $"G non-monotonic at {i}");
                Assert.True(lb[i] >= lb[i - 1] - 1e-12, $"B non-monotonic at {i}");
            }
        }

        [Fact]
        public void Compose_LutsAlreadyPerChannel_PassThroughUntouched()
        {
            // LUTs that already differ per channel were built through LutGenerator's
            // per-channel inversion of these same curves — composing the delta again
            // would double-correct. They must pass through by reference.
            var c = Characterize(SyntheticPanel(0.02));
            var shared = SharedInverseLut(c.NeutralToneCurve!);
            var differentG = (double[])shared.Clone();
            differentG[500] = Math.Min(1.0, differentG[500] + 0.01);

            var (r, g, b) = Lut3DGenerator.ComposePerChannelToneLuts(c, shared, differentG, (double[])shared.Clone());

            Assert.Same(shared, r);
            Assert.Same(differentG, g);
        }

        [Fact]
        public void Compose_NoPerChannelData_PassThroughUntouched()
        {
            var c = Characterize(SyntheticPanel(0.02, includeRamps: false));
            Assert.False(c.HasPerChannelToneCurves);

            var shared = SharedInverseLut(c.NeutralToneCurve!);
            var gIn = (double[])shared.Clone();
            var bIn = (double[])shared.Clone();

            var (r, g, b) = Lut3DGenerator.ComposePerChannelToneLuts(c, shared, gIn, bIn);

            Assert.Same(shared, r);
            Assert.Same(gIn, g);
            Assert.Same(bIn, b);
        }

        [Fact]
        public void Compose_ClosedLoopNeutral_GetsPerChannelDeltaOnTop()
        {
            // The closed-loop install path ships DecomposeCorrection's refined NEUTRAL
            // curve identically on all channels. The composition must apply the
            // level-dependent delta on top of that curve — i.e. the green channel drops
            // below the neutral curve at mid-levels (green runs hot, so it needs less
            // drive), while red/blue stay approximately on it.
            const double bump = 0.02;
            var c = Characterize(SyntheticPanel(bump));

            // A refined neutral that is NOT the plain shared inverse (closed loop moved
            // it): blend a touch of extra contrast in, monotone by construction.
            var neutral = SharedInverseLut(c.NeutralToneCurve!);
            for (int i = 0; i < neutral.Length; i++)
            {
                double v = i / (double)(neutral.Length - 1);
                neutral[i] = Math.Clamp(0.9 * neutral[i] + 0.1 * Math.Pow(v, 1.05), 0.0, 1.0);
            }
            for (int i = 1; i < neutral.Length; i++)
                if (neutral[i] < neutral[i - 1]) neutral[i] = neutral[i - 1];

            var (lr, lg, lb) = Lut3DGenerator.ComposePerChannelToneLuts(
                c, neutral, (double[])neutral.Clone(), (double[])neutral.Clone());

            // The shared/neutral luminance curve CREDITS green's excess light (weighted
            // ~0.72 into Y), so relative to it the correct per-channel deltas push green
            // DOWN and red/blue UP at mid-levels — grays then hold both the neutral
            // luminance behavior and the white-point chromaticity.
            int mid = neutral.Length / 2;
            Assert.True(lg[mid] < neutral[mid] - 0.0005,
                $"hot green must be driven below the neutral curve at mid-levels ({lg[mid]:F5} vs {neutral[mid]:F5})");
            Assert.True(lr[mid] > neutral[mid] + 0.0005,
                $"red must be driven above the neutral curve at mid-levels ({lr[mid]:F5} vs {neutral[mid]:F5})");
            Assert.True(lr[mid] - lg[mid] > 0.002,
                $"green/red separation too small: {lr[mid] - lg[mid]:F5}");
            // Delta is identity at the endpoints (curves agree at full drive).
            Assert.Equal(neutral[^1], lg[^1], 3);
            Assert.Equal(0.0, lg[0], 9);
        }
    }
}
