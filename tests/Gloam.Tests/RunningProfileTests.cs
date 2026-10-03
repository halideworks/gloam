using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Gloam.Core;
using Gloam.Interop;
using Gloam.ViewModels;
using Xunit;

namespace Gloam.Tests
{
    public sealed class RunningProfileTests
    {
        [Fact]
        public void ApplyWhileRunning_DefaultsOffAndSurvivesEditorCloneAndPersistence()
        {
            var original = JsonSerializer.Deserialize<GamerProfileRule>("{\"AppName\":\"mpv.exe\"}")!;
            Assert.False(original.ApplyWhileRunning);
            var editor = new GamerProfileEditorItem(original);
            string? changedProperty = null;
            editor.PropertyChanged += (_, args) => changedProperty = args.PropertyName;
            editor.ApplyWhileRunning = true;
            Assert.Equal(nameof(editor.ApplyWhileRunning), changedProperty);
            GamerProfileRule edited = editor.ToRule();
            Assert.True(edited.Clone().ApplyWhileRunning);
            Assert.False(original.SemanticallyEquals(edited));
            Assert.True(edited.SemanticallyEquals(edited.Clone()));
            GamerProfileRule recent = edited.Clone();
            recent.LastUsedUtc = DateTime.UtcNow;
            Assert.False(edited.SemanticallyEquals(recent));
            Assert.True(edited.SemanticallyEquals(recent, includeRecency: false));
            recent.GameplayLock = !edited.GameplayLock;
            Assert.False(edited.SemanticallyEquals(recent, includeRecency: false));

            WithSettings(settings =>
            {
                edited.ExecutablePath = @"C:\Players\mpv.exe";
                Assert.True(settings.TrySetGamerProfiles(new[] { edited }));
                Assert.True(Assert.Single(new SettingsManager().GamerProfiles).ApplyWhileRunning);
            });
        }

        [Theory]
        [InlineData(GamerDisplayScope.SpecificDisplay, false, 1)]
        [InlineData(GamerDisplayScope.AllDisplays, false, 2)]
        [InlineData(GamerDisplayScope.WindowDisplays, true, 1)]
        [InlineData(GamerDisplayScope.WindowDisplays, false, 0)]
        public void BackgroundProfile_StartsWithoutFocusAndRestoresAfterExit(
            GamerDisplayScope scope, bool hasWindow, int expectedDisplays)
        {
            WithSettings(settings =>
            {
                var monitors = Monitors();
                GamerProfileRule profile = Profile("mpv.exe");
                profile.DisplayScope = scope;
                Assert.True(settings.TrySetGamerProfiles(new[] { profile }));
                Assert.True(settings.TrySetGamerModeEnabled(true));
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(new DispwinRunner(), settings, night,
                    (_, _) => GamerCalibrationStatus.None);
                IReadOnlyList<RunningAppObservation> running = Array.Empty<RunningAppObservation>();
                using var coordinator = new GamerModeCoordinator(settings, apply, () => monitors, () => running);
                coordinator.ObserveForeground("editor.exe", @"C:\Tools\editor.exe", monitors[0].MonitorBounds);
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Empty(coordinator.ActiveSessions);

                running = new[] { new RunningAppObservation(profile.AppName, profile.ExecutablePath,
                    hasWindow ? monitors[1].MonitorBounds : null) };
                coordinator.RefreshRunningApps();
                Assert.True(coordinator.WaitForIdle(TimeSpan.FromSeconds(5)));
                Assert.Equal(expectedDisplays, coordinator.ActiveSessions.Count);
                if (expectedDisplays == 1)
                    Assert.Equal("display-2", Assert.Single(coordinator.ActiveSessions).MonitorDevicePath);
                DateTime? started = coordinator.ActiveSessions.FirstOrDefault()?.StartedUtc;
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Equal(started, coordinator.ActiveSessions.FirstOrDefault()?.StartedUtc);

                running = Array.Empty<RunningAppObservation>();
                coordinator.RefreshRunningApps();
                Assert.True(coordinator.WaitForIdle(TimeSpan.FromSeconds(5)));
                Assert.Empty(coordinator.ActiveSessions);
            });
        }

