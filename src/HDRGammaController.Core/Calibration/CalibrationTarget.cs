using System;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Defines a calibration target color space with primaries, white point,
    /// and transfer function specifications.
    /// </summary>
    /// <remarks>
    /// A calibration target represents the "ideal" color behavior we want
    /// the display to achieve. The calibration process measures the display's
    /// actual behavior and creates a correction LUT to map actual → target.
    ///
    /// References:
    /// - ITU-R BT.709-6 - HDTV video
    /// - ITU-R BT.2020-2 - UHDTV
    /// - ITU-R BT.2100-2 - HDR television
    /// - SMPTE ST 2084:2014 - PQ EOTF
    /// - IEC 61966-2-1:1999 - sRGB
    /// - SMPTE EG 432-1:2010 - DCI-P3
    /// </remarks>
    public class CalibrationTarget
    {
        /// <summary>
        /// Human-readable name for this target (e.g., "sRGB", "Rec.2020 PQ").
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Detailed description of this target.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Red primary chromaticity.
        /// </summary>
        public required Chromaticity RedPrimary { get; init; }

        /// <summary>
        /// Green primary chromaticity.
        /// </summary>
        public required Chromaticity GreenPrimary { get; init; }

        /// <summary>
        /// Blue primary chromaticity.
        /// </summary>
        public required Chromaticity BluePrimary { get; init; }

        /// <summary>
        /// White point chromaticity (typically D65).
        /// </summary>
        public required Chromaticity WhitePoint { get; init; }

        /// <summary>
        /// Target gamma value (e.g., 2.2, 2.4) for power-law transfer functions.
        /// Null for non-gamma transfer functions (sRGB, PQ, HLG).
        /// </summary>
        public double? Gamma { get; init; }

        /// <summary>
        /// Transfer function type for this target.
        /// </summary>
        public required TransferFunctionType TransferFunction { get; init; }

        /// <summary>
        /// Target peak luminance in cd/m² (nits).
        /// Null means "use display native" or "not specified".
        /// </summary>
        public double? PeakLuminance { get; init; }

        /// <summary>
        /// Target black level in cd/m² (nits).
        /// Default 0 means perfect black (OLED-like).
        /// </summary>
        public double BlackLevel { get; init; } = 0.0;

        /// <summary>
        /// Target reference white level in cd/m² for SDR content.
        /// Typically 80-120 for SDR, 100-400 for HDR reference white.
        /// </summary>
        public double? ReferenceWhite { get; init; }

        /// <summary>
        /// Whether this target is for HDR content.
        /// </summary>
        public bool IsHdr => TransferFunction == TransferFunctionType.Pq ||
                            TransferFunction == TransferFunctionType.Hlg;

        /// <summary>
        /// When set, the installer corrects ONLY the white point and tone: the gamut matrix
        /// is built with the target primaries replaced by the panel's measured primaries, so
        /// no gamut re-mapping happens. The right choice for panels whose native gamut
        /// already lands on target (verified sub-1.5 ΔE primaries) or whose processing is
        /// too nonlinear for a 3×3 to improve — e.g. QD-OLED in HDR, where full gamut
        /// correction measurably overfits (primaries 1.49 → 3.34 on the first attempt).
        /// </summary>
        public bool WhitePointOnly { get; init; }

        /// <summary>Clone of this target with a different white point (visual white trim).</summary>
        public CalibrationTarget WithWhitePoint(Chromaticity white) => new()
        {
            Name = Name,
            Description = Description,
            RedPrimary = RedPrimary,
            GreenPrimary = GreenPrimary,
            BluePrimary = BluePrimary,
            WhitePoint = white,
            Gamma = Gamma,
            TransferFunction = TransferFunction,
            PeakLuminance = PeakLuminance,
            BlackLevel = BlackLevel,
            ReferenceWhite = ReferenceWhite,
            WhitePointOnly = WhitePointOnly,
        };

        /// <summary>Clone of this target with <see cref="WhitePointOnly"/> set.</summary>
        public CalibrationTarget AsWhitePointOnly() => new()
        {
            Name = Name,
            Description = Description,
            RedPrimary = RedPrimary,
            GreenPrimary = GreenPrimary,
            BluePrimary = BluePrimary,
            WhitePoint = WhitePoint,
            Gamma = Gamma,
            TransferFunction = TransferFunction,
            PeakLuminance = PeakLuminance,
            BlackLevel = BlackLevel,
            ReferenceWhite = ReferenceWhite,
            WhitePointOnly = true,
        };

        /// <summary>
        /// Clone of this target with a different black level (in the same cd/m² domain as
        /// <see cref="PeakLuminance"/>/<see cref="ReferenceWhite"/>). Used to wire the
        /// MEASURED black into a BT.1886 target: the standard's a/b coefficients are a
        /// function of the actual display's Lw/Lb, so a target with BlackLevel = 0
        /// degenerates to pure 2.4 and never uses the measured contrast.
        /// </summary>
        public CalibrationTarget WithBlackLevel(double blackLevel) => new()
        {
            Name = Name,
            Description = Description,
            RedPrimary = RedPrimary,
            GreenPrimary = GreenPrimary,
            BluePrimary = BluePrimary,
            WhitePoint = WhitePoint,
            Gamma = Gamma,
            TransferFunction = TransferFunction,
            PeakLuminance = PeakLuminance,
            BlackLevel = double.IsFinite(blackLevel) && blackLevel > 0.0 ? blackLevel : 0.0,
            ReferenceWhite = ReferenceWhite,
            WhitePointOnly = WhitePointOnly,
        };

        /// <summary>
        /// Gets the RGB to XYZ matrix for this color space.
        /// JsonIgnore: derived from the serialized primaries, and System.Text.Json
        /// cannot serialize double[,] (it broke report-snapshot persistence).
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double[,] RgbToXyzMatrix =>
            ColorMath.CalculateRgbToXyzMatrix(RedPrimary, GreenPrimary, BluePrimary, WhitePoint);

        /// <summary>
        /// Gets the XYZ to RGB matrix for this color space.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double[,] XyzToRgbMatrix => ColorMath.Invert3x3(RgbToXyzMatrix);

        /// <summary>
        /// Converts linear RGB in this color space to XYZ.
        /// </summary>
        public CieXyz LinearRgbToXyz(LinearRgb rgb)
        {
            var matrix = RgbToXyzMatrix;
            double x = matrix[0, 0] * rgb.R + matrix[0, 1] * rgb.G + matrix[0, 2] * rgb.B;
            double y = matrix[1, 0] * rgb.R + matrix[1, 1] * rgb.G + matrix[1, 2] * rgb.B;
            double z = matrix[2, 0] * rgb.R + matrix[2, 1] * rgb.G + matrix[2, 2] * rgb.B;
            return new CieXyz(x, y, z);
        }

        /// <summary>
        /// Converts XYZ to linear RGB in this color space.
        /// </summary>
        public LinearRgb XyzToLinearRgb(CieXyz xyz)
        {
            var matrix = XyzToRgbMatrix;
            double r = matrix[0, 0] * xyz.X + matrix[0, 1] * xyz.Y + matrix[0, 2] * xyz.Z;
            double g = matrix[1, 0] * xyz.X + matrix[1, 1] * xyz.Y + matrix[1, 2] * xyz.Z;
            double b = matrix[2, 0] * xyz.X + matrix[2, 1] * xyz.Y + matrix[2, 2] * xyz.Z;
            return new LinearRgb(r, g, b);
        }

        /// <summary>
        /// Applies the target transfer function (OETF) to encode linear light.
        /// </summary>
        public double ApplyOetf(double linear)
        {
            double safeLinear = Clamp01(linear);

            return TransferFunction switch
            {
                TransferFunctionType.Srgb => ColorMath.SrgbOetf(safeLinear),
                TransferFunctionType.Gamma => ColorMath.GammaEncode(safeLinear, SafeGamma(Gamma)),
                TransferFunctionType.Bt1886 => ApplyBt1886InverseEotf(safeLinear),
                TransferFunctionType.Rec2020 => ColorMath.Rec2020Oetf(safeLinear),
                TransferFunctionType.Pq => TransferFunctions.PqInverseEotf(safeLinear * SafePeakLuminance()),
                TransferFunctionType.Hlg => ApplyHlgOetf(safeLinear),
                TransferFunctionType.Linear => safeLinear,
                _ => ColorMath.GammaEncode(safeLinear, 2.2)
            };
        }

        /// <summary>
        /// Applies the inverse transfer function (EOTF) to decode to linear light.
        /// </summary>
        public double ApplyEotf(double signal)
        {
            double safeSignal = Clamp01(signal);

            return TransferFunction switch
            {
                TransferFunctionType.Srgb => ColorMath.SrgbEotf(safeSignal),
                TransferFunctionType.Gamma => ColorMath.GammaDecode(safeSignal, SafeGamma(Gamma)),
                TransferFunctionType.Bt1886 => ApplyBt1886Eotf(safeSignal),
                TransferFunctionType.Rec2020 => ColorMath.Rec2020Eotf(safeSignal),
                TransferFunctionType.Pq => TransferFunctions.PqEotf(safeSignal) / SafePeakLuminance(),
                TransferFunctionType.Hlg => ApplyHlgEotf(safeSignal),
                TransferFunctionType.Linear => safeSignal,
                _ => ColorMath.GammaDecode(safeSignal, 2.2)
            };
        }

        // HLG constants (ITU-R BT.2100-2)
        private const double HlgA = 0.17883277;
        private const double HlgB = 0.28466892; // 1 - 4 * a
        private const double HlgC = 0.55991073; // 0.5 - a * ln(4a), pre-computed

        private double Bt1886WhiteLevel => SafePositive(PeakLuminance ?? ReferenceWhite, 1.0);

        private double ApplyBt1886Eotf(double signal)
        {
            double white = Bt1886WhiteLevel;
            if (white <= 0 || BlackLevel <= 0)
                return ColorMath.GammaDecode(signal, 2.4);

            return TransferFunctions.Bt1886Eotf(signal, white, BlackLevel) / white;
        }

        private double ApplyBt1886InverseEotf(double linear)
        {
            double white = Bt1886WhiteLevel;
            if (white <= 0 || BlackLevel <= 0)
                return ColorMath.GammaEncode(linear, 2.4);

            return TransferFunctions.Bt1886InverseEotf(
                Clamp01(linear) * white,
                white,
                BlackLevel);
        }

        /// <summary>
        /// HLG system gamma per BT.2100-2 Note 5f: γ = 1.2 + 0.42·log10(L_W / 1000),
        /// with L_W = nominal display peak luminance (<see cref="PeakLuminance"/>).
        /// At the 1000 cd/m² reference display γ is exactly 1.2.
        /// </summary>
        private double HlgSystemGamma()
        {
            double lw = SafePositive(PeakLuminance, 1000.0); // HLG reference display is 1000 cd/m²
            double gamma = 1.2 + 0.42 * Math.Log10(lw / 1000.0);
            return double.IsFinite(gamma) ? Math.Max(gamma, 1.0) : 1.2;
        }

        /// <summary>
        /// HLG inverse EOTF (display linear → signal), per ITU-R BT.2100-2:
        /// EOTF = OOTF[OETF⁻¹], so the inverse is OETF[OOTF⁻¹]. On the neutral axis the
        /// OOTF is F_D/L_W = Y_S^(γ−1)·E_S = E_S^γ, so OOTF⁻¹ is the 1/γ root.
        /// </summary>
        private double ApplyHlgOetf(double displayLinear)
        {
            displayLinear = Clamp01(displayLinear);
            double sceneLinear = Math.Pow(displayLinear, 1.0 / HlgSystemGamma()); // inverse OOTF
            // Clamp: the spec's rounded a/b/c constants leave ~2e-8 of overshoot at the top.
            return Clamp01(sceneLinear <= 1.0 / 12.0
                ? Math.Sqrt(3.0 * sceneLinear)
                : HlgA * Math.Log(12.0 * sceneLinear - HlgB) + HlgC);
        }

        /// <summary>
        /// HLG EOTF (signal → display linear, normalized to L_W = 1) per ITU-R BT.2100-2:
        /// F_D = OOTF[OETF⁻¹(signal)] = α·Y_S^(γ−1)·E_S with α = L_W. For a per-channel
        /// scalar on the neutral axis Y_S = E_S, so F_D/L_W = E_S^γ. The previous
        /// implementation returned SCENE linear (inverse OETF only), silently omitting the
        /// system-gamma OOTF that is part of the HLG EOTF by definition.
        /// </summary>
        private double ApplyHlgEotf(double signal)
        {
            signal = Clamp01(signal);
            double sceneLinear = signal <= 0.5
                ? (signal * signal) / 3.0
                : (Math.Exp((signal - HlgC) / HlgA) + HlgB) / 12.0;
            // Clamp: the spec's rounded a/b/c constants put OETF^-1(1) at 1 + ~2.4e-8.
            return Clamp01(Math.Pow(sceneLinear, HlgSystemGamma())); // OOTF
        }

        private double SafePeakLuminance() => SafePositive(PeakLuminance, 10000.0);

        private static double SafePositive(double? value, double fallback) =>
            value.HasValue && double.IsFinite(value.Value) && value.Value > 0.0 ? value.Value : fallback;

        private static double SafeGamma(double? gamma) =>
            gamma.HasValue && double.IsFinite(gamma.Value) && gamma.Value is >= 1.0 and <= 4.0
                ? gamma.Value
                : 2.2;

        private static double Clamp01(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

        public override string ToString() => Name;
    }

    /// <summary>
    /// Types of electro-optical transfer functions (EOTFs).
    /// </summary>
    public enum TransferFunctionType
    {
        /// <summary>Linear (no gamma, 1:1 mapping).</summary>
        Linear,

        /// <summary>IEC 61966-2-1 sRGB (piecewise with linear segment).</summary>
        Srgb,

        /// <summary>Pure power-law gamma (e.g., 2.2, 2.4).</summary>
        Gamma,

        /// <summary>ITU-R BT.1886 broadcast EOTF; pure 2.4 when target black is zero.</summary>
        Bt1886,

        /// <summary>ITU-R BT.2020 (Rec.2020 OETF).</summary>
        Rec2020,

        /// <summary>SMPTE ST 2084 Perceptual Quantizer (HDR).</summary>
        Pq,

        /// <summary>ITU-R BT.2100 Hybrid Log-Gamma (HDR).</summary>
        Hlg
    }

    /// <summary>
    /// Standard calibration targets for common color spaces.
    /// </summary>
    public static class StandardTargets
    {
        /// <summary>
        /// sRGB / Rec.709 primaries with a PURE POWER 2.2 EOTF (standard PC/web content).
        /// A display-calibration target named "Gamma 2.2" means pure power 2.2: the sRGB
        /// piecewise curve (linear toe) is an ENCODING function; consumer displays that
        /// sRGB content is mastered on decode with a plain 2.2 power law. Grading pure-2.2
        /// tracking against the piecewise curve builds in a permanent ~1.5-2 ΔE2000 shadow
        /// penalty below 10% signal. A true piecewise-sRGB EOTF target exists separately as
        /// <see cref="SrgbPiecewise"/>, deliberately outside the default flow.
        /// </summary>
        public static CalibrationTarget SrgbGamma22 { get; } = new()
        {
            Name = "sRGB (Gamma 2.2)",
            Description = "Standard sRGB color space with pure 2.2 gamma. Best for general PC use and web content.",
            RedPrimary = Chromaticity.Rec709Red,
            GreenPrimary = Chromaticity.Rec709Green,
            BluePrimary = Chromaticity.Rec709Blue,
            WhitePoint = Chromaticity.D65,
            TransferFunction = TransferFunctionType.Gamma,
            Gamma = 2.2,
            ReferenceWhite = 80
        };

        /// <summary>
        /// sRGB with the TRUE IEC 61966-2-1 piecewise EOTF (linear toe below ~0.04 signal).
        /// Kept for users who explicitly want piecewise-sRGB tracking; NOT in
        /// <see cref="All"/> (the default flow) — see the <see cref="SrgbGamma22"/> remarks.
        /// </summary>
        public static CalibrationTarget SrgbPiecewise { get; } = new()
        {
            Name = "sRGB (piecewise)",
            Description = "sRGB color space with the exact IEC 61966-2-1 piecewise transfer function. " +
                          "Only for workflows that specifically require the linear-toe sRGB EOTF.",
            RedPrimary = Chromaticity.Rec709Red,
            GreenPrimary = Chromaticity.Rec709Green,
            BluePrimary = Chromaticity.Rec709Blue,
            WhitePoint = Chromaticity.D65,
            TransferFunction = TransferFunctionType.Srgb,
            ReferenceWhite = 80
        };

        /// <summary>
        /// Rec.709 with BT.1886 gamma 2.4 (broadcast standard, dark room viewing).
        /// </summary>
        public static CalibrationTarget Rec709Gamma24 { get; } = new()
        {
            Name = "Rec.709 (Gamma 2.4 / BT.1886)",
            Description = "ITU-R BT.709 with 2.4 gamma per BT.1886. For broadcast content and dark room viewing.",
            RedPrimary = Chromaticity.Rec709Red,
            GreenPrimary = Chromaticity.Rec709Green,
            BluePrimary = Chromaticity.Rec709Blue,
            WhitePoint = Chromaticity.D65,
            TransferFunction = TransferFunctionType.Bt1886,
            Gamma = 2.4,
            ReferenceWhite = 100
        };

        /// <summary>
        /// Rec.709 with pure 2.2 gamma (no sRGB linear segment).
        /// </summary>
        public static CalibrationTarget Rec709PureGamma22 { get; } = new()
        {
            Name = "Rec.709 (Pure Gamma 2.2)",
            Description = "ITU-R BT.709 primaries with pure 2.2 power-law gamma. Common for photo editing.",
            RedPrimary = Chromaticity.Rec709Red,
            GreenPrimary = Chromaticity.Rec709Green,
            BluePrimary = Chromaticity.Rec709Blue,
            WhitePoint = Chromaticity.D65,
            TransferFunction = TransferFunctionType.Gamma,
            Gamma = 2.2,
            ReferenceWhite = 80
        };

        /// <summary>
        /// DCI-P3 with D65 white point and 2.2 gamma (common for wide-gamut monitors).
        /// </summary>
        public static CalibrationTarget P3D65Gamma22 { get; } = new()
        {
            Name = "Display P3 (Gamma 2.2)",
            Description = "DCI-P3 primaries with D65 white point. Common for Apple displays and wide-gamut monitors.",
            RedPrimary = Chromaticity.P3Red,
            GreenPrimary = Chromaticity.P3Green,
            BluePrimary = Chromaticity.P3Blue,
            WhitePoint = Chromaticity.D65,
            TransferFunction = TransferFunctionType.Gamma,
            Gamma = 2.2,
            ReferenceWhite = 100
        };

        /// <summary>
        /// DCI-P3 with D65 white point and 2.6 gamma (DCI digital cinema specification).
        /// </summary>
        public static CalibrationTarget P3D65Gamma26 { get; } = new()
        {
            Name = "DCI-P3 D65 (Gamma 2.6)",
            Description = "DCI-P3 primaries with D65 white point and 2.6 gamma. Digital cinema specification.",
            RedPrimary = Chromaticity.P3Red,
            GreenPrimary = Chromaticity.P3Green,
            BluePrimary = Chromaticity.P3Blue,
            WhitePoint = Chromaticity.D65,
            TransferFunction = TransferFunctionType.Gamma,
            Gamma = 2.6,
            ReferenceWhite = 48 // DCI spec is 48 cd/m²
        };

        /// <summary>
        /// Rec.2020 with 2.4 gamma (SDR UHD content).
        /// </summary>
        public static CalibrationTarget Rec2020Gamma24 { get; } = new()
        {
            Name = "Rec.2020 (Gamma 2.4)",
            Description = "ITU-R BT.2020 wide color gamut with 2.4 gamma. For SDR UHD content.",
            RedPrimary = Chromaticity.Rec2020Red,
            GreenPrimary = Chromaticity.Rec2020Green,
            BluePrimary = Chromaticity.Rec2020Blue,
            WhitePoint = Chromaticity.D65,
            TransferFunction = TransferFunctionType.Gamma,
            Gamma = 2.4,
            ReferenceWhite = 100
        };

        /// <summary>
        /// Rec.2020 with PQ (SMPTE ST 2084) for HDR10 content.
        /// </summary>
        public static CalibrationTarget Rec2020Pq { get; } = new()
        {
            Name = "Rec.2020 PQ (HDR10)",
            Description = "ITU-R BT.2020 with SMPTE ST 2084 PQ transfer function. For HDR10 content.",
            RedPrimary = Chromaticity.Rec2020Red,
            GreenPrimary = Chromaticity.Rec2020Green,
            BluePrimary = Chromaticity.Rec2020Blue,
            WhitePoint = Chromaticity.D65,
            TransferFunction = TransferFunctionType.Pq,
            PeakLuminance = 1000, // Can be adjusted based on display capability
            ReferenceWhite = 203  // HDR reference white per ITU-R BT.2408
        };

        /// <summary>
        /// Rec.2020 with HLG for broadcast HDR.
        /// </summary>
        public static CalibrationTarget Rec2020Hlg { get; } = new()
        {
            Name = "Rec.2020 HLG",
            Description = "ITU-R BT.2020 with Hybrid Log-Gamma. For broadcast HDR content.",
            RedPrimary = Chromaticity.Rec2020Red,
            GreenPrimary = Chromaticity.Rec2020Green,
            BluePrimary = Chromaticity.Rec2020Blue,
            WhitePoint = Chromaticity.D65,
            TransferFunction = TransferFunctionType.Hlg,
            PeakLuminance = 1000,
            ReferenceWhite = 203
        };

        /// <summary>
        /// HDR desktop calibration: sRGB/Rec.709 CONTENT gamut with PQ tone. In HDR Windows
        /// already color-manages app content (sRGB) onto the wire, so a calibration measured
        /// through that pipeline sees content-basis primaries ≈ sRGB — the correction's job
        /// is to fix the residual gamut/white error and the panel's PQ tracking, not to
        /// expand to the container gamut (that's what made wide targets clip).
        /// </summary>
        public static CalibrationTarget Rec709Pq { get; } = new()
        {
            Name = "HDR Desktop PQ (sRGB gamut)",
            Description = "sRGB/Rec.709 content gamut, D65 white, SMPTE ST 2084 PQ tone. " +
                          "The right target for calibrating the HDR desktop experience.",
            RedPrimary = Chromaticity.Rec709Red,
            GreenPrimary = Chromaticity.Rec709Green,
            BluePrimary = Chromaticity.Rec709Blue,
            WhitePoint = Chromaticity.D65,
            TransferFunction = TransferFunctionType.Pq,
            PeakLuminance = 1000,
            ReferenceWhite = 203
        };

        /// <summary>
        /// P3 with PQ for HDR content on P3 displays.
        /// </summary>
        public static CalibrationTarget P3Pq { get; } = new()
        {
            Name = "Display P3 PQ",
            Description = "DCI-P3 primaries with D65 white point and PQ transfer function. For HDR content on P3 displays.",
            RedPrimary = Chromaticity.P3Red,
            GreenPrimary = Chromaticity.P3Green,
            BluePrimary = Chromaticity.P3Blue,
            WhitePoint = Chromaticity.D65,
            TransferFunction = TransferFunctionType.Pq,
            PeakLuminance = 1000,
            ReferenceWhite = 203
        };

        /// <summary>
        /// All standard calibration targets.
        /// </summary>
        public static CalibrationTarget[] All { get; } = new[]
        {
            SrgbGamma22,
            Rec709PureGamma22,
            Rec709Gamma24,
            P3D65Gamma22,
            P3D65Gamma26,
            Rec2020Gamma24,
            Rec709Pq
        };

        /// <summary>
        /// Gets a target by name (case-insensitive partial match).
        /// </summary>
        public static CalibrationTarget? GetByName(string name)
        {
            foreach (var target in All)
            {
                if (target.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return target;
            }
            return null;
        }

        /// <summary>
        /// Creates a custom calibration target from measured display primaries.
        /// </summary>
        public static CalibrationTarget CreateNative(
            Chromaticity measuredRed,
            Chromaticity measuredGreen,
            Chromaticity measuredBlue,
            Chromaticity measuredWhite,
            double gamma = 2.2)
        {
            return new CalibrationTarget
            {
                Name = "Display Native",
                Description = "Calibrated to display's native primaries and white point.",
                RedPrimary = measuredRed,
                GreenPrimary = measuredGreen,
                BluePrimary = measuredBlue,
                WhitePoint = measuredWhite,
                TransferFunction = TransferFunctionType.Gamma,
                Gamma = gamma
            };
        }
    }
}
