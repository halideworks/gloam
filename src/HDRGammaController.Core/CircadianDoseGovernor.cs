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
    /// fixed kelvin the constraint is monotone after the content estimate is lifted to a
    /// conservative dimming-domain envelope, so maximum compliant brightness is found by
    /// deterministic bisection. Results are memoized (the apply path re-evaluates on every
    /// fade tick).
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
            double Strength, bool UltraWarm, bool Preserve, double Ceiling,
            double DoseReferenceNits, double Omega, CcssMelanopicEstimator.CcssSpectra Spectra,
            NightMelanopicCoefficients? Melanopic, NightBasis Basis,
            double LuminanceCeiling, bool LinearBrightness,
            int ContentRKey, int ContentGKey, int ContentBKey);

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
            NightMelanopicCoefficients? melanopic = null,
            NightBasis basis = NightBasis.Srgb,
            double luminanceCeiling = 1.0,
            bool useLinearBrightness = false,
            (double R, double G, double B)? contentLinearRgb = null,
            double? doseReferenceNits = null)
        {
            ArgumentNullException.ThrowIfNull(spectra);
            scheduledKelvin = Math.Clamp(scheduledKelvin, MinKelvin, 10000);
            scheduledBrightnessPercent = double.IsFinite(scheduledBrightnessPercent)
                ? Math.Clamp(scheduledBrightnessPercent, MinBrightnessPercent, 100.0)
                : 100.0;
            sdrWhiteNits = double.IsFinite(sdrWhiteNits)
                ? Math.Clamp(sdrWhiteNits, 1.0, 10000.0)
                : 200.0;
            double doseNits = doseReferenceNits.HasValue && double.IsFinite(doseReferenceNits.Value)
                ? Math.Clamp(doseReferenceNits.Value, 1.0, 10000.0)
                : sdrWhiteNits;
            perceptualStrength = NightModeSettings.ClampPerceptualStrength(perceptualStrength);
            viewingSolidAngleSr = double.IsFinite(viewingSolidAngleSr)
                ? Math.Clamp(viewingSolidAngleSr, 0.0, 4.0)
                : MelanopicCalculator.DefaultViewingSolidAngleSr;
            if (!double.IsFinite(ceilingMelLux) || ceilingMelLux <= 0)
                throw new ArgumentOutOfRangeException(nameof(ceilingMelLux));
            luminanceCeiling = double.IsFinite(luminanceCeiling)
                ? Math.Clamp(luminanceCeiling, 1.0, 4.0)
                : 1.0;
            var content = contentLinearRgb ?? (1.0, 1.0, 1.0);
            content = (
                double.IsFinite(content.R) ? Math.Clamp(content.R, 0.0, 1.0) : 1.0,
                double.IsFinite(content.G) ? Math.Clamp(content.G, 0.0, 1.0) : 1.0,
                double.IsFinite(content.B) ? Math.Clamp(content.B, 0.0, 1.0) : 1.0);

            // Never let memoization decide compliance. Evaluate the precise scheduled state
            // first; only an actual violation enters the bucketed correction solver below.
            // This closes the near-threshold case where rounding to a slightly warmer/dimmer
            // cache key could otherwise understate dose and return Adjusted=false.
            var exactReading = EvaluateReading(
                spectra, algorithm, perceptualStrength, useUltraWarmMode, preserveLuminance,
                scheduledKelvin, scheduledBrightnessPercent, doseNits,
                viewingSolidAngleSr, melanopic, basis, luminanceCeiling,
                useLinearBrightness, content);
            if (exactReading.MelanopicEdiLux <= ceilingMelLux)
            {
                return new Solution(scheduledKelvin, scheduledBrightnessPercent,
                    exactReading.MelanopicEdiLux, 0.0, CeilingMet: true, Adjusted: false);
            }

            // Floor temperature and brightness so a cached correction can never COOL or
            // BRIGHTEN the requested state. Content is rounded upward so reuse within its
            // 5% bucket can only overestimate dose. The exact check above preserves truly
            // compliant states unchanged.
            scheduledKelvin = scheduledKelvin / KelvinCacheBucket * KelvinCacheBucket;
            scheduledKelvin = Math.Clamp(scheduledKelvin, MinKelvin, 10000);
            scheduledBrightnessPercent = Math.Floor(scheduledBrightnessPercent / 2.0) * 2.0;
            scheduledBrightnessPercent = Math.Max(MinBrightnessPercent, scheduledBrightnessPercent);
            content = (
                Math.Ceiling(content.R * 20.0) / 20.0,
                Math.Ceiling(content.G * 20.0) / 20.0,
                Math.Ceiling(content.B * 20.0) / 20.0);

            var key = new CacheKey(
                scheduledKelvin, (int)Math.Round(scheduledBrightnessPercent), algorithm,
                perceptualStrength, useUltraWarmMode, preserveLuminance,
                ceilingMelLux, doseNits, viewingSolidAngleSr, spectra, melanopic, basis,
                luminanceCeiling, useLinearBrightness,
                (int)Math.Round(content.R * 20), (int)Math.Round(content.G * 20),
                (int)Math.Round(content.B * 20));
            if (Cache.TryGetValue(key, out var cached))
                return cached;

            var solution = SolveCore(spectra, algorithm, perceptualStrength, useUltraWarmMode,
                preserveLuminance, scheduledKelvin, scheduledBrightnessPercent, doseNits,
                viewingSolidAngleSr, ceilingMelLux, melanopic, basis, luminanceCeiling,
                useLinearBrightness, content);

            // The floored bucket itself can comply even though the precise requested state
            // did not. It is still a real correction and must be applied.
            if (!solution.Adjusted)
                solution = solution with { Adjusted = true };

            if (Cache.Count >= MaxCacheEntries) Cache.Clear();
            Cache[key] = solution;
            return solution;
        }

        private static MelanopicReading EvaluateReading(
            CcssMelanopicEstimator.CcssSpectra spectra,
            NightModeAlgorithm algorithm, double strength, bool ultraWarm, bool preserve,
            int kelvin, double brightness, double doseReferenceNits, double omega,
            NightMelanopicCoefficients? melanopic, NightBasis basis,
            double luminanceCeiling, bool useLinearBrightness,
            (double R, double G, double B) content)
        {
            double scale = (kelvin - 6500) / 70.0;
            var gains = ColorAdjustments.GetTemperatureMultipliers(
                scale, algorithm, ultraWarm, strength, melanopic, basis);
            if (preserve && algorithm != NightModeAlgorithm.UltraNight)
            {
                double dimmedWhite = ColorAdjustments.ApplyDimming(1.0, brightness, useLinearBrightness);
                gains = ColorAdjustments.RescaleToConstantLuminance(
                    gains, basis, luminanceCeiling, dimmedWhite);
            }
            var dimmedContent = ConservativeContentAfterDimming(
                content, brightness, useLinearBrightness);
            return MelanopicCalculator.Compute(
                spectra,
                (gains.R * dimmedContent.R, gains.G * dimmedContent.G, gains.B * dimmedContent.B),
                doseReferenceNits * brightness / 100.0, omega, hasSpectra: true);
        }

        private static Solution SolveCore(
            CcssMelanopicEstimator.CcssSpectra spectra,
            NightModeAlgorithm algorithm, double strength, bool ultraWarm, bool preserve,
            int scheduledKelvin, double scheduledBrightness, double doseReferenceNits,
            double omega, double ceiling, NightMelanopicCoefficients? melanopic,
            NightBasis basis, double luminanceCeiling, bool useLinearBrightness,
            (double R, double G, double B) content)
        {
            // At the white point every dimming curve reduces to brightness/100 (see
            // ApplyDimming: value=1 → brightness), so white luminance is linear in the
            // brightness percent regardless of the perceptual/linear dimming choice.
            double WhiteNitsAt(double brightnessPercent) => doseReferenceNits * brightnessPercent / 100.0;

            // For x in [0,1], x^p is largest at the smallest brightness because the app's
            // shadow-lift exponent p rises with brightness. Holding that maximum across the
            // solve is an upper envelope for every candidate. Combined with constant-Y's
            // min(ideal gain, headroom/brightness) form, dose is monotone in brightness and
            // bisection remains both fast and valid.
            var contentEnvelope = ConservativeContentAfterDimming(
                content, MinBrightnessPercent, useLinearBrightness);

            MelanopicReading ReadingAt(int kelvin, double brightnessPercent)
            {
                var gains = GainsAt(kelvin, brightnessPercent);
                return MelanopicCalculator.Compute(
                    spectra,
                    (gains.R * contentEnvelope.R, gains.G * contentEnvelope.G, gains.B * contentEnvelope.B),
                    WhiteNitsAt(brightnessPercent), omega, hasSpectra: true);
            }

            (double R, double G, double B) GainsAt(int kelvin, double brightnessPercent)
            {
                double scale = (kelvin - 6500) / 70.0;
                var m = ColorAdjustments.GetTemperatureMultipliers(
                    scale, algorithm, ultraWarm, strength, melanopic, basis);
                if (preserve && algorithm != NightModeAlgorithm.UltraNight)
                {
                    // Match the actual apply path exactly. In particular, dimming creates
                    // representable headroom, so the white-shape gains are brightness-dependent
                    // and the old closed-form brightness solve was not valid in constant-Y mode.
                    double dimmedWhite = ColorAdjustments.ApplyDimming(
                        1.0, brightnessPercent, useLinearBrightness);
                    m = ColorAdjustments.RescaleToConstantLuminance(
                        m, basis, luminanceCeiling, dimmedWhite);
                }
                return m;
            }

            CieXyz StateXyz(int kelvin, double brightnessPercent)
            {
                var g = GainsAt(kelvin, brightnessPercent);
                var xyz = basis == NightBasis.Rec2020
                    ? ColorMath.LinearRec2020ToXyz(new LinearRgb(g.R, g.G, g.B))
                    : ColorMath.LinearSrgbToXyz(new LinearRgb(g.R, g.G, g.B));
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
                var scheduledProbe = ReadingAt(kelvin, scheduledBrightness);
                if (!double.IsFinite(scheduledProbe.MelanopicEdiLux) || scheduledProbe.MelanopicEdiLux <= 0)
                    continue;

                double brightness;
                if (scheduledProbe.MelanopicEdiLux <= ceiling)
                {
                    brightness = scheduledBrightness;
                }
                else
                {
                    var floorProbe = ReadingAt(kelvin, MinBrightnessPercent);
                    if (!double.IsFinite(floorProbe.MelanopicEdiLux)) continue;
                    if (floorProbe.MelanopicEdiLux > ceiling)
                    {
                        brightness = MinBrightnessPercent;
                    }
                    else
                    {
                        // Maximum compliant brightness. The content upper envelope above and
                        // constant-Y's capped gain make this exact evaluator monotone. Eighteen
                        // iterations resolves far below the 0.1% output precision.
                        double low = MinBrightnessPercent;
                        double high = scheduledBrightness;
                        for (int iteration = 0; iteration < 18; iteration++)
                        {
                            double mid = (low + high) * 0.5;
                            if (ReadingAt(kelvin, mid).MelanopicEdiLux <= ceiling) low = mid;
                            else high = mid;
                        }
                        brightness = low;
                    }
                }

                // Low from the bisection is known compliant. Quantize downward so display
                // precision cannot round that proof point across the hard ceiling.
                brightness = Math.Floor(brightness * 10.0 + 1e-9) / 10.0;
                double edi = ReadingAt(kelvin, brightness).MelanopicEdiLux;
                bool feasible = double.IsFinite(edi) && edi <= ceiling + 1e-9;
                double cost = Cam16Ucs.DeltaEPrime(
                    scheduledJab, Cam16Ucs.ToJabPrime(StateXyz(kelvin, brightness), vc));

                var candidate = new Solution(kelvin, brightness, edi, cost,
                    CeilingMet: feasible, Adjusted: true);

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

        private static (double R, double G, double B) ConservativeContentAfterDimming(
            (double R, double G, double B) meanLinearContent,
            double brightnessPercent,
            bool useLinearBrightness)
        {
            if (useLinearBrightness || brightnessPercent >= 100.0)
                return meanLinearContent;

            // Perceptual dimming maps each channel as brightness·x^p, p < 1. We retain
            // brightness in WhiteNitsAt and need E[x^p] here. x^p is concave, so Jensen
            // gives E[x^p] <= E[x]^p: transforming the captured mean is a rigorous upper
            // bound on emitted content dose, without retaining a histogram or any pixels.
            double brightness = Math.Clamp(brightnessPercent / 100.0, 0.0, 1.0);
            double exponent = 1.0 / (1.0 + (1.0 - brightness) * 0.3);
            return (
                Math.Pow(meanLinearContent.R, exponent),
                Math.Pow(meanLinearContent.G, exponent),
                Math.Pow(meanLinearContent.B, exponent));
        }
    }
}
