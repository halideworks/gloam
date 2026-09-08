using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gloam.Core
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

                string json = TextFileStore.ReadBounded(path, maximumBytes, (int)Math.Min(maximumBytes, int.MaxValue));
                var options = ReadOptions;
                try
                {
                    var root = JsonNode.Parse(json) as JsonObject
                        ?? throw new JsonException("Settings must be a JSON object.");

                    // Read the version before decoding fields whose shape may have changed.
                    // Even a failed best-effort load must never make a future file writable.
                    foreach (var property in root)
                    {
                        if (property.Key.Equals(nameof(SettingsManager.SettingsData.SchemaVersion),
                                StringComparison.OrdinalIgnoreCase) &&
                            property.Value is JsonValue version && version.TryGetValue<int>(out int number))
                            newerSchema |= number > currentSchemaVersion;
                    }

                    // Only the old string exclusion list needs conversion. Rebuilding the
                    // entire document from a legacy DTO silently drops every other setting.
                    bool convertedLegacy = false;
                    foreach (var property in root.ToList())
                    {
                        if (!property.Key.Equals(nameof(SettingsManager.SettingsData.ExcludedApps),
                                StringComparison.OrdinalIgnoreCase) || property.Value is not JsonArray apps)
                            continue;
                        for (int i = 0; i < apps.Count; i++)
                        {
                            if (apps[i] is JsonValue value && value.TryGetValue<string>(out string? appName))
                            {
                                apps[i] = new JsonObject { [nameof(AppExclusionRule.AppName)] = appName };
                                convertedLegacy = true;
                            }
                        }
                    }

                    loaded = root.Deserialize<SettingsManager.SettingsData>(options)
                        ?? throw new JsonException("Settings must be a JSON object.");
                    SettingsMigration.Apply(loaded);
                    SettingsNormalization.Validate(loaded);
                    migratedLegacy = convertedLegacy;
                }
                catch (Exception ex)
                {
                    Log.Error($"SettingsPersistence: deserialization failed ({ex.Message}); preserving the original file.");
                    TryCopyCorruptBackup(path);
                    loaded = new SettingsManager.SettingsData();
                    parseFailed = true;
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
            JsonSerializer.Serialize(data, WriteOptions);

        internal static void WriteAtomic(string path, string json) =>
            TextFileStore.WriteAtomic(path, json, SettingsManager.MaxSettingsFileBytes);

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

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new TolerantJsonStringEnumConverter() },
        };

        private static readonly JsonSerializerOptions WriteOptions = new()
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
