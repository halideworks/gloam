using System;
using System.IO;
using System.Linq;
using HDRGammaController.Core;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Tests for the settings-loading hardening: unknown enum tolerance, never
    /// reset-and-save on parse failure, and SchemaVersion forward-compatibility
    /// (an old binary must never clobber a newer settings file).
    /// </summary>
    public sealed class SettingsResilienceTests : IDisposable
    {
        private readonly string _originalDataDir;
        private readonly string _originalRoamingDir;
        private readonly string _tempDir;

        public SettingsResilienceTests()
        {
            _originalDataDir = AppPaths.DataDir;
            _originalRoamingDir = AppPaths.RoamingDataDir;
            _tempDir = Path.Combine(Path.GetTempPath(), "GloamSettingsResilienceTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            AppPaths.UseDataDirectoriesForCurrentProcess(_tempDir, Path.Combine(_tempDir, "roaming"));
        }

        private string SettingsPath => Path.Combine(AppPaths.DataDir, "settings.json");

        [Fact]
        public void Load_UnknownEnumValue_DoesNotThrowAndKeepsOtherProperties()
        {
            // A NightModeAlgorithm value from a future build must not fail the whole load
            // (the legacy v1.0.0 build threw here and then destructively reset settings).
            File.WriteAllText(SettingsPath, """
                {
                  "SchemaVersion": 2,
                  "MonitorProfiles": {
                    "\\\\?\\DISPLAY#TEST#1": { "GammaMode": "Gamma24", "Brightness": 80 }
                  },
                  "NightMode": {
                    "Enabled": true,
                    "TemperatureKelvin": 2200,
                    "Algorithm": "SomeFutureAlgorithm"
                  },
                  "DarkTheme": true
                }
                """);

            var sm = new SettingsManager();

            Assert.True(sm.LoadedExistingSettingsFile);
            Assert.False(sm.LoadFailedPreservingFile);

            // Unknown enum string maps to the enum default instead of throwing.
            Assert.Equal(default(NightModeAlgorithm), sm.NightMode.Algorithm);

            // Everything else in the file survives.
            Assert.True(sm.NightMode.Enabled);
            Assert.Equal(2200, sm.NightMode.TemperatureKelvin);
            Assert.True(sm.DarkTheme);
            var profile = sm.GetMonitorProfile(@"\\?\DISPLAY#TEST#1");
            Assert.NotNull(profile);
            Assert.Equal(GammaMode.Gamma24, profile.GammaMode);
            Assert.Equal(80.0, profile.Brightness);
        }

        [Fact]
        public void Load_ParseFailure_DoesNotRewriteFileOnDisk()
        {
            const string corrupt = "{ this is not valid json !!!";
            File.WriteAllText(SettingsPath, corrupt);

            var sm = new SettingsManager();

            Assert.True(sm.LoadFailedPreservingFile);
            // The unreadable file must be left untouched on disk...
            Assert.Equal(corrupt, File.ReadAllText(SettingsPath));
            // ...with the existing backup behavior preserved.
            Assert.NotEmpty(Directory.GetFiles(AppPaths.DataDir, "settings.json.bak-*"));

            // A routine Save() with no in-memory change must not overwrite it either.
            sm.Save();
            Assert.Equal(corrupt, File.ReadAllText(SettingsPath));
        }

        [Fact]
        public void Load_ParseFailure_SaveResumesAfterRealChange()
        {
            File.WriteAllText(SettingsPath, "{ this is not valid json !!!");

            var sm = new SettingsManager();
            Assert.True(sm.LoadFailedPreservingFile);

            // The user actually changing a setting is consent to persist again.
            sm.SetDarkTheme(true);

            var reloaded = new SettingsManager();
            Assert.False(reloaded.LoadFailedPreservingFile);
            Assert.True(reloaded.DarkTheme);
        }

        [Fact]
        public void Load_NewerSchemaVersion_LoadsBestEffortButRefusesSave()
        {
            string newer = """
                {
                  "SchemaVersion": 99,
                  "DarkTheme": true
                }
                """;
            File.WriteAllText(SettingsPath, newer);

            var sm = new SettingsManager();

            // Best-effort load still surfaces what this binary understands...
            Assert.True(sm.SettingsFileFromNewerVersion);
            Assert.True(sm.DarkTheme);

            // ...but no save path may clobber the newer file, not even user changes.
            sm.Save();
            sm.SetDarkTheme(false);

            Assert.Equal(newer, File.ReadAllText(SettingsPath));
        }

        [Fact]
        public void GamerChanges_RollBackWhenDurableSaveIsRefused()
        {
            string newer = """
                {
                  "SchemaVersion": 99,
                  "GamerModeEnabled": false,
                  "GamerProfiles": [
                    { "AppName": "existing.exe", "DisplayName": "Existing" }
                  ]
                }
                """;
            File.WriteAllText(SettingsPath, newer);
            var sm = new SettingsManager();

            Assert.False(sm.TrySetGamerModeEnabled(true));
            Assert.False(sm.TrySetGamerProfiles(new[]
            {
                GamerPresetCatalog.Create("replacement.exe", GamerPictureIntent.Reference)
            }));

            Assert.False(sm.GamerModeEnabled);
            Assert.Equal("existing.exe", Assert.Single(sm.GamerProfiles).AppName);
            Assert.Equal(newer, File.ReadAllText(SettingsPath));
        }

        [Fact]
        public void Save_StampsCurrentSchemaVersion()
        {
            var sm = new SettingsManager();
            sm.SetDarkTheme(true);

            string json = File.ReadAllText(SettingsPath);
            Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", json);

            var reloaded = new SettingsManager();
            Assert.False(reloaded.SettingsFileFromNewerVersion);
            Assert.True(reloaded.DarkTheme);
        }

        [Fact]
        public void Load_MissingSchemaVersion_TreatedAsV1AndWritable()
        {
            // Pre-versioning (v1) files have no SchemaVersion property at all.
            File.WriteAllText(SettingsPath, """
                {
                  "DarkTheme": true
                }
                """);

            var sm = new SettingsManager();

            Assert.False(sm.SettingsFileFromNewerVersion);
            Assert.True(sm.DarkTheme);

            // Saving upgrades the file to the current schema stamp.
            sm.SetDarkTheme(false);
            Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", File.ReadAllText(SettingsPath));
        }

        public void Dispose()
        {
            AppPaths.UseDataDirectoriesForCurrentProcess(_originalDataDir, _originalRoamingDir);
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }
}
