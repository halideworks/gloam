using System;
using System.IO;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public sealed class ArgyllPathFinderTests
    {
        [Fact]
        public void IsValidArgyllBinPath_RequiresSpotreadAndDispwin()
        {
            string dir = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(dir, "spotread.exe"), "");

                Assert.False(ArgyllPathFinder.IsValidArgyllBinPath(dir));

                File.WriteAllText(Path.Combine(dir, "dispwin.exe"), "");

                Assert.True(ArgyllPathFinder.IsValidArgyllBinPath(dir));
            }
            finally
            {
                DeleteDirectory(dir);
            }
        }

        [Theory]
        [InlineData(@"..\dispwin.exe")]
        [InlineData(@"bin\dispwin.exe")]
        [InlineData(@"C:\Argyll\bin\dispwin.exe")]
        public void FindArgyllToolPath_RejectsPathArguments(string executableName)
        {
            Assert.Throws<ArgumentException>(() => ArgyllPathFinder.FindArgyllToolPath(executableName));
        }

        private static string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "GloamArgyllPathFinderTests", Guid.NewGuid().ToString("N"));
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
