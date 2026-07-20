using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HDRGammaController.Core;
using HDRGammaController.Services;
using HDRGammaController.ViewModels;
using Velopack;
using Xunit;

namespace HDRGammaController.Tests
{
    public class DashboardViewModelTests
    {
        // "Pause until morning" must target the NEXT 7 AM, not unconditionally
        // tomorrow's: clicked at 1 AM it should pause ~6 hours, not ~30.

        [Fact]
        public void NextMorning_AfterMidnightBeforeSeven_TargetsTodaySevenAm()
        {
            var now = new DateTime(2026, 7, 3, 1, 0, 0);
            Assert.Equal(new DateTime(2026, 7, 3, 7, 0, 0), DashboardViewModel.NextMorning(now));
        }

        [Fact]
        public void NextMorning_InTheEvening_TargetsTomorrowSevenAm()
        {
            var now = new DateTime(2026, 7, 3, 22, 30, 0);
            Assert.Equal(new DateTime(2026, 7, 4, 7, 0, 0), DashboardViewModel.NextMorning(now));
        }

        [Fact]
        public void NextMorning_ExactlySevenAm_TargetsTomorrow()
        {
            // At exactly 7 AM "until morning" means the next one, not a zero-length pause.
            var now = new DateTime(2026, 7, 3, 7, 0, 0);
            Assert.Equal(new DateTime(2026, 7, 4, 7, 0, 0), DashboardViewModel.NextMorning(now));
        }

        [Fact]
        public void NextMorning_JustAfterSevenAm_TargetsTomorrow()
        {
            var now = new DateTime(2026, 7, 3, 7, 0, 1);
            Assert.Equal(new DateTime(2026, 7, 4, 7, 0, 0), DashboardViewModel.NextMorning(now));
        }

        [Fact]
        public void FormatEffectiveTemperatureText_ComposesAdjustmentsInMiredSpace()
        {
            // Base temperature, per-monitor offset and night shift all compose in mired
            // space in the apply path. The dashboard card must report that same result
            // rather than the old linear scale sum.
            double baseScale = -10.0;
            double offsetScale = -10.0;
            double nightShiftScale = -20.0;

            double expectedScale = ColorAdjustments.ComposeTemperatureScaleMired(
                ColorAdjustments.ComposeTemperatureScaleMired(baseScale, offsetScale),
                nightShiftScale);
            string expected = $"{ColorAdjustments.TemperatureScaleToKelvin(expectedScale)}K (Night)";

            string actual = DashboardViewModel.FormatEffectiveTemperatureText(
                baseScale,
                offsetScale,
                nightShiftScale,
                nightModeActive: true);

            Assert.Equal(expected, actual);
            Assert.NotEqual("3700K (Night)", actual);
        }

        [Fact]
        public void SyncDashboardItems_BlendRefresh_PreservesInteractiveExclusionCard()
        {
            var originalMonitorCard = MonitorCard("monitor-a", "5000K (Night)");
            var exclusionCard = new AppExclusionItem { NewAppText = "resolve.exe" };
            var items = new ObservableCollection<object> { originalMonitorCard, exclusionCard };
            bool temperatureChanged = false;
            originalMonitorCard.PropertyChanged += (_, args) =>
                temperatureChanged |= args.PropertyName == nameof(DashboardItem.CurrentTemperatureText);

            DashboardViewModel.SyncDashboardItems(
                items,
                new[] { MonitorCard("monitor-a", "4900K (Night)") },
                exclusionCard);

            Assert.Same(originalMonitorCard, items[0]);
            Assert.Same(exclusionCard, items[1]);
            Assert.True(temperatureChanged);
            Assert.Equal("4900K (Night)", originalMonitorCard.CurrentTemperatureText);
            Assert.Equal("resolve.exe", exclusionCard.NewAppText);
        }

