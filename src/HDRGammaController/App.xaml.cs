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

        internal static ServiceProvider ConfigureServices()
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
            services.AddSingleton<IToastService, ToastService>();
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
            // One-time rebrand migration: move %LocalAppData%\HDRGammaController (and
            // the roaming profile folder) to Gloam. Must run before Log.Initialize,
            // which would otherwise create the new folder and block the move.
            var migrationMessages = AppPaths.MigrateLegacyData();

            // Enable the file sink first: a WinExe's console output is discarded, so
            // without this no diagnostics from the tray app survive anywhere.
            Log.Initialize();

            try
            {
                Log.Info("App.OnStartup: Starting...");
                foreach (var message in migrationMessages)
                {
                    Log.Info(message);
                }
                base.OnStartup(e);

                // Extract embedded ICM profiles if missing or updated
                int extracted = ResourceExtractor.ExtractIcmProfiles();
                if (extracted > 0)
                {
                    Log.Info($"App.OnStartup: Extracted/updated {extracted} ICM profiles");
                }

                // Re-apply theme when the user flips between Light and Dark in Windows Settings.
                // If Gloam has a saved theme override, that override remains authoritative.
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                BrutalistTheme.Changed += ApplyTrayMenuTheme;
                Exit += (_, _) =>
                {
                    SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                    BrutalistTheme.Changed -= ApplyTrayMenuTheme;
                };

                Log.Info("App.OnStartup: Building service provider...");
                _serviceProvider = ConfigureServices();
                Services = _serviceProvider;

                // Brutalist theme: restore the saved light/dark choice, else follow the OS.
                // The dashboard / calibration-setup toggles persist through this hook.
                var themeSettings = _serviceProvider.GetRequiredService<SettingsManager>();
                BrutalistTheme.Persist = dark => themeSettings.SetDarkTheme(dark);
                BrutalistTheme.Initialize(themeSettings.DarkTheme ?? ThemeDetector.IsDarkMode());
                ApplyTrayMenuTheme();

                var updateService = _serviceProvider.GetRequiredService<UpdateService>();
                StartupManager.EnableByDefaultForFreshInstall(themeSettings, updateService.IsInstalled);

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
                Dispatcher.Invoke(() =>
                {
                    var settings = _serviceProvider?.GetService<SettingsManager>();
                    if (settings?.DarkTheme == null)
                        BrutalistTheme.Initialize(ThemeDetector.IsDarkMode());
                    else
                        ApplyTrayMenuTheme();
                });
            }
        }

        private void ApplyTrayMenuTheme()
        {
            Log.Info($"App.ApplyTrayMenuTheme: Dark mode = {BrutalistTheme.IsDark}");

            Resources["MenuBackground"] = Resources["ThemeBg"];
            Resources["MenuForeground"] = Resources["ThemeText"];
            Resources["MenuBorder"] = Resources["ThemeBorder"];
            Resources["MenuHighlight"] = Resources["ThemeHover"];
        }

        // Once-per-session guard so a pathological handler that keeps throwing doesn't open a
        // fresh dialog each time the previous one closes (a dialog-spam loop).
        private bool _crashDialogShown;

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("CRITICAL RUNTIME ERROR: " + e.Exception);

            // Show a themed dialog the first time so the user actually sees that something
            // broke (the previous behavior swallowed every exception silently). If this one
            // has already shown, fall back to swallowing-and-logging to avoid a loop.
            if (_crashDialogShown)
            {
                e.Handled = true;
                return;
            }

            _crashDialogShown = true;
            string message = e.Exception?.Message ?? "An unexpected error occurred.";
            string details = e.Exception == null ? "" : $"{e.Exception.GetType().FullName}: {e.Exception.Message}\n\n{e.Exception.StackTrace}";

            Window? owner = MainWindow;
            bool? exit = null;
            // ShowDialog must run on the UI thread; we're already there for a Dispatcher
            // unhandled exception. Guard with a try/catch so a dialog failure can't make
            // things worse than the original error.
            try
            {
                exit = CrashDialog.Show(owner, "Something went wrong", message, details);
            }
            catch (Exception dialogEx)
            {
                Log.Error($"App: crash dialog failed to show: {dialogEx}");
            }

            // Exit → let the exception propagate (crash). Continue (or dialog failure) → swallow.
            e.Handled = exit != true;
        }
    }

    /// <summary>
        /// App-wide light/dark swap for the design tokens defined in App.xaml.
    /// Replaces the Theme* brush entries in Application.Resources, which every
    /// {DynamicResource Theme*} consumer re-resolves. Swapping (not mutating) is
    /// required because sealed styles freeze the brushes they reference.
    /// </summary>
    public static class BrutalistTheme
    {
        public static bool IsDark { get; private set; } = true;

        /// <summary>Raised after a swap. Canvas-drawn UI (e.g. the schedule graph)
        /// subscribes to repaint, since it samples token colors once at draw time.</summary>
        public static event Action? Changed;

        private static readonly (string Key, string Dark, string Light)[] Palette =
        {
            ("ThemeBg",       "#0E1116", "#F7F8FA"),
            ("ThemeSurface",  "#171C23", "#FFFFFF"),
            ("ThemeHover",    "#222A34", "#EEF1F4"),
            ("ThemeText",     "#F4F7FA", "#15191F"),
            ("ThemeTextDim",  "#A8B0BC", "#64707D"),
            ("ThemeBorder",   "#465567", "#B9C4D0"),
            ("ThemeAccent",   "#E35F52", "#CF4A40"),
            ("ThemeOnAccent", "#FFFFFF", "#FFFFFF"),
            ("ThemeMeter",    "#82B7F2", "#327CC7"),
            ("ThemeTrack",    "#29313B", "#DFE5EC"),
            ("ThemeAmber",    "#D89A2B", "#B87512"),
            ("ThemeWindowFrame", "#364252", "#B9C4D0"),
        };

        // Legacy resource keys the older windows use, kept pointed at a token brush so
        // those windows theme without per-reference edits. (alias -> token)
        private static readonly (string Alias, string Token)[] Aliases =
        {
            ("AccentBrush",        "ThemeAccent"),
            ("AccentHoverBrush",   "ThemeAccent"),
            ("CardBackground",     "ThemeSurface"),
            ("CardBorder",         "ThemeBorder"),
            ("TextDim",            "ThemeTextDim"),
            ("InputBackground",    "ThemeSurface"),
            ("DropdownBackground", "ThemeSurface"),
            ("DarkAccent",         "ThemeAccent"),
            ("DarkCardBorder",     "ThemeBorder"),
        };

        /// <summary>Set by App to persist the user's manual choice. Initialize does not call it.</summary>
        public static Action<bool>? Persist;

        public static void Initialize(bool dark) { IsDark = dark; Apply(); }
        public static void Toggle() { IsDark = !IsDark; Apply(); Persist?.Invoke(IsDark); }

        /// <summary>Current value of a token as a Color, for code-drawn visuals.</summary>
        public static Color Color(string key)
            => (Application.Current?.Resources[key] as SolidColorBrush)?.Color ?? Colors.Magenta;

        private static void Apply()
        {
            var res = Application.Current?.Resources;
            if (res == null) return;
            foreach (var (key, dark, light) in Palette)
                res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? dark : light));
            // Point each legacy alias at its token's fresh brush instance.
            foreach (var (alias, token) in Aliases)
                res[alias] = res[token];
            Changed?.Invoke();
        }
    }
}
