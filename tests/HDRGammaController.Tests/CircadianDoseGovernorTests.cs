using System;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Dose-based circadian governor (roadmap 3.2): the ceiling is a hard constraint, the
    /// solution is the perceptually-cheapest compliant state, and the governor never
    /// brightens, cools, or fires when the schedule already complies.
    /// </summary>
    public class CircadianDoseGovernorTests
    {
        private static CcssMelanopicEstimator.CcssSpectra Spectra => MelanopicCalculator.GenericPrimaries();

        private static CircadianDoseGovernor.Solution Solve(
            int kelvin, double brightness, double ceiling,
            NightModeAlgorithm algorithm = NightModeAlgorithm.Perceptual,
            bool preserve = false)
            => CircadianDoseGovernor.Solve(
                Spectra, algorithm, 0.8, useUltraWarmMode: false, preserve,
                kelvin, brightness, sdrWhiteNits: 200.0,
                viewingSolidAngleSr: 0.20, ceilingMelLux: ceiling);

        private static double EdiOf(int kelvin, double brightness)
        {
            double scale = (kelvin - 6500) / 70.0;
            var gains = ColorAdjustments.GetTemperatureMultipliers(
                scale, NightModeAlgorithm.Perceptual, false, 0.8, null, NightBasis.Srgb);
            return MelanopicCalculator.Compute(
                Spectra, gains, 200.0 * brightness / 100.0, 0.20).MelanopicEdiLux;
        }

        [Fact]
        public void CompliantSchedule_IsReturnedUnchanged()
        {
            // A very warm dim state is far below a generous ceiling.
            var solution = Solve(kelvin: 2200, brightness: 30, ceiling: 100.0);

            Assert.False(solution.Adjusted);
            Assert.True(solution.CeilingMet);
            Assert.Equal(2200, solution.Kelvin);
            Assert.Equal(30, solution.BrightnessPercent);
        }

        [Fact]
        public void ViolatingSchedule_SolutionMeetsCeiling()
        {
            var solution = Solve(kelvin: 3400, brightness: 100, ceiling: 10.0);

            Assert.True(solution.Adjusted);
            Assert.True(solution.CeilingMet, "a 10 mel-lx ceiling is reachable on a 200-nit panel");
            Assert.True(solution.MelanopicEdiLux <= 10.0 + 0.1,
                $"solution dose {solution.MelanopicEdiLux:F2} must respect the ceiling");

            // Independent recomputation of the solution's dose agrees.
            Assert.Equal(EdiOf(solution.Kelvin, solution.BrightnessPercent),
                solution.MelanopicEdiLux, 1);
        }

        [Fact]
        public void Governor_NeverCools_NeverBrightens()
        {
            var solution = Solve(kelvin: 3000, brightness: 80, ceiling: 5.0);

            Assert.True(solution.Kelvin <= 3000);
            Assert.True(solution.BrightnessPercent <= 80.0);
        }

        [Fact]
        public void SolutionIsPerceptuallyCheapest_AmongCompliantCandidates()
        {
            // Spot-check optimality: no compliant candidate on the search grid beats the
            // returned one by more than numerical slack.
            var solution = Solve(kelvin: 4500, brightness: 100, ceiling: 15.0);
            Assert.True(solution.CeilingMet);

            var vcWhiteGains = ColorAdjustments.GetTemperatureMultipliers(
                0, NightModeAlgorithm.Perceptual, false, 0.8, null, NightBasis.Srgb);
            var conditionsWhiteRgb = ColorMath.LinearSrgbToXyz(new LinearRgb(1, 1, 1));
            var vc = Cam16Ucs.DisplayConditions(new CieXyz(
                conditionsWhiteRgb.X * 200, conditionsWhiteRgb.Y * 200, conditionsWhiteRgb.Z * 200));

            CieXyz StateXyz(int kelvin, double brightness)
            {
                double scale = (kelvin - 6500) / 70.0;
                var g = ColorAdjustments.GetTemperatureMultipliers(
                    scale, NightModeAlgorithm.Perceptual, false, 0.8, null, NightBasis.Srgb);
                var xyz = ColorMath.LinearSrgbToXyz(new LinearRgb(g.R, g.G, g.B));
                double nits = 200.0 * brightness / 100.0;
                return new CieXyz(xyz.X * nits, xyz.Y * nits, xyz.Z * nits);
            }

            var scheduled = Cam16Ucs.ToJabPrime(StateXyz(4500, 100), vc);
            double bestFound = Cam16Ucs.DeltaEPrime(scheduled,
                Cam16Ucs.ToJabPrime(StateXyz(solution.Kelvin, solution.BrightnessPercent), vc));

            for (int kelvin = 4500; kelvin >= 1900; kelvin -= 250)
            {
                double edi = EdiOf(kelvin, 100);
                if (edi <= 0) continue;
                double brightness = Math.Clamp(100.0 * 15.0 / edi, 10.0, 100.0);
                if (EdiOf(kelvin, brightness) > 15.0 + 0.1) continue; // not compliant
                double cost = Cam16Ucs.DeltaEPrime(scheduled,
                    Cam16Ucs.ToJabPrime(StateXyz(kelvin, brightness), vc));
                Assert.True(cost >= bestFound - 1.0,
                    $"candidate {kelvin}K/{brightness:F0}% costs {cost:F2}, beating chosen {bestFound:F2}");
            }
        }

        [Fact]
        public void UnreachableCeiling_BestEffort_MaximallyReducesDose()
        {
            // An absurd ceiling no display state can reach: the governor floors out at the
            // warm/dim corner and says so honestly.
            var solution = Solve(kelvin: 6400, brightness: 100, ceiling: 0.5);

            Assert.True(solution.Adjusted);
            Assert.False(solution.CeilingMet);
            Assert.True(solution.Kelvin <= 2000);
            Assert.Equal(CircadianDoseGovernor.MinBrightnessPercent, solution.BrightnessPercent, 1);
        }

        [Fact]
        public void MildViolation_PrefersSmallAdjustment()
        {
            // Just over the ceiling: the fix should be gentle (small ΔE′), not a plunge to
            // the warm/dim corner.
            double scheduledEdi = EdiOf(3000, 60);
            var solution = Solve(kelvin: 3000, brightness: 60, ceiling: scheduledEdi * 0.85);

            Assert.True(solution.Adjusted);
            Assert.True(solution.CeilingMet);
            Assert.True(solution.DeltaEPrimeFromScheduled < 15.0,
                $"a 15% dose trim should cost little visibility, got ΔE′ {solution.DeltaEPrimeFromScheduled:F1}");
            Assert.True(solution.Kelvin >= 2400, "mild violation should not slam to minimum kelvin");
        }

        [Fact]
        public void Solve_IsMemoized()
        {
            var a = Solve(kelvin: 3800, brightness: 90, ceiling: 12.0);
            var b = Solve(kelvin: 3800, brightness: 90, ceiling: 12.0);
            Assert.Same(a, b);
        }

        [Fact]
        public void Solve_QuantizesKelvin_SoFadesHitTheCache()
        {
            // Nearby kelvins within a 25 K bucket must share the cached solution — this is
            // what keeps a night-mode fade (kelvin changing every step) from re-running the
            // CAM16 scan per tick on the apply/UI thread (the freeze this prevents).
            var a = Solve(kelvin: 3810, brightness: 90, ceiling: 12.0);
            var b = Solve(kelvin: 3799, brightness: 90, ceiling: 12.0); // same 3800 bucket
            Assert.Same(a, b);

            // A kelvin a full bucket away is a different (but still valid) solve.
            var far = Solve(kelvin: 3700, brightness: 90, ceiling: 12.0);
            Assert.NotSame(a, far);
        }

        [Fact]
        public void InvalidCeiling_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Solve(3000, 80, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Solve(3000, 80, double.NaN));
        }

        [Fact]
        public void ClampMelanopicCeiling_ZeroMeansOff()
        {
            Assert.Equal(0.0, NightModeSettings.ClampMelanopicCeiling(0));
            Assert.Equal(0.0, NightModeSettings.ClampMelanopicCeiling(-5));
            Assert.Equal(0.0, NightModeSettings.ClampMelanopicCeiling(double.NaN));
            Assert.Equal(10.0, NightModeSettings.ClampMelanopicCeiling(10.0));
            Assert.Equal(1000.0, NightModeSettings.ClampMelanopicCeiling(99999));
        }

        [Fact]
        public void SettingsRoundTrip_CarriesCeiling()
        {
            var settings = new NightModeSettings { MelanopicEdiCeiling = 12.5 };
            var data = NightModeSettingsData.FromNightModeSettings(settings);
            Assert.Equal(12.5, data.MelanopicEdiCeiling);
            Assert.Equal(12.5, data.ToNightModeSettings().MelanopicEdiCeiling);
            Assert.Equal(12.5, NightModeService.CloneSettings(settings).MelanopicEdiCeiling);
        }
    }
}
