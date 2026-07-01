namespace HDRGammaController.Core
{
    public enum NightModeAlgorithm
    {
        /// <summary>
        /// Standard approximation (Tanner Helland's algorithm).
        /// Provides a pleasant, warm, photo-realistic tint.
        /// </summary>
        Standard,
        
        /// <summary>
        /// Physically accurate conversion based on the CIE 1931 color space.
        /// Simulates a true black-body radiator color.
        /// </summary>
        AccurateCIE1931,
        
        /// <summary>
        /// Specifically targets blue light reduction (460-480nm range) for circadian rhythm.
        /// Less color accurate, but potentially better for sleep.
        /// </summary>
        BlueReduction,

        /// <summary>
        /// Colorimetric Planckian white point (same locus as <see cref="AccurateCIE1931"/>)
        /// softened by an incomplete degree of chromatic adaptation (Von Kries D &lt; 1). Real
        /// adaptation is never 100%, so easing the shift toward neutral keeps the brightest
        /// channel at 1.0 (no gamut clipping / colour cast) while preserving far more of the
        /// blue/green content than a full colorimetric shift — the goal being to cut blue light
        /// while disturbing colour as little as possible. Applied as a per-channel curve, which
        /// is all the 1D display LUT can represent. This is the recommended default.
        /// </summary>
        Perceptual,

        /// <summary>
        /// Maximum circadian protection: an amber/red curve that removes blue and deeply cuts
        /// green (blue carries most of a display's melanopic weight). Not colour-accurate — a
        /// deliberate deep-evening mode. See <see cref="ColorAdjustments.GetUltraNightMultipliers"/>.
        /// </summary>
        UltraNight
    }
}
