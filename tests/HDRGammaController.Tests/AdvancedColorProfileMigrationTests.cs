using System;
using System.IO;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Interop;
using Xunit;

namespace HDRGammaController.Tests
{
    public sealed class AdvancedColorProfileMigrationTests
    {
        [Fact]
        public void Run_RepairsLegacyGloamProfile_UpdatesSettings_AndActivatesVerifiedCopy()
        {
            string? template = FindTemplate();
            if (template == null) return;

            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), $"gloam-migration-test-{Guid.NewGuid():N}");
            string data = Path.Combine(root, "data");
            string store = Path.Combine(root, "color");
            Directory.CreateDirectory(store);

            var previousPlatform = AdvancedColorProfileAssociation.Platform;
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(data, Path.Combine(root, "roaming"));
                var settings = new SettingsManager();
                var monitor = Monitor();
                string legacyName = CalibrationProfileInstaller.BuildProfileNamePrefix(monitor) +
                                    "HDR Desktop - legacy.icm";
                string legacyPath = Path.Combine(store, legacyName);
                BuildLegacyProfile(template, legacyPath);
                settings.SetMhc2Calibration(monitor.MonitorDevicePath, legacyName);
                Assert.True(File.Exists(Path.Combine(data, "settings.json")));
                settings = new SettingsManager(); // startup instance: existing file eligible for backup
                Assert.True(settings.LoadedExistingSettingsFile);

                var platform = new FakeAdvancedColorPlatform
                {
                    ColorStoreDirectory = store,
                    PerUserEnabled = false,
                    SystemDefault = "factory.icm"
                };
                platform.SystemProfiles.Add("factory.icm");
                platform.CurrentProfiles.Add(legacyName);
                platform.CurrentDefault = legacyName;
                AdvancedColorProfileAssociation.Platform = platform;

                var service = new AdvancedColorProfileMigrationService(
                    settings, () => new[] { monitor });
                var result = service.Run();

                Assert.Equal(1, result.Inspected);
                Assert.Equal(1, result.Repaired);
                Assert.Equal(0, result.Failed);
                string repairedName = AdvancedColorProfileMigrationService.BuildRepairedProfileName(legacyName);
                string repairedPath = Path.Combine(store, repairedName);
                Assert.True(File.Exists(repairedPath));
                Assert.False(Mhc2ProfileBuilder.NeedsAdvancedColorIccCharacterizationRepair(repairedPath));
                Assert.Equal(repairedName,
                    settings.GetMonitorProfile(monitor.MonitorDevicePath)?.Mhc2ProfileName);
                Assert.True(platform.PerUserEnabled);
                Assert.Equal(repairedName, platform.CurrentDefault);
                Assert.Contains(repairedName, platform.CurrentProfiles);
                Assert.DoesNotContain(legacyName, platform.CurrentProfiles);
                Assert.NotEmpty(Directory.GetFiles(data,
                    "settings.json.pre-1.7.8-profile-migration-*.bak"));
            }
            finally
            {
                AdvancedColorProfileAssociation.Platform = previousPlatform;
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Run_SafeProfileInInactiveUserList_IsReactivatedWithoutCreatingCopy()
        {
            string? template = FindTemplate();
            if (template == null) return;

            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), $"gloam-reactivate-test-{Guid.NewGuid():N}");
            string store = Path.Combine(root, "color");
            Directory.CreateDirectory(store);
            var previousPlatform = AdvancedColorProfileAssociation.Platform;
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(Path.Combine(root, "data"), Path.Combine(root, "roaming"));
                var monitor = Monitor();
                string safeName = CalibrationProfileInstaller.BuildProfileNamePrefix(monitor) + "HDR safe.icm";
                BuildSafeProfile(template, Path.Combine(store, safeName));
                var settings = new SettingsManager();
                settings.SetMhc2Calibration(monitor.MonitorDevicePath, safeName);

                var platform = new FakeAdvancedColorPlatform
                {
                    ColorStoreDirectory = store,
                    PerUserEnabled = false,
                    SystemDefault = safeName
                };
                platform.SystemProfiles.Add(safeName);
                platform.CurrentProfiles.Add(safeName);
                AdvancedColorProfileAssociation.Platform = platform;

                var result = new AdvancedColorProfileMigrationService(
                    settings, () => new[] { monitor }).Run();

                Assert.Equal(0, result.Repaired);
                Assert.Equal(1, result.Reactivated);
                Assert.Equal(safeName, platform.CurrentDefault);
                Assert.True(platform.PerUserEnabled);
                Assert.Single(Directory.GetFiles(store, "*.icm"));
            }
            finally
            {
                AdvancedColorProfileAssociation.Platform = previousPlatform;
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }

        private static MonitorInfo Monitor() => new()
        {
            DeviceName = @"\\.\DISPLAY1",
            MonitorDevicePath = @"MONITOR\TEST\INSTANCE",
            FriendlyName = "Test Display",
            IsHdrActive = true,
            HasDisplayConfigIds = true,
            DisplayConfigAdapterId = new Dxgi.LUID { LowPart = 10, HighPart = 20 },
            DisplayConfigSourceId = 30
        };

        private static string? FindTemplate()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "srgb_to_gamma2p2_200_mhc2.icm");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static void BuildSafeProfile(string template, string path)
        {
            var identity = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            var lut = new double[1024];
            for (int i = 0; i < lut.Length; i++) lut[i] = i / 1023.0;
            Mhc2ProfileBuilder.Build(template, path, identity, lut, lut, lut,
                minLuminanceNits: 0.02, maxLuminanceNits: 242,
                colorimetry: StandardTargets.Rec709Pq);
        }

        private static void BuildLegacyProfile(string template, string path)
        {
            BuildSafeProfile(template, path);
            byte[] bytes = File.ReadAllBytes(path);
            int trc = FindTag(bytes, 0x72545243);
            for (int i = 0; i < 1024; i++)
            {
                int value = (int)Math.Round(Math.Clamp(
                    StandardTargets.Rec709Pq.ApplyEotf(i / 1023.0), 0, 1) * 65535);
                int offset = trc + 12 + i * 2;
                bytes[offset] = (byte)(value >> 8);
                bytes[offset + 1] = (byte)value;
            }
            File.WriteAllBytes(path, bytes);
        }

        private static int FindTag(byte[] bytes, int signature)
        {
            int count = ReadU32(bytes, 128);
            for (int i = 0; i < count; i++)
            {
                int entry = 132 + i * 12;
                if (ReadU32(bytes, entry) == signature) return ReadU32(bytes, entry + 4);
            }
            throw new InvalidDataException($"Tag 0x{signature:X8} missing.");
        }

        private static int ReadU32(byte[] bytes, int offset) =>
            (bytes[offset] << 24) | (bytes[offset + 1] << 16) |
            (bytes[offset + 2] << 8) | bytes[offset + 3];
    }
}
