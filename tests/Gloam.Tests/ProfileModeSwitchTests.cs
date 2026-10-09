using System;
using System.IO;
using Gloam.Core;
using Gloam.Core.Calibration;
using Gloam.Interop;
using Xunit;

namespace Gloam.Tests
{
    public sealed class ProfileModeSwitchTests
    {
        [Theory]
        // sdrSlot, hdrSlot, active, hdr, disable, enable, verifyOnly, restorePrevious
        [InlineData(null, null, null, true, null, null, false, false)]
        [InlineData(null, null, "legacy.icm", false, null, null, false, false)]
        [InlineData("sdr.icm", "hdr.icm", "sdr.icm", true, "sdr.icm", "hdr.icm", false, false)]
        [InlineData("sdr.icm", "hdr.icm", "hdr.icm", false, "hdr.icm", "sdr.icm", false, false)]
        [InlineData("sdr.icm", null, "sdr.icm", true, "sdr.icm", null, false, true)]
        [InlineData(null, "hdr.icm", "hdr.icm", false, "hdr.icm", null, false, true)]
        [InlineData("sdr.icm", null, null, false, null, "sdr.icm", false, false)]
        [InlineData("sdr.icm", null, null, true, null, null, false, false)]
        [InlineData("sdr.icm", "hdr.icm", "HDR.icm", true, null, "hdr.icm", true, false)]
        public void DecideModeSwitch_PicksTheProfileBuiltForTheCurrentMode(
            string? sdrSlot, string? hdrSlot, string? active, bool hdr,
            string? disable, string? enable, bool verifyOnly, bool restorePrevious)
        {
            var plan = AdvancedColorProfileMigrationService.DecideModeSwitch(sdrSlot, hdrSlot, active, hdr);

            Assert.Equal(disable, plan.Disable);
            Assert.Equal(enable, plan.Enable, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(verifyOnly, plan.VerifyOnly);
            Assert.Equal(restorePrevious, plan.RestorePrevious);
        }

        [Fact]
        public void Settings_SlotsFollowInstallsAndExplicitForgets()
        {
            WithIsolatedSettings(settings =>
            {
                const string path = @"MONITOR\TEST\INSTANCE";
                settings.SetMhc2Calibration(path, "sdr.icm", builtForHdr: false);
                settings.SetMhc2Calibration(path, "hdr.icm", builtForHdr: true);
                var p = settings.GetMonitorProfile(path)!;
                Assert.Equal(("sdr.icm", "hdr.icm", "hdr.icm"), (p.Mhc2SdrProfileName, p.Mhc2HdrProfileName, p.Mhc2ProfileName));

                // Clearing the active profile (measurement bypass) drops only its own slot.
                settings.SetMhc2Calibration(path, null);
                p = settings.GetMonitorProfile(path)!;
                Assert.Equal(("sdr.icm", (string?)null, (string?)null), (p.Mhc2SdrProfileName, p.Mhc2HdrProfileName, p.Mhc2ProfileName));

                settings.SetActiveMhc2Profile(path, "sdr.icm");
                settings.ForgetMhc2Profile(path, "sdr.icm");
                p = settings.GetMonitorProfile(path)!;
                Assert.Null(p.Mhc2SdrProfileName);
                Assert.Null(p.Mhc2ProfileName);

                settings.RecordDisplacedAdvancedColorProfile(path, "first.icc");
                settings.RecordDisplacedAdvancedColorProfile(path, "second.icc");
                Assert.Equal("first.icc", settings.GetMonitorProfile(path)!.PreviousAdvancedColorProfileName);
            });
        }

        [Fact]
        public void Reconcile_SwitchToHdr_SwapsInTheHdrProfile_OnlyOnTransition()
        {
            WithIsolatedSettings(settings =>
            {
                var monitor = Monitor(hdr: true);
                settings.SetMhc2Calibration(monitor.MonitorDevicePath, "hdr.icm", builtForHdr: true);
                settings.SetMhc2Calibration(monitor.MonitorDevicePath, "sdr.icm", builtForHdr: false);
                var platform = new FakeAdvancedColorPlatform { PerUserEnabled = true, CurrentDefault = "sdr.icm" };
                platform.CurrentProfiles.Add("sdr.icm");

                WithPlatform(platform, () =>
                {
                    var service = new AdvancedColorProfileMigrationService(settings, () => new[] { monitor });
                    service.ReconcileModeChanges();

                    Assert.Equal("hdr.icm", platform.CurrentDefault);
                    Assert.DoesNotContain("sdr.icm", platform.CurrentProfiles);
                    var p = settings.GetMonitorProfile(monitor.MonitorDevicePath)!;
                    Assert.Equal(("hdr.icm", "sdr.icm", "hdr.icm"), (p.Mhc2ProfileName, p.Mhc2SdrProfileName, p.Mhc2HdrProfileName));

                    // Same mode again: a resolution change or burst event must not touch Windows.
                    platform.CurrentDefault = "user choice.icm";
                    service.ReconcileModeChanges();
                    Assert.Equal("user choice.icm", platform.CurrentDefault);
                });
            });
        }

        [Fact]
        public void Reconcile_SwitchToSdrWithoutSdrProfile_RestoresDisplacedForeignProfile()
        {
            WithIsolatedSettings(settings =>
            {
                var monitor = Monitor(hdr: false);
                settings.SetMhc2Calibration(monitor.MonitorDevicePath, "hdr.icm", builtForHdr: true);
                settings.RecordDisplacedAdvancedColorProfile(monitor.MonitorDevicePath, "HDR Calibration.icc");
                var platform = new FakeAdvancedColorPlatform { PerUserEnabled = true, CurrentDefault = "hdr.icm" };
                platform.CurrentProfiles.Add("hdr.icm");

                WithPlatform(platform, () =>
                {
                    new AdvancedColorProfileMigrationService(settings, () => new[] { monitor }).ReconcileModeChanges();

                    Assert.Equal("HDR Calibration.icc", platform.CurrentDefault);
                    var p = settings.GetMonitorProfile(monitor.MonitorDevicePath)!;
                    Assert.Null(p.Mhc2ProfileName);
                    Assert.Equal("hdr.icm", p.Mhc2HdrProfileName);
                    // Restored, so a profile the user picks later is never overwritten with this one.
                    Assert.Null(p.PreviousAdvancedColorProfileName);
                });
            });
        }

        [Theory]
        [InlineData("Test Display - HDR Desktop PQ sRGB gamut - 2026-01-01 1200.icm", true)]
        [InlineData("Test Display - HDR Desktop PQ sRGB gamut - 2026-01-01 1200 ICC-safe 1.7.8.icm", true)]
        [InlineData("Test Display - Rec.2020 HLG - 2026-01-01 1200 (2).icm", true)]
        [InlineData("Test Display - sRGB G2.2 - 2026-01-01 1200.icm", false)]
        [InlineData("Test Display - sRGB G2.2 WPonly - 2026-01-01 1200.icm", false)]
        [InlineData("Test Display - Display Native - 2026-01-01 1200.icm", null)]
        [InlineData("HDR-kalibreret profil.icc", null)]
        public void ClassifyHdrFromName_ReadsTheTargetSegment(string name, bool? expected)
        {
            Assert.Equal(expected, CalibrationProfileInstaller.ClassifyHdrFromName(name));
        }

        [Fact]
        public void Reconcile_LegacyHdrRecordInSdr_IsClassifiedAndRetired()
        {
            WithIsolatedSettings(settings =>
            {
                var monitor = Monitor(hdr: false);
                const string legacy = "Test Display - HDR Desktop PQ sRGB gamut - 2026-01-01 1200.icm";
                settings.SetMhc2Calibration(monitor.MonitorDevicePath, legacy);
                var platform = new FakeAdvancedColorPlatform { PerUserEnabled = true, CurrentDefault = legacy };
                platform.CurrentProfiles.Add(legacy);

                WithPlatform(platform, () =>
                {
                    new AdvancedColorProfileMigrationService(settings, () => new[] { monitor }).ReconcileModeChanges();

                    var p = settings.GetMonitorProfile(monitor.MonitorDevicePath)!;
                    Assert.Equal(legacy, p.Mhc2HdrProfileName);
                    Assert.Null(p.Mhc2ProfileName);
                    Assert.Null(platform.CurrentDefault);
                });
            });
        }

        [Fact]
        public void Reconcile_WhenWindowsRefusesTheRetire_KeepsActiveAndRetriesNextEvent()
        {
            WithIsolatedSettings(settings =>
            {
                var monitor = Monitor(hdr: true);
                settings.SetMhc2Calibration(monitor.MonitorDevicePath, "sdr.icm", builtForHdr: false);
                var platform = new FakeAdvancedColorPlatform { PerUserEnabled = true, CurrentDefault = "sdr.icm", FailRemove = true };
                platform.CurrentProfiles.Add("sdr.icm");

                WithPlatform(platform, () =>
                {
                    var service = new AdvancedColorProfileMigrationService(settings, () => new[] { monitor });
                    service.ReconcileModeChanges();
                    Assert.Equal("sdr.icm", settings.GetMonitorProfile(monitor.MonitorDevicePath)!.Mhc2ProfileName);

                    platform.FailRemove = false;
                    service.ReconcileModeChanges(); // same mode, but the failed attempt must retry
                    Assert.Null(settings.GetMonitorProfile(monitor.MonitorDevicePath)!.Mhc2ProfileName);
                    Assert.Null(platform.CurrentDefault);
                });
            });
        }

        [Fact]
        public void Reconcile_RefusedForeignRestore_IsRetriedOnTheNextEvent()
        {
            WithIsolatedSettings(settings =>
            {
                var monitor = Monitor(hdr: false);
                settings.SetMhc2Calibration(monitor.MonitorDevicePath, "hdr.icm", builtForHdr: true);
                settings.RecordDisplacedAdvancedColorProfile(monitor.MonitorDevicePath, "HDR Calibration.icc");
                var platform = new FakeAdvancedColorPlatform
                {
                    PerUserEnabled = true, CurrentDefault = "hdr.icm", IgnoreDefaultWrites = true
                };
                platform.CurrentProfiles.Add("hdr.icm");

                WithPlatform(platform, () =>
                {
                    var service = new AdvancedColorProfileMigrationService(settings, () => new[] { monitor });
                    service.ReconcileModeChanges();
                    Assert.NotEqual("HDR Calibration.icc", platform.CurrentDefault);

                    platform.IgnoreDefaultWrites = false;
                    service.ReconcileModeChanges();
                    Assert.Equal("HDR Calibration.icc", platform.CurrentDefault);
                });
            });
        }

        [Fact]
        public void Settings_StaleWholeRecordSave_DoesNotRollBackCalibrationFields()
        {
            WithIsolatedSettings(settings =>
            {
                const string path = @"MONITOR\TEST\INSTANCE";
                settings.SetMhc2Calibration(path, "sdr.icm", builtForHdr: false);
                var staleEditorCopy = settings.GetMonitorProfile(path)!;
                settings.SetMhc2Calibration(path, "hdr.icm", builtForHdr: true);

                staleEditorCopy.Brightness = 42;
                settings.SetMonitorProfile(path, staleEditorCopy);

                var p = settings.GetMonitorProfile(path)!;
                Assert.Equal(42, p.Brightness);
                Assert.Equal("hdr.icm", p.Mhc2ProfileName);
                Assert.Equal("hdr.icm", p.Mhc2HdrProfileName);
            });
        }

        [Fact]
        public void Settings_NewFieldsSurviveReload_AndRepairRenamesTheHdrSlot()
        {
            WithIsolatedSettings(settings =>
            {
                const string path = @"MONITOR\TEST\INSTANCE";
                settings.SetMhc2Calibration(path, "sdr.icm", builtForHdr: false);
                settings.SetMhc2Calibration(path, "hdr.icm", builtForHdr: true);
                settings.RecordDisplacedAdvancedColorProfile(path, "foreign.icc");
                Assert.True(settings.TryReplaceMhc2Calibration(path, "hdr.icm", "hdr ICC-safe.icm"));

                var p = new SettingsManager().GetMonitorProfile(path)!;
                Assert.Equal("sdr.icm", p.Mhc2SdrProfileName);
                Assert.Equal("hdr ICC-safe.icm", p.Mhc2HdrProfileName);
                Assert.Equal("hdr ICC-safe.icm", p.Mhc2ProfileName);
                Assert.Equal("foreign.icc", p.PreviousAdvancedColorProfileName);
            });
        }

        private static MonitorInfo Monitor(bool hdr) => new()
        {
            DeviceName = @"\\.\DISPLAY1",
            MonitorDevicePath = @"MONITOR\TEST\INSTANCE",
            FriendlyName = "Test Display",
            IsHdrActive = hdr,
            HasDisplayConfigIds = true,
            DisplayConfigAdapterId = new Dxgi.LUID { LowPart = 10, HighPart = 20 },
            DisplayConfigSourceId = 30
        };

        private static void WithPlatform(IAdvancedColorProfilePlatform platform, Action body)
        {
            var previous = AdvancedColorProfileAssociation.Platform;
            AdvancedColorProfileAssociation.Platform = platform;
            try { body(); }
            finally { AdvancedColorProfileAssociation.Platform = previous; }
        }

        private static void WithIsolatedSettings(Action<SettingsManager> body)
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), $"gloam-mode-switch-test-{Guid.NewGuid():N}");
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(Path.Combine(root, "data"), Path.Combine(root, "roaming"));
                body(new SettingsManager());
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
