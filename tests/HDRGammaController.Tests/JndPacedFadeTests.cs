using System;
using System.Collections.Generic;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// JND-paced fade steps (roadmap 3.5). The headline promise: every realized hardware
    /// write during a normal fade stays at or below 0.5 ΔE ITP at the 100 cd/m² reference —
    /// and the integer-Kelvin dedupe floor is MEASURED to sit under that threshold rather
    /// than assumed (the mired-grid contingency triggers if this ever fails).
    /// </summary>
    public class JndPacedFadeTests
    {
        private static readonly NightModeAlgorithm[] Algorithms =
        {
            NightModeAlgorithm.Perceptual,
            NightModeAlgorithm.AccurateCIE1931,
            NightModeAlgorithm.Standard,
            NightModeAlgorithm.UltraNight,
        };

        // ---- ComputeMaxStepMired sanity across the range -----------------------------------

        [Fact]
        public void ComputeMaxStepMired_FinitePositiveInRange_AcrossKelvinAlgorithmsAndPreserve()
        {
            foreach (var algorithm in Algorithms)
            foreach (bool preserve in new[] { false, true })
            foreach (int kelvin in new[] { 1900, 2200, 2700, 3400, 4500, 5500, 6400, 6500 })
            {
                double step = JndPacedFade.ComputeMaxStepMired(
                    kelvin, algorithm, 0.8, useUltraWarmMode: false, preserve);
                Assert.True(double.IsFinite(step) && step > 0,
                    $"{algorithm} @ {kelvin}K preserve={preserve}: step {step}");
                Assert.InRange(step, 0.01, 2.0);
            }
        }

        [Fact]
        public void ComputeMaxStepMired_DegenerateKelvin_FallsBackToHeuristic()
        {
            Assert.Equal(JndPacedFade.FallbackStepMired,
                JndPacedFade.ComputeMaxStepMired(0, NightModeAlgorithm.Perceptual, 0.8, false, false));
            Assert.Equal(JndPacedFade.FallbackStepMired,
                JndPacedFade.ComputeMaxStepMired(-100, NightModeAlgorithm.Perceptual, 0.8, false, false));
        }

        [Fact]
        public void ComputeMaxStepMired_UltraNight_PacesTighterThanPerceptual()
        {
            // UltraNight's deliberate dimming makes each mired step MORE visible (the
            // luminance component of ΔE ITP), so its ceiling must be smaller — the strongest
            // argument for the ITP metric over pure-chromaticity pacing.
            double ultra = JndPacedFade.ComputeMaxStepMired(
                3000, NightModeAlgorithm.UltraNight, 0.8, false, false);
            double perceptual = JndPacedFade.ComputeMaxStepMired(
                3000, NightModeAlgorithm.Perceptual, 0.8, false, false);
            Assert.True(ultra < perceptual,
                $"UltraNight step {ultra:F3} should be tighter than Perceptual {perceptual:F3}");
        }

        // ---- The headline promise: simulated fade stays sub-threshold -----------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SimulatedFade_6500To2700_Every250msWrite_StaysUnderHalfJnd(bool preserveLuminance)
        {
            // 30-minute fade, mired-linear trajectory (the service's stateless interpolation),
            // sampled at the 250 ms hardware-write floor. Every consecutive pair of realized
            // whites must sit within the 0.5 ΔE ITP ceiling.
            const int startKelvin = 6500, endKelvin = 2700;
            const double fadeMinutes = 30;
            const double writeMs = 250.0;

            int steps = (int)(fadeMinutes * 60_000.0 / writeMs);
            var kelvins = new List<int>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                kelvins.Add(NightModeService.InterpolateKelvinInMired(
                    startKelvin, endKelvin, i / (double)steps));
            }

            double worst = 0;
            int worstKelvin = 0;
            for (int i = 1; i < kelvins.Count; i++)
            {
                if (kelvins[i] == kelvins[i - 1]) continue; // integer dedupe: no write
                double dE = StepDeltaE(kelvins[i - 1], kelvins[i],
                    NightModeAlgorithm.Perceptual, preserveLuminance);
                if (dE > worst) { worst = dE; worstKelvin = kelvins[i]; }
            }

            Assert.True(worst <= JndPacedFade.TargetStepItp,
                $"worst realized step {worst:F3} ΔE ITP at ~{worstKelvin}K exceeds the " +
                $"{JndPacedFade.TargetStepItp} ceiling (preserve={preserveLuminance})");
        }

        // ---- Integer-Kelvin quantization floor: measured, not assumed -----------------------

        [Fact]
        public void OneKelvinStep_IsSubThreshold_AcrossRangeAndAlgorithms()
        {
            // The BlendChanged dedupe quantizes to integer Kelvin, so 1 K is the smallest
            // realizable step. If this ever exceeds the 0.5 ΔE ITP ceiling anywhere in the
            // supported range, the documented contingency (mired-grid dedupe instead of
            // integer Kelvin) must be implemented — this test is the tripwire.
            foreach (var algorithm in Algorithms)
            foreach (bool preserve in new[] { false, true })
            foreach (int kelvin in new[] { 1900, 2000, 2200, 2700, 3400, 4500, 6499 })
            {
                double dE = StepDeltaE(kelvin, kelvin + 1, algorithm, preserve);
                Assert.True(dE <= JndPacedFade.TargetStepItp,
                    $"1 K step at {kelvin}K ({algorithm}, preserve={preserve}) is {dE:F3} ΔE ITP — " +
                    "integer-Kelvin quantization now exceeds the JND ceiling; implement the mired-grid dedupe contingency");
            }
        }

        // ---- Duration promise & tick clamps --------------------------------------------------

        [Fact]
        public void FadeTrajectory_IsUnchangedByPacing()
        {
            // Pacing shapes the sampling cadence only; the mired-linear trajectory is
            // recomputed from wall clock, so position at any progress is pacing-independent.
            Assert.Equal(6500, NightModeService.InterpolateKelvinInMired(6500, 2700, 0.0));
            Assert.Equal(2700, NightModeService.InterpolateKelvinInMired(6500, 2700, 1.0));
            int mid = NightModeService.InterpolateKelvinInMired(6500, 2700, 0.5);
            double midMired = (1e6 / 6500 + 1e6 / 2700) / 2.0;
            Assert.Equal(1e6 / midMired, mid, 0);
        }

        [Fact]
        public void CalculateFadeTick_FourArg_RespectsClampsAndDegenerates()
        {
            // Degenerate inputs return the idle-side clamp.
            Assert.Equal(500.0, NightModeService.CalculateFadeTickMilliseconds(6500, 6500, 30, 0.5));
            Assert.Equal(500.0, NightModeService.CalculateFadeTickMilliseconds(6500, 2700, 0, 0.5));
            Assert.Equal(500.0, NightModeService.CalculateFadeTickMilliseconds(0, 2700, 30, 0.5));

            // A huge step ceiling clamps at MaxFadeTickMs; a tiny one at MinFadeTickMs.
            Assert.Equal(500.0, NightModeService.CalculateFadeTickMilliseconds(6500, 6400, 240, 2.0));
            Assert.Equal(250.0, NightModeService.CalculateFadeTickMilliseconds(6500, 1900, 0.05, 0.01), 6);

            // Non-finite/invalid ceiling falls back to the historical 0.05 heuristic.
            Assert.Equal(
                NightModeService.CalculateFadeTickMilliseconds(6500, 2700, 30),
                NightModeService.CalculateFadeTickMilliseconds(6500, 2700, 30, double.NaN));
        }

        [Fact]
        public void CalculateFadeTick_ThreeArgOverload_MatchesFallbackContract()
        {
            Assert.Equal(
                NightModeService.CalculateFadeTickMilliseconds(6500, 2700, 30, JndPacedFade.FallbackStepMired),
                NightModeService.CalculateFadeTickMilliseconds(6500, 2700, 30));
        }

        // ---- Reference values ----------------------------------------------------------------

        [Fact]
        public void AdaptedWhite_At6500K_IsReferenceWhite()
        {
            var white = JndPacedFade.AdaptedWhiteXyz(
                6500, NightModeAlgorithm.Perceptual, 0.8, false, false);
            Assert.Equal(JndPacedFade.ReferenceWhiteNits, white.Y, 6);
        }

        [Fact]
        public void PacedStep_TimesDerivative_ApproximatesTargetItp()
        {
            // Internal consistency: stepping by exactly ComputeMaxStepMired at a point where
            // the clamp is not binding lands near the 0.5 ΔE ITP target (within linearization
            // error of the finite difference).
            const int kelvin = 3000;
            double step = JndPacedFade.ComputeMaxStepMired(
                kelvin, NightModeAlgorithm.AccurateCIE1931, 0.8, false, false);
            if (step is <= 0.01 or >= 2.0) return; // clamp binding — nothing to check

            double mired = 1e6 / kelvin;
            int next = (int)Math.Round(1e6 / (mired + step));
            double dE = StepDeltaE(kelvin, next, NightModeAlgorithm.AccurateCIE1931, false);

            Assert.InRange(dE, 0.3, 0.7);
        }

        private static double StepDeltaE(
            int fromKelvin, int toKelvin, NightModeAlgorithm algorithm, bool preserve)
        {
            var a = JndPacedFade.AdaptedWhiteXyz(fromKelvin, algorithm, 0.8, false, preserve);
            var b = JndPacedFade.AdaptedWhiteXyz(toKelvin, algorithm, 0.8, false, preserve);
            return CalibrationVerifier.DeltaEItp(a, b);
        }
    }
}
