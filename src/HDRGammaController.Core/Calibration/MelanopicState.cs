using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// One melanopic evaluation of the current screen state (roadmap 3.1).
    /// </summary>
    /// <param name="MelDerState">Melanopic D65 efficacy ratio of the CURRENT emitted SPD.</param>
    /// <param name="MelDerBaseline">Mel-DER of the panel's unshifted (6500K identity) white.</param>
    /// <param name="RelativePhotopicLuminance">Photopic luminance of the state relative to
    /// full white (spectral shape only, brightness excluded).</param>
    /// <param name="ScreenLuminanceNits">Absolute photopic luminance of the state.</param>
    /// <param name="MelanopicEdiLux">Melanopic EDI at the assumed viewing geometry — the ONLY
    /// number here that depends on the solid-angle assumption.</param>
    /// <param name="ReductionFraction">Melanopic reduction vs the 6500K baseline at the same
    /// brightness: 1 − (Yrel·melDER_state)/melDER_baseline. Geometry-free: Ω, brightness and
    /// white level all cancel — this is the headline number.</param>
    /// <param name="HasSpectra">False when the generic-primary fallback supplied the SPDs.</param>
    public readonly record struct MelanopicReading(
        double MelDerState,
        double MelDerBaseline,
        double RelativePhotopicLuminance,
        double ScreenLuminanceNits,
        double MelanopicEdiLux,
        double ReductionFraction,
        bool HasSpectra);

    /// <summary>
    /// Melanopic-EDI math over CCSS channel spectra. Pure — no I/O, no clocks. The honest
    /// split: % reduction vs 6500K is ratiometric and immune to every assumption except the
    /// CCSS itself; absolute mel-EDI additionally assumes a viewing geometry (screen fills
    /// Ω_eff steradian at the cornea: E_v ≈ L·Ω_eff), which callers surface as a setting and
    /// a dominant uncertainty term, never as hidden truth.
    /// </summary>
    public static class MelanopicCalculator
    {
        /// <summary>Default effective solid angle: ≈ a 27″ 16:9 panel at ~60 cm, projected.</summary>
        public const double DefaultViewingSolidAngleSr = 0.20;

        /// <summary>
        /// Computes the melanopic state for the current per-channel gains.
        /// </summary>
        /// <param name="spectra">Panel channel SPDs (from CCSS or the generic fallback).</param>
        /// <param name="linearGains">Linear-light per-channel coefficients of the emitted
        /// state. These are normally white-shape gains and may also include aggregate content;
        /// brightness magnitude must stay outside and be passed via
        /// <paramref name="whiteLuminanceNits"/>.</param>
        /// <param name="whiteLuminanceNits">Absolute reference luminance at the current
        /// brightness (normally diffuse white; an HDR safety envelope may use full-field
        /// luminance).</param>
        /// <param name="viewingSolidAngleSr">Assumed Ω_eff for the corneal-illuminance step.</param>
        /// <param name="hasSpectra">False when using synthesized generic primaries.</param>
        public static MelanopicReading Compute(
            CcssMelanopicEstimator.CcssSpectra spectra,
            (double R, double G, double B) linearGains,
            double whiteLuminanceNits,
            double viewingSolidAngleSr = DefaultViewingSolidAngleSr,
            bool hasSpectra = true)
        {
            ArgumentNullException.ThrowIfNull(spectra);
            int n = Math.Min(
                spectra.Wavelengths.Count,
                Math.Min(spectra.Red.Count, Math.Min(spectra.Green.Count, spectra.Blue.Count)));

            // Additive panel: baseline white is exactly the channel sum, so the state SPD
            // and the baseline decompose consistently (the CCSS white row's residual against
            // this sum is carried separately as an uncertainty term). Fuse the four spectral
            // integrations into this one pass: a ceiling solve evaluates dozens of candidates,
            // so materializing two SPDs and traversing them six times was pure memory traffic.
            double gainR = Math.Max(0, linearGains.R);
            double gainG = Math.Max(0, linearGains.G);
            double gainB = Math.Max(0, linearGains.B);
            double melanopicState = 0.0;
            double melanopicBaseline = 0.0;
            double photopicState = 0.0;
            double photopicBaseline = 0.0;
            for (int i = 0; i < n; i++)
            {
                double r = Math.Max(0, spectra.Red[i]);
                double g = Math.Max(0, spectra.Green[i]);
                double b = Math.Max(0, spectra.Blue[i]);
                double state = gainR * r + gainG * g + gainB * b;
                double baseline = r + g + b;
                double wavelength = spectra.Wavelengths[i];
                double dLambda;
                if (n == 1)
                    dLambda = 1.0;
                else if (i == 0)
                    dLambda = Math.Abs((spectra.Wavelengths[1] - wavelength) * 0.5);
                else if (i == n - 1)
                    dLambda = Math.Abs((wavelength - spectra.Wavelengths[n - 2]) * 0.5);
                else
                    dLambda = Math.Abs((spectra.Wavelengths[i + 1] - spectra.Wavelengths[i - 1]) * 0.5);

                double melWeight = CcssMelanopicEstimator.MelanopicSensitivityAt(wavelength) * dLambda;
                double photoWeight = CcssMelanopicEstimator.PhotopicSensitivityAt(wavelength) * dLambda;
                melanopicState += state * melWeight;
                melanopicBaseline += baseline * melWeight;
                photopicState += state * photoWeight;
                photopicBaseline += baseline * photoWeight;
            }

            double melDerState = n >= 2
                ? CcssMelanopicEstimator.MelanopicDerFromIntegrals(melanopicState, photopicState)
                : double.NaN;
            double melDerBaseline = n >= 2
                ? CcssMelanopicEstimator.MelanopicDerFromIntegrals(melanopicBaseline, photopicBaseline)
                : double.NaN;
            double yRel = photopicBaseline > 0 ? photopicState / photopicBaseline : 0.0;

            double screenNits = Math.Max(0, whiteLuminanceNits) * yRel;
            double eV = screenNits * Math.Max(0, viewingSolidAngleSr); // corneal lux, E_v ≈ L·Ω
            double melEdi = double.IsFinite(melDerState) ? eV * melDerState : double.NaN;

            double reduction =
                double.IsFinite(melDerState) && double.IsFinite(melDerBaseline) && melDerBaseline > 0
                    ? 1.0 - (yRel * melDerState) / melDerBaseline
                    : double.NaN;

            return new MelanopicReading(
                melDerState, melDerBaseline, yRel, screenNits, melEdi, reduction, hasSpectra);
        }

        /// <summary>
        /// Fallback channel spectra when no CCSS is available: Gaussian primaries at typical
        /// LED-LCD peaks, luminance-weighted so their sum approximates a D65-ish white. A
        /// deliberately generic estimate — callers pair it with the wide GenericOrOtherPanel
        /// uncertainty and a "load a CCSS" prompt, and % reduction remains meaningful because
        /// it is a ratio through the same assumed spectra.
        /// </summary>
        private static readonly Lazy<CcssMelanopicEstimator.CcssSpectra> GenericSrgbPrimaries =
            new(() => CreateGenericPrimaries(wideGamut: false));
        private static readonly Lazy<CcssMelanopicEstimator.CcssSpectra> GenericWideGamutPrimaries =
            new(() => CreateGenericPrimaries(wideGamut: true));

        /// <summary>Returns a shared immutable-by-contract fallback spectrum. Callers must not
        /// mutate its backing lists; reuse avoids rebuilding five 81-sample arrays per apply.</summary>
        public static CcssMelanopicEstimator.CcssSpectra GenericPrimaries(bool wideGamut = false)
            => wideGamut ? GenericWideGamutPrimaries.Value : GenericSrgbPrimaries.Value;

        private static CcssMelanopicEstimator.CcssSpectra CreateGenericPrimaries(bool wideGamut)
        {
            var wavelengths = Enumerable.Range(0, 81).Select(i => 380.0 + i * 5.0).ToArray();

            // (peak nm, full width at half max nm, relative amplitude). Wide-gamut panels
            // (QD/OLED) have narrower, deeper primaries.
            (double Peak, double Fwhm, double Amp) red = wideGamut ? (630, 30, 1.00) : (615, 60, 1.00);
            (double Peak, double Fwhm, double Amp) green = wideGamut ? (530, 35, 1.05) : (545, 70, 1.05);
            (double Peak, double Fwhm, double Amp) blue = wideGamut ? (455, 20, 0.60) : (455, 25, 0.60);

            double[] Gauss((double Peak, double Fwhm, double Amp) p) =>
                wavelengths.Select(w =>
                {
                    double sigma = p.Fwhm / 2.3548;
                    double d = (w - p.Peak) / sigma;
                    return p.Amp * Math.Exp(-0.5 * d * d);
                }).ToArray();

            var r = Gauss(red);
            var g = Gauss(green);
            var b = Gauss(blue);
            var w = new double[wavelengths.Length];
            for (int i = 0; i < w.Length; i++) w[i] = r[i] + g[i] + b[i];

            return new CcssMelanopicEstimator.CcssSpectra(
                wavelengths, w, r, g, b,
                wideGamut ? "Generic wide-gamut primaries (estimate)" : "Generic sRGB-class primaries (estimate)",
                WhiteResidualFraction: 0.0);
        }
    }
}
