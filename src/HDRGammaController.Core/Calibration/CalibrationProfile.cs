using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// A complete calibration profile for a display, including measured data,
    /// correction LUT, and metadata for version management.
    /// </summary>
    /// <remarks>
    /// CalibrationProfile is the top-level container that stores everything
    /// needed to calibrate a display and compare calibration results over time.
    ///
    /// Key features:
    /// - Full versioning with timestamps
    /// - Before/after comparison support
    /// - 3D LUT storage
    /// - Profile history management
    /// - JSON serialization for persistence
    /// </remarks>
    public class CalibrationProfile
    {
        #region Identification

        /// <summary>
        /// Unique identifier for this profile.
        /// </summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>
        /// Monitor device path this profile is for.
        /// </summary>
        public required string MonitorDevicePath { get; init; }

        /// <summary>
        /// Human-readable monitor name (from EDID).
        /// </summary>
        public required string MonitorName { get; init; }

        /// <summary>
        /// Monitor serial number if available.
        /// </summary>
        public string? MonitorSerial { get; init; }

        /// <summary>
        /// User-assigned name for this profile.
        /// </summary>
        public string? ProfileName { get; set; }

        #endregion

        #region Version Management

        /// <summary>
        /// Profile version number. Incremented with each calibration.
        /// </summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// UTC timestamp when this profile was created.
        /// </summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// UTC timestamp when this profile was last modified.
        /// </summary>
        public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC timestamp of the last calibration measurement.
        /// </summary>
        public DateTime? LastCalibratedAt { get; set; }

        /// <summary>
        /// Whether this is the currently active profile for the monitor.
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// User notes about this profile or calibration.
        /// </summary>
        public string? Notes { get; set; }

        #endregion

        #region Calibration Target

        /// <summary>
        /// The calibration target (color space, gamma, etc.) this profile was made for.
        /// </summary>
        public required CalibrationTarget Target { get; init; }

        #endregion

        #region Correction Data

        /// <summary>
        /// The 3D correction LUT (serialized as bytes for JSON storage).
        /// </summary>
        [JsonIgnore]
        public Lut3D? CorrectionLut { get; set; }

        /// <summary>
        /// Serialized 3D LUT data for JSON persistence.
        /// </summary>
        public byte[]? CorrectionLutData
        {
            get => CorrectionLut?.ToBytes();
            set => CorrectionLut = value != null ? Lut3D.FromBytes(value) : null;
        }

        /// <summary>
        /// 3D LUT size used for this calibration.
        /// </summary>
        public int LutSize { get; set; } = 17;

        #endregion

        #region Measured Characteristics

        /// <summary>
        /// Display characteristics measured during calibration.
        /// </summary>
        public DisplayCharacteristics? MeasuredCharacteristics { get; set; }

        #endregion

        #region Quality Metrics

        /// <summary>
        /// Overall Delta E average before calibration.
        /// </summary>
        public double? PreCalibrationDeltaE { get; set; }

        /// <summary>
        /// Overall Delta E average after calibration.
        /// </summary>
        public double? PostCalibrationDeltaE { get; set; }

        /// <summary>
        /// Delta E improvement (pre - post).
        /// </summary>
        public double? DeltaEImprovement => PreCalibrationDeltaE - PostCalibrationDeltaE;

        /// <summary>
        /// Calibration quality grade.
        /// </summary>
        public CalibrationGrade? QualityGrade { get; set; }

        /// <summary>
        /// Detailed calibration report (may be large, consider separate storage).
        /// </summary>
        [JsonIgnore]
        public CalibrationReport? Report { get; set; }

        #endregion

        #region Calibration Settings

        /// <summary>
        /// Number of patches measured during calibration.
        /// </summary>
        public int PatchCount { get; set; }

        /// <summary>
        /// Colorimeter model used for measurements.
        /// </summary>
        public string? ColorimeterModel { get; set; }

        /// <summary>
        /// Software version used for calibration.
        /// </summary>
        public string? SoftwareVersion { get; set; }

        #endregion

        #region Profile History

        /// <summary>
        /// Previous versions of this profile for comparison.
        /// </summary>
        [JsonIgnore]
        public List<CalibrationProfileSnapshot> History { get; } = new();

        /// <summary>
        /// Maximum number of history entries to keep.
        /// </summary>
        public static int MaxHistoryEntries { get; set; } = 10;

        /// <summary>
        /// Creates a snapshot of the current profile state for history.
        /// </summary>
        public CalibrationProfileSnapshot CreateSnapshot()
        {
            return new CalibrationProfileSnapshot
            {
                Id = Guid.NewGuid(),
                ProfileId = Id,
                Version = Version,
                Timestamp = ModifiedAt,
                TargetName = Target.Name,
                PreCalibrationDeltaE = PreCalibrationDeltaE,
                PostCalibrationDeltaE = PostCalibrationDeltaE,
                QualityGrade = QualityGrade,
                PeakLuminance = MeasuredCharacteristics?.PeakLuminance,
                WhiteCct = MeasuredCharacteristics?.MeasuredCct,
                Notes = Notes
            };
        }

        /// <summary>
        /// Adds current state to history and increments version.
        /// </summary>
        public void AdvanceVersion()
        {
            History.Add(CreateSnapshot());

            // Trim history if needed
            while (History.Count > MaxHistoryEntries)
                History.RemoveAt(0);

            Version++;
            ModifiedAt = DateTime.UtcNow;
        }

        #endregion

        #region Persistence

        /// <summary>
        /// Saves the profile to a JSON file.
        /// </summary>
        public void SaveToFile(string path)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            // Create a serializable version
            var data = new CalibrationProfileData
            {
                Id = Id,
                MonitorDevicePath = MonitorDevicePath,
                MonitorName = MonitorName,
                MonitorSerial = MonitorSerial,
                ProfileName = ProfileName,
                Version = Version,
                CreatedAt = CreatedAt,
                ModifiedAt = ModifiedAt,
                LastCalibratedAt = LastCalibratedAt,
                IsActive = IsActive,
                Notes = Notes,
                TargetName = Target.Name,
                LutSize = LutSize,
                CorrectionLutData = CorrectionLutData,
                MeasuredCharacteristics = MeasuredCharacteristics,
                PreCalibrationDeltaE = PreCalibrationDeltaE,
                PostCalibrationDeltaE = PostCalibrationDeltaE,
                QualityGrade = QualityGrade,
                PatchCount = PatchCount,
                ColorimeterModel = ColorimeterModel,
                SoftwareVersion = SoftwareVersion
            };

            string json = JsonSerializer.Serialize(data, options);
            // Write-then-rename: a crash mid-write can't corrupt an existing profile.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>
        /// Loads a profile from a JSON file.
        /// </summary>
        public static CalibrationProfile LoadFromFile(string path)
        {
            string json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<CalibrationProfileData>(json)
                       ?? throw new InvalidDataException("Failed to deserialize profile");

            // Find the matching target
            var target = StandardTargets.GetByName(data.TargetName ?? "sRGB")
                         ?? StandardTargets.SrgbGamma22;

            var profile = new CalibrationProfile
            {
                Id = data.Id,
                MonitorDevicePath = data.MonitorDevicePath ?? "",
                MonitorName = data.MonitorName ?? "Unknown",
                MonitorSerial = data.MonitorSerial,
                ProfileName = data.ProfileName,
                Version = data.Version,
                CreatedAt = data.CreatedAt,
                ModifiedAt = data.ModifiedAt,
                LastCalibratedAt = data.LastCalibratedAt,
                IsActive = data.IsActive,
                Notes = data.Notes,
                Target = target,
                LutSize = data.LutSize,
                CorrectionLutData = data.CorrectionLutData,
                MeasuredCharacteristics = data.MeasuredCharacteristics,
                PreCalibrationDeltaE = data.PreCalibrationDeltaE,
                PostCalibrationDeltaE = data.PostCalibrationDeltaE,
                QualityGrade = data.QualityGrade,
                PatchCount = data.PatchCount,
                ColorimeterModel = data.ColorimeterModel,
                SoftwareVersion = data.SoftwareVersion
            };

            return profile;
        }

        /// <summary>
        /// Gets the default profile directory.
        /// </summary>
        public static string GetProfileDirectory()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HDRGammaController", "Profiles");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Gets the file path for this profile.
        /// </summary>
        public string GetFilePath()
        {
            string safeName = string.Join("_", MonitorName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(GetProfileDirectory(), $"{safeName}_{Id:N}.json");
        }

        #endregion
    }

    /// <summary>
    /// A lightweight snapshot of a calibration profile for history tracking.
    /// </summary>
    public class CalibrationProfileSnapshot
    {
        public Guid Id { get; init; }
        public Guid ProfileId { get; init; }
        public int Version { get; init; }
        public DateTime Timestamp { get; init; }
        public string? TargetName { get; init; }
        public double? PreCalibrationDeltaE { get; init; }
        public double? PostCalibrationDeltaE { get; init; }
        public CalibrationGrade? QualityGrade { get; init; }
        public double? PeakLuminance { get; init; }
        public double? WhiteCct { get; init; }
        public string? Notes { get; init; }

        public double? DeltaEImprovement => PreCalibrationDeltaE - PostCalibrationDeltaE;

        public override string ToString() =>
            $"v{Version} ({Timestamp:d}) - {TargetName}, ΔE: {PostCalibrationDeltaE:F1}";
    }

    /// <summary>
    /// Internal class for JSON serialization of CalibrationProfile.
    /// </summary>
    internal class CalibrationProfileData
    {
        public Guid Id { get; set; }
        public string? MonitorDevicePath { get; set; }
        public string? MonitorName { get; set; }
        public string? MonitorSerial { get; set; }
        public string? ProfileName { get; set; }
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ModifiedAt { get; set; }
        public DateTime? LastCalibratedAt { get; set; }
        public bool IsActive { get; set; }
        public string? Notes { get; set; }
        public string? TargetName { get; set; }
        public int LutSize { get; set; }
        public byte[]? CorrectionLutData { get; set; }
        public DisplayCharacteristics? MeasuredCharacteristics { get; set; }
        public double? PreCalibrationDeltaE { get; set; }
        public double? PostCalibrationDeltaE { get; set; }
        public CalibrationGrade? QualityGrade { get; set; }
        public int PatchCount { get; set; }
        public string? ColorimeterModel { get; set; }
        public string? SoftwareVersion { get; set; }
    }

    /// <summary>
    /// Manages calibration profiles for all monitors.
    /// </summary>
    public class CalibrationProfileManager
    {
        private readonly Dictionary<string, CalibrationProfile> _profiles = new();

        /// <summary>
        /// Gets all loaded profiles.
        /// </summary>
        public IReadOnlyDictionary<string, CalibrationProfile> Profiles => _profiles;

        /// <summary>
        /// Gets the active profile for a monitor.
        /// </summary>
        public CalibrationProfile? GetActiveProfile(string monitorDevicePath)
        {
            return _profiles.TryGetValue(monitorDevicePath, out var profile) && profile.IsActive
                ? profile
                : null;
        }

        /// <summary>
        /// Sets a profile as active for its monitor.
        /// </summary>
        public void SetActiveProfile(CalibrationProfile profile)
        {
            // Deactivate any existing profile for this monitor
            if (_profiles.TryGetValue(profile.MonitorDevicePath, out var existing))
            {
                existing.IsActive = false;
            }

            profile.IsActive = true;
            _profiles[profile.MonitorDevicePath] = profile;
        }

        /// <summary>
        /// Loads all profiles from the profile directory.
        /// </summary>
        public void LoadAllProfiles()
        {
            var dir = CalibrationProfile.GetProfileDirectory();
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var profile = CalibrationProfile.LoadFromFile(file);
                    _profiles[profile.MonitorDevicePath] = profile;
                }
                catch (Exception ex)
                {
                    Log.Info($"Failed to load profile {file}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Saves all profiles to the profile directory.
        /// </summary>
        public void SaveAllProfiles()
        {
            foreach (var profile in _profiles.Values)
            {
                try
                {
                    profile.SaveToFile(profile.GetFilePath());
                }
                catch (Exception ex)
                {
                    Log.Info($"Failed to save profile for {profile.MonitorName}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets all profiles for a specific monitor, including historical versions.
        /// </summary>
        public IEnumerable<CalibrationProfileSnapshot> GetProfileHistory(string monitorDevicePath)
        {
            if (_profiles.TryGetValue(monitorDevicePath, out var profile))
            {
                foreach (var snapshot in profile.History)
                {
                    yield return snapshot;
                }
                yield return profile.CreateSnapshot();
            }
        }
    }
}
