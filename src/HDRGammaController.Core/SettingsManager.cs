using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Per-monitor profile data stored in settings.
    /// </summary>
    public class AppExclusionRule
    {
        public string AppName { get; set; } = string.Empty;
        public bool FullDisable { get; set; } = false;
    }

    /// <summary>
    /// Per-monitor profile data stored in settings.
    /// </summary>
    public class MonitorProfileData
    {
        public GammaMode GammaMode { get; set; } = GammaMode.Gamma22;
        public double Brightness { get; set; } = 100.0;
        public bool UseLinearBrightness { get; set; } = false;
        public double Temperature { get; set; } = 0.0;
        public double TemperatureOffset { get; set; } = 0.0;
        public double Tint { get; set; } = 0.0;
        public double RedGain { get; set; } = 1.0;
        public double GreenGain { get; set; } = 1.0;
        public double BlueGain { get; set; } = 1.0;
        public double RedOffset { get; set; } = 0.0;
        public double GreenOffset { get; set; } = 0.0;
        public double BlueOffset { get; set; } = 0.0;

        /// <summary>
        /// ID of the active calibration profile for this monitor.
        /// Null if no calibration profile is active.
        /// </summary>
        public string? CalibrationProfileId { get; set; }

        /// <summary>
        /// Whether to use the calibration profile for gamma adjustments (Adaptive mode).
        /// When true, gamma switching uses the measured display response for accurate compensation.
        /// </summary>
        public bool UseCalibrationForGamma { get; set; } = true;

        /// <summary>
        /// Filename of the installed native MHC2 calibration profile for this monitor, if any.
        /// Its presence means Windows is applying the full gamut+tone correction at the
        /// compositor, so the app's GPU gamma ramp must NOT re-apply a tone curve (it would
        /// double the gamma) — it should layer only night-mode warmth on top.
        /// </summary>
        public string? Mhc2ProfileName { get; set; }

        /// <summary>
        /// Colorimeter spectral correction file (.ccss/.ccmx) for this monitor's panel
        /// type. Three-filter colorimeters like the i1 Display read narrow-spectrum panels
        /// (QD-OLED especially) wrong without one — typically a green/magenta white error
        /// the calibration then faithfully reproduces.
        /// </summary>
        public string? MeterCorrectionPath { get; set; }
        
        public CalibrationSettings ToCalibrationSettings() => new CalibrationSettings
        {
            Brightness = Brightness,
            UseLinearBrightness = UseLinearBrightness,
            Temperature = Temperature,
            TemperatureOffset = TemperatureOffset,
            Tint = Tint,
            RedGain = RedGain,
            GreenGain = GreenGain,
            BlueGain = BlueGain,
            RedOffset = RedOffset,
            GreenOffset = GreenOffset,
            BlueOffset = BlueOffset
        };
        
        public static MonitorProfileData FromCalibrationSettings(CalibrationSettings settings, GammaMode mode) => new MonitorProfileData
        {
            GammaMode = mode,
            Brightness = settings.Brightness,
            UseLinearBrightness = settings.UseLinearBrightness,
            Temperature = settings.Temperature,
            TemperatureOffset = settings.TemperatureOffset,
            Tint = settings.Tint,
            RedGain = settings.RedGain,
            GreenGain = settings.GreenGain,
            BlueGain = settings.BlueGain,
            RedOffset = settings.RedOffset,
            GreenOffset = settings.GreenOffset,
            BlueOffset = settings.BlueOffset
        };
        
        public MonitorProfileData Clone() => new MonitorProfileData
        {
            GammaMode = GammaMode,
            Brightness = Brightness,
            UseLinearBrightness = UseLinearBrightness,
            Temperature = Temperature,
            TemperatureOffset = TemperatureOffset,
            Tint = Tint,
            RedGain = RedGain,
            GreenGain = GreenGain,
            BlueGain = BlueGain,
            RedOffset = RedOffset,
            GreenOffset = GreenOffset,
            BlueOffset = BlueOffset
        };
    }
    
    /// <summary>
    /// Night mode settings stored in settings file.
    /// </summary>
    public class NightModeSettingsData
    {
        public bool Enabled { get; set; } = false;
        public bool UseAutoSchedule { get; set; } = false;
        public double? Latitude { get; set; } = null;
        public double? Longitude { get; set; } = null;
        public string StartTime { get; set; } = "21:00";
        public string EndTime { get; set; } = "07:00";
        public int TemperatureKelvin { get; set; } = 2700;
        public int FadeMinutes { get; set; } = 30;
        public NightModeAlgorithm Algorithm { get; set; } = NightModeAlgorithm.Standard;
        public bool UseUltraWarmMode { get; set; } = false;

        public List<NightModeSchedulePoint> Schedule { get; set; } = new List<NightModeSchedulePoint>();
        
        public NightModeSettings ToNightModeSettings() => new NightModeSettings
        {
            Enabled = Enabled,
            UseAutoSchedule = UseAutoSchedule,
            Latitude = Latitude,
            Longitude = Longitude,
            StartTime = TimeSpan.TryParse(StartTime, out var start) ? start : new TimeSpan(21, 0, 0),
            EndTime = TimeSpan.TryParse(EndTime, out var end) ? end : new TimeSpan(7, 0, 0),
            TemperatureKelvin = TemperatureKelvin,
            FadeMinutes = FadeMinutes,
            Algorithm = Algorithm,
            UseUltraWarmMode = UseUltraWarmMode,
            Schedule = Schedule ?? new List<NightModeSchedulePoint>()
        };

        public static NightModeSettingsData FromNightModeSettings(NightModeSettings settings) => new NightModeSettingsData
        {
            Enabled = settings.Enabled,
            UseAutoSchedule = settings.UseAutoSchedule,
            Latitude = settings.Latitude,
            Longitude = settings.Longitude,
            StartTime = settings.StartTime.ToString(@"hh\:mm"),
            EndTime = settings.EndTime.ToString(@"hh\:mm"),
            TemperatureKelvin = settings.TemperatureKelvin,
            FadeMinutes = settings.FadeMinutes,
            Algorithm = settings.Algorithm,
            UseUltraWarmMode = settings.UseUltraWarmMode,
            Schedule = settings.Schedule ?? new List<NightModeSchedulePoint>()
        };
    }
    
    public class SettingsManager
    {
        // Use LocalApplicationData to avoid Resilio Sync corruption
        private static readonly string AppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HDRGammaController");
        
        private static readonly string SettingsFilePath = Path.Combine(AppDataPath, "settings.json");
        
        private SettingsData _data = new SettingsData();

        public NightModeSettings NightMode => _data.NightMode.ToNightModeSettings();
        
        public event Action<NightModeSettings>? NightModeChanged;
        
        public void NotifyNightModeChanged(NightModeSettings? settings = null)
        {
            // Invoke with provided settings or current
             NightModeChanged?.Invoke(settings ?? NightMode);
        }

        public SettingsManager()
        {
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var options = new JsonSerializerOptions 
                    { 
                        Converters = { new JsonStringEnumConverter() },
                        PropertyNameCaseInsensitive = true
                    };

                    try
                    {
                        _data = JsonSerializer.Deserialize<SettingsData>(json, options) ?? new SettingsData();
                        ValidateAndClampSettings(_data);
                    }
                    catch (Exception ex)
                    {
                        // Fallback: Try defining a legacy structure or just reset ExcludedApps
                        Log.Info($"SettingsManager: Primary deserialization failed ({ex.Message}), attempting legacy migration...");
                        try 
                        {
                            var legacy = JsonSerializer.Deserialize<LegacySettingsData>(json, options);
                            if (legacy != null)
                            {
                                _data = new SettingsData 
                                {
                                    MonitorProfiles = legacy.MonitorProfiles,
                                    NightMode = legacy.NightMode,
                                    ExcludedApps = legacy.ExcludedApps?.Select(path => new AppExclusionRule { AppName = path, FullDisable = false }).ToList() ?? new List<AppExclusionRule>()
                                };
                                Log.Info("SettingsManager: Legacy migration successful.");
                                Save(); // Save immediately in new format
                            }
                        }
                        catch (Exception innerEx)
                        {
                            Log.Info($"SettingsManager: Legacy migration failed ({innerEx.Message}). Using defaults.");
                            // Backup corrupted file
                            try { File.Copy(SettingsFilePath, SettingsFilePath + $".bak-{DateTime.Now.Ticks}", true); } catch { }
                            _data = new SettingsData();
                        }
                    }
                    
                    Log.Info($"SettingsManager: Loaded {_data.MonitorProfiles.Count} monitor profiles.");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"SettingsManager: Failed to load settings: {ex.Message}");
                _data = new SettingsData();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppDataPath);
                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                string json = JsonSerializer.Serialize(_data, options);
                // Write-then-rename so a crash mid-write can't leave a truncated settings.json.
                string tempPath = SettingsFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, SettingsFilePath, overwrite: true);
                Log.Info($"SettingsManager: Saved {_data.MonitorProfiles.Count} monitor profiles.");
            }
            catch (Exception ex)
            {
                Log.Info($"SettingsManager: Failed to save settings: {ex.Message}");
            }
        }

        public GammaMode? GetProfileForMonitor(string monitorDevicePath)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return null;
            
            if (_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile))
            {
                return profile.GammaMode;
            }
            return null;
        }

        public void SetProfileForMonitor(string monitorDevicePath, GammaMode mode)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return;

            if (!_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile))
            {
                profile = new MonitorProfileData();
                _data.MonitorProfiles[monitorDevicePath] = profile;
            }
            else if (profile.GammaMode == mode)
            {
                // Called on every apply (including each night-mode fade step); skip the
                // serialize-and-write when nothing actually changed.
                return;
            }
            profile.GammaMode = mode;
            Save();
        }
        
        public MonitorProfileData? GetMonitorProfile(string monitorDevicePath)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return null;
            _data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile);
            return profile;
        }
        
        public void SetMonitorProfile(string monitorDevicePath, MonitorProfileData profile)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return;
            if (profile == null)
            {
                Log.Info($"SettingsManager.SetMonitorProfile: WARNING - Null profile for {monitorDevicePath}, skipping save");
                return;
            }
            Log.Info($"SettingsManager.SetMonitorProfile: Saving {monitorDevicePath} - Brightness={profile.Brightness}, Gamma={profile.GammaMode}");
            _data.MonitorProfiles[monitorDevicePath] = profile;
            Save();
        }
        
        public void SetNightMode(NightModeSettings settings)
        {
            _data.NightMode = NightModeSettingsData.FromNightModeSettings(settings);
            Save();
            NightModeChanged?.Invoke(settings);
        }

        /// <summary>
        /// Records (or clears, with null) the installed native MHC2 calibration profile for a
        /// monitor. Used by the apply path to compose night mode on top without double-gamma.
        /// </summary>
        public void SetMhc2Calibration(string monitorDevicePath, string? profileName)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return;
            if (!_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile))
            {
                profile = new MonitorProfileData();
                _data.MonitorProfiles[monitorDevicePath] = profile;
            }
            profile.Mhc2ProfileName = profileName;
            Save();
        }

        /// <summary>Records (or clears) the meter spectral correction file for a monitor.</summary>
        public void SetMeterCorrection(string monitorDevicePath, string? ccssPath)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return;
            if (!_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile))
            {
                profile = new MonitorProfileData();
                _data.MonitorProfiles[monitorDevicePath] = profile;
            }
            profile.MeterCorrectionPath = ccssPath;
            Save();
        }

        /// <summary>True if a native MHC2 calibration is installed for this monitor.</summary>
        public bool HasMhc2Calibration(string monitorDevicePath)
            => !string.IsNullOrEmpty(GetMonitorProfile(monitorDevicePath)?.Mhc2ProfileName);

        public List<AppExclusionRule> ExcludedApps => _data.ExcludedApps;

        public void SetExcludedApps(List<AppExclusionRule> apps)
        {
            _data.ExcludedApps = apps ?? new List<AppExclusionRule>();
            Save();
        }

        #region Calibration Profile Management

        private static readonly string CalibrationProfilesPath = Path.Combine(AppDataPath, "CalibrationProfiles");

        /// <summary>
        /// Saves a calibration profile to disk.
        /// </summary>
        public void SaveCalibrationProfile(DisplayCalibrationProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            Directory.CreateDirectory(CalibrationProfilesPath);
            string filePath = Path.Combine(CalibrationProfilesPath, $"{profile.Id}.json");
            profile.SaveToFile(filePath);
            Log.Info($"SettingsManager: Saved calibration profile '{profile.Name}' to {filePath}");
        }

        /// <summary>
        /// Loads a calibration profile by ID.
        /// </summary>
        public DisplayCalibrationProfile? LoadCalibrationProfile(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return null;

            string filePath = Path.Combine(CalibrationProfilesPath, $"{profileId}.json");
            if (!File.Exists(filePath))
            {
                Log.Info($"SettingsManager: Calibration profile not found: {filePath}");
                return null;
            }

            var profile = DisplayCalibrationProfile.LoadFromFile(filePath);
            Log.Info($"SettingsManager: Loaded calibration profile '{profile?.Name}' from {filePath}");
            return profile;
        }

        /// <summary>
        /// Lists all available calibration profiles.
        /// </summary>
        public List<DisplayCalibrationProfile> ListCalibrationProfiles()
        {
            var profiles = new List<DisplayCalibrationProfile>();

            if (!Directory.Exists(CalibrationProfilesPath))
                return profiles;

            foreach (var file in Directory.GetFiles(CalibrationProfilesPath, "*.json"))
            {
                try
                {
                    var profile = DisplayCalibrationProfile.LoadFromFile(file);
                    if (profile != null)
                        profiles.Add(profile);
                }
                catch (Exception ex)
                {
                    Log.Info($"SettingsManager: Failed to load profile from {file}: {ex.Message}");
                }
            }

            return profiles.OrderByDescending(p => p.CalibratedAt).ToList();
        }

        /// <summary>
        /// Lists calibration profiles for a specific monitor.
        /// </summary>
        public List<DisplayCalibrationProfile> ListCalibrationProfilesForMonitor(string monitorDevicePath)
        {
            return ListCalibrationProfiles()
                .Where(p => p.MonitorDevicePath == monitorDevicePath)
                .ToList();
        }

        /// <summary>
        /// Deletes a calibration profile by ID.
        /// </summary>
        public bool DeleteCalibrationProfile(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return false;

            string filePath = Path.Combine(CalibrationProfilesPath, $"{profileId}.json");
            if (!File.Exists(filePath))
                return false;

            try
            {
                File.Delete(filePath);
                Log.Info($"SettingsManager: Deleted calibration profile: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Info($"SettingsManager: Failed to delete profile {profileId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the active calibration profile for a monitor, if any.
        /// </summary>
        public DisplayCalibrationProfile? GetActiveCalibrationProfile(string monitorDevicePath)
        {
            var monitorProfile = GetMonitorProfile(monitorDevicePath);
            if (monitorProfile?.CalibrationProfileId == null)
                return null;

            return LoadCalibrationProfile(monitorProfile.CalibrationProfileId);
        }

        /// <summary>
        /// Sets the active calibration profile for a monitor.
        /// </summary>
        public void SetActiveCalibrationProfile(string monitorDevicePath, string? profileId)
        {
            var monitorProfile = GetMonitorProfile(monitorDevicePath);
            if (monitorProfile == null)
            {
                monitorProfile = new MonitorProfileData();
            }

            monitorProfile.CalibrationProfileId = profileId;
            SetMonitorProfile(monitorDevicePath, monitorProfile);
            Log.Info($"SettingsManager: Set active calibration profile for {monitorDevicePath} to {profileId ?? "none"}");
        }

        #endregion

        /// <summary>
        /// Validates and clamps all settings to safe ranges to prevent malicious/corrupted values.
        /// </summary>
        private static void ValidateAndClampSettings(SettingsData data)
        {
            if (data == null) return;

            // Validate monitor profiles
            foreach (var profile in data.MonitorProfiles.Values)
            {
                if (profile == null) continue;

                // Brightness: 10-100%
                profile.Brightness = Math.Clamp(profile.Brightness, 10.0, 100.0);

                // Temperature offset: -50 to +50
                profile.Temperature = Math.Clamp(profile.Temperature, -50.0, 50.0);
                profile.TemperatureOffset = Math.Clamp(profile.TemperatureOffset, -50.0, 50.0);

                // Tint: -50 to +50
                profile.Tint = Math.Clamp(profile.Tint, -50.0, 50.0);

                // RGB Gains: 0.5 to 1.5
                profile.RedGain = Math.Clamp(profile.RedGain, 0.5, 1.5);
                profile.GreenGain = Math.Clamp(profile.GreenGain, 0.5, 1.5);
                profile.BlueGain = Math.Clamp(profile.BlueGain, 0.5, 1.5);

                // RGB Offsets: -0.5 to +0.5
                profile.RedOffset = Math.Clamp(profile.RedOffset, -0.5, 0.5);
                profile.GreenOffset = Math.Clamp(profile.GreenOffset, -0.5, 0.5);
                profile.BlueOffset = Math.Clamp(profile.BlueOffset, -0.5, 0.5);
            }

            // Validate night mode settings
            var nm = data.NightMode;
            if (nm != null)
            {
                // Latitude: -90 to +90
                if (nm.Latitude.HasValue)
                    nm.Latitude = Math.Clamp(nm.Latitude.Value, -90.0, 90.0);

                // Longitude: -180 to +180
                if (nm.Longitude.HasValue)
                    nm.Longitude = Math.Clamp(nm.Longitude.Value, -180.0, 180.0);

                // Temperature: 1900K to 6500K (valid color temperature range)
                nm.TemperatureKelvin = Math.Clamp(nm.TemperatureKelvin, 1900, 6500);

                // Fade duration: 0 to 120 minutes
                nm.FadeMinutes = Math.Clamp(nm.FadeMinutes, 0, 120);

                // Validate schedule points
                if (nm.Schedule != null)
                {
                    foreach (var point in nm.Schedule)
                    {
                        if (point == null) continue;
                        // Clamp TargetKelvin to valid range
                        point.TargetKelvin = Math.Clamp(point.TargetKelvin, 1900, 6500);
                        // Clamp FadeMinutes to valid range
                        point.FadeMinutes = Math.Clamp(point.FadeMinutes, 0, 120);
                        // Clamp OffsetMinutes to reasonable range (-120 to +120)
                        point.OffsetMinutes = Math.Clamp(point.OffsetMinutes, -120.0, 120.0);
                    }
                }
            }
        }

        private class SettingsData
        {
            public Dictionary<string, MonitorProfileData> MonitorProfiles { get; set; } = new Dictionary<string, MonitorProfileData>();
            public NightModeSettingsData NightMode { get; set; } = new NightModeSettingsData();
            public List<AppExclusionRule> ExcludedApps { get; set; } = new List<AppExclusionRule>();
        }

        private class LegacySettingsData
        {
            public Dictionary<string, MonitorProfileData> MonitorProfiles { get; set; } = new Dictionary<string, MonitorProfileData>();
            public NightModeSettingsData NightMode { get; set; } = new NightModeSettingsData();
            public List<string> ExcludedApps { get; set; } = new List<string>();
        }
    }
}
