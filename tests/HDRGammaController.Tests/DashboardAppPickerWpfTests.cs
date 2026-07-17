using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
                }
                finally
                {
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
