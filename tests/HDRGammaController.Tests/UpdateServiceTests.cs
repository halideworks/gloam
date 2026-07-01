using System;
using System.IO;
using System.Threading.Tasks;
using HDRGammaController.Core;
using HDRGammaController.Services;
using Velopack;
using Xunit;

namespace HDRGammaController.Tests
{
    public sealed class UpdateServiceTests : IDisposable
    {
        private readonly string _originalDataDir;
        private readonly string _originalRoamingDir;
        private readonly string _tempDir;

        public UpdateServiceTests()
        {
            _originalDataDir = AppPaths.DataDir;
            _originalRoamingDir = AppPaths.RoamingDataDir;
            _tempDir = Path.Combine(Path.GetTempPath(), "GloamUpdateServiceTests", Guid.NewGuid().ToString("N"));
            AppPaths.UseDataDirectoriesForCurrentProcess(_tempDir, Path.Combine(_tempDir, "roaming"));
        }

        [Fact]
        public async Task CheckForUpdatesAsync_IgnoresSameVersionTarget()
        {
            var manager = new FakeUpdateManager("1.0.1")
            {
                NextUpdate = CreateUpdate("1.0.1")
            };
            var service = new UpdateService(manager);

            var update = await service.CheckForUpdatesAsync();

            Assert.Null(update);
            Assert.Equal(1, manager.CheckCount);
        }

        [Fact]
        public async Task CheckForUpdatesAsync_ReturnsNewerTarget()
        {
            var manager = new FakeUpdateManager("1.0.1")
            {
                NextUpdate = CreateUpdate("1.0.2")
            };
            var service = new UpdateService(manager);

            var update = await service.CheckForUpdatesAsync();

            Assert.NotNull(update);
            Assert.Equal("1.0.2", UpdateService.VersionLabel(update!));
        }

        [Fact]
        public async Task CheckForUpdatesAsync_IgnoresDowngradeTarget()
        {
            var manager = new FakeUpdateManager("1.0.1")
            {
                NextUpdate = CreateUpdate("1.0.0", isDowngrade: true)
            };
            var service = new UpdateService(manager);

            var update = await service.CheckForUpdatesAsync();

            Assert.Null(update);
        }

        [Fact]
        public async Task CheckForUpdatesAsync_IgnoresUnexpectedPackageId()
        {
            var manager = new FakeUpdateManager("1.0.1")
            {
                NextUpdate = CreateUpdate("1.0.2", packageId: "OtherApp")
            };
            var service = new UpdateService(manager);

            var update = await service.CheckForUpdatesAsync();

            Assert.Null(update);
        }

        [Fact]
        public async Task CheckForUpdatesAsync_IgnoresUpdateWithoutSha256Metadata()
        {
            var manager = new FakeUpdateManager("1.0.1")
            {
                NextUpdate = CreateUpdate("1.0.2", sha256: "")
            };
            var service = new UpdateService(manager);

            var update = await service.CheckForUpdatesAsync();

            Assert.Null(update);
        }

        [Fact]
        public async Task CheckForUpdatesAsync_IgnoresUpdateWithInvalidSha256Metadata()
        {
            var manager = new FakeUpdateManager("1.0.1")
            {
                NextUpdate = CreateUpdate("1.0.2", sha256: new string('z', 64))
            };
            var service = new UpdateService(manager);

            var update = await service.CheckForUpdatesAsync();

            Assert.Null(update);
        }

        [Fact]
        public async Task DownloadAndSchedule_RefuseSameVersionEvenIfCalledDirectly()
        {
            var manager = new FakeUpdateManager("1.0.1");
            var service = new UpdateService(manager);
            var update = CreateUpdate("1.0.1");

            bool downloaded = await service.DownloadUpdatesAsync(update);
            bool scheduled = service.ApplyUpdatesOnExit(update);

            Assert.False(downloaded);
            Assert.False(scheduled);
            Assert.Equal(0, manager.DownloadCount);
            Assert.Equal(0, manager.ScheduleCount);
        }

        [Fact]
        public async Task DownloadAndSchedule_AllowsNewerVersion()
        {
            var manager = new FakeUpdateManager("1.0.1");
            var service = new UpdateService(manager);
            var update = CreateUpdate("1.0.2");

            bool downloaded = await service.DownloadUpdatesAsync(update);
            bool scheduled = service.ApplyUpdatesOnExit(update);

            Assert.True(downloaded);
            Assert.True(scheduled);
            Assert.Equal(1, manager.DownloadCount);
            Assert.Equal(1, manager.ScheduleCount);
        }

