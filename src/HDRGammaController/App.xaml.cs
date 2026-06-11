using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Hardcodet.Wpf.TaskbarNotification;
using HDRGammaController.Core;
using HDRGammaController.Services;
using HDRGammaController.ViewModels;

namespace HDRGammaController
{
    public partial class App : Application
    {
        private ServiceProvider? _serviceProvider;

        /// <summary>
        /// Composition root. Built in OnStartup before any window exists; all
        /// long-lived services are container-owned singletons.
        /// </summary>
        public static IServiceProvider Services { get; private set; } = null!;

        private static ServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            services.AddSingleton<MonitorManager>();
            services.AddSingleton<SettingsManager>();
            services.AddSingleton<DispwinRunner>(); // Auto-detects
            services.AddSingleton(sp =>
                new NightModeService(sp.GetRequiredService<SettingsManager>().NightMode));
            // Assumes template is in the same directory (needs to be sourced by user)
            services.AddSingleton(_ => new ProfileManager(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "srgb_to_gamma2p2_100_mhc2.icm")));
            services.AddSingleton(sp => new GammaApplyService(
                sp.GetRequiredService<DispwinRunner>(),
                sp.GetRequiredService<SettingsManager>(),
                sp.GetRequiredService<NightModeService>()));
            services.AddSingleton<AppDetectionService>();
            services.AddSingleton<UpdateService>();
            // The live TrayViewModel is created by MainWindow through
            // ActivatorUtilities.CreateInstance, which pulls the singletons above and
            // passes the window-bound HotkeyManager explicitly. Resolving this
            // registration directly would yield an instance without hotkeys
            // (HotkeyManager is an optional ctor parameter).
            services.AddSingleton<TrayViewModel>();

            return services.BuildServiceProvider();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Enable the file sink first: a WinExe's console output is discarded, so
            // without this no diagnostics from the tray app survive anywhere.
            Log.Initialize();

            try
            {
                Log.Info("App.OnStartup: Starting...");
                base.OnStartup(e);

                // Extract embedded ICM profiles if missing or updated
                int extracted = ResourceExtractor.ExtractIcmProfiles();
                if (extracted > 0)
                {
                    Log.Info($"App.OnStartup: Extracted/updated {extracted} ICM profiles");
                }

                // Apply theme based on Windows settings; re-apply when the user flips
                // between Light and Dark in Windows Settings without restarting the app.
                ApplyTheme();
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

                Log.Info("App.OnStartup: Building service provider...");
                _serviceProvider = ConfigureServices();
                Services = _serviceProvider;

                Log.Info("App.OnStartup: Creating MainWindow...");
                var mainWindow = new MainWindow();
                Log.Info("App.OnStartup: MainWindow created.");
            }
            catch (Exception ex)
            {
                // Log goes to LocalAppData — the old startup_log.txt in the working
                // directory was unwritable when installed under Program Files.
                Log.Error("CRITICAL STARTUP ERROR: " + ex);
                Shutdown(-1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Disposes container-owned IDisposable singletons (night mode timer,
            // ramp guard, foreground hook). TrayViewModel.Dispose already stops
            // these when MainWindow closes; their Dispose methods are idempotent.
            _serviceProvider?.Dispose();
            base.OnExit(e);
        }

        private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        {
            // UserPreferenceCategory.General fires on most color/theme changes.
            if (e.Category == UserPreferenceCategory.General ||
                e.Category == UserPreferenceCategory.Color)
            {
                Dispatcher.Invoke(ApplyTheme);
            }
        }

        private void ApplyTheme()
        {
            bool isDark = ThemeDetector.IsDarkMode();
            Log.Info($"App.ApplyTheme: Dark mode = {isDark}");
            
            if (isDark)
            {
                Resources["MenuBackground"] = Resources["DarkMenuBackground"];
                Resources["MenuForeground"] = Resources["DarkMenuForeground"];
                Resources["MenuBorder"] = Resources["DarkMenuBorder"];
                Resources["MenuHighlight"] = Resources["DarkMenuHighlight"];
            }
            else
            {
                Resources["MenuBackground"] = Resources["LightMenuBackground"];
                Resources["MenuForeground"] = Resources["LightMenuForeground"];
                Resources["MenuBorder"] = Resources["LightMenuBorder"];
                Resources["MenuHighlight"] = Resources["LightMenuHighlight"];
            }
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("CRITICAL RUNTIME ERROR: " + e.Exception);
            e.Handled = true; // Prevent crash if possible
        }
    }
}
