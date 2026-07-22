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

        internal AppExclusionRule Clone() => new AppExclusionRule
        {
            AppName = AppName,
            FullDisable = FullDisable
        };

        public static string NormalizeAppName(string? appName)
        {
            string normalized = appName?.Trim().Trim('"') ?? string.Empty;
            if (normalized.Length == 0) return string.Empty;

            // Process.GetProcessById reports a base executable name, while users may paste
            // a full path into the editable picker. Store the same basename format emitted
            // by AppDetectionService so the foreground lookup can actually match it.
            normalized = Path.GetFileName(normalized);
            if (normalized.Length == 0 || normalized.Length > 260) return string.Empty;

            if (!normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                normalized += ".exe";

            return normalized;
        }
    }

    public class WindowBoundsData
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        public WindowBoundsData Clone() => new WindowBoundsData
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height
        };
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
        /// Windows color profile that was the display default before Gloam made an MHC2
        /// calibration active. Kept so explicit deactivation/delete can restore the user's
        /// prior color-management state instead of leaving the display with no default.
        /// </summary>
        public string? PreviousColorProfileName { get; set; }

        /// <summary>Whether <see cref="PreviousColorProfileName"/> came from HDR/Advanced Color.</summary>
        public bool? PreviousColorProfileHdrMode { get; set; }

        /// <summary>
        /// Colorimeter spectral correction file (.ccss/.ccmx) for this monitor's panel
        /// type. Three-filter colorimeters like the i1 Display read narrow-spectrum panels
        /// (QD-OLED especially) wrong without one — typically a green/magenta white error
        /// the calibration then faithfully reproduces.
        /// </summary>
        public string? MeterCorrectionPath { get; set; }

        /// <summary>
        /// Optional DisplayCAL/Argyll .ccss spectral sample used to estimate per-primary
        /// melanopic weights for Advanced Ultra Night. Unlike MeterCorrectionPath, this is
        /// used without a colorimeter and only affects night-mode rendering.
        /// </summary>
        public string? NightModeCcssPath { get; set; }

        /// <summary>Last-used display type in calibration setup (DisplayType enum name).</summary>
        public string? CalibDisplayType { get; set; }

        /// <summary>Last-used "white point correction only" choice in calibration setup.</summary>
        public bool? CalibWhitePointOnly { get; set; }

        /// <summary>Last-used calibration target name in calibration setup.</summary>
        public string? CalibTargetName { get; set; }

        /// <summary>Last-used calibration preset in calibration setup (CalibrationPreset enum name).</summary>
        public string? CalibPreset { get; set; }
        
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
            BlueOffset = BlueOffset,
            NightModeCcssPath = NightModeCcssPath
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
            BlueOffset = settings.BlueOffset,
            NightModeCcssPath = settings.NightModeCcssPath
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
            BlueOffset = BlueOffset,
            CalibrationProfileId = CalibrationProfileId,
            UseCalibrationForGamma = UseCalibrationForGamma,
            Mhc2ProfileName = Mhc2ProfileName,
            PreviousColorProfileName = PreviousColorProfileName,
            PreviousColorProfileHdrMode = PreviousColorProfileHdrMode,
            MeterCorrectionPath = MeterCorrectionPath,
            NightModeCcssPath = NightModeCcssPath,
            CalibDisplayType = CalibDisplayType,
            CalibWhitePointOnly = CalibWhitePointOnly,
            CalibTargetName = CalibTargetName,
            CalibPreset = CalibPreset
        };
    }
    
    /// <summary>
    /// Night mode settings stored in settings file.
    /// </summary>
    public class NightModeSettingsData
    {
        public bool Enabled { get; set; } = false;
        public bool ManualOverrideEnabled { get; set; } = false;
        public bool UseAutoSchedule { get; set; } = false;
        public double? Latitude { get; set; } = null;
        public double? Longitude { get; set; } = null;
        public string StartTime { get; set; } = "21:00";
        public string EndTime { get; set; } = "07:00";
        public int TemperatureKelvin { get; set; } = 2700;
        public int FadeMinutes { get; set; } = 30;
        public NightModeAlgorithm Algorithm { get; set; } = NightModeAlgorithm.Perceptual;
        public bool UseUltraWarmMode { get; set; } = false;
        public double PerceptualStrength { get; set; } = ColorAdjustments.DefaultPerceptualStrength;
        // Constant-Y night mode (3.3). Bool defaulting false: absent in older settings files
        // deserializes to off — no schema bump needed.
        public bool PreserveLuminance { get; set; } = false;
        // Dose-based ceiling (3.2), mel-lux; 0/absent = off — no schema bump needed.
        public double MelanopicEdiCeiling { get; set; } = 0.0;

        public List<NightModeSchedulePoint> Schedule { get; set; } = new List<NightModeSchedulePoint>();
        
        public NightModeSettings ToNightModeSettings() => new NightModeSettings
        {
            Enabled = Enabled,
            ManualOverrideEnabled = ManualOverrideEnabled,
            UseAutoSchedule = UseAutoSchedule,
            Latitude = NightModeSettings.ClampLatitude(Latitude),
            Longitude = NightModeSettings.ClampLongitude(Longitude),
            StartTime = TimeSpan.TryParse(StartTime, out var start)
                ? NightModeSettings.NormalizeTimeOfDay(start)
                : new TimeSpan(21, 0, 0),
            EndTime = TimeSpan.TryParse(EndTime, out var end)
                ? NightModeSettings.NormalizeTimeOfDay(end)
                : new TimeSpan(7, 0, 0),
            TemperatureKelvin = NightModeSettings.ClampKelvin(TemperatureKelvin),
            FadeMinutes = NightModeSettings.ClampFadeMinutes(FadeMinutes),
            Algorithm = ResolveAlgorithm(Algorithm),
            UseUltraWarmMode = UseUltraWarmMode,
            PerceptualStrength = NightModeSettings.ClampPerceptualStrength(PerceptualStrength),
            PreserveLuminance = PreserveLuminance,
            MelanopicEdiCeiling = NightModeSettings.ClampMelanopicCeiling(MelanopicEdiCeiling),
            Schedule = CloneSchedule(Schedule)
        };

        /// <summary>
        /// Blue reduction was retired as a selectable mode; migrate it — and any unknown value —
        /// to the perceptual default. The enum member is kept so old settings still deserialize.
        /// </summary>
        private static NightModeAlgorithm ResolveAlgorithm(NightModeAlgorithm algorithm) =>
            algorithm == NightModeAlgorithm.BlueReduction || !Enum.IsDefined(typeof(NightModeAlgorithm), algorithm)
                ? NightModeAlgorithm.Perceptual
                : algorithm;

        public static NightModeSettingsData FromNightModeSettings(NightModeSettings settings) => new NightModeSettingsData
        {
            Enabled = settings.Enabled,
            ManualOverrideEnabled = settings.ManualOverrideEnabled,
            UseAutoSchedule = settings.UseAutoSchedule,
            Latitude = NightModeSettings.ClampLatitude(settings.Latitude),
            Longitude = NightModeSettings.ClampLongitude(settings.Longitude),
            StartTime = NightModeSettings.NormalizeTimeOfDay(settings.StartTime).ToString(@"hh\:mm"),
            EndTime = NightModeSettings.NormalizeTimeOfDay(settings.EndTime).ToString(@"hh\:mm"),
            TemperatureKelvin = NightModeSettings.ClampKelvin(settings.TemperatureKelvin),
            FadeMinutes = NightModeSettings.ClampFadeMinutes(settings.FadeMinutes),
            Algorithm = settings.Algorithm,
            UseUltraWarmMode = settings.UseUltraWarmMode,
            PerceptualStrength = NightModeSettings.ClampPerceptualStrength(settings.PerceptualStrength),
            PreserveLuminance = settings.PreserveLuminance,
            MelanopicEdiCeiling = NightModeSettings.ClampMelanopicCeiling(settings.MelanopicEdiCeiling),
            Schedule = CloneSchedule(settings.Schedule)
        };

        private static List<NightModeSchedulePoint> CloneSchedule(IEnumerable<NightModeSchedulePoint>? schedule)
        {
            var cloned = new List<NightModeSchedulePoint>();
            if (schedule == null) return cloned;

            foreach (var point in schedule)
            {
                if (point == null) continue;
                cloned.Add(new NightModeSchedulePoint
                {
                    TriggerType = point.TriggerType,
                    Time = NightModeSettings.NormalizeTimeOfDay(point.Time),
                    OffsetMinutes = NightModeSettings.ClampOffsetMinutes(point.OffsetMinutes),
                    TargetKelvin = NightModeSettings.ClampKelvin(point.TargetKelvin),
                    FadeMinutes = NightModeSettings.ClampFadeMinutes(point.FadeMinutes)
                });
            }

            return cloned;
        }
    }
    
    /// <summary>
    /// Enum converter that never throws on unknown values: an unrecognized enum string in
    /// settings.json (e.g. a NightModeAlgorithm added by a newer build) deserializes to the
    /// enum's default value instead of failing the whole settings load. Writing matches
    /// JsonStringEnumConverter (member name for defined values).
    /// </summary>
    internal sealed class TolerantJsonStringEnumConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(
                typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;

        private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    string? text = reader.GetString();
                    if (Enum.TryParse<T>(text, ignoreCase: true, out var parsed))
                        return parsed;

                    Log.Info($"SettingsManager: Unknown {typeof(T).Name} value '{text}'; using default '{default(T)}'.");
                    return default;
                }

                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int numeric))
                    return (T)Enum.ToObject(typeof(T), numeric);

                Log.Info($"SettingsManager: Unexpected {reader.TokenType} token for {typeof(T).Name}; using default '{default(T)}'.");
                return default;
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
                => writer.WriteStringValue(value.ToString());
        }
    }

    public class SettingsManager
    {
        /// <summary>
        /// Version of the on-disk settings schema this binary understands. Absent/0 in the
        /// file means a v1 (pre-versioning) file. A file with a GREATER version was written
        /// by a newer build: it is loaded best-effort but this manager refuses to Save so an
        /// old binary can never clobber a newer file.
        /// </summary>
        public const int CurrentSchemaVersion = 4;
        internal const long MaxSettingsFileBytes = 16L * 1024 * 1024;

        // Use LocalApplicationData to avoid Resilio Sync corruption
        private static string AppDataPath => AppPaths.DataDir;

        private static string SettingsFilePath => Path.Combine(AppDataPath, "settings.json");

        // SettingsManager is a DI singleton read/written from the UI thread, the night-mode
        // timer's threadpool thread (via Dispatcher.Invoke → RequestApply), and any background
        // calibration path. All access to _data and every Save() file-write must take this lock:
        //   - it closes the temp-file + File.Move write race under concurrent saves,
        //   - it stops ValidateAndClampSettings from enumerating MonitorProfiles.Values while a
        //     Set* mutator is mid-insert (which threw InvalidOperationException).
        // Reads that hand out mutable objects (GetMonitorProfile, ExcludedApps) return COPIES so
        // callers can't mutate shared state out from under us after the lock releases.
        private readonly object _dataLock = new();
        private readonly object _saveLock = new();
        private readonly object _gamerWriteLock = new();
        private long _dataVersion;

        private SettingsData _data = new SettingsData();
        public bool LoadedExistingSettingsFile { get; private set; }

        /// <summary>
        /// True when the settings file was written by a newer schema than this binary knows.
        /// The file was loaded best-effort, but every Save() is refused so this (older)
        /// binary can never clobber the newer file.
        /// </summary>
        public bool SettingsFileFromNewerVersion { get; private set; }

        /// <summary>
        /// True when the settings file existed but could not be parsed. The file is left
        /// untouched on disk and in-memory defaults are used; Save() is suppressed until
        /// something actually changes (_dataVersion advances), so the unreadable file is
        /// never silently overwritten with defaults.
        /// </summary>
        public bool LoadFailedPreservingFile { get; private set; }

        // _dataVersion at the moment Load() completed; used to detect "no change yet" for
        // the post-parse-failure save suppression above.
        private long _dataVersionAtLoad;

        public NightModeSettings NightMode
        {
            get
            {
                lock (_dataLock) { return _data.NightMode.ToNightModeSettings(); }
            }
        }

        /// <summary>UI theme override: true = dark, false = light, null = follow the OS.</summary>
        public bool? DarkTheme
        {
            get { lock (_dataLock) { return _data.DarkTheme; } }
        }

        public bool StartupDefaultApplied
        {
            get { lock (_dataLock) { return _data.StartupDefaultApplied; } }
        }

        public void MarkStartupDefaultApplied()
        {
            lock (_dataLock)
            {
                if (_data.StartupDefaultApplied) return;
                _data.StartupDefaultApplied = true;
                _dataVersion++;
            }
            Save();
        }

        /// <summary>
        /// When true, Gloam leaves Windows Night Light alone. Default (false): Gloam turns
        /// Night Light off whenever it's detected active, because it layers a second warm
        /// shift through the same pipeline Gloam owns and corrupts calibrated output. No UI
        /// for this escape hatch — set "AllowWindowsNightLight": true in settings.json.
        /// </summary>
        public bool AllowWindowsNightLight
        {
            get { lock (_dataLock) { return _data.AllowWindowsNightLight; } }
        }

        /// <summary>True once the user has been told about a lingering legacy (pre-Velopack) install.</summary>
        public bool LegacyInstallWarningShown
        {
            get { lock (_dataLock) { return _data.LegacyInstallWarningShown; } }
        }

        public void MarkLegacyInstallWarningShown()
        {
            lock (_dataLock)
            {
                if (_data.LegacyInstallWarningShown) return;
                _data.LegacyInstallWarningShown = true;
                _dataVersion++;
            }
            Save();
        }

        public void SetDarkTheme(bool dark)
        {
            lock (_dataLock) { _data.DarkTheme = dark; _dataVersion++; }
            Save();
        }

        /// <summary>
        /// Trust-check reminder cadence in days; 0 = reminders off (the default — the check
        /// needs the probe attached, so it is opt-in). The last-run time is not stored here:
        /// it is derived from the newest TrustCheckHistory entry, the actual source of truth.
        /// </summary>
        public int TrustCheckReminderDays
        {
            get { lock (_dataLock) { return _data.TrustCheckReminderDays; } }
        }

        public void SetTrustCheckReminderDays(int days)
        {
            days = Math.Clamp(days, 0, 365);
            lock (_dataLock)
            {
                if (_data.TrustCheckReminderDays == days) return;
                _data.TrustCheckReminderDays = days;
                _dataVersion++;
            }
            Save();
        }

        public WindowBoundsData? GetWindowBounds(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            lock (_dataLock)
            {
                return _data.WindowBounds.TryGetValue(key, out var bounds)
                    ? bounds.Clone()
                    : null;
            }
        }

        public void SetWindowBounds(string key, WindowBoundsData bounds)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            lock (_dataLock)
            {
                _data.WindowBounds[key] = bounds.Clone();
                _dataVersion++;
            }
            Save();
        }

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
            var loaded = SettingsPersistence.Load(
                SettingsFilePath,
                MaxSettingsFileBytes,
                CurrentSchemaVersion);

            lock (_dataLock)
            {
                _data = loaded.Data;
                LoadedExistingSettingsFile = loaded.FileExists;
                SettingsFileFromNewerVersion = loaded.NewerSchema;
                LoadFailedPreservingFile = loaded.ParseFailed;
                _dataVersion++;
                _dataVersionAtLoad = _dataVersion;
            }

            Log.Info($"SettingsManager: Loaded {_data.MonitorProfiles.Count} monitor profiles.");
            if (loaded.NewerSchema)
            {
                Log.Error(
                    $"SettingsManager: settings.json uses schema {_data.SchemaVersion}, newer than supported " +
                    $"schema {CurrentSchemaVersion}; saves are disabled to preserve the newer file.");
            }
            if (loaded.MigratedLegacy)
            {
                lock (_dataLock) _dataVersion++;
                Save();
            }
        }

        public bool Save()
        {
            string json;
            int profileCount;
            long snapshotVersion;
            lock (_dataLock)
            {
                if (SettingsFileFromNewerVersion)
                {
                    // A newer build wrote this file; an older binary must never clobber it.
                    Log.Info($"SettingsManager: Save refused; settings.json schema is newer than this build supports (v{CurrentSchemaVersion}).");
                    return false;
                }

                if (LoadFailedPreservingFile && _dataVersion == _dataVersionAtLoad)
                {
                    // The file on disk could not be parsed and nothing has changed in memory
                    // since: a save now would just overwrite the user's file with defaults.
                    Log.Info("SettingsManager: Save suppressed; settings.json failed to parse at load and no setting has changed since.");
                    return false;
                }

                // Stamp the schema version we are about to write.
                _data.SchemaVersion = CurrentSchemaVersion;

                json = SettingsPersistence.Serialize(_data);
                profileCount = _data.MonitorProfiles.Count;
                snapshotVersion = _dataVersion;
            }

            // File I/O happens outside _dataLock so a slow disk write cannot block UI/settings
            // reads. _saveLock serializes writers, and the version check below prevents an older
            // snapshot from overwriting a newer settings state when two Save() calls overlap.
            try
            {
                lock (_saveLock)
                {
                    lock (_dataLock)
                    {
                        if (snapshotVersion != _dataVersion)
                        {
                            json = SettingsPersistence.Serialize(_data);
                            profileCount = _data.MonitorProfiles.Count;
                            snapshotVersion = _dataVersion;
                        }
                    }

                    SettingsPersistence.WriteAtomic(SettingsFilePath, json);
                    Log.Info($"SettingsManager: Saved {profileCount} monitor profiles (v{snapshotVersion}).");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Info($"SettingsManager: Failed to save settings: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates a recoverable point-in-time copy before an automatic profile migration.
        /// The same writer lock used by Save prevents a backup from racing a temp-file swap.
        /// </summary>
        public string? TryCreateBackup(string label)
        {
            try
            {
                lock (_saveLock)
                {
                    return SettingsPersistence.TryCreateBackup(SettingsFilePath, label);
                }
            }
            catch (Exception ex)
            {
                Log.Info($"SettingsManager: Could not create settings backup: {ex.Message}");
                return null;
            }
        }

        public GammaMode? GetProfileForMonitor(string monitorDevicePath)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return null;

            lock (_dataLock)
            {
                if (_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile))
                {
                    return profile.GammaMode;
                }
            }
            return null;
        }

        public void SetProfileForMonitor(string monitorDevicePath, GammaMode mode)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return;

            bool changed = false;
            lock (_dataLock)
            {
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
                _dataVersion++;
                changed = true;
            }
            if (changed)
            {
                Save();
                NotifyMonitorProfileChanged(monitorDevicePath);
            }
        }

        public MonitorProfileData? GetMonitorProfile(string monitorDevicePath)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return null;
            // Return a clone so a caller can't mutate shared in-memory state after the lock
            // releases (several call sites read+modify+write these fields).
            lock (_dataLock)
            {
                _data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile);
                return profile?.Clone();
            }
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
            lock (_dataLock)
            {
                _data.MonitorProfiles[monitorDevicePath] = profile;
                _dataVersion++;
            }
            Save();
            NotifyMonitorProfileChanged(monitorDevicePath);
        }

        public void SetNightMode(NightModeSettings settings)
        {
            lock (_dataLock)
            {
                _data.NightMode = NightModeSettingsData.FromNightModeSettings(settings);
                _dataVersion++;
            }
            Save();
            // Raised outside the lock; handlers may call back into settings (deadlock risk).
            NightModeChanged?.Invoke(settings);
        }

        /// <summary>
        /// Records (or clears, with null) the installed native MHC2 calibration profile for a
        /// monitor. Used by the apply path to compose night mode on top without double-gamma.
        /// </summary>
        public void SetMhc2Calibration(
            string monitorDevicePath,
            string? profileName,
            string? previousColorProfileName = null,
            bool? previousColorProfileHdrMode = null)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return;
            lock (_dataLock)
            {
                if (!_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile))
                {
                    profile = new MonitorProfileData();
                    _data.MonitorProfiles[monitorDevicePath] = profile;
                }
                profile.Mhc2ProfileName = profileName;
                if (!string.IsNullOrEmpty(previousColorProfileName))
                {
                    profile.PreviousColorProfileName = previousColorProfileName;
                    profile.PreviousColorProfileHdrMode = previousColorProfileHdrMode;
                }
                _dataVersion++;
            }
            Save();
            NotifyMonitorProfileChanged(monitorDevicePath);
        }

        /// <summary>Clears the saved pre-Gloam Windows profile after it has been restored.</summary>
        public void ClearMhc2PreviousColorProfile(string monitorDevicePath)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return;
            lock (_dataLock)
            {
                if (!_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile))
                    return;
                profile.PreviousColorProfileName = null;
                profile.PreviousColorProfileHdrMode = null;
                _dataVersion++;
            }
            Save();
        }

        /// <summary>
        /// Records the calibration-setup choices for a monitor so the next session opens
        /// pre-configured.
        /// </summary>
        public void SetCalibrationPrefs(
            string monitorDevicePath,
            string? ccssPath,
            string displayType,
            bool whitePointOnly,
            string? targetName = null,
            string? preset = null)
        {
            if (string.IsNullOrEmpty(monitorDevicePath)) return;
            lock (_dataLock)
            {
                if (!_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile))
                {
                    profile = new MonitorProfileData();
                    _data.MonitorProfiles[monitorDevicePath] = profile;
                }
                profile.MeterCorrectionPath = ccssPath;
                profile.CalibDisplayType = displayType;
                profile.CalibWhitePointOnly = whitePointOnly;
                profile.CalibTargetName = targetName;
                profile.CalibPreset = preset;
                _dataVersion++;
            }
            Save();
        }

        /// <summary>True if a native MHC2 calibration is installed for this monitor.</summary>
        public bool HasMhc2Calibration(string monitorDevicePath)
            => !string.IsNullOrEmpty(GetMonitorProfile(monitorDevicePath)?.Mhc2ProfileName);

        /// <summary>A snapshot copy of the excluded-apps list. Mutating it does not change settings.</summary>
        public List<AppExclusionRule> ExcludedApps
        {
            get
            {
                lock (_dataLock)
                {
                    return _data.ExcludedApps.Select(rule => rule.Clone()).ToList();
                }
            }
        }

        public void SetExcludedApps(List<AppExclusionRule> apps)
        {
            lock (_dataLock)
            {
                _data.ExcludedApps = NormalizeExcludedApps(apps);
                _dataVersion++;
            }
            Save();
        }

        public bool GetUiSectionExpanded(string key, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(key)) return defaultValue;
            lock (_dataLock)
            {
                return _data.UiSectionExpanded.TryGetValue(key.Trim(), out bool expanded)
                    ? expanded
                    : defaultValue;
            }
        }

        public void SetUiSectionExpanded(string key, bool expanded)
        {
            key = key?.Trim() ?? string.Empty;
            if (key.Length == 0 || key.Length > 80) return;
            lock (_dataLock)
            {
                if (_data.UiSectionExpanded.TryGetValue(key, out bool current) && current == expanded)
                    return;
                _data.UiSectionExpanded[key] = expanded;
                _dataVersion++;
            }
            Save();
        }

        /// <summary>Global gate for automatic per-game profile activation.</summary>
        public bool GamerModeEnabled
        {
            get { lock (_dataLock) { return _data.GamerModeEnabled; } }
        }

        /// <summary>A deep snapshot of persistent per-game profiles.</summary>
        public List<GamerProfileRule> GamerProfiles
        {
            get
            {
                lock (_dataLock)
                {
                    return _data.GamerProfiles.Select(profile => profile.Clone()).ToList();
                }
            }
        }

        public void SetGamerModeEnabled(bool enabled)
            => TrySetGamerModeEnabled(enabled);

        /// <summary>
        /// Changes the global gamer gate only when the new value can be durably written.
        /// A failed settings write restores the previous in-memory value and does not
        /// publish a settings-changed event, so UI and foreground policy cannot claim a
        /// state that will disappear on restart.
        /// </summary>
        public bool TrySetGamerModeEnabled(bool enabled)
        {
            lock (_gamerWriteLock) return TrySetGamerModeEnabledCore(enabled);
        }

        private bool TrySetGamerModeEnabledCore(bool enabled)
        {
            bool previous;
            lock (_dataLock)
            {
                if (_data.GamerModeEnabled == enabled) return true;
                previous = _data.GamerModeEnabled;
                _data.GamerModeEnabled = enabled;
                _dataVersion++;
            }

            if (!Save())
            {
                lock (_dataLock)
                {
                    if (_data.GamerModeEnabled == enabled)
                    {
                        _data.GamerModeEnabled = previous;
                        _dataVersion++;
                    }
                }
                return false;
            }

            NotifyGamerSettingsChanged();
            return true;
        }

        public void SetGamerProfiles(IEnumerable<GamerProfileRule>? profiles)
            => TrySetGamerProfiles(profiles);

        /// <summary>
        /// Replaces game profiles transactionally. The authoritative in-memory snapshot is
        /// rolled back when the atomic settings write fails; callers may therefore report
        /// "Applied" only when this method returns true.
        /// </summary>
        public bool TrySetGamerProfiles(IEnumerable<GamerProfileRule>? profiles)
        {
            lock (_gamerWriteLock) return TrySetGamerProfilesCore(profiles);
        }

        private bool TrySetGamerProfilesCore(IEnumerable<GamerProfileRule>? profiles)
        {
            List<GamerProfileRule> incoming = NormalizeGamerProfiles(profiles);
            List<GamerProfileRule> previous;
            lock (_dataLock)
            {
                if (GamerProfileListsEqual(_data.GamerProfiles, incoming)) return true;
                previous = _data.GamerProfiles.Select(profile => profile.Clone()).ToList();
                _data.GamerProfiles = incoming;
                _dataVersion++;
            }

            if (!Save())
            {
                lock (_dataLock)
                {
                    if (GamerProfileListsEqual(_data.GamerProfiles, incoming))
                    {
                        _data.GamerProfiles = previous;
                        _dataVersion++;
                    }
                }
                return false;
            }

            NotifyGamerSettingsChanged();
            return true;
        }

        private static bool GamerProfileListsEqual(
            IReadOnlyList<GamerProfileRule> left,
            IReadOnlyList<GamerProfileRule> right)
        {
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                if (!left[i].SemanticallyEquals(right[i])) return false;
            }
            return true;
        }

        /// <summary>
        /// Records a genuine foreground activation for recent-game ordering. This uses a
        /// separate event from GamerSettingsChanged so the tray does not re-run foreground
        /// policy recursively for metadata that cannot affect rendering.
        /// </summary>
        public void MarkGamerProfileUsed(string appName, DateTime usedUtc)
        {
            lock (_gamerWriteLock)
                MarkGamerProfileUsedCore(appName, usedUtc);
        }

        private void MarkGamerProfileUsedCore(string appName, DateTime usedUtc)
        {
            string normalized = AppExclusionRule.NormalizeAppName(appName);
            usedUtc = usedUtc.ToUniversalTime();
            bool changed = false;
            DateTime? previousUsedUtc = null;
            lock (_dataLock)
            {
                GamerProfileRule? profile = _data.GamerProfiles.FirstOrDefault(profile =>
                    profile.AppName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                if (profile == null) return;
                if (profile.LastUsedUtc.HasValue && usedUtc - profile.LastUsedUtc.Value < TimeSpan.FromMinutes(1))
                    return;

                previousUsedUtc = profile.LastUsedUtc;
                profile.LastUsedUtc = usedUtc;
                _dataVersion++;
                changed = true;
            }

            if (!changed) return;
            if (!Save())
            {
                lock (_dataLock)
                {
                    GamerProfileRule? profile = _data.GamerProfiles.FirstOrDefault(profile =>
                        profile.AppName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                    if (profile?.LastUsedUtc == usedUtc)
                    {
                        profile.LastUsedUtc = previousUsedUtc;
                        _dataVersion++;
                    }
                }
                return;
            }
            try { GamerProfileUsed?.Invoke(normalized, usedUtc); }
            catch (Exception ex) { Log.Info($"SettingsManager: gamer-recency subscriber failed: {ex.Message}"); }
        }

        public event Action<string, DateTime>? GamerProfileUsed;

        public event Action? GamerSettingsChanged;

        public event Action<string>? MonitorProfileChanged;

        private void NotifyGamerSettingsChanged()
        {
            try { GamerSettingsChanged?.Invoke(); }
            catch (Exception ex) { Log.Info($"SettingsManager: gamer-settings subscriber failed: {ex.Message}"); }
        }

        private void NotifyMonitorProfileChanged(string monitorDevicePath)
        {
            try { MonitorProfileChanged?.Invoke(monitorDevicePath); }
            catch (Exception ex) { Log.Info($"SettingsManager: monitor-profile subscriber failed: {ex.Message}"); }
        }

        internal static List<GamerProfileRule> NormalizeGamerProfiles(
            IEnumerable<GamerProfileRule>? profiles) => SettingsNormalization.GamerProfiles(profiles);

        /// <summary>
        /// Atomically replaces the recorded native profile only when it still names the
        /// profile the migration inspected. The in-memory value is restored if the durable
        /// settings write fails, allowing the caller to roll Windows' association back too.
        /// </summary>
        public bool TryReplaceMhc2Calibration(
            string monitorDevicePath, string expectedProfileName, string replacementProfileName)
        {
            if (string.IsNullOrWhiteSpace(monitorDevicePath) ||
                string.IsNullOrWhiteSpace(expectedProfileName) ||
                string.IsNullOrWhiteSpace(replacementProfileName))
                return false;

            lock (_dataLock)
            {
                if (!_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile) ||
                    !string.Equals(profile.Mhc2ProfileName, expectedProfileName,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                profile.Mhc2ProfileName = replacementProfileName;
                _dataVersion++;
            }

            if (Save())
            {
                NotifyMonitorProfileChanged(monitorDevicePath);
                return true;
            }

            lock (_dataLock)
            {
                if (_data.MonitorProfiles.TryGetValue(monitorDevicePath, out var profile) &&
                    string.Equals(profile.Mhc2ProfileName, replacementProfileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    profile.Mhc2ProfileName = expectedProfileName;
                    _dataVersion++;
                }
            }
            return false;
        }

        internal static List<AppExclusionRule> NormalizeExcludedApps(
            IEnumerable<AppExclusionRule?>? apps) => SettingsNormalization.ExcludedApps(apps);

        #region Calibration Profile Management

        private static string CalibrationProfilesPath => Path.Combine(AppDataPath, "CalibrationProfiles");

        private static bool TryGetCalibrationProfilePath(string? profileId, out string filePath)
        {
            filePath = string.Empty;
            if (string.IsNullOrWhiteSpace(profileId))
                return false;

            // Profile IDs are generated GUIDs. Constrain persisted/hand-edited values to the
            // two formats Gloam has emitted so they can never become rooted paths or contain
            // directory traversal segments when used as filenames below.
            if (!Guid.TryParseExact(profileId, "N", out _) &&
                !Guid.TryParseExact(profileId, "D", out _))
            {
                return false;
            }

            filePath = Path.Combine(CalibrationProfilesPath, profileId + ".json");
            return true;
        }

        /// <summary>
        /// Saves a calibration profile to disk.
        /// </summary>
        public void SaveCalibrationProfile(DisplayCalibrationProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            if (!TryGetCalibrationProfilePath(profile.Id, out string filePath))
                throw new ArgumentException("Calibration profile ID must be a GUID.", nameof(profile));

            Directory.CreateDirectory(CalibrationProfilesPath);
            profile.SaveToFile(filePath);
            Log.Info($"SettingsManager: Saved calibration profile '{profile.Name}' to {filePath}");
        }

        /// <summary>
        /// Loads a calibration profile by ID.
        /// </summary>
        public DisplayCalibrationProfile? LoadCalibrationProfile(string profileId)
        {
            if (!TryGetCalibrationProfilePath(profileId, out string filePath))
            {
                Log.Info("SettingsManager: Ignoring invalid calibration profile ID.");
                return null;
            }

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
        /// Loads a calibration profile only if it is usable for runtime calibrated LUT generation.
        /// </summary>
        public DisplayCalibrationProfile? LoadUsableCalibrationProfile(string profileId)
        {
            var profile = LoadCalibrationProfile(profileId);
            if (profile == null)
                return null;

            if (!LutGenerator.CanUseCalibratedLut(profile))
            {
                Log.Info($"SettingsManager: Ignoring unusable calibration profile '{profile.Name}' ({profile.Id})");
                return null;
            }

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
            if (!TryGetCalibrationProfilePath(profileId, out string filePath))
            {
                Log.Info("SettingsManager: Refusing to delete an invalid calibration profile ID.");
                return false;
            }

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

            return LoadUsableCalibrationProfile(monitorProfile.CalibrationProfileId);
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

        internal class SettingsData
        {
            /// <summary>
            /// On-disk schema version (see <see cref="CurrentSchemaVersion"/>). Absent/0 in
            /// older files means v1. Stamped to the current version on every save.
            /// </summary>
            public int SchemaVersion { get; set; }

            public Dictionary<string, MonitorProfileData> MonitorProfiles { get; set; } = new Dictionary<string, MonitorProfileData>();
            public NightModeSettingsData NightMode { get; set; } = new NightModeSettingsData();
            public List<AppExclusionRule> ExcludedApps { get; set; } = new List<AppExclusionRule>();
            public bool GamerModeEnabled { get; set; }
            public List<GamerProfileRule> GamerProfiles { get; set; } = new List<GamerProfileRule>();
            // Brutalist UI theme: true = dark, false = light, null = follow OS.
            public bool? DarkTheme { get; set; } = null;
            public bool StartupDefaultApplied { get; set; }
            public bool LegacyInstallWarningShown { get; set; }
            public bool AllowWindowsNightLight { get; set; }
            // Trust-check reminder cadence in days; 0 (the default for absent field) = off.
            public int TrustCheckReminderDays { get; set; }
            public Dictionary<string, WindowBoundsData> WindowBounds { get; set; } = new Dictionary<string, WindowBoundsData>();
            public Dictionary<string, bool> UiSectionExpanded { get; set; } = new Dictionary<string, bool>();
        }

        internal class LegacySettingsData
        {
            public Dictionary<string, MonitorProfileData> MonitorProfiles { get; set; } = new Dictionary<string, MonitorProfileData>();
            public NightModeSettingsData NightMode { get; set; } = new NightModeSettingsData();
            public List<string> ExcludedApps { get; set; } = new List<string>();
        }
    }
}
