namespace HDRGammaController.Core
{
    /// <summary>
    /// How HDR pixels above diffuse SDR white should participate in night mode.
    /// </summary>
    public enum NightHdrHighlightPolicy
    {
        /// <summary>Keep a modest amount of warmth while retaining most highlight colour intent.</summary>
        Comfort = 0,

        /// <summary>Preserve the creative grade by fading warmth out of HDR highlights.</summary>
        Creative = 1,

        /// <summary>Keep the spectral shift through the HDR range; selected automatically by a dose ceiling.</summary>
        DoseBound = 2
    }
}