        [Fact]
        public void DisplayVersion_UsesInstalledPackageVersion()
        {
            var service = new UpdateService(new FakeUpdateManager("1.2.3"));

            Assert.Equal("1.2.3", service.InstalledVersion);
            Assert.Equal("v1.2.3", service.DisplayVersion);
        }

        [Fact]
        public async Task CheckForUpdatesAsync_ThrottlesRecentSuccessfulCheck()
        {
            var now = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
            var manager = new FakeUpdateManager("1.0.1")
            {
                NextUpdate = null
            };
            var service = new UpdateService(manager, () => now);

            Assert.Null(await service.CheckForUpdatesAsync());
            Assert.Null(await service.CheckForUpdatesAsync());

            Assert.Equal(1, manager.CheckCount);
        }

        [Fact]
        public async Task CheckForUpdatesAsync_RetriesFailuresAfterShorterInterval()
        {
            var now = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
            var manager = new FakeUpdateManager("1.0.1")
            {
                ThrowOnCheck = true
            };
            var service = new UpdateService(manager, () => now);

            Assert.Null(await service.CheckForUpdatesAsync());
            now += UpdateService.FailedCheckRetryInterval + TimeSpan.FromSeconds(1);
            Assert.Null(await service.CheckForUpdatesAsync());

            Assert.Equal(2, manager.CheckCount);
            Assert.Equal(2, service.StateSnapshot.ConsecutiveFailures);
        }

        [Fact]
        public void TrySchedulePendingUpdateOnExit_SchedulesNewerPreparedUpdate()
        {
            var manager = new FakeUpdateManager("1.0.1")
            {
                PendingAsset = CreateAsset("1.0.2")
            };
            var service = new UpdateService(manager);

            bool scheduled = service.TrySchedulePendingUpdateOnExit();

            Assert.True(scheduled);
            Assert.Equal(1, manager.ScheduleCount);
            Assert.Equal("1.0.2", service.StateSnapshot.LastScheduledVersion);
        }

        [Fact]
        public void TrySchedulePendingUpdateOnExit_IgnoresSameVersionPreparedUpdate()
        {
            var manager = new FakeUpdateManager("1.0.1")
            {
                PendingAsset = CreateAsset("1.0.1")
            };
            var service = new UpdateService(manager);

            bool scheduled = service.TrySchedulePendingUpdateOnExit();

            Assert.False(scheduled);
            Assert.Equal(0, manager.ScheduleCount);
        }

        [Fact]
        public async Task PersistentFailures_NotifyAfterThresholdAndRespectCooldown()
        {
            var now = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
            var manager = new FakeUpdateManager("1.0.1")
            {
                ThrowOnCheck = true
            };
            var service = new UpdateService(manager, () => now);

            for (int i = 0; i < 3; i++)
            {
                Assert.Null(await service.CheckForUpdatesAsync());
                now += UpdateService.FailedCheckRetryInterval + TimeSpan.FromSeconds(1);
            }

            Assert.True(service.ShouldNotifyPersistentFailure());
            service.MarkFailureNotified();
            Assert.False(service.ShouldNotifyPersistentFailure());
            now += UpdateService.FailureNotificationInterval + TimeSpan.FromSeconds(1);
            Assert.True(service.ShouldNotifyPersistentFailure());
        }

        [Fact]
        public async Task InformationalResults_DoNotClearLastFailureMessage()
        {
            var now = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
            var manager = new FakeUpdateManager("1.0.1")
            {
                ThrowOnCheck = true
            };
            var service = new UpdateService(manager, () => now);

            Assert.Null(await service.CheckForUpdatesAsync());
            Assert.Equal("feed unavailable", service.StateSnapshot.LastError);

            Assert.False(service.TrySchedulePendingUpdateOnExit());

            Assert.Equal("feed unavailable", service.StateSnapshot.LastError);
        }

        [Fact]
        public async Task CheckForUpdatesAsync_WritesDiagnosticStateFile()
        {
            var manager = new FakeUpdateManager("1.0.1")
            {
                NextUpdate = CreateUpdate("1.0.2")
            };
            var service = new UpdateService(manager);

            Assert.NotNull(await service.CheckForUpdatesAsync());

            string statePath = Path.Combine(AppPaths.DataDir, "update-state.json");
            Assert.True(File.Exists(statePath));
            string json = File.ReadAllText(statePath);
            Assert.Contains("\"InstalledVersion\": \"1.0.1\"", json);
            Assert.Contains("\"LastTargetVersion\": \"1.0.2\"", json);
        }

