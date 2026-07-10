using System;
using System.Collections.Concurrent;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Dose-based circadian scheduling (roadmap 3.2): given a melanopic-EDI ceiling, finds
    /// the (kelvin, brightness) operating point that satisfies it with the LEAST VISIBLE
    /// departure from the scheduled state — visibility measured as CAM16-UCS ΔE′ under
    /// dim-surround desktop viewing conditions, which correctly prices luminance changes
    /// against chromaticity changes (ΔE ITP-style metrics under-price the adaptation
    /// benefit of dimming; a pure-kelvin rule ignores dimming entirely).
    /// </summary>
    /// <remarks>
    /// The governor never brightens and never cools: candidates range from the scheduled
    /// kelvin down to 1900 K and from the scheduled brightness down to a 10% floor. For a
    /// fixed kelvin, mel-EDI is linear in white luminance, so the minimum dimming that
    /// meets the ceiling is computed in closed form rather than searched. Results are
    /// memoized (the apply path re-evaluates on every fade tick).
    /// </remarks>
    public static class CircadianDoseGovernor
    {
        public const double MinBrightnessPercent = 10.0;
        private const int KelvinStep = 50;
        private const int MinKelvin = 1900;
        private const int KelvinCacheBucket = 25;

        public sealed record Solution(
            int Kelvin,
            double BrightnessPercent,
            double MelanopicEdiLux,
            double DeltaEPrimeFromScheduled,
            bool CeilingMet,
            bool Adjusted);

        private sealed record CacheKey(
            int ScheduledKelvin, int BrightnessKey, NightModeAlgorithm Algorithm,
            int StrengthKey, bool UltraWarm, bool Preserve, double CeilingKey,
            int WhiteNitsKey, double OmegaKey, string SpectraSource);

        private static readonly ConcurrentDictionary<CacheKey, Solution> Cache = new();
        private const int MaxCacheEntries = 512;

        /// <summary>
        /// Solves for the ceiling-respecting operating point. Returns the scheduled state
        /// unmodified (Adjusted=false) when it already meets the ceiling.
        /// </summary>
        public static Solution Solve(
            CcssMelanopicEstimator.CcssSpectra spectra,
            NightModeAlgorithm algorithm,
            double perceptualStrength,
            bool useUltraWarmMode,
            bool preserveLuminance,
            int scheduledKelvin,
            double scheduledBrightnessPercent,
            double sdrWhiteNits,
            double viewingSolidAngleSr,
            double ceilingMelLux,
            NightMelanopicCoefficients? melanopic = null)
        {
            ArgumentNullException.ThrowIfNull(spectra);
            scheduledKelvin = Math.Clamp(scheduledKelvin, MinKelvin, 10000);
            scheduledBrightnessPercent = Math.Clamp(scheduledBrightnessPercent, MinBrightnessPercent, 100.0);
            sdrWhiteNits = Math.Clamp(sdrWhiteNits, 1.0, 10000.0);
            if (!double.IsFinite(ceilingMelLux) || ceilingMelLux <= 0)
                throw new ArgumentOutOfRangeException(nameof(ceilingMelLux));

            // Quantize the scheduled inputs so a night-mode fade (which changes kelvin every
            // step) mostly HITS the memo instead of re-running the CAM16 scan per tick on the
            // apply/UI thread. 25 K and 2% steps are imperceptible in the governed trajectory.
            scheduledKelvin = (int)(Math.Round(scheduledKelvin / (double)KelvinCacheBucket) * KelvinCacheBucket);
            scheduledKelvin = Math.Clamp(scheduledKelvin, MinKelvin, 10000);
            scheduledBrightnessPercent = Math.Round(scheduledBrightnessPercent / 2.0) * 2.0;

            var key = new CacheKey(
                scheduledKelvin, (int)Math.Round(scheduledBrightnessPercent), algorithm,
                (int)Math.Round(perceptualStrength * 100), useUltraWarmMode, preserveLuminance,
                Math.Round(ceilingMelLux, 2), (int)Math.Round(sdrWhiteNits),
                Math.Round(viewingSolidAngleSr, 3), spectra.SourceName);
            if (Cache.TryGetValue(key, out var cached))
                return cached;

            var solution = SolveCore(spectra, algorithm, perceptualStrength, useUltraWarmMode,
                preserveLuminance, scheduledKelvin, scheduledBrightnessPercent, sdrWhiteNits,
                viewingSolidAngleSr, ceilingMelLux, melanopic);

            if (Cache.Count >= MaxCacheEntries) Cache.Clear();
            Cache[key] = solution;
            return solution;
        }

        private static Solution SolveCore(
            CcssMelanopicEstimator.CcssSpectra spectra,
            NightModeAlgorithm algorithm, double strength, bool ultraWarm, bool preserve,
            int scheduledKelvin, double scheduledBrightness, double sdrWhiteNits,
            double omega, double ceiling, NightMelanopicCoefficients? melanopic)
        {
            // At the white point every dimming curve reduces to brightness/100 (see
            // ApplyDimming: value=1 → brightness), so white luminance is linear in the
            // brightness percent regardless of the perceptual/linear dimming choice.
            double WhiteNitsAt(double brightnessPercent) => sdrWhiteNits * brightnessPercent / 100.0;

            MelanopicReading ReadingAt(int kelvin, double brightnessPercent)
                => MelanopicCalculator.Compute(
                    spectra, GainsAt(kelvin), WhiteNitsAt(brightnessPercent), omega, hasSpectra: true);

            (double R, double G, double B) GainsAt(int kelvin)
            {
                double scale = (kelvin - 6500) / 70.0;
                var m = ColorAdjustments.GetTemperatureMultipliers(
                    scale, algorithm, ultraWarm, strength, melanopic, NightBasis.Srgb);
                if (preserve && algorithm != NightModeAlgorithm.UltraNight)
                {
                    // Nominal ceiling for the estimate; per-monitor exactness is not
                    // warranted for a scheduling decision (same convention as JndPacedFade).
                    m = ColorAdjustments.RescaleToConstantLuminance(m, NightBasis.Srgb, 2.0, 1.0);
                }
                return m;
            }

            CieXyz StateXyz(int kelvin, double brightnessPercent)
            {
                var g = GainsAt(kelvin);
                var xyz = ColorMath.LinearSrgbToXyz(new LinearRgb(g.R, g.G, g.B));
                double nits = WhiteNitsAt(brightnessPercent);
                return new CieXyz(xyz.X * nits, xyz.Y * nits, xyz.Z * nits);
            }

            var scheduledReading = ReadingAt(scheduledKelvin, scheduledBrightness);
            if (scheduledReading.MelanopicEdiLux <= ceiling)
            {
                return new Solution(scheduledKelvin, scheduledBrightness,
                    scheduledReading.MelanopicEdiLux, 0.0, CeilingMet: true, Adjusted: false);
            }

            // Appearance reference: the SCHEDULED state (the look the user asked for), under
            // viewing conditions anchored to the display's unshifted white at scheduled
            // brightness — shared by every candidate so the comparison is apples-to-apples.
            var conditionsWhite = StateXyz(6500, scheduledBrightness);
            var vc = Cam16Ucs.DisplayConditions(conditionsWhite);
            var scheduledJab = Cam16Ucs.ToJabPrime(StateXyz(scheduledKelvin, scheduledBrightness), vc);

            Solution? best = null;
            for (int kelvin = scheduledKelvin; kelvin >= MinKelvin; kelvin -= KelvinStep)
            {
                // Mel-EDI is linear in white nits at fixed kelvin: solve the minimum dimming
                // in closed form from a probe at the scheduled brightness.
                var probe = ReadingAt(kelvin, scheduledBrightness);
                if (!double.IsFinite(probe.MelanopicEdiLux) || probe.MelanopicEdiLux <= 0)
                    continue;

                double neededBrightness = scheduledBrightness * ceiling / probe.MelanopicEdiLux;
                bool feasible = neededBrightness >= MinBrightnessPercent;
                double brightness = Math.Min(scheduledBrightness,
                    Math.Max(neededBrightness, MinBrightnessPercent));

                double edi = probe.MelanopicEdiLux * brightness / scheduledBrightness;
                double cost = Cam16Ucs.DeltaEPrime(
                    scheduledJab, Cam16Ucs.ToJabPrime(StateXyz(kelvin, brightness), vc));

                var candidate = new Solution(kelvin, Math.Round(brightness, 1), edi, cost,
                    CeilingMet: feasible || edi <= ceiling + 1e-9, Adjusted: true);

                // Prefer ceiling-met candidates; among them, minimum ΔE′. Among unmet ones
                // (ceiling unreachable), minimum residual dose.
                if (best == null) { best = candidate; continue; }
                bool better = candidate.CeilingMet == best.CeilingMet
                    ? (candidate.CeilingMet
                        ? candidate.DeltaEPrimeFromScheduled < best.DeltaEPrimeFromScheduled
                        : candidate.MelanopicEdiLux < best.MelanopicEdiLux)
                    : candidate.CeilingMet;
                if (better) best = candidate;
            }

            return best ?? new Solution(scheduledKelvin, scheduledBrightness,
                scheduledReading.MelanopicEdiLux, 0.0, CeilingMet: false, Adjusted: false);
        }
    }
}
