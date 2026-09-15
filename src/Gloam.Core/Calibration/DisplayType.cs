namespace Gloam.Core.Calibration
{
    /// <summary>
    /// Display technology types for colorimeter measurement configuration.
    /// Resolved to an ArgyllCMS spotread -y selector per instrument by
    /// <see cref="SpotreadDisplayTypeTable"/>.
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
        /// True for technologies Argyll measures in refresh mode (CRT, plasma, DLP). OLED and
        /// every LCD are non-refresh, matching Argyll's disptechs.c table.
        /// </summary>
        /// <remarks>
        /// The spotread <c>-y</c> selector for a display type is NOT a fixed letter: Argyll
        /// builds the table per instrument at run time and the technology letters only exist
        /// when matching corrections are installed. Use
        /// <see cref="SpotreadDisplayTypeTable.Resolve"/> with the table parsed from the
        /// instrument's usage output.
        /// </remarks>
        public static bool IsRefreshTechnology(this DisplayType type) => type switch
        {
            DisplayType.Crt => true,
            DisplayType.Plasma => true,
            DisplayType.Projector => true,
            _ => false
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