        [Fact]
        public void UpdateReadyNotification_IsOncePerTargetVersion()
        {
            var service = new UpdateService(new FakeUpdateManager("1.0.1"));

            Assert.True(service.ShouldNotifyUpdateReady("1.0.2"));
            service.MarkUpdateReadyNotified("1.0.2");

            Assert.False(service.ShouldNotifyUpdateReady("1.0.2"));
            Assert.True(service.ShouldNotifyUpdateReady("1.0.3"));
            Assert.Equal("1.0.2", service.StateSnapshot.LastUpdateReadyNotificationVersion);
        }

        [Fact]
        public void UpdateAvailableNotification_IsOncePerTargetVersion()
        {
            var service = new UpdateService(new FakeUpdateManager("1.0.1"));

            Assert.True(service.ShouldNotifyUpdateAvailable("1.0.2"));
            service.MarkUpdateAvailableNotified("1.0.2");

            Assert.False(service.ShouldNotifyUpdateAvailable("1.0.2"));
            Assert.True(service.ShouldNotifyUpdateAvailable("1.0.3"));
            Assert.Equal("1.0.2", service.StateSnapshot.LastUpdateAvailableNotificationVersion);
        }

        [Fact]
        public void UpdatedNotification_IsOncePerInstalledVersion()
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "update-state.json"), """
                {
                  "IsInstalled": true,
                  "InstalledVersion": "1.0.1"
                }
                """);

            var service = new UpdateService(new FakeUpdateManager("1.0.2"));

            Assert.Equal("1.0.1", service.UpdatedFromVersion);
            Assert.Equal("1.0.2", service.UpdatedToVersion);
            Assert.True(service.ShouldNotifyUpdated("1.0.2"));

            service.MarkUpdatedNotified("1.0.2");

            Assert.False(service.ShouldNotifyUpdated("1.0.2"));
            Assert.True(service.ShouldNotifyUpdated("1.0.3"));
            Assert.Equal("1.0.2", service.StateSnapshot.LastUpdatedNotificationVersion);
        }

        private static UpdateInfo CreateUpdate(
            string version,
            bool isDowngrade = false,
            string packageId = "GloamApp",
            string? sha256 = null)
            => new(CreateAsset(version, packageId, sha256), isDowngrade, CreateAsset(version, packageId, sha256), Array.Empty<VelopackAsset>());

        private static VelopackAsset CreateAsset(string version, string packageId = "GloamApp", string? sha256 = null)
        {
            return new VelopackAsset
            {
                PackageId = packageId,
                Version = SemanticVersion.Parse(version),
                Type = VelopackAssetType.Full,
                FileName = $"GloamApp-{version}-full.nupkg",
                SHA256 = sha256 ?? new string('a', 64),
                Size = 1
            };
        }

        private sealed class FakeUpdateManager : IUpdateManagerAdapter
        {
            public FakeUpdateManager(string? currentVersion)
            {
                CurrentVersion = currentVersion == null ? null : SemanticVersion.Parse(currentVersion);
            }

            public bool IsInstalled { get; init; } = true;
            public bool IsPortable { get; init; }
            public SemanticVersion? CurrentVersion { get; }
            public UpdateInfo? NextUpdate { get; init; }
            public VelopackAsset? PendingAsset { get; init; }
            public bool ThrowOnCheck { get; init; }
            public int CheckCount { get; private set; }
            public int DownloadCount { get; private set; }
            public int ScheduleCount { get; private set; }
            public VelopackAsset? UpdatePendingRestart => PendingAsset;

            public Task<UpdateInfo?> CheckForUpdatesAsync()
            {
                CheckCount++;
                if (ThrowOnCheck)
                    throw new InvalidOperationException("feed unavailable");

                return Task.FromResult(NextUpdate);
            }

            public Task DownloadUpdatesAsync(UpdateInfo info)
            {
                DownloadCount++;
                return Task.CompletedTask;
            }

            public void ApplyUpdatesAndRestart(UpdateInfo info)
            {
            }

            public void WaitExitThenApplyUpdates(VelopackAsset asset, bool silent, bool restart)
            {
                Assert.True(silent);
                Assert.False(restart);
                ScheduleCount++;
            }
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
