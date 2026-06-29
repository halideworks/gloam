using System;
using System.IO;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public sealed class LogFileRotatorTests
    {
        [Fact]
        public void RotateIfNeeded_ShiftsArchivesAndDeletesOldest()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "app.log");
                File.WriteAllText(path, "current");
                File.WriteAllText(path + ".1", "archive-1");
                File.WriteAllText(path + ".2", "archive-2");

                LogFileRotator.RotateIfNeeded(path, maxBytes: 1, maxArchives: 2);

                Assert.False(File.Exists(path));
                Assert.Equal("current", File.ReadAllText(path + ".1"));
                Assert.Equal("archive-1", File.ReadAllText(path + ".2"));
                Assert.DoesNotContain("archive-2", File.ReadAllText(path + ".2"));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Fact]
        public void RotateIfNeeded_BelowLimit_DoesNothing()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "app.log");
                File.WriteAllText(path, "short");

                LogFileRotator.RotateIfNeeded(path, maxBytes: 100, maxArchives: 2);

                Assert.True(File.Exists(path));
                Assert.False(File.Exists(path + ".1"));
                Assert.Equal("short", File.ReadAllText(path));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Fact]
        public void ColorimeterLogPath_FollowsCurrentAppDataDirectory()
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string dir = CreateTempDir();
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(dir, Path.Combine(dir, "roaming"));

                Assert.Equal(
                    Path.Combine(dir, "colorimeter.log"),
                    ColorimeterService.GetLogFilePath());
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                DeleteTempDir(dir);
            }
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "GloamLogTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTempDir(string dir)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
