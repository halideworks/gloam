using Xunit;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Tests for settings validation and security-related input handling.
    /// </summary>
    public class SettingsValidationTests
    {
        [Fact]
        public void MonitorProfileClone_PreservesCalibrationMetadata()
        {
            var source = new MonitorProfileData
            {
                CalibrationProfileId = "calibration-id",
                UseCalibrationForGamma = false,
                Mhc2ProfileName = "gloam-profile.icm",
                PreviousColorProfileName = "factory.icm",
                PreviousColorProfileHdrMode = true,
                MeterCorrectionPath = @"C:\corrections\panel.ccss",
                NightModeCcssPath = @"C:\corrections\night-spectrum.ccss",
                CalibDisplayType = "Oled",
                CalibWhitePointOnly = true,
                CalibTargetName = "HDR Desktop PQ (sRGB gamut)",
                CalibPreset = "Thorough"
            };

            var clone = source.Clone();

            Assert.Equal(source.CalibrationProfileId, clone.CalibrationProfileId);
            Assert.Equal(source.UseCalibrationForGamma, clone.UseCalibrationForGamma);
            Assert.Equal(source.Mhc2ProfileName, clone.Mhc2ProfileName);
            Assert.Equal(source.PreviousColorProfileName, clone.PreviousColorProfileName);
            Assert.Equal(source.PreviousColorProfileHdrMode, clone.PreviousColorProfileHdrMode);
            Assert.Equal(source.MeterCorrectionPath, clone.MeterCorrectionPath);
            Assert.Equal(source.NightModeCcssPath, clone.NightModeCcssPath);
            Assert.Equal(source.CalibDisplayType, clone.CalibDisplayType);
            Assert.Equal(source.CalibWhitePointOnly, clone.CalibWhitePointOnly);
            Assert.Equal(source.CalibTargetName, clone.CalibTargetName);
            Assert.Equal(source.CalibPreset, clone.CalibPreset);
        }

        [Fact]
        public void SelectPreviousProfileBackup_PreservesExistingBackup()
        {
            string? selected = CalibrationProfileInstaller.SelectPreviousProfileBackup(
                currentDefaultProfile: "current.icm",
                activeGloamProfile: "gloam.icm",
                existingBackup: "factory.icm");

            Assert.Equal("factory.icm", selected);
        }

        [Fact]
        public void SelectPreviousProfileBackup_DoesNotCaptureActiveGloamProfile()
        {
            string? selected = CalibrationProfileInstaller.SelectPreviousProfileBackup(
                currentDefaultProfile: "gloam.icm",
                activeGloamProfile: "gloam.icm",
                existingBackup: null);

            Assert.Null(selected);
        }

        [Fact]
        public void SelectPreviousProfileBackup_CapturesNonGloamDefault()
        {
            string? selected = CalibrationProfileInstaller.SelectPreviousProfileBackup(
                currentDefaultProfile: "factory.icm",
                activeGloamProfile: "gloam.icm",
                existingBackup: null);

            Assert.Equal("factory.icm", selected);
        }

        [Fact]
        public void BuildProfileNamePrefix_SanitizesInvalidMonitorNameCharacters()
        {
            var monitor = new MonitorInfo { FriendlyName = "Panel:Name/With*Invalid?Chars" };

            string prefix = CalibrationProfileInstaller.BuildProfileNamePrefix(monitor);

            Assert.Equal("Panel Name With Invalid Chars - ", prefix);
            Assert.DoesNotContain(':', prefix);
            Assert.DoesNotContain('/', prefix);
            Assert.DoesNotContain('*', prefix);
            Assert.DoesNotContain('?', prefix);
        }

        [Fact]
        public void BuildProfileNamePrefix_TruncatesLikeInstalledProfileNames()
        {
            var monitor = new MonitorInfo
            {
                FriendlyName = "Very Long Professional Reference Monitor Name With EDID Suffix 123456"
            };

            string prefix = CalibrationProfileInstaller.BuildProfileNamePrefix(monitor);

            Assert.Equal("Very Long Professional Reference Monitor - ", prefix);
            Assert.Equal(43, prefix.Length);
        }

        #region MonitorProfileData Validation

        [Fact]
        public void MonitorProfileData_DefaultValues_AreValid()
        {
            var profile = new MonitorProfileData();

            Assert.Equal(GammaMode.Gamma22, profile.GammaMode);
            Assert.Equal(100.0, profile.Brightness);
            Assert.Equal(0.0, profile.Temperature);
            Assert.Equal(0.0, profile.Tint);
            Assert.Equal(1.0, profile.RedGain);
            Assert.Equal(1.0, profile.GreenGain);
            Assert.Equal(1.0, profile.BlueGain);
        }

        [Fact]
        public void MonitorProfileData_Clone_CreatesIndependentCopy()
        {
            var original = new MonitorProfileData
            {
                Brightness = 75.0,
                Temperature = 10.0,
                RedGain = 1.1
            };

            var clone = original.Clone();
            clone.Brightness = 50.0;

            Assert.NotEqual(original.Brightness, clone.Brightness);
            Assert.Equal(75.0, original.Brightness);
        }

        [Fact]
        public void MonitorProfileData_ToCalibrationSettings_PreservesValues()
        {
            var profile = new MonitorProfileData
            {
                Brightness = 80.0,
                Temperature = 15.0,
                Tint = -5.0,
                RedGain = 1.05,
                GreenGain = 0.98,
                BlueGain = 1.02,
                NightModeCcssPath = @"C:\corrections\night.ccss"
            };

            var settings = profile.ToCalibrationSettings();

            Assert.Equal(80.0, settings.Brightness);
            Assert.Equal(15.0, settings.Temperature);
            Assert.Equal(-5.0, settings.Tint);
            Assert.Equal(1.05, settings.RedGain);
            Assert.Equal(profile.NightModeCcssPath, settings.NightModeCcssPath);
        }

        #endregion

        #region NightModeSettingsData Validation

        [Fact]
        public void NightModeSettingsData_DefaultValues_AreValid()
        {
            var settings = new NightModeSettingsData();

            Assert.False(settings.Enabled);
            Assert.False(settings.ManualOverrideEnabled);
            Assert.False(settings.UseAutoSchedule);
            Assert.Null(settings.Latitude);
            Assert.Null(settings.Longitude);
            Assert.Equal(2700, settings.TemperatureKelvin);
            Assert.Equal(30, settings.FadeMinutes);
            Assert.Equal(NightModeAlgorithm.Perceptual, settings.Algorithm);
            Assert.Equal(ColorAdjustments.DefaultPerceptualStrength, settings.PerceptualStrength, 6);
        }

        [Fact]
        public void NightModeSettingsData_RetiredBlueReduction_MigratesToPerceptual()
        {
            var data = new NightModeSettingsData { Algorithm = NightModeAlgorithm.BlueReduction };

            var settings = data.ToNightModeSettings();

            Assert.Equal(NightModeAlgorithm.Perceptual, settings.Algorithm);
        }

        [Theory]
        [InlineData(0.65, 0.65)]
        [InlineData(1.5, 1.0)]     // clamped high
        [InlineData(-0.2, 0.0)]    // clamped low
        public void NightModeSettingsData_PerceptualStrength_RoundTripsAndClamps(double input, double expected)
        {
            var data = new NightModeSettingsData { PerceptualStrength = input };

            var settings = data.ToNightModeSettings();

            Assert.Equal(expected, settings.PerceptualStrength, 6);
        }

        [Fact]
        public void NightModeSettingsData_ToNightModeSettings_ParsesTimeCorrectly()
        {
            var data = new NightModeSettingsData
            {
                StartTime = "22:30",
                EndTime = "06:45"
            };

            var settings = data.ToNightModeSettings();

            Assert.Equal(new TimeSpan(22, 30, 0), settings.StartTime);
            Assert.Equal(new TimeSpan(6, 45, 0), settings.EndTime);
        }

        [Fact]
        public void NightModeSettingsData_ToNightModeSettings_InvalidTime_UsesDefault()
        {
            var data = new NightModeSettingsData
            {
                StartTime = "invalid",
                EndTime = "also-invalid"
            };

            var settings = data.ToNightModeSettings();

            // Should use default values when parsing fails
            Assert.Equal(new TimeSpan(21, 0, 0), settings.StartTime);
            Assert.Equal(new TimeSpan(7, 0, 0), settings.EndTime);
        }

        #endregion

        #region Coordinate Validation

        [Theory]
        [InlineData(-90.0, true)]   // South pole
        [InlineData(90.0, true)]    // North pole
        [InlineData(0.0, true)]     // Equator
        [InlineData(45.0, true)]    // Normal latitude
        [InlineData(-91.0, false)]  // Invalid - too south
        [InlineData(91.0, false)]   // Invalid - too north
        public void Latitude_Validation(double latitude, bool shouldBeValid)
        {
            // The validation happens in SettingsManager.ValidateAndClampSettings
            // After clamping, latitude should be within -90 to 90
            double clamped = Math.Clamp(latitude, -90.0, 90.0);

            if (shouldBeValid)
            {
                Assert.Equal(latitude, clamped);
            }
            else
            {
                Assert.NotEqual(latitude, clamped);
                Assert.InRange(clamped, -90.0, 90.0);
            }
        }

        [Theory]
        [InlineData(-180.0, true)]  // Date line west
        [InlineData(180.0, true)]   // Date line east
        [InlineData(0.0, true)]     // Prime meridian
        [InlineData(-181.0, false)] // Invalid - too west
        [InlineData(181.0, false)]  // Invalid - too east
        public void Longitude_Validation(double longitude, bool shouldBeValid)
        {
            double clamped = Math.Clamp(longitude, -180.0, 180.0);

            if (shouldBeValid)
            {
                Assert.Equal(longitude, clamped);
            }
            else
            {
                Assert.NotEqual(longitude, clamped);
                Assert.InRange(clamped, -180.0, 180.0);
            }
        }

        #endregion

        #region Temperature Kelvin Validation

        [Theory]
        [InlineData(1900, true)]   // Minimum valid
        [InlineData(6500, true)]   // Maximum valid
        [InlineData(2700, true)]   // Common warm value
        [InlineData(4000, true)]   // Mid value
        [InlineData(1899, false)]  // Invalid - too low
        [InlineData(6501, false)]  // Invalid - too high
        [InlineData(0, false)]     // Invalid - zero
        [InlineData(-1000, false)] // Invalid - negative
        public void TemperatureKelvin_Validation(int kelvin, bool shouldBeValid)
        {
            int clamped = Math.Clamp(kelvin, 1900, 6500);

            if (shouldBeValid)
            {
                Assert.Equal(kelvin, clamped);
            }
            else
            {
                Assert.NotEqual(kelvin, clamped);
                Assert.InRange(clamped, 1900, 6500);
            }
        }

        #endregion

        #region FadeMinutes Validation

        [Theory]
        [InlineData(0, true)]     // Instant
        [InlineData(30, true)]    // Default
        [InlineData(120, true)]   // Maximum valid
        [InlineData(-1, false)]   // Invalid - negative
        [InlineData(121, false)]  // Invalid - too long
        [InlineData(1000, false)] // Invalid - way too long
        public void FadeMinutes_Validation(int minutes, bool shouldBeValid)
        {
            int clamped = Math.Clamp(minutes, 0, 120);

            if (shouldBeValid)
            {
                Assert.Equal(minutes, clamped);
            }
            else
            {
                Assert.NotEqual(minutes, clamped);
                Assert.InRange(clamped, 0, 120);
            }
        }

        #endregion

        #region Brightness Validation

        [Theory]
        [InlineData(10.0, true)]   // Minimum valid
        [InlineData(100.0, true)]  // Maximum valid
        [InlineData(50.0, true)]   // Mid value
        [InlineData(9.9, false)]   // Invalid - too low
        [InlineData(101.0, false)] // Invalid - too high
        [InlineData(0.0, false)]   // Invalid - zero
        [InlineData(-50.0, false)] // Invalid - negative
        public void Brightness_Validation(double brightness, bool shouldBeValid)
        {
            double clamped = Math.Clamp(brightness, 10.0, 100.0);

            if (shouldBeValid)
            {
                Assert.Equal(brightness, clamped);
            }
            else
            {
                Assert.NotEqual(brightness, clamped);
                Assert.InRange(clamped, 10.0, 100.0);
            }
        }

        #endregion

        #region RGB Gain Validation

        [Theory]
        [InlineData(0.5, true)]   // Minimum valid
        [InlineData(1.5, true)]   // Maximum valid
        [InlineData(1.0, true)]   // Neutral
        [InlineData(0.49, false)] // Invalid - too low
        [InlineData(1.51, false)] // Invalid - too high
        [InlineData(0.0, false)]  // Invalid - zero
        [InlineData(-1.0, false)] // Invalid - negative
        public void RgbGain_Validation(double gain, bool shouldBeValid)
        {
            double clamped = Math.Clamp(gain, 0.5, 1.5);

            if (shouldBeValid)
            {
                Assert.Equal(gain, clamped);
            }
            else
            {
                Assert.NotEqual(gain, clamped);
                Assert.InRange(clamped, 0.5, 1.5);
            }
        }

        #endregion

        #region CalibrationSettings Tests

        [Fact]
        public void CalibrationSettings_Default_HasNoAdjustments()
        {
            var settings = CalibrationSettings.Default;

            Assert.False(settings.HasAdjustments);
        }

        [Fact]
        public void CalibrationSettings_WithBrightness_HasAdjustments()
        {
            var settings = new CalibrationSettings { Brightness = 80.0 };

            Assert.True(settings.HasAdjustments);
        }

        [Fact]
        public void CalibrationSettings_WithTemperature_HasAdjustments()
        {
            var settings = new CalibrationSettings { Temperature = 10.0 };

            Assert.True(settings.HasAdjustments);
        }

        [Fact]
        public void CalibrationSettings_Sanitized_PreservesExactNightModeTemperatureFloor()
        {
            var settings = new CalibrationSettings { Temperature = -100.0 };

            var sanitized = settings.Sanitized();

            Assert.Equal(CalibrationSettings.MinimumTemperatureScale, sanitized.Temperature, 12);
            Assert.Equal(1900.0, 6500.0 + sanitized.Temperature * 70.0, 9);
        }

        [Fact]
        public void CalibrationSettings_WithGain_HasAdjustments()
        {
            var settings = new CalibrationSettings { RedGain = 1.05 };

            Assert.True(settings.HasAdjustments);
        }

        [Fact]
        public void CalibrationSettings_Hash_IncludesMeasuredCorrectionLut()
        {
            // Regression: GetHashCode previously ignored MeasuredCorrectionLut, so two settings
            // with identical scalars but a freshly-regenerated Lut3D (same profile id) collided
            // in LutGenerator's cache and returned a stale LUT. Reference identity must
            // contribute to the hash so a different LUT instance is never a false hit.
            var lutA = new Lut3D(17);
            var lutB = new Lut3D(17);

            var a = new CalibrationSettings { MeasuredCorrectionLut = lutA };
            var b = new CalibrationSettings { MeasuredCorrectionLut = lutB };

            Assert.NotEqual(a.GetHashCode(), b.GetHashCode());

            // And the same instance should be stable / equal-hash.
            var c = new CalibrationSettings { MeasuredCorrectionLut = lutA };
            Assert.Equal(a.GetHashCode(), c.GetHashCode());
        }

        #endregion

        #region SettingsManager Concurrency

        /// <summary>
        /// SettingsManager is a DI singleton touched from the UI thread, the night-mode timer
        /// thread, and background calibration paths. This is a smoke test that concurrent
        /// Set/Get access doesn't throw (InvalidOperationException from enumerating a mutating
        /// dictionary) or corrupt the in-memory state. It exercises the _dataLock added in C1.
        /// </summary>
        [Fact]
        public async Task SettingsManager_ConcurrentAccess_DoesNotThrow()
        {
            var sm = new SettingsManager();
            string device = @"\\?\DISPLAY#TEST#...";

            await Task.WhenAll(
                Task.Run(() =>
                {
                    for (int i = 0; i < 200; i++)
                        sm.SetProfileForMonitor(device, (i & 1) == 0 ? GammaMode.Gamma24 : GammaMode.Gamma22);
                }),
                Task.Run(() =>
                {
                    for (int i = 0; i < 200; i++)
                        sm.GetProfileForMonitor(device);
                }),
                Task.Run(() =>
                {
                    for (int i = 0; i < 200; i++)
                        sm.GetMonitorProfile(device);
                }),
                Task.Run(() =>
                {
                    for (int i = 0; i < 200; i++)
                    { var _ = sm.ExcludedApps; }
                }));

            // After the storm: the last written mode should be consistent on read.
            sm.SetProfileForMonitor(device, GammaMode.Gamma24);
            Assert.Equal(GammaMode.Gamma24, sm.GetProfileForMonitor(device));
        }

        [Fact]
        public void SettingsManager_ActiveCalibrationProfileRejectsUnusableMeasurements()
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string tempDir = CreateTempDirectory();
            string device = @"\\?\DISPLAY#CORRUPT#1";

            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(tempDir, Path.Combine(tempDir, "roaming"));

                var sm = new SettingsManager();
                var corrupt = ValidCalibrationProfile(device);
                sm.SaveCalibrationProfile(corrupt);
                string corruptJson = corrupt.ToJson()
                    .Replace("\"peakLuminance\": 120", "\"peakLuminance\": -100")
                    .Replace("\"measuredGamma\": 2.25", "\"measuredGamma\": 0");
                File.WriteAllText(Path.Combine(tempDir, "CalibrationProfiles", $"{corrupt.Id}.json"), corruptJson);
                sm.SetActiveCalibrationProfile(device, corrupt.Id);

                Assert.NotNull(sm.LoadCalibrationProfile(corrupt.Id));
                Assert.Null(sm.LoadUsableCalibrationProfile(corrupt.Id));
                Assert.Null(sm.GetActiveCalibrationProfile(device));
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                DeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void SettingsManager_ActiveCalibrationProfileReturnsValidMeasurements()
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string tempDir = CreateTempDirectory();
            string device = @"\\?\DISPLAY#VALID#1";

            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(tempDir, Path.Combine(tempDir, "roaming"));

                var sm = new SettingsManager();
                var profile = ValidCalibrationProfile(device);
                sm.SaveCalibrationProfile(profile);
                sm.SetActiveCalibrationProfile(device, profile.Id);

                var active = sm.GetActiveCalibrationProfile(device);

                Assert.NotNull(active);
                Assert.Equal(profile.Id, active.Id);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                DeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void SettingsManager_ProfileIdsCannotEscapeCalibrationDirectory()
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string tempDir = CreateTempDirectory();
            string outsidePath = Path.Combine(tempDir, "outside.json");

            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(tempDir, Path.Combine(tempDir, "roaming"));
                File.WriteAllText(outsidePath, "do not touch");

                var settings = new SettingsManager();
                var profile = ValidCalibrationProfile(@"\\?\DISPLAY#TRAVERSAL#1");
                profile.Id = @"..\outside";

                Assert.Throws<ArgumentException>(() => settings.SaveCalibrationProfile(profile));
                Assert.Null(settings.LoadCalibrationProfile(@"..\outside"));
                Assert.False(settings.DeleteCalibrationProfile(@"..\outside"));
                Assert.Equal("do not touch", File.ReadAllText(outsidePath));
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                DeleteDirectory(tempDir);
            }
        }

        [Fact]
        public void NightModeSettingsData_RoundTripsManualOverride()
        {
            var data = NightModeSettingsData.FromNightModeSettings(new NightModeSettings
            {
                Enabled = true,
                ManualOverrideEnabled = true
            });

            var settings = data.ToNightModeSettings();

            Assert.True(settings.Enabled);
            Assert.True(settings.ManualOverrideEnabled);
        }

        [Fact]
        public void SettingsManager_StartupDefaultFlag_Persists()
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string tempDir = CreateTempDirectory();

            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(tempDir, Path.Combine(tempDir, "roaming"));

                var first = new SettingsManager();
                Assert.False(first.LoadedExistingSettingsFile);
                Assert.False(first.StartupDefaultApplied);

                first.MarkStartupDefaultApplied();

                var second = new SettingsManager();
                Assert.True(second.LoadedExistingSettingsFile);
                Assert.True(second.StartupDefaultApplied);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                DeleteDirectory(tempDir);
            }
        }

        #endregion

        #region AppExclusionRule Tests

        [Fact]
        public void AppExclusionRule_DefaultValues()
        {
            var rule = new AppExclusionRule();

            Assert.Equal(string.Empty, rule.AppName);
            Assert.False(rule.FullDisable);
        }

        [Fact]
        public void AppExclusionRule_CanSetValues()
        {
            var rule = new AppExclusionRule
            {
                AppName = "game.exe",
                FullDisable = true
            };

            Assert.Equal("game.exe", rule.AppName);
            Assert.True(rule.FullDisable);
        }

        [Fact]
        public void NormalizeExcludedApps_DropsInvalidRules_AndMergesDuplicates()
        {
            var normalized = SettingsManager.NormalizeExcludedApps(new AppExclusionRule?[]
            {
                null,
                new AppExclusionRule { AppName = " " },
                new AppExclusionRule { AppName = @"C:\Apps\Resolve", FullDisable = false },
                new AppExclusionRule { AppName = "resolve.EXE", FullDisable = true }
            });

            var rule = Assert.Single(normalized);
            Assert.Equal("Resolve.exe", rule.AppName);
            Assert.True(rule.FullDisable);
        }

        [Fact]
        public void SettingsManager_ExcludedApps_AreDeepSnapshots()
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string tempDir = CreateTempDirectory();

            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(tempDir, Path.Combine(tempDir, "roaming"));
                var settings = new SettingsManager();
                var source = new AppExclusionRule { AppName = "resolve.exe", FullDisable = false };
                settings.SetExcludedApps(new System.Collections.Generic.List<AppExclusionRule> { source });

                source.AppName = "mutated.exe";
                var snapshot = settings.ExcludedApps;
                snapshot[0].AppName = "also-mutated.exe";

                var stored = Assert.Single(settings.ExcludedApps);
                Assert.Equal("resolve.exe", stored.AppName);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                DeleteDirectory(tempDir);
            }
        }

        #endregion

        private static DisplayCalibrationProfile ValidCalibrationProfile(string monitorDevicePath) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Valid measured profile",
            MonitorDevicePath = monitorDevicePath,
            MonitorName = "Test Monitor",
            RedPrimaryX = Chromaticity.Rec709Red.X,
            RedPrimaryY = Chromaticity.Rec709Red.Y,
            GreenPrimaryX = Chromaticity.Rec709Green.X,
            GreenPrimaryY = Chromaticity.Rec709Green.Y,
            BluePrimaryX = Chromaticity.Rec709Blue.X,
            BluePrimaryY = Chromaticity.Rec709Blue.Y,
            WhitePointX = Chromaticity.D65.X,
            WhitePointY = Chromaticity.D65.Y,
            BlackLevel = 0.05,
            PeakLuminance = 120.0,
            MeasuredGamma = 2.25
        };

        private static string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "GloamSettingsValidationTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for test temp files.
            }
        }
    }
}