        [Fact]
        public void BackgroundProfile_RespectsPathPauseDisablePanicAndForegroundPriority()
        {
            WithSettings(settings =>
            {
                var monitors = Monitors();
                var background = Profile("mpv.exe");
                var focused = Profile("arena.exe");
                focused.ApplyWhileRunning = false;
                focused.MonitorDevicePath = "display-1";
                Assert.True(settings.TrySetGamerProfiles(new[] { background, focused }));
                Assert.True(settings.TrySetGamerModeEnabled(true));
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(new DispwinRunner(), settings, night,
                    (_, _) => GamerCalibrationStatus.None);
                IReadOnlyList<RunningAppObservation> running = new[]
                {
                    new RunningAppObservation("mpv.exe", @"D:\Other\mpv.exe", null),
                    new RunningAppObservation("arena.exe", focused.ExecutablePath, null)
                };
                using var coordinator = new GamerModeCoordinator(settings, apply, () => monitors, () => running);
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Empty(coordinator.ActiveSessions);
                running = new[] { new RunningAppObservation("MPV.EXE", background.ExecutablePath, null) };
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Equal("mpv.exe", Assert.Single(coordinator.ActiveSessions).AppName);

                coordinator.ObserveForeground(focused.AppName, focused.ExecutablePath, monitors[0].MonitorBounds);
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Equal("arena.exe", Assert.Single(coordinator.ActiveSessions).AppName);
                coordinator.ObserveForeground("editor.exe", null, monitors[0].MonitorBounds);
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Equal("mpv.exe", Assert.Single(coordinator.ActiveSessions).AppName);

                Assert.True(coordinator.TrySetEnabled(false));
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Empty(coordinator.ActiveSessions);
                Assert.True(coordinator.TrySetEnabled(true));
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Single(coordinator.ActiveSessions);
                background.Enabled = false;
                Assert.True(settings.TrySetGamerProfiles(new[] { background, focused }));
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Empty(coordinator.ActiveSessions);
                background.Enabled = true;
                Assert.True(settings.TrySetGamerProfiles(new[] { background, focused }));
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Single(coordinator.ActiveSessions);
                coordinator.EmergencySuspend();
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Empty(coordinator.ActiveSessions);
            });
        }

        [Fact]
        public void ConcurrentPollingAndFocus_KeepTheLatestForegroundAndPauseClearsIt()
        {
            WithSettings(settings =>
            {
                var first = Profile("first.exe");
                var second = Profile("second.exe");
                settings.SetGamerProfiles(new[] { first, second });
                settings.SetGamerModeEnabled(true);
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(new DispwinRunner(), settings, night,
                    (_, _) => GamerCalibrationStatus.None);
                using var coordinator = new GamerModeCoordinator(settings, apply, Monitors, () => new[]
                {
                    new RunningAppObservation(first.AppName, first.ExecutablePath, null),
                    new RunningAppObservation(second.AppName, second.ExecutablePath, null)
                });
                for (int round = 0; round < 12; round++)
                {
                    Parallel.For(0, 64, i =>
                    {
                        if (i % 2 == 0) coordinator.RefreshRunningApps();
                        else
                        {
                            GamerProfileRule profile = i % 4 == 1 ? first : second;
                            coordinator.ObserveForeground(profile.AppName, profile.ExecutablePath, null);
                        }
                    });
                    Assert.True(coordinator.WaitForIdle(TimeSpan.FromSeconds(5)));
                    Assert.Equal(coordinator.LastExternalForegroundApp,
                        Assert.Single(coordinator.ActiveSessions).AppName);
                }
                Parallel.Invoke(() => coordinator.TrySetEnabled(false),
                    () => coordinator.ObserveForeground(first.AppName, first.ExecutablePath, null));
                Assert.Empty(coordinator.ActiveSessions);
                Assert.True(coordinator.WaitForIdle(TimeSpan.FromSeconds(5)));
                Assert.Empty(coordinator.ActiveSessions);
            });
        }

