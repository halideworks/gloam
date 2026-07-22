using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Calibration mode that determines how the calibration is applied.
    /// </summary>
    public enum CalibrationMode
    {
        /// <summary>
        /// Reference mode: Bakes color space + gamma + white point into a single LUT.
        /// Most accurate for the specific target, but changing gamma requires re-calibrating
        /// or loading a different pre-baked profile.
        /// Best for: Color grading, photo editing, broadcast mastering.
        /// </summary>
        Reference,

        /// <summary>
        /// Adaptive mode: Stores display linearization and primary correction separately.
        /// Gamma can be adjusted on top of the calibration in real-time.
        /// Slightly less accurate than Reference (~99.5%) but allows instant gamma switching.
        /// Best for: Mixed content, gaming, general use.
        /// </summary>
        Adaptive
    }

    /// <summary>
    /// Target color space for calibration.
    /// </summary>
    public enum TargetColorSpace
    {
        /// <summary>sRGB / Rec.709 primaries with D65 white point.</summary>
        SRgb,

        /// <summary>DCI-P3 primaries with D65 white point.</summary>
        DisplayP3,

        /// <summary>Rec.2020 primaries with D65 white point.</summary>
        Rec2020,

        /// <summary>Native display primaries (no primary correction, only gamma/white point).</summary>
        Native
    }

    /// <summary>
    /// Persisted calibration profile for a display.
    /// Contains the measured display characteristics and calibration settings.
    /// </summary>
    public class DisplayCalibrationProfile
    {
        internal const long MaxProfileFileBytes = 16L * 1024 * 1024;
        internal const int MaxProfileCharacters = 8_000_000;

        /// <summary>
        /// Profile format version for compatibility checking.
        /// </summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// Unique identifier for this profile.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// User-friendly name for this profile.
        /// </summary>
        public string Name { get; set; } = "Untitled Profile";

        /// <summary>
        /// Monitor device path this profile was created for.
        /// </summary>
        public string? MonitorDevicePath { get; set; }

        /// <summary>
        /// Monitor friendly name at time of calibration.
        /// </summary>
        public string? MonitorName { get; set; }

        /// <summary>
        /// When the calibration was performed.
        /// </summary>
        public DateTime CalibratedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Colorimeter model used for calibration.
        /// </summary>
        public string? ColorimeterModel { get; set; }

        /// <summary>
        /// Calibration mode (Reference or Adaptive).
        /// </summary>
        public CalibrationMode Mode { get; set; } = CalibrationMode.Adaptive;

        /// <summary>
        /// Target color space for the calibration.
        /// </summary>
        public TargetColorSpace TargetColorSpace { get; set; } = TargetColorSpace.SRgb;

        /// <summary>
        /// Target gamma for Reference mode (ignored in Adaptive mode).
        /// </summary>
        public double TargetGamma { get; set; } = 2.2;

        /// <summary>
        /// Whether this profile is currently active for the monitor.
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// True when a loaded profile contained calibration-critical values that had to be
        /// repaired. Such profiles are safe to display, but must not be used to drive LUTs.
        /// </summary>
        [JsonIgnore]
        public bool WasRepairedOnLoad { get; private set; }

        #region Measured Display Characteristics

        /// <summary>
        /// Measured red primary chromaticity (CIE xy).
        /// </summary>
        public double RedPrimaryX { get; set; }
        public double RedPrimaryY { get; set; }

        /// <summary>
        /// Measured green primary chromaticity (CIE xy).
        /// </summary>
        public double GreenPrimaryX { get; set; }
        public double GreenPrimaryY { get; set; }

        /// <summary>
        /// Measured blue primary chromaticity (CIE xy).
        /// </summary>
        public double BluePrimaryX { get; set; }
        public double BluePrimaryY { get; set; }

        /// <summary>
        /// Measured white point chromaticity (CIE xy).
        /// </summary>
        public double WhitePointX { get; set; }
        public double WhitePointY { get; set; }

        /// <summary>
        /// Measured black level in cd/m².
        /// </summary>
        public double BlackLevel { get; set; }

        /// <summary>
        /// Measured peak luminance in cd/m².
        /// </summary>
        public double PeakLuminance { get; set; }

        /// <summary>
        /// Measured average gamma.
        /// </summary>
        public double MeasuredGamma { get; set; }

        /// <summary>
        /// Per-channel tone response curves (4096 entries each).
        /// Stored as normalized 0-1 values representing the display's actual response.
        /// </summary>
        public double[]? RedToneCurve { get; set; }
        public double[]? GreenToneCurve { get; set; }
        public double[]? BlueToneCurve { get; set; }

        #endregion

        #region Pre-computed LUTs (for Reference mode)

        /// <summary>
        /// Pre-computed 3D LUT for Reference mode (serialized as base64).
        /// Null in Adaptive mode where LUTs are computed on-the-fly.
        /// </summary>
        public string? ReferenceLutBase64 { get; set; }

        /// <summary>
        /// Size of the 3D LUT (e.g., 17 for 17x17x17).
        /// </summary>
        public int ReferenceLutSize { get; set; }

        #endregion

        #region Factory Methods

        /// <summary>
        /// Creates a calibration profile from a DisplayCharacterization.
        /// </summary>
        public static DisplayCalibrationProfile FromCharacterization(
            DisplayCharacterization characterization,
            CalibrationMode mode,
            TargetColorSpace targetColorSpace,
            double targetGamma,
            string? monitorPath = null,
            string? monitorName = null,
            string? colorimeterModel = null)
        {
            var profile = new DisplayCalibrationProfile
            {
                Mode = mode,
                TargetColorSpace = targetColorSpace,
                TargetGamma = targetGamma,
                MonitorDevicePath = monitorPath,
                MonitorName = monitorName,
                ColorimeterModel = colorimeterModel,
                CalibratedAt = DateTime.UtcNow,

                // Copy measured characteristics
                RedPrimaryX = characterization.RedPrimary.X,
                RedPrimaryY = characterization.RedPrimary.Y,
                GreenPrimaryX = characterization.GreenPrimary.X,
                GreenPrimaryY = characterization.GreenPrimary.Y,
                BluePrimaryX = characterization.BluePrimary.X,
                BluePrimaryY = characterization.BluePrimary.Y,
                WhitePointX = characterization.WhitePoint.X,
                WhitePointY = characterization.WhitePoint.Y,
                BlackLevel = characterization.BlackLevel,
                PeakLuminance = characterization.PeakLuminance,
                MeasuredGamma = characterization.MeasuredGamma,

                // Copy tone curves
                RedToneCurve = characterization.RedToneCurve?.ToArray(),
                GreenToneCurve = characterization.GreenToneCurve?.ToArray(),
                BlueToneCurve = characterization.BlueToneCurve?.ToArray()
            };

            // Generate profile name
            string colorSpaceName = targetColorSpace switch
            {
                TargetColorSpace.SRgb => "sRGB",
                TargetColorSpace.DisplayP3 => "Display P3",
                TargetColorSpace.Rec2020 => "Rec.2020",
                TargetColorSpace.Native => "Native",
                _ => "Unknown"
            };

            string modeName = mode == CalibrationMode.Reference ? "Reference" : "Adaptive";
            profile.Name = $"{colorSpaceName} {modeName} - {profile.CalibratedAt:yyyy-MM-dd}";

            return profile.CreatePersistableCopy();
        }

        /// <summary>
        /// Converts this profile back to a DisplayCharacterization for LUT generation.
        /// </summary>
        public DisplayCharacterization ToCharacterization()
        {
            var characterization = new DisplayCharacterization
            {
                RedPrimary = SafeChromaticity(RedPrimaryX, RedPrimaryY, Chromaticity.Rec709Red),
                GreenPrimary = SafeChromaticity(GreenPrimaryX, GreenPrimaryY, Chromaticity.Rec709Green),
                BluePrimary = SafeChromaticity(BluePrimaryX, BluePrimaryY, Chromaticity.Rec709Blue),
                WhitePoint = SafeChromaticity(WhitePointX, WhitePointY, Chromaticity.D65),
                BlackLevel = SafeNonNegative(BlackLevel, 0.0),
                PeakLuminance = SafePositive(PeakLuminance, 100.0),
                MeasuredGamma = SafeGamma(MeasuredGamma)
            };

            // Restore tone curves
            if (RedToneCurve != null && RedToneCurve.Length > 0)
            {
                characterization.RedToneCurve = ToneCurve.CreateFromArray(RedToneCurve);
            }
            if (GreenToneCurve != null && GreenToneCurve.Length > 0)
            {
                characterization.GreenToneCurve = ToneCurve.CreateFromArray(GreenToneCurve);
            }
            if (BlueToneCurve != null && BlueToneCurve.Length > 0)
            {
                characterization.BlueToneCurve = ToneCurve.CreateFromArray(BlueToneCurve);
            }

            return characterization;
        }

        private static Chromaticity SafeChromaticity(double x, double y, Chromaticity fallback)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) ||
                x <= 0.0 || y <= 0.0 || x >= 0.8 || y >= 0.9 || x + y > 1.000001)
            {
                return fallback;
            }

            return new Chromaticity(x, y);
        }

        private static double SafeNonNegative(double value, double fallback) =>
            double.IsFinite(value) && value >= 0.0 ? value : fallback;

        private static double SafePositive(double value, double fallback) =>
            double.IsFinite(value) && value > 0.0 ? value : fallback;

        private static double SafeGamma(double gamma) =>
            double.IsFinite(gamma) && gamma is >= 1.0 and <= 4.0 ? gamma : 2.2;

        #endregion

        #region Serialization

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Saves the profile to a JSON file.
        /// </summary>
        public void SaveToFile(string path)
        {
            var json = JsonSerializer.Serialize(CreatePersistableCopy(), JsonOptions);
            // Write-then-rename so a crash mid-write can't corrupt an existing profile.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>
        /// Loads a profile from a JSON file.
        /// </summary>
        public static DisplayCalibrationProfile? LoadFromFile(string path)
        {
            if (!File.Exists(path))
                return null;
            if (new FileInfo(path).Length > MaxProfileFileBytes)
                throw new InvalidDataException("Calibration profile exceeds the size limit.");

            var json = File.ReadAllText(path);
            return FromJson(json);
        }

        /// <summary>
        /// Serializes the profile to JSON.
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(CreatePersistableCopy(), JsonOptions);
        }

        /// <summary>
        /// Deserializes a profile from JSON.
        /// </summary>
        public static DisplayCalibrationProfile? FromJson(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            if (json.Length > MaxProfileCharacters)
                throw new InvalidDataException("Calibration profile exceeds the size limit.");

            var raw = JsonSerializer.Deserialize<DisplayCalibrationProfile>(json, JsonOptions);
            if (raw == null)
                return null;

            var sanitized = raw.CreatePersistableCopy();
            sanitized.WasRepairedOnLoad = raw.HasRuntimeCriticalInvalidValues();
            return sanitized;
        }

        private DisplayCalibrationProfile CreatePersistableCopy()
        {
            return new DisplayCalibrationProfile
            {
                Version = Math.Max(1, Version),
                Id = Id,
                Name = Name,
                MonitorDevicePath = MonitorDevicePath,
                MonitorName = MonitorName,
                CalibratedAt = CalibratedAt,
                ColorimeterModel = ColorimeterModel,
                Mode = SafeEnum(Mode, CalibrationMode.Adaptive),
                TargetColorSpace = SafeEnum(TargetColorSpace, TargetColorSpace.SRgb),
                TargetGamma = SafeGamma(TargetGamma),
                IsActive = IsActive,
                RedPrimaryX = SafeChromaticity(RedPrimaryX, RedPrimaryY, Chromaticity.Rec709Red).X,
                RedPrimaryY = SafeChromaticity(RedPrimaryX, RedPrimaryY, Chromaticity.Rec709Red).Y,
                GreenPrimaryX = SafeChromaticity(GreenPrimaryX, GreenPrimaryY, Chromaticity.Rec709Green).X,
                GreenPrimaryY = SafeChromaticity(GreenPrimaryX, GreenPrimaryY, Chromaticity.Rec709Green).Y,
                BluePrimaryX = SafeChromaticity(BluePrimaryX, BluePrimaryY, Chromaticity.Rec709Blue).X,
                BluePrimaryY = SafeChromaticity(BluePrimaryX, BluePrimaryY, Chromaticity.Rec709Blue).Y,
                WhitePointX = SafeChromaticity(WhitePointX, WhitePointY, Chromaticity.D65).X,
                WhitePointY = SafeChromaticity(WhitePointX, WhitePointY, Chromaticity.D65).Y,
                BlackLevel = SafeNonNegative(BlackLevel, 0.0),
                PeakLuminance = SafePositive(PeakLuminance, 100.0),
                MeasuredGamma = SafeGamma(MeasuredGamma),
                RedToneCurve = SafeToneCurveForPersistence(RedToneCurve),
                GreenToneCurve = SafeToneCurveForPersistence(GreenToneCurve),
                BlueToneCurve = SafeToneCurveForPersistence(BlueToneCurve),
                ReferenceLutBase64 = ReferenceLutBase64,
                ReferenceLutSize = Math.Max(0, ReferenceLutSize)
            };
        }

        private static double[]? SafeToneCurveForPersistence(double[]? values)
        {
            if (values == null) return null;
            return ToneCurve.CreateFromArray(values, enforceMonotonic: true).ToArray();
        }

        private static TEnum SafeEnum<TEnum>(TEnum value, TEnum fallback)
            where TEnum : struct, Enum =>
            Enum.IsDefined(value) ? value : fallback;

        private bool HasRuntimeCriticalInvalidValues()
        {
            if (!Enum.IsDefined(Mode) || !Enum.IsDefined(TargetColorSpace))
                return true;
            if (SafeGamma(TargetGamma) != TargetGamma)
                return true;
            if (SafeChromaticity(RedPrimaryX, RedPrimaryY, Chromaticity.Rec709Red) != new Chromaticity(RedPrimaryX, RedPrimaryY) ||
                SafeChromaticity(GreenPrimaryX, GreenPrimaryY, Chromaticity.Rec709Green) != new Chromaticity(GreenPrimaryX, GreenPrimaryY) ||
                SafeChromaticity(BluePrimaryX, BluePrimaryY, Chromaticity.Rec709Blue) != new Chromaticity(BluePrimaryX, BluePrimaryY) ||
                SafeChromaticity(WhitePointX, WhitePointY, Chromaticity.D65) != new Chromaticity(WhitePointX, WhitePointY))
                return true;
            if (SafeNonNegative(BlackLevel, 0.0) != BlackLevel ||
                SafePositive(PeakLuminance, 100.0) != PeakLuminance ||
                SafeGamma(MeasuredGamma) != MeasuredGamma)
                return true;

            bool anyToneCurves = RedToneCurve != null || GreenToneCurve != null || BlueToneCurve != null;
            if (!anyToneCurves)
                return false;

            return !IsRuntimeToneCurveValid(RedToneCurve) ||
                   !IsRuntimeToneCurveValid(GreenToneCurve) ||
                   !IsRuntimeToneCurveValid(BlueToneCurve);
        }

        private static bool IsRuntimeToneCurveValid(double[]? values) =>
            values != null &&
            values.Length >= 2 &&
            values.All(v => double.IsFinite(v) && v is >= 0.0 and <= 1.0) &&
            IsMonotonicNonDecreasing(values);

        private static bool IsMonotonicNonDecreasing(double[] values)
        {
            for (int i = 1; i < values.Length; i++)
            {
                if (values[i] < values[i - 1] - 1e-10)
                    return false;
            }

            return true;
        }

        #endregion
    }
}
