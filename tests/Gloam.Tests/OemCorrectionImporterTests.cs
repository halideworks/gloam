using System;
using System.IO;
using Gloam.Core.Calibration;
using Xunit;

namespace Gloam.Tests
{
    public class OemCorrectionImporterTests
    {
        [Fact]
        public void FindEdrFiles_SearchesVendorFoldersRecursively()
        {
            string root = Path.Combine(Path.GetTempPath(), "gloam-edr-" + Guid.NewGuid().ToString("N"));
            try
            {
                string xrite = Path.Combine(root, "X-Rite", "Devices", "i1d3", "Calibrations");
                string calibrite = Path.Combine(root, "Calibrite", "PROFILER", "Calibrations");
                string other = Path.Combine(root, "Datacolor");
                Directory.CreateDirectory(xrite);
                Directory.CreateDirectory(calibrite);
                Directory.CreateDirectory(other);
                File.WriteAllText(Path.Combine(xrite, "WLEDFamily.edr"), "");
                File.WriteAllText(Path.Combine(xrite, "readme.txt"), "");
                File.WriteAllText(Path.Combine(calibrite, "OLEDFamily.edr"), "");
                File.WriteAllText(Path.Combine(other, "Spyder.edr"), "");

                var files = OemCorrectionImporter.FindEdrFiles(new[] { root, Path.Combine(root, "missing") });

                Assert.Equal(2, files.Count);
                Assert.Contains(files, f => f.EndsWith("OLEDFamily.edr", StringComparison.Ordinal));
                Assert.Contains(files, f => f.EndsWith("WLEDFamily.edr", StringComparison.Ordinal));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void PendingEdrFiles_SkipsEdrsWhoseCcssIsAlreadyInAnyArgyllDataDir()
        {
            string root = Path.Combine(Path.GetTempPath(), "gloam-pending-" + Guid.NewGuid().ToString("N"));
            try
            {
                string user = Path.Combine(root, "user");
                string system = Path.Combine(root, "system");
                Directory.CreateDirectory(user);
                Directory.CreateDirectory(system);
                File.WriteAllText(Path.Combine(user, "WLEDFamily_07Feb11.ccss"), "");
                File.WriteAllText(Path.Combine(system, "oledfamily_20jul12.ccss"), "");

                var edrs = new[]
                {
                    @"C:\Program Files\X-Rite\Devices\i1d3\Calibrations\WLEDFamily_07Feb11.edr",
                    @"C:\Program Files\X-Rite\Devices\i1d3\Calibrations\OLEDFamily_20Jul12.edr",
                    @"C:\Program Files\X-Rite\Devices\i1d3\Calibrations\RGBLEDFamily_07Feb11.edr",
                };

                var pending = OemCorrectionImporter.PendingEdrFiles(edrs, new[] { user, system, Path.Combine(root, "missing") });

                var only = Assert.Single(pending);
                Assert.EndsWith("RGBLEDFamily_07Feb11.edr", only, StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task EnsureImportedAsync_NothingPending_ReturnsNullWithoutRunning()
        {
            // Only meaningful on a machine without vendor EDR files (CI, and most dev boxes).
            if (OemCorrectionImporter.FindEdrFiles().Count > 0) return;
            var result = await OemCorrectionImporter.EnsureImportedAsync(
                Path.Combine(Path.GetTempPath(), "no-argyll-here"), null, System.Threading.CancellationToken.None);

            Assert.Null(result);
        }

        [Fact]
        public void ParseOutput_CountsWrittenFilesAndExcludesArgyllsOwnCrt()
        {
            // oeminst -v output shape (Argyll 3.5.0). CRT.ccss comes from Argyll's ref
            // folder, not from the EDRs, so it is not counted in the message.
            string output =
                "Loading file 'C:\\Program Files\\X-Rite\\Devices\\i1d3\\Calibrations\\WLEDFamily_07Feb11.edr'..done\n" +
                "Wrote 'C:/Users/u/AppData/Roaming/ArgyllCMS/WLEDFamily_07Feb11.ccss' 5921 bytes\n" +
                "Wrote 'C:/Users/u/AppData/Roaming/ArgyllCMS/OLEDFamily_20Jul12.ccss' 5877 bytes\n" +
                "Wrote 'C:/Users/u/AppData/Roaming/ArgyllCMS/CRT.ccss' 6070 bytes\n";

            var result = OemCorrectionImporter.ParseOutput(output, 0);

            Assert.True(result.Success);
            Assert.Equal(3, result.InstalledFiles.Count);
            string dir = Path.GetDirectoryName("C:/Users/u/AppData/Roaming/ArgyllCMS/CRT.ccss")!;
            Assert.StartsWith($"Imported 2 meter correction(s) into {dir}", result.Message);
        }

        [Fact]
        public void ParseOutput_NonZeroExit_ReportsLastLine()
        {
            var result = OemCorrectionImporter.ParseOutput(
                "Looking for OEM install files ..\noeminst: Error - Didn't locate any files to install - no CD present ?\n", 1);

            Assert.False(result.Success);
            Assert.Empty(result.InstalledFiles);
            Assert.Contains("exit 1", result.Message);
            Assert.Contains("Didn't locate", result.Message);
        }

        [Fact]
        public void ParseOutput_CleanExitWithoutWrites_IsNotSuccess()
        {
            var result = OemCorrectionImporter.ParseOutput("Looking for OEM install files ..\n", 0);

            Assert.False(result.Success);
            Assert.Contains("no installed files", result.Message);
        }

        [Fact]
        public async System.Threading.Tasks.Task ImportAsync_NoFiles_ReturnsNothingFoundWithoutRunning()
        {
            var result = await OemCorrectionImporter.ImportAsync(
                Path.GetTempPath(), Array.Empty<string>(), null, System.Threading.CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(OemCorrectionImporter.NothingFoundMessage, result.Message);
        }

        [Fact]
        public async System.Threading.Tasks.Task ImportAsync_MissingOeminst_ReportsIt()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gloam-nobin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var result = await OemCorrectionImporter.ImportAsync(
                    dir, new[] { "C:\\nowhere\\a.edr" }, null, System.Threading.CancellationToken.None);

                Assert.False(result.Success);
                Assert.Contains("oeminst", result.Message);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
