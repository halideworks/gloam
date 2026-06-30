using HDRGammaController.Interop;

namespace HDRGammaController.Core
{
    public class MonitorInfo
    {
        public const double DefaultSdrWhiteLevel = 200.0;
        public const double MinSdrWhiteLevel = 40.0;
        public const double MaxSdrWhiteLevel = 1000.0;

        /// <summary>
        /// GDI Device Name (e.g. \\.\DISPLAY1).
        /// </summary>
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// Friendly name from EDID/GDI (e.g. "LG OLED TV").
        /// </summary>
        public string FriendlyName { get; set; } = string.Empty;

        /// <summary>
        /// ID of the specific monitor instance (e.g. \\?\DISPLAY#GSM5B08#...).
        /// Used for persistence.
        /// </summary>
        public string MonitorDevicePath { get; set; } = string.Empty;

        public bool IsHdrCapable { get; set; }
        
        /// <summary>
        /// True if the OS is currently outputting Advanced Color HDR as ST.2084/PQ.
        /// </summary>
        public bool IsHdrActive { get; set; }

        /// <summary>Raw DXGI color-space enum for diagnostics.</summary>
        public int DxgiColorSpace { get; set; }

        /// <summary>DXGI bits-per-color for the active output path.</summary>
        public int BitsPerColor { get; set; }

        public GammaMode CurrentGamma { get; set; } = GammaMode.Gamma24;
        
        /// <summary>
        /// SDR White Level in nits (from Windows settings or manual override).
        /// </summary>
        public double SdrWhiteLevel { get; set; } = 200.0;

        // DXGI details
        public Dxgi.LUID AdapterLuid { get; set; }
        public uint OutputId { get; set; } // Index in EnumOutputs
        public IntPtr HMonitor { get; set; }
        public Dxgi.RECT MonitorBounds { get; set; }

        /// <summary>Panel HDR range from DXGI (display-reported metadata), nits. 0 = unknown.</summary>
        public double HdrPeakNits { get; set; }
        public double HdrMinNits { get; set; }
        public double HdrMaxFullFrameNits { get; set; }

        // DisplayConfig identity (adapter LUID + source id) — required by the Advanced Color
        // profile association APIs, which is the association list Windows uses in HDR.
        public bool HasDisplayConfigIds { get; set; }
        public Dxgi.LUID DisplayConfigAdapterId { get; set; }
        public uint DisplayConfigSourceId { get; set; }

        /// <summary>
        /// The display's reported gamut from its EDID color-characteristics block, if parsed.
        /// Lets the calibration UI recommend reachable targets BEFORE measuring (the
        /// colorimeter still measures the true gamut during calibration).
        /// </summary>
        public EdidColorInfo? EdidColor { get; set; }

        public static double SanitizeSdrWhiteLevel(double value) =>
            double.IsFinite(value)
                ? Math.Clamp(value, MinSdrWhiteLevel, MaxSdrWhiteLevel)
                : DefaultSdrWhiteLevel;

        public static double SanitizeNonNegativeNits(double value) =>
            double.IsFinite(value) && value > 0.0 ? value : 0.0;
    }

    /// <summary>
    /// Display chromaticities (CIE 1931 xy) reported in the EDID color-characteristics block.
    /// Manufacturer-reported, so approximate — good enough to pre-filter calibration targets.
    /// </summary>
    public class EdidColorInfo
    {
        public double RedX { get; init; }
        public double RedY { get; init; }
        public double GreenX { get; init; }
        public double GreenY { get; init; }
        public double BlueX { get; init; }
        public double BlueY { get; init; }
        public double WhiteX { get; init; }
        public double WhiteY { get; init; }
    }
}
