using System;
using System.IO;
using System.Linq;
using System.Threading;
using HDRGammaController.Core;
using HDRGammaController.Services;
using Microsoft.Extensions.DependencyInjection;
using Velopack;

namespace HDRGammaController
{
    /// <summary>
    /// Process entry point. Velopack requires its hook handling to run before the WPF
    /// application starts, so the entry point is here (wired via &lt;StartupObject&gt; in the
    /// csproj) rather than the Main that App.xaml would otherwise generate.
    /// </summary>
    public static class Program
    {
        // Held for the whole process lifetime so a second instance cannot start. Kept in a
        // static field so the GC does not collect it (and release the mutex) while we run.
        private static Mutex? _singleInstanceMutex;

        [STAThread]
        public static int Main(string[] args)
        {
            // MUST be the first thing that runs. On normal launches this is a no-op and
            // returns immediately; when Velopack invokes the app with install / update /
            // uninstall hook arguments it handles them and exits the process here, before
            // any window is created.
            try
            {
                VelopackApp.Build()
                    .OnAfterInstallFastCallback(_ => ApplyFreshInstallStartupDefault())
                    .OnBeforeUninstallFastCallback(_ => RemoveStartupRegistration())
                    .OnFirstRun(_ => ApplyFreshInstallStartupDefault())
                    .Run();
            }
            catch (Exception ex)
            {
                // The file log may not be initialised yet (App.OnStartup does that), so this
                // is best-effort; a hook failure must not block the app from starting.
                try { Log.Error("VelopackApp.Run failed: " + ex); } catch { }
            }

            if (IsLaunchSmoke(args))
                return RunLaunchSmoke(args);

            // Single-instance guard. Two trays would fight over the GPU gamma ramp and clobber
            // settings.json, so a second instance (manual launch, "start with Windows" race, or
            // Velopack's update relaunch) must bow out. The "Local\" prefix scopes the mutex to
            // the current login session, which is what we want for a per-user tray app.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: "Local\\Gloam-SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                try { Log.Info("Another instance is already running; exiting."); } catch { }
                return 0;
            }

            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }

        private static void ApplyFreshInstallStartupDefault()
        {
            try
            {
                var settings = new SettingsManager();
                StartupManager.EnableByDefaultForFreshInstall(settings, isInstalled: true);
            }
            catch (Exception ex)
            {
                try { Log.Error("Fresh-install startup default failed: " + ex); } catch { }
            }
        }

        private static void RemoveStartupRegistration()
        {
            try
            {
                StartupManager.TrySetStartupEnabled(false);
            }
            catch (Exception ex)
            {
                try { Log.Error("Uninstall startup cleanup failed: " + ex); } catch { }
            }
        }

        private static bool IsLaunchSmoke(string[] args)
            => args.Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase));

        private static int RunLaunchSmoke(string[] args)
        {
            try
            {
                string dataDir = GetRequiredOption(args, "--data-dir");
                AppPaths.UseDataDirectoriesForCurrentProcess(
                    dataDir,
                    Path.Combine(dataDir, "Roaming"));
                Directory.CreateDirectory(AppPaths.DataDir);

                string logPath = Path.Combine(AppPaths.DataDir, "app.log");
                Log.Initialize(logPath);
                Log.Info("Launch smoke: starting.");
                Log.Info($"Launch smoke: data dir = {AppPaths.DataDir}");

                int extracted = ResourceExtractor.ExtractIcmProfiles();
                Log.Info($"Launch smoke: ICM resource extraction checked ({extracted} updated).");

                using var services = App.ConfigureServices();
                _ = services.GetRequiredService<SettingsManager>();
                _ = services.GetRequiredService<NightModeService>();
                _ = services.GetRequiredService<ProfileManager>();

                Log.Info("Launch smoke: service composition completed.");
                Log.Info("Launch smoke: completed.");

                if (!File.Exists(logPath))
                    throw new IOException($"Launch smoke log was not created at '{logPath}'.");

                return 0;
            }
            catch (Exception ex)
            {
                try { Log.Error("Launch smoke failed: " + ex); } catch { }
                return 2;
            }
        }

        private static string GetRequiredOption(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            throw new ArgumentException($"{name} is required for --smoke.");
        }
    }
}
