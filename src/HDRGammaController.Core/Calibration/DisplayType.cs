namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Display technology types for colorimeter measurement configuration.
    /// These map to ArgyllCMS spotread -y flag values.
    /// </summary>
    public enum DisplayType
    {
        /// <summary>LCD with white LED backlight (most common modern display).</summary>
        LcdLed,

        /// <summary>OLED display (self-emissive pixels).</summary>
        Oled,

        /// <summary>LCD with wide gamut white LED backlight.</summary>
        LcdWideGamut,

        /// <summary>LCD with CCFL backlight (older displays).</summary>
        LcdCcfl,

        /// <summary>CRT display (cathode ray tube).</summary>
        Crt,

        /// <summary>Plasma display.</summary>
        Plasma,

        /// <summary>DLP projector.</summary>
        Projector
    }

    /// <summary>
    /// Extension methods for DisplayType.
    /// </summary>
    public static class DisplayTypeExtensions
    {
        /// <summary>
        /// Gets the ArgyllCMS spotread -y flag value for this display type.
        /// </summary>
        public static string ToSpotreadFlag(this DisplayType type) => type switch
        {
            DisplayType.LcdLed => "e",      // LCD with white LED backlight
            DisplayType.Oled => "o",        // OLED (since Argyll V2.2.0)
            DisplayType.LcdWideGamut => "b", // LCD with wide gamut LED backlight
            DisplayType.LcdCcfl => "l",     // LCD with CCFL backlight
            DisplayType.Crt => "c",         // CRT
            DisplayType.Plasma => "m",      // Plasma
            DisplayType.Projector => "p",   // DLP projector
            _ => "e"                        // Default to LED LCD
        };

        /// <summary>
        /// Gets a user-friendly display name for this display type.
        /// </summary>
        public static string ToDisplayName(this DisplayType type) => type switch
        {
            DisplayType.LcdLed => "LCD (LED backlight)",
            DisplayType.Oled => "OLED",
            DisplayType.LcdWideGamut => "LCD (Wide Gamut LED)",
            DisplayType.LcdCcfl => "LCD (CCFL backlight)",
            DisplayType.Crt => "CRT",
            DisplayType.Plasma => "Plasma",
            DisplayType.Projector => "Projector (DLP)",
            _ => "Unknown"
        };

        /// <summary>
        /// Gets a description of this display type.
        /// </summary>
        public static string GetDescription(this DisplayType type) => type switch
        {
            DisplayType.LcdLed => "Most modern monitors and TVs",
            DisplayType.Oled => "Self-emissive pixels, deep blacks",
            DisplayType.LcdWideGamut => "Professional monitors with extended color",
            DisplayType.LcdCcfl => "Older LCD monitors (pre-2012)",
            DisplayType.Crt => "Old tube monitors",
            DisplayType.Plasma => "Plasma TVs",
            DisplayType.Projector => "DLP projectors",
            _ => ""
        };
    }
}