        [Fact]
        public void SyncRunningApps_RetainedSelection_IsNeverRemoved()
        {
            var runningApps = new ObservableCollection<string>
            {
                "codex.exe",
                "resolve.exe"
            };
            var removed = new List<string>();
            bool reset = false;
            runningApps.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Reset)
                    reset = true;
                if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems != null)
                {
                    foreach (string item in args.OldItems)
                        removed.Add(item);
                }
            };

            DashboardViewModel.SyncRunningApps(
                runningApps,
                new[] { "explorer.exe", "resolve.exe" });

            Assert.Equal(new[] { "explorer.exe", "resolve.exe" }, runningApps);
            Assert.False(reset);
            Assert.DoesNotContain("resolve.exe", removed);
        }

        [Fact]
        public void TryAddExcludedApp_NormalizesTypedPath_AndClearsEditor()
        {
            var exclusionCard = new AppExclusionItem
            {
                NewAppText = @" C:\Program Files\Blackmagic Design\DaVinci Resolve\Resolve "
            };

            bool added = DashboardViewModel.TryAddExcludedApp(exclusionCard);

            var rule = Assert.Single(exclusionCard.ExcludedApps);
            Assert.True(added);
            Assert.Equal("Resolve.exe", rule.AppName);
            Assert.Equal(string.Empty, exclusionCard.NewAppText);
        }

        [Fact]
        public void TryAddExcludedApp_Duplicate_IsCaseInsensitive()
        {
            var exclusionCard = new AppExclusionItem { NewAppText = "RESOLVE" };
            exclusionCard.ExcludedApps.Add(new AppExclusionRule { AppName = "resolve.exe" });

            bool added = DashboardViewModel.TryAddExcludedApp(exclusionCard);

            Assert.False(added);
            Assert.Single(exclusionCard.ExcludedApps);
            Assert.Equal(string.Empty, exclusionCard.NewAppText);
        }

        [Fact]
        public void TryAddGamerProfile_UnverifiedTypedNameStartsDisabled()
        {
            var gamerMode = new GamerModeItem
            {
                NewAppText = @" C:\Games\Arena\arena "
            };

            bool added = DashboardViewModel.TryAddGamerProfile(gamerMode);

            GamerProfileRule profile = Assert.Single(gamerMode.Profiles).ToRule();
            Assert.True(added);
            Assert.Equal("arena.exe", profile.AppName);
            Assert.Equal(GamerPictureIntent.CompetitiveClarity, profile.PictureIntent);
            Assert.True(profile.GameplayLock);
            Assert.True(profile.ShadowDetailStrength > 0);
            Assert.False(profile.Enabled);
            Assert.Null(profile.ExecutablePath);
            Assert.Contains("disabled", gamerMode.EditorStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(string.Empty, gamerMode.NewAppText);
        }

        [Fact]
        public void TryAddGamerProfile_RunningChoiceCapturesVerifiedPathAndStaysVisible()
        {
            string root = Path.Combine(Path.GetTempPath(), $"gloam-running-game-{Guid.NewGuid():N}");
            string executable = Path.Combine(root, "Arena.exe");
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(executable, string.Empty);
                var gamerMode = new GamerModeItem { NewAppText = "arena.exe" };
                for (int i = 0; i < 10; i++)
                {
                    gamerMode.Profiles.Add(new GamerProfileEditorItem(new GamerProfileRule
                    {
                        AppName = $"existing-{i}.exe",
                        DisplayName = $"Existing {i}",
                        ExecutablePath = Path.Combine(root, $"existing-{i}.exe")
                    }));
                }
                gamerMode.SetRunningAppPaths(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["arena.exe"] = executable
                });

                bool added = DashboardViewModel.TryAddGamerProfile(gamerMode);

                GamerProfileEditorItem editor = Assert.IsType<GamerProfileEditorItem>(gamerMode.SelectedProfile);
                GamerProfileRule profile = editor.ToRule();
                Assert.True(added);
                Assert.True(profile.Enabled);
                Assert.Equal(Path.GetFullPath(executable), profile.ExecutablePath);
                Assert.Contains(editor, gamerMode.FilteredProfiles);
                Assert.Equal(5, gamerMode.FilteredProfiles.Count);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void TryAddGamerProfile_DuplicateIsCaseInsensitive()
        {
            var gamerMode = new GamerModeItem { NewAppText = "ARENA" };
            gamerMode.Profiles.Add(new GamerProfileEditorItem(
                GamerPresetCatalog.Create("arena.exe", GamerPictureIntent.Reference)));

            bool added = DashboardViewModel.TryAddGamerProfile(gamerMode);

            Assert.False(added);
            Assert.Single(gamerMode.Profiles);
            Assert.Equal(string.Empty, gamerMode.NewAppText);
        }

        [Fact]
        public void SuggestGamerApp_SelectsSavedGameAndRejectsDesktopProcesses()
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), $"gloam-suggest-game-{Guid.NewGuid():N}");
            NightModeService? nightMode = null;
            DashboardViewModel? viewModel = null;
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(
                    Path.Combine(root, "data"), Path.Combine(root, "roaming"));
                var settings = new SettingsManager();
                settings.SetGamerProfiles(new[]
                {
                    new GamerProfileRule
                    {
                        AppName = "arena.exe",
                        ExecutablePath = Path.Combine(root, "Arena.exe"),
                        DisplayName = "Arena"
                    }
                });
                nightMode = new NightModeService(settings.NightMode);
                viewModel = new DashboardViewModel(
                    new MonitorManager(), settings, nightMode,
                    new UpdateService(new DashboardUpdateManager()), (_, _, _, _, _) => { });

                viewModel.SuggestGamerApp("explorer.exe");
                Assert.Equal(string.Empty, viewModel.GamerMode.NewAppText);

                viewModel.SuggestGamerApp("ARENA.EXE");
                Assert.Equal("arena.exe", viewModel.GamerMode.SelectedProfile?.AppName);
                Assert.Equal(string.Empty, viewModel.GamerMode.NewAppText);
            }
            finally
            {
                viewModel?.Dispose();
                nightMode?.Dispose();
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void SyncGamerDisplays_RemovesDisconnectedAndRefreshesFriendlyName()
        {
            var displays = new ObservableCollection<GamerDisplayOption>
            {
                new("display-b", "Old name"),
                new("stale", "Disconnected")
            };

            DashboardViewModel.SyncGamerDisplays(displays, new[]
            {
                new MonitorInfo { MonitorDevicePath = "display-a", FriendlyName = "Alpha" },
                new MonitorInfo { MonitorDevicePath = "DISPLAY-B", FriendlyName = "Bravo" }
            });

            Assert.Equal(new[] { "display-a", "DISPLAY-B" }, displays.Select(d => d.DevicePath));
            Assert.Equal(new[] { "Alpha", "Bravo" }, displays.Select(d => d.Label));
        }

        private static DashboardItem MonitorCard(string devicePath, string temperatureText) => new DashboardItem
        {
            Model = new MonitorInfo
            {
                MonitorDevicePath = devicePath,
                DeviceName = devicePath
            },
            FriendlyName = devicePath,
            CurrentTemperatureText = temperatureText
        };

        private sealed class DashboardUpdateManager : IUpdateManagerAdapter
        {
            public bool IsInstalled => false;
            public bool IsPortable => true;
            public SemanticVersion? CurrentVersion => null;
            public VelopackAsset? UpdatePendingRestart => null;
            public Task<UpdateInfo?> CheckForUpdatesAsync() => Task.FromResult<UpdateInfo?>(null);
            public Task DownloadUpdatesAsync(UpdateInfo info) => Task.CompletedTask;
            public void ApplyUpdatesAndRestart(UpdateInfo info) { }
            public void WaitExitThenApplyUpdates(VelopackAsset asset, bool silent, bool restart) { }
            public string? GetLocalPackagePath(VelopackAsset asset) => null;
        }
    }
}
