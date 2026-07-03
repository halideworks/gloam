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
        public const int CurrentSchemaVersion = 2;

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
            SettingsData? loaded = null;
            bool fileExists = false;
            bool parseFailed = false;
            bool newerSchema = false;
            try
            {
                fileExists = File.Exists(SettingsFilePath);
                if (fileExists)
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var options = new JsonSerializerOptions
                    {
                        // Tolerant enum handling: an unknown enum string (e.g. written by a
                        // newer build) maps to the enum default instead of throwing away the
                        // whole file. Applies to ALL enums in the settings graph.
                        Converters = { new TolerantJsonStringEnumConverter() },
                        PropertyNameCaseInsensitive = true
                    };

                    try
                    {
                        loaded = JsonSerializer.Deserialize<SettingsData>(json, options) ?? new SettingsData();
                        ValidateAndClampSettings(loaded);
                        Log.Info($"SettingsManager: Loaded {loaded.MonitorProfiles.Count} monitor profiles.");

                        if (loaded.SchemaVersion > CurrentSchemaVersion)
                        {
                            // The file was written by a newer build. Loaded best-effort above;
                            // mark read-only so this old binary can never clobber the newer file.
                            newerSchema = true;
                            Log.Error(
                                $"SettingsManager: settings.json has SchemaVersion {loaded.SchemaVersion}, newer than this " +
                                $"build's supported version {CurrentSchemaVersion}. Loading best-effort and REFUSING all saves " +
                                "so the newer file is not overwritten by an older binary.");
                        }
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
                                loaded = new SettingsData
                                {
                                    MonitorProfiles = legacy.MonitorProfiles,
                                    NightMode = legacy.NightMode,
                                    ExcludedApps = legacy.ExcludedApps?.Select(path => new AppExclusionRule { AppName = path, FullDisable = false }).ToList() ?? new List<AppExclusionRule>()
                                };
                                Log.Info("SettingsManager: Legacy migration successful.");
                                Log.Info($"SettingsManager: Loaded {loaded.MonitorProfiles.Count} monitor profiles.");
                                // Persist in the new format under the lock to avoid racing another Load.
                                lock (_dataLock)
                                {
                                    _data = loaded;
                                    LoadedExistingSettingsFile = fileExists;
                                    SettingsFileFromNewerVersion = false;
                                    LoadFailedPreservingFile = false;
                                    _dataVersion++;
                                    _dataVersionAtLoad = _dataVersion;
                                }
                                Save();
                                return;
                            }

                            parseFailed = true;
                        }
                        catch (Exception innerEx)
                        {
                            // NEVER reset-and-save here: leave the unreadable file untouched on
                            // disk (a legacy binary destructively resetting settings on parse
                            // failure is exactly the bug this guards against). Keep a .bak copy,
                            // run on in-memory defaults, and suppress routine saves until a real
                            // change happens (see Save()).
                            Log.Error(
                                $"SettingsManager: Legacy migration failed ({innerEx.Message}). settings.json is left " +
                                "UNTOUCHED on disk; running with in-memory defaults and suppressing saves until a setting changes.");
                            try { File.Copy(SettingsFilePath, SettingsFilePath + $".bak-{DateTime.Now.Ticks}", true); } catch { }
                            parseFailed = true;
                            loaded = new SettingsData();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"SettingsManager: Failed to load settings: {ex.Message}");
                // If the file exists but could not even be read, treat it like a parse
                // failure: keep it on disk and do not auto-overwrite it with defaults.
                parseFailed = fileExists;
                loaded = new SettingsData();
            }

            lock (_dataLock)
            {
                _data = loaded ?? new SettingsData();
                LoadedExistingSettingsFile = fileExists;
                SettingsFileFromNewerVersion = newerSchema;
                LoadFailedPreservingFile = parseFailed;
                _dataVersion++;
                _dataVersionAtLoad = _dataVersion;
            }
        }

        public void Save()
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
                    return;
                }

                if (LoadFailedPreservingFile && _dataVersion == _dataVersionAtLoad)
                {
                    // The file on disk could not be parsed and nothing has changed in memory
                    // since: a save now would just overwrite the user's file with defaults.
                    Log.Info("SettingsManager: Save suppressed; settings.json failed to parse at load and no setting has changed since.");
                    return;
                }

                // Stamp the schema version we are about to write.
                _data.SchemaVersion = CurrentSchemaVersion;

                var options = CreateJsonOptions();
                json = JsonSerializer.Serialize(_data, options);
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
                            var options = CreateJsonOptions();
                            json = JsonSerializer.Serialize(_data, options);
                            profileCount = _data.MonitorProfiles.Count;
                            snapshotVersion = _dataVersion;
                        }
                    }

                    Directory.CreateDirectory(AppDataPath);
                    // Write-then-rename so a crash mid-write can't leave a truncated settings.json.
                    string tempPath = SettingsFilePath + $".{Guid.NewGuid():N}.tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, SettingsFilePath, overwrite: true);
                    Log.Info($"SettingsManager: Saved {profileCount} monitor profiles (v{snapshotVersion}).");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"SettingsManager: Failed to save settings: {ex.Message}");
            }
        }

        private static JsonSerializerOptions CreateJsonOptions() => new()
        {
            WriteIndented = true,
            // Tolerant on read (unknown enum -> default), JsonStringEnumConverter-compatible
            // names on write. Shared by Save so round-trips stay symmetric.
            Converters = { new TolerantJsonStringEnumConverter() }
        };

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
            if (changed) Save();
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
                    return _data.ExcludedApps.ToList();
                }
            }
        }

        public void SetExcludedApps(List<AppExclusionRule> apps)
        {
            lock (_dataLock)
            {
                _data.ExcludedApps = apps ?? new List<AppExclusionRule>();
                _dataVersion++;
            }
            Save();
        }

        #region Calibration Profile Management

        private static string CalibrationProfilesPath => Path.Combine(AppDataPath, "CalibrationProfiles");

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
                profile.Brightness = ClampFinite(profile.Brightness, 10.0, 100.0, 100.0);

                // Temperature offset: -50 to +50
                profile.Temperature = ClampFinite(profile.Temperature, -50.0, 50.0, 0.0);
                profile.TemperatureOffset = ClampFinite(profile.TemperatureOffset, -50.0, 50.0, 0.0);

                // Tint: -50 to +50
                profile.Tint = ClampFinite(profile.Tint, -50.0, 50.0, 0.0);

                // RGB Gains: 0.5 to 1.5
                profile.RedGain = ClampFinite(profile.RedGain, 0.5, 1.5, 1.0);
                profile.GreenGain = ClampFinite(profile.GreenGain, 0.5, 1.5, 1.0);
                profile.BlueGain = ClampFinite(profile.BlueGain, 0.5, 1.5, 1.0);

                // RGB Offsets: -0.5 to +0.5
                profile.RedOffset = ClampFinite(profile.RedOffset, -0.5, 0.5, 0.0);
                profile.GreenOffset = ClampFinite(profile.GreenOffset, -0.5, 0.5, 0.0);
                profile.BlueOffset = ClampFinite(profile.BlueOffset, -0.5, 0.5, 0.0);
            }

            // Validate night mode settings
            var nm = data.NightMode;
            if (nm != null)
            {
                // Latitude: -90 to +90
                if (nm.Latitude.HasValue)
                    nm.Latitude = ClampFinite(nm.Latitude.Value, -90.0, 90.0, 0.0);

                // Longitude: -180 to +180
                if (nm.Longitude.HasValue)
                    nm.Longitude = ClampFinite(nm.Longitude.Value, -180.0, 180.0, 0.0);

                // Temperature: 1900K to 6500K (valid color temperature range)
                nm.TemperatureKelvin = Math.Clamp(nm.TemperatureKelvin, 1900, 6500);

                // Fade duration: 0 to 120 minutes
                nm.FadeMinutes = Math.Clamp(nm.FadeMinutes, 0, 120);

                // Respect the user's chosen rendering algorithm on reload, but migrate the
                // retired Blue reduction mode (and any out-of-range value) to the perceptual
                // default. Clamp the perceptual intensity to [0,1].
                if (nm.Algorithm == NightModeAlgorithm.BlueReduction ||
                    !Enum.IsDefined(typeof(NightModeAlgorithm), nm.Algorithm))
                {
                    nm.Algorithm = NightModeAlgorithm.Perceptual;
                }
                nm.PerceptualStrength = ClampFinite(nm.PerceptualStrength, 0.0, 1.0, ColorAdjustments.DefaultPerceptualStrength);

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
                        point.OffsetMinutes = ClampFinite(point.OffsetMinutes, -120.0, 120.0, 0.0);
                    }
                }
            }

            if (data.WindowBounds == null)
                data.WindowBounds = new Dictionary<string, WindowBoundsData>();

            foreach (var key in data.WindowBounds.Keys.ToList())
            {
                var bounds = data.WindowBounds[key];
                if (bounds == null ||
                    !double.IsFinite(bounds.Left) ||
                    !double.IsFinite(bounds.Top) ||
                    !double.IsFinite(bounds.Width) ||
                    !double.IsFinite(bounds.Height) ||
                    bounds.Width < 320 ||
                    bounds.Height < 240)
                {
                    data.WindowBounds.Remove(key);
                }
            }
        }

        private static double ClampFinite(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

        private class SettingsData
        {
            /// <summary>
            /// On-disk schema version (see <see cref="CurrentSchemaVersion"/>). Absent/0 in
            /// older files means v1. Stamped to the current version on every save.
            /// </summary>
            public int SchemaVersion { get; set; }

            public Dictionary<string, MonitorProfileData> MonitorProfiles { get; set; } = new Dictionary<string, MonitorProfileData>();
            public NightModeSettingsData NightMode { get; set; } = new NightModeSettingsData();
            public List<AppExclusionRule> ExcludedApps { get; set; } = new List<AppExclusionRule>();
            // Brutalist UI theme: true = dark, false = light, null = follow OS.
            public bool? DarkTheme { get; set; } = null;
            public bool StartupDefaultApplied { get; set; }
            public bool LegacyInstallWarningShown { get; set; }
            public Dictionary<string, WindowBoundsData> WindowBounds { get; set; } = new Dictionary<string, WindowBoundsData>();
        }

        private class LegacySettingsData
        {
            public Dictionary<string, MonitorProfileData> MonitorProfiles { get; set; } = new Dictionary<string, MonitorProfileData>();
            public NightModeSettingsData NightMode { get; set; } = new NightModeSettingsData();
            public List<string> ExcludedApps { get; set; } = new List<string>();
        }
    }
}
