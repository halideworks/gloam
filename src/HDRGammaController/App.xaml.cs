using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Hardcodet.Wpf.TaskbarNotification;
using HDRGammaController.Core;
using HDRGammaController.Services;

namespace HDRGammaController
{
    public partial class App : Application
    {
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
