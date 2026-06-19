using System;
using System.Threading;
using HDRGammaController.Core;
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
        public static void Main(string[] args)
        {
            // MUST be the first thing that runs. On normal launches this is a no-op and
            // returns immediately; when Velopack invokes the app with install / update /
            // uninstall hook arguments it handles them and exits the process here, before
            // any window is created.
            try
            {
                VelopackApp.Build().Run();
            }
            catch (Exception ex)
            {
                // The file log may not be initialised yet (App.OnStartup does that), so this
                // is best-effort; a hook failure must not block the app from starting.
                try { Log.Error("VelopackApp.Run failed: " + ex); } catch { }
            }

            // Single-instance guard. Two trays would fight over the GPU gamma ramp and clobber
            // settings.json, so a second instance (manual launch, "start with Windows" race, or
            // Velopack's update relaunch) must bow out. The "Local\" prefix scopes the mutex to
            // the current login session, which is what we want for a per-user tray app.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: "Local\\Gloam-SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                try { Log.Info("Another instance is already running; exiting."); } catch { }
                return;
            }

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
