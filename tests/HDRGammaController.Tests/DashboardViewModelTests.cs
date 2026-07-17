using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using HDRGammaController.Core;
using HDRGammaController.ViewModels;
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
    }
}
