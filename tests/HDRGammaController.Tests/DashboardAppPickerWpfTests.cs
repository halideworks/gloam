using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HDRGammaController.Core;
using HDRGammaController.Services;
using HDRGammaController.ViewModels;
using Velopack;
using Xunit;

namespace HDRGammaController.Tests
{
    public sealed class DashboardAppPickerWpfTests
    {
        [Fact]
        public void Picker_StaysOpenAcrossLiveRefresh_AndAddButtonPersistsTypedApp()
        {
            RunSta(() =>
            {
                string originalData = AppPaths.DataDir;
                string originalRoaming = AppPaths.RoamingDataDir;
                string root = Path.Combine(Path.GetTempPath(), $"gloam-picker-wpf-{Guid.NewGuid():N}");
                Application? app = null;
                DashboardWindow? window = null;
                GameLabWindow? gameLabWindow = null;
                NightModeService? nightMode = null;
                try
                {
                    AppPaths.UseDataDirectoriesForCurrentProcess(
                        Path.Combine(root, "data"), Path.Combine(root, "roaming"));
                    app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/Gloam;component/Themes/Tokens.xaml")
                    });

                    var settings = new SettingsManager();
                    nightMode = new NightModeService(settings.NightMode);
                    var update = new UpdateService(new PickerUpdateManager());
                    window = new DashboardWindow(
                        new MonitorManager(), settings, nightMode, update,
                        (_, _, _, _, _) => { })
                    {
                        ShowInTaskbar = false,
                        Left = -10000,
                        Top = -10000,
                        Opacity = 0
                    };
                    window.Show();
                    Pump(TimeSpan.FromMilliseconds(100));

                    var viewModel = Assert.IsType<DashboardViewModel>(window.DataContext);
                    var exclusionItem = Assert.Single(viewModel.Items.OfType<AppExclusionItem>());
                    exclusionItem.RunningApps.Add("photoshop.exe");

                    var items = Assert.IsType<ItemsControl>(window.FindName("DashboardItems"));
                    items.UpdateLayout();
                    var presenter = Assert.IsType<ContentPresenter>(
                        items.ItemContainerGenerator.ContainerFromItem(exclusionItem));
                    presenter.ApplyTemplate();
                    var picker = Assert.IsType<ComboBox>(
                        FindVisualChild<ComboBox>(presenter, "ExcludedAppPicker"));
                    var add = Assert.IsType<Button>(
                        FindVisualChild<Button>(presenter, "AddExcludedAppButton"));

                    picker.IsDropDownOpen = true;
                    viewModel.Refresh(reEnumerate: false); // same path as a night-mode blend tick
                    Pump(TimeSpan.FromMilliseconds(1100));

                    Assert.True(picker.IsDropDownOpen);
                    Assert.Same(presenter,
                        items.ItemContainerGenerator.ContainerFromItem(exclusionItem));

                    picker.Text = @"C:\Program Files\Adobe\Adobe Photoshop\Photoshop";
                    picker.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
                    Pump(TimeSpan.FromMilliseconds(50));
                    Assert.EndsWith("Photoshop", exclusionItem.NewAppText, StringComparison.OrdinalIgnoreCase);
                    Assert.NotNull(add.Command);
                    Assert.True(add.Command.CanExecute(add.CommandParameter));
                    add.Command.Execute(add.CommandParameter);
                    Pump(TimeSpan.FromMilliseconds(50));

                    Assert.Equal("Photoshop.exe", Assert.Single(exclusionItem.ExcludedApps).AppName);
                    Assert.Equal("Photoshop.exe", Assert.Single(settings.ExcludedApps).AppName);

                    var gamerPicker = Assert.IsType<ComboBox>(window.FindName("GamerAppPicker"));
                    var gamerAdd = Assert.IsType<Button>(window.FindName("AddGamerProfileButton"));
                    gamerPicker.Text = @"C:\Games\Arena\arena";
                    gamerPicker.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
                    Assert.NotNull(gamerAdd.Command);
                    gamerAdd.Command.Execute(gamerAdd.CommandParameter);
                    Pump(TimeSpan.FromMilliseconds(50));

                    GamerProfileEditorItem editor = Assert.Single(viewModel.GamerMode.Profiles);
                    GamerProfileRule stored = Assert.Single(settings.GamerProfiles);
                    Assert.Equal("arena.exe", stored.AppName);
                    Assert.Equal(GamerPictureIntent.CompetitiveClarity, stored.PictureIntent);

                    editor.PictureIntent = GamerPictureIntent.NightOps;
                    Pump(TimeSpan.FromMilliseconds(350));
                    stored = Assert.Single(settings.GamerProfiles);
                    Assert.Equal(GamerPictureIntent.NightOps, stored.PictureIntent);
                    Assert.Equal(GamerNightPolicy.NightOps, stored.NightPolicy);
                    Assert.True(stored.ShadowDetailStrength > 0);
                    Assert.Equal(0.0, stored.NightOpsMelanopicCeiling);
                    editor.SetExecutablePath(Path.Combine(root, "games", "arena.exe"));
                    editor.Enabled = true;
                    Pump(TimeSpan.FromMilliseconds(350));

                    Assert.Equal("Night Play", editor.AvailablePictureIntents[3].ToString());

                    var largeLibrary = settings.GamerProfiles;
                    largeLibrary[0].HdrCapability = GamerHdrCapability.Detected;
                    for (int i = 0; i < 120; i++)
                    {
                        largeLibrary.Add(new GamerProfileRule
                        {
                            AppName = $"game-{i:000}.exe",
                            ExecutablePath = Path.Combine(root, "games", $"game-{i:000}.exe"),
                            DisplayName = $"Game {i:000}",
                            HdrCapability = i == 0
                                ? GamerHdrCapability.UserConfirmed
                                : GamerHdrCapability.Unknown,
                            LastUsedUtc = i < 8 ? DateTime.UtcNow.AddMinutes(-i) : null
                        });
                    }
                    settings.SetGamerProfiles(largeLibrary);
                    Pump(TimeSpan.FromMilliseconds(260));

                    var dashboardProfileList = Assert.IsType<ListBox>(window.FindName("DashboardGameProfileList"));
                    Assert.Equal(5, dashboardProfileList.Items.Count);
                    Assert.True(double.IsNaN(dashboardProfileList.Height));
                    Assert.Equal(260, dashboardProfileList.MaxHeight);
                    ScrollViewer dashboardProfileScroll = Assert.Single(
                        FindVisualChildren<ScrollViewer>(dashboardProfileList));
                    Assert.Equal(Visibility.Collapsed,
                        dashboardProfileScroll.ComputedVerticalScrollBarVisibility);
                    Assert.Equal(Visibility.Collapsed,
                        dashboardProfileScroll.ComputedHorizontalScrollBarVisibility);
                    Rect recentCountBounds = BoundsIn(
                        Assert.IsType<TextBlock>(window.FindName("DashboardProfileCount")), window);
                    Rect viewAllBounds = BoundsIn(
                        Assert.IsType<Button>(window.FindName("DashboardShowAllProfilesButton")), window);
                    Rect libraryPaneBounds = BoundsIn(
                        Assert.IsType<Border>(window.FindName("DashboardGameLibraryPane")), window);
                    Assert.True(recentCountBounds.Right + 4 <= viewAllBounds.Left);
                    Assert.True(viewAllBounds.Right <= libraryPaneBounds.Right - 9);
                    Assert.True(viewAllBounds.Width >= 48);
                    var gamerExpander = Assert.IsType<Expander>(window.FindName("GamerModeExpander"));
                    gamerExpander.BringIntoView();
                    Pump(TimeSpan.FromMilliseconds(120));
                    SaveScreenshotIfRequested(window, "dashboard-game-library.png");
                    dashboardProfileList.BringIntoView();
                    Pump(TimeSpan.FromMilliseconds(120));
                    SaveScreenshotIfRequested(window, "dashboard-game-library-detail.png");

                    gameLabWindow = new GameLabWindow(
                        new MonitorManager(), settings, nightMode, update,
                        (_, _, _, _, _) => { },
                        suggestedGameApp: "teardown.exe")
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000,
                        Top = -10000,
                        ShowActivated = false
                    };
                    gameLabWindow.Show();
                    Pump(TimeSpan.FromMilliseconds(100));

                    Assert.Equal("Gloam - Game Lab", gameLabWindow.Title);
                    Assert.False(gameLabWindow.Topmost);
                    Assert.True(gameLabWindow.ShowInTaskbar);
                    Assert.InRange(gameLabWindow.ActualHeight, 640, 660);
                    var viewport = Assert.IsType<Grid>(gameLabWindow.FindName("GameLabViewport"));
                    var workspace = Assert.IsType<Grid>(gameLabWindow.FindName("LibraryWorkspace"));
                    Assert.Equal(Visibility.Visible, workspace.Visibility);
                    Assert.True(viewport.ActualHeight <= gameLabWindow.ActualHeight);
                    var profileList = Assert.IsType<ListBox>(gameLabWindow.FindName("GameProfileList"));
                    Assert.Equal(5, profileList.Items.Count);
                    ScrollViewer profileScroll = Assert.Single(
                        FindVisualChildren<ScrollViewer>(profileList));
                    Assert.Equal(Visibility.Collapsed, profileScroll.ComputedVerticalScrollBarVisibility);
                    Assert.Equal(Visibility.Collapsed, profileScroll.ComputedHorizontalScrollBarVisibility);
                    Assert.All(FindVisualChildren<ScrollViewer>(viewport), scroll =>
                        Assert.True(
                            HasVisualAncestor<ItemsControl>(scroll) ||
                            HasVisualAncestor<TextBox>(scroll),
                            "Game Lab must not have a window-level ScrollViewer."));
                    var gameLabViewModel = Assert.IsType<DashboardViewModel>(gameLabWindow.DataContext);
                    Assert.Equal("teardown.exe", gameLabViewModel.GamerMode.NewAppText);

                    GamerLibraryPresetOption hdrDefault = Assert.Single(
                        gameLabViewModel.GamerMode.AvailableLibraryPresets,
                        option => option.HdrCapableOnly);
                    gameLabViewModel.GamerMode.SelectedLibraryPreset = hdrDefault;
                    gameLabViewModel.ApplyGamerLibraryPresetCommand.Execute(null);
                    Pump(TimeSpan.FromMilliseconds(100));
                    Assert.Equal(2, gameLabViewModel.GamerMode.Profiles.Count(profile =>
                        profile.PictureIntent == GamerPictureIntent.CinematicHdr));
                    Assert.All(gameLabViewModel.GamerMode.Profiles.Where(profile => !profile.IsHdrCapable),
                        profile => Assert.NotEqual(GamerPictureIntent.CinematicHdr, profile.PictureIntent));

                    Assert.NotNull(gameLabViewModel.GamerMode.SelectedProfile);
                    gameLabViewModel.GamerMode.SelectedProfile!.PictureIntent =
                        GamerPictureIntent.CompetitiveClarity;
                    Pump(TimeSpan.FromMilliseconds(260));
                    SaveScreenshotIfRequested(gameLabWindow, "game-lab-compact.png");

                    gameLabViewModel.GamerMode.SelectedProfile.PictureIntent = GamerPictureIntent.NightOps;
                    Expander advanced = Assert.Single(
                        FindVisualChildren<Expander>(viewport),
                        expander => Equals(expander.Header, "Advanced signal & display controls"));
                    advanced.IsExpanded = true;
                    Pump(TimeSpan.FromMilliseconds(260));
                    Assert.True(advanced.ActualHeight < workspace.ActualHeight);

                    Rect profileBounds = BoundsIn(
                        Assert.IsType<TextBox>(FindVisualChild<TextBox>(viewport, "AdvancedProfileNameBox")),
                        workspace);
                    Rect hdrSupportBounds = BoundsIn(
                        Assert.IsType<ComboBox>(FindVisualChild<ComboBox>(viewport, "AdvancedHdrSupportBox")),
                        workspace);
                    Rect expectedSignalBounds = BoundsIn(
                        Assert.IsType<ComboBox>(FindVisualChild<ComboBox>(viewport, "AdvancedExpectedSignalBox")),
                        workspace);
                    Rect targetDisplaysBounds = BoundsIn(
                        Assert.IsType<ComboBox>(FindVisualChild<ComboBox>(viewport, "AdvancedTargetDisplaysBox")),
                        workspace);
                    Rect nightBehaviorBounds = BoundsIn(
                        Assert.IsType<ComboBox>(FindVisualChild<ComboBox>(viewport, "AdvancedNightBehaviorBox")),
                        workspace);
                    Rect gammaBounds = BoundsIn(
                        Assert.IsType<ComboBox>(FindVisualChild<ComboBox>(viewport, "AdvancedGammaModeBox")),
                        workspace);
                    Rect paperWhiteBounds = BoundsIn(
                        Assert.IsType<TextBox>(FindVisualChild<TextBox>(viewport, "AdvancedPaperWhiteBox")),
                        workspace);
                    Rect peakNitsBounds = BoundsIn(
                        Assert.IsType<TextBox>(FindVisualChild<TextBox>(viewport, "AdvancedPeakNitsBox")),
                        workspace);
                    Rect melanopicBounds = BoundsIn(
                        Assert.IsType<TextBox>(FindVisualChild<TextBox>(viewport, "AdvancedMelanopicCeilingBox")),
                        workspace);

                    AssertAligned(profileBounds.Top, hdrSupportBounds.Top, expectedSignalBounds.Top, targetDisplaysBounds.Top);
                    AssertAligned(nightBehaviorBounds.Top, gammaBounds.Top, paperWhiteBounds.Top, peakNitsBounds.Top);
                    AssertAligned(profileBounds.Left, nightBehaviorBounds.Left);
                    AssertAligned(profileBounds.Right, nightBehaviorBounds.Right);
                    AssertAligned(hdrSupportBounds.Left, gammaBounds.Left);
                    AssertAligned(hdrSupportBounds.Right, gammaBounds.Right);
                    AssertAligned(expectedSignalBounds.Left, paperWhiteBounds.Left);
                    AssertAligned(expectedSignalBounds.Right, paperWhiteBounds.Right);
                    AssertAligned(targetDisplaysBounds.Left, peakNitsBounds.Left, melanopicBounds.Left);
                    AssertAligned(targetDisplaysBounds.Right, peakNitsBounds.Right, melanopicBounds.Right);
                    Assert.True(paperWhiteBounds.Right < peakNitsBounds.Left);
                    SaveScreenshotIfRequested(gameLabWindow, "game-lab-advanced.png");
                    advanced.IsExpanded = false;

                    var discovered = Enumerable.Range(0, 80)
                        .Select(i => new DiscoveredGame(
                            $"Found Game {i:000}",
                            $"found-{i:000}.exe",
                            Path.Combine(root, "found", $"found-{i:000}.exe"),
                            i % 2 == 0 ? "Steam" : "Epic"))
                        .ToArray();
                    gameLabViewModel.GamerMode.SetDiscoveryResults(
                        discovered, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    foreach (DiscoveredGameItem item in gameLabViewModel.GamerMode.DiscoveredGames.Take(3))
                        item.IsSelected = true;
                    Pump(TimeSpan.FromMilliseconds(80));
                    Assert.Equal(Visibility.Visible,
                        Assert.IsType<Border>(gameLabWindow.FindName("GameScanPanel")).Visibility);
                    Assert.Equal(80,
                        Assert.IsType<ListBox>(gameLabWindow.FindName("DiscoveredGamesList")).Items.Count);
                    SaveScreenshotIfRequested(gameLabWindow, "game-lab-scan-review.png");
                }
                finally
                {
                    try { gameLabWindow?.Close(); } catch { }
                    try { window?.Close(); } catch { }
                    nightMode?.Dispose();
                    try { app?.Shutdown(); } catch { }
                    AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                    try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
                }
            });
        }

        private static void Pump(TimeSpan duration)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = duration
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static T? FindVisualChild<T>(DependencyObject root, string name)
            where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match && match.Name == name) return match;
                var nested = FindVisualChild<T>(child, name);
                if (nested != null) return nested;
            }
            return null;
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
            where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (T nested in FindVisualChildren<T>(child))
                    yield return nested;
            }
        }

        private static bool HasVisualAncestor<T>(DependencyObject child)
            where T : DependencyObject
        {
            DependencyObject? current = VisualTreeHelper.GetParent(child);
            while (current != null)
            {
                if (current is T) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static Rect BoundsIn(FrameworkElement element, Visual ancestor)
        {
            Point origin = element.TransformToAncestor(ancestor).Transform(new Point(0, 0));
            return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
        }

        private static void AssertAligned(params double[] coordinates)
        {
            Assert.NotEmpty(coordinates);
            double expected = coordinates[0];
            foreach (double coordinate in coordinates.Skip(1))
                Assert.InRange(coordinate, expected - 0.75, expected + 0.75);
        }

        private static void SaveScreenshotIfRequested(Window window, string fileName)
        {
            string? outputDirectory = Environment.GetEnvironmentVariable("GLOAM_UI_SCREENSHOT_DIR");
            if (string.IsNullOrWhiteSpace(outputDirectory)) return;

            Directory.CreateDirectory(outputDirectory);
            double originalOpacity = window.Opacity;
            window.Opacity = 1;
            window.UpdateLayout();
            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
            int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(
                width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream output = File.Create(Path.Combine(outputDirectory, fileName));
            encoder.Save(output);
            window.Opacity = originalOpacity;
        }

        private static void RunSta(Action body)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            failure?.Throw();
        }

        private sealed class PickerUpdateManager : IUpdateManagerAdapter
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