        [Fact]
        public void FocusedRunningProfile_ExitsWhileGloamHasFocus()
        {
            WithSettings(settings =>
            {
                var profile = Profile("mpv.exe");
                settings.SetGamerProfiles(new[] { profile });
                settings.SetGamerModeEnabled(true);
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(new DispwinRunner(), settings, night,
                    (_, _) => GamerCalibrationStatus.None);
                IReadOnlyList<RunningAppObservation> running = new[]
                {
                    new RunningAppObservation(profile.AppName, profile.ExecutablePath, null)
                };
                using var coordinator = new GamerModeCoordinator(settings, apply, Monitors, () => running);
                coordinator.ObserveForeground(profile.AppName, profile.ExecutablePath, Monitors()[1].MonitorBounds);
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Single(coordinator.ActiveSessions);
                coordinator.ObserveForeground("Gloam.exe", null, null);
                Assert.Equal("mpv.exe", coordinator.LastExternalForegroundApp);
                running = Array.Empty<RunningAppObservation>();
                coordinator.RefreshRunningApps();
                Assert.True(coordinator.WaitForIdle(TimeSpan.FromSeconds(5)));
                Assert.Empty(coordinator.ActiveSessions);
            });
        }

        [Fact]
        public void Polling_DetectsBackgroundLaunchWithoutForegroundEventAndStopsOnDispose()
        {
            WithSettings(settings =>
            {
                var profile = Profile("mpv.exe");
                settings.SetGamerProfiles(new[] { profile });
                settings.SetGamerModeEnabled(true);
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(new DispwinRunner(), settings, night,
                    (_, _) => GamerCalibrationStatus.None);
                using var activated = new ManualResetEventSlim();
                int snapshots = 0;
                using var coordinator = new GamerModeCoordinator(settings, apply, Monitors, () =>
                {
                    Interlocked.Increment(ref snapshots);
                    return new[] { new RunningAppObservation(profile.AppName, profile.ExecutablePath, null) };
                });
                coordinator.PolicyChanged += _ => activated.Set();
                Assert.True(activated.Wait(TimeSpan.FromSeconds(6)), "Background polling did not activate the profile.");
                Assert.Equal("mpv.exe", Assert.Single(coordinator.ActiveSessions).AppName);
                coordinator.Dispose();
                int disposedSnapshots = snapshots;
                coordinator.RefreshRunningApps();
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Equal(disposedSnapshots, snapshots);
            });
        }

        [Fact]
        public void NativeProcessSnapshot_ResolvesExecutableAndSkipsDisabledProfiles()
        {
            using Process process = Process.GetCurrentProcess();
            var profile = Profile(process.ProcessName + ".exe");
            profile.ExecutablePath = process.MainModule!.FileName;
            Assert.Contains(GamerModeCoordinator.CaptureRunningApps(new[] { profile }), app =>
                GamerModeCoordinator.MatchesForeground(profile, app.AppName, app.ExecutablePath));
            profile.Enabled = false;
            Assert.Empty(GamerModeCoordinator.CaptureRunningApps(new[] { profile }));
            profile.Enabled = true;
            profile.ApplyWhileRunning = false;
            Assert.Empty(GamerModeCoordinator.CaptureRunningApps(new[] { profile }));
        }

        private static GamerProfileRule Profile(string name) => new()
        {
            AppName = name,
            ExecutablePath = @"C:\Apps\" + name,
            ApplyWhileRunning = true,
            DisplayScope = GamerDisplayScope.SpecificDisplay,
            MonitorDevicePath = "display-2"
        };

        private static MonitorInfo[] Monitors() => new[]
        {
            new MonitorInfo { HMonitor = (IntPtr)101, MonitorDevicePath = "display-1",
                MonitorBounds = new Dxgi.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 } },
            new MonitorInfo { HMonitor = (IntPtr)102, MonitorDevicePath = "display-2",
                MonitorBounds = new Dxgi.RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 } }
        };

        private static void WithSettings(Action<SettingsManager> body)
        {
            string oldData = AppPaths.DataDir;
            string oldRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), "GloamRunningProfileTests", Guid.NewGuid().ToString("N"));
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(root, Path.Combine(root, "roaming"));
                body(new SettingsManager());
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(oldData, oldRoaming);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
