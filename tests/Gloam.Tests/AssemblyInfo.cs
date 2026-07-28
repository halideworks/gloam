using System;
using System.IO;
using System.Runtime.CompilerServices;
using Gloam.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestProcessBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        NormalizeWindir();
        RedirectAppDataAwayFromTheRealUser();
    }

    /// <summary>
    /// WPF's native theme loader reads WINDIR directly. Some isolated test runners expose
    /// only SystemRoot, causing otherwise-valid UI tests to fail before a Window is created.
    /// Normalize the standard aliases once for the whole test process.
    /// </summary>
    private static void NormalizeWindir()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDIR")))
        {
            string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(systemRoot))
                Environment.SetEnvironmentVariable("WINDIR", systemRoot);
        }
    }

    /// <summary>
    /// Point every app-owned path at a throwaway directory for the whole test process,
    /// before any test runs.
    ///
    /// SettingsManager resolves its file statically through <see cref="AppPaths.DataDir"/>,
    /// so a plain `new SettingsManager()` followed by any mutation writes
    /// %LOCALAPPDATA%\Gloam\settings.json — the developer's REAL settings, monitor
    /// profiles, calibration associations, and game profiles. Individual tests were
    /// already opting into <see cref="AppPaths.UseDataDirectoriesForCurrentProcess"/> and
    /// restoring it in a finally, but opt-in is the wrong shape for this: one test that
    /// forgets, anywhere in the suite, silently destroys real user data on the machine of
    /// whoever runs `dotnet test`. That happened, and it is a nasty thing to hand a first
    /// contributor.
    ///
    /// Redirecting here inverts the default. Tests that set their own override still work
    /// unchanged — the value they capture and restore is this temp root rather than the
    /// real path, so even a botched restore lands somewhere harmless. Logs, reports, and
    /// downloaded tooling follow DataDir too, so the suite also stops appending to the
    /// user's real app.log.
    ///
    /// The directory is per-process (test runs must not collide) and deliberately not
    /// deleted afterwards: xUnit gives no assembly-teardown hook that reliably runs, and
    /// a temp directory is a much cheaper leak than deleting a path that a
    /// mid-teardown test might still be writing to. The OS reclaims it.
    /// </summary>
    private static void RedirectAppDataAwayFromTheRealUser()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "GloamTests",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);
        AppPaths.UseDataDirectoriesForCurrentProcess(root, Path.Combine(root, "roaming"));
    }
}
