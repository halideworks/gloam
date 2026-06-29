using System;
using System.IO;
using HDRGammaController.Core;
using Xunit;

namespace HDRGammaController.Tests
{
    public sealed class ServiceDisposalTests
    {
        [Fact]
        public void TimerBackedServices_AreIdempotentForTrayAndContainerShutdown()
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string tempDir = CreateTempDirectory();

            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(tempDir, Path.Combine(tempDir, "roaming"));

                var nightMode = new NightModeService(new NightModeSettings());
                var apply = new GammaApplyService(new DispwinRunner(), new SettingsManager(), nightMode);

                apply.Dispose();
                nightMode.Dispose();

                // App.OnExit disposes the DI container after TrayViewModel.Dispose has already
                // stopped these singletons. This must be a no-op, not an ObjectDisposedException.
                apply.Dispose();
                nightMode.Dispose();
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                DeleteDirectory(tempDir);
            }
        }

        private static string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "GloamServiceDisposalTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for test temp files.
            }
        }
    }
}
