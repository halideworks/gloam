using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HDRGammaController.Core
{
    /// <summary>All settings.json I/O, format probing, and atomic replacement.</summary>
    internal static class SettingsPersistence
    {
        internal sealed record LoadResult(
            SettingsManager.SettingsData Data,
            bool FileExists,
            bool ParseFailed,
            bool NewerSchema,
            bool MigratedLegacy);

        internal static LoadResult Load(string path, long maximumBytes, int currentSchemaVersion)
        {
            SettingsManager.SettingsData? loaded = null;
            bool fileExists = File.Exists(path);
            bool parseFailed = false;
            bool newerSchema = false;
            bool migratedLegacy = false;

            try
            {
                if (!fileExists)
                    return new LoadResult(new SettingsManager.SettingsData(), false, false, false, false);
                if (new FileInfo(path).Length > maximumBytes)
                    throw new InvalidDataException("settings.json exceeds the size limit.");

                string json = File.ReadAllText(path);
                var options = ReadOptions();
                try
                {
                    loaded = JsonSerializer.Deserialize<SettingsManager.SettingsData>(json, options)
                        ?? new SettingsManager.SettingsData();
                    SettingsMigration.Apply(loaded);
                    SettingsNormalization.Validate(loaded);
                    newerSchema = loaded.SchemaVersion > currentSchemaVersion;
                }
                catch (Exception ex)
                {
                    Log.Info($"SettingsPersistence: primary deserialization failed ({ex.Message}); attempting legacy migration.");
                    try
                    {
                        var legacy = JsonSerializer.Deserialize<SettingsManager.LegacySettingsData>(json, options);
                        if (legacy != null)
                        {
                            loaded = new SettingsManager.SettingsData
                            {
                                MonitorProfiles = legacy.MonitorProfiles,
                                NightMode = legacy.NightMode,
                                ExcludedApps = legacy.ExcludedApps?
                                    .Select(value => new AppExclusionRule { AppName = value })
                                    .ToList() ?? new(),
                            };
                            SettingsMigration.Apply(loaded);
                            SettingsNormalization.Validate(loaded);
                            migratedLegacy = true;
                        }
                        else
                        {
                            parseFailed = true;
                        }
                    }
                    catch (Exception innerEx)
                    {
                        Log.Error($"SettingsPersistence: legacy migration failed ({innerEx.Message}); preserving the original file.");
                        TryCopyCorruptBackup(path);
                        loaded = new SettingsManager.SettingsData();
                        parseFailed = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"SettingsPersistence: failed to load settings: {ex.Message}");
                loaded = new SettingsManager.SettingsData();
                parseFailed = fileExists;
            }

            return new LoadResult(
                loaded ?? new SettingsManager.SettingsData(),
                fileExists,
                parseFailed,
                newerSchema,
                migratedLegacy);
        }

        internal static string Serialize(SettingsManager.SettingsData data) =>
            JsonSerializer.Serialize(data, WriteOptions());

        internal static void WriteAtomic(string path, string json)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string tempPath = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }

        internal static string? TryCreateBackup(string path, string label)
        {
            if (!File.Exists(path)) return null;
            string safeLabel = new string((label ?? "backup")
                .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
            if (safeLabel.Length == 0) safeLabel = "backup";
            string backupPath = path + $".{safeLabel}-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(path, backupPath, overwrite: false);
            return backupPath;
        }

        private static JsonSerializerOptions ReadOptions() => new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new TolerantJsonStringEnumConverter() },
        };

        private static JsonSerializerOptions WriteOptions() => new()
        {
            WriteIndented = true,
            Converters = { new TolerantJsonStringEnumConverter() },
        };

        private static void TryCopyCorruptBackup(string path)
        {
            try { File.Copy(path, path + $".bak-{DateTime.Now.Ticks}", true); }
            catch (Exception ex) { Log.Info($"SettingsPersistence: could not copy unreadable settings backup: {ex.Message}"); }
        }
    }
}
