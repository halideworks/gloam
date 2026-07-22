using System;
using System.IO;
using System.Threading;
using HDRGammaController.Core;
using Xunit;

namespace HDRGammaController.Tests
{
    public sealed class GameDiscoveryTests
    {
        [Fact]
        public void SteamScanReadsManifestAndPrefersGameExecutable()
        {
            string root = NewTempDirectory();
            try
            {
                string steamApps = Path.Combine(root, "steamapps");
                string game = Path.Combine(steamApps, "common", "StarArena");
                Directory.CreateDirectory(Path.Combine(game, "Tools"));
                File.WriteAllText(Path.Combine(steamApps, "appmanifest_42.acf"),
                    "\"AppState\"\n{\n\"name\" \"Star Arena\"\n\"installdir\" \"StarArena\"\n}");
                File.WriteAllBytes(Path.Combine(game, "StarArena.exe"), new byte[32]);
                File.WriteAllBytes(Path.Combine(game, "Tools", "CrashReporter.exe"), new byte[64]);

                DiscoveredGame result = Assert.Single(GameDiscoveryService.ScanSteamLibrary(root));

                Assert.Equal("Star Arena", result.DisplayName);
                Assert.Equal("StarArena.exe", result.ExecutableName);
                Assert.Equal("Steam", result.Library);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void SteamScanFollowsAdditionalLibraryFolder()
        {
            string root = NewTempDirectory();
            string library = NewTempDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "steamapps"));
                File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
                    $"\"libraryfolders\"\n{{\n\"1\"\n{{\n\"path\" \"{library.Replace("\\", "\\\\")}\"\n}}\n}}");
                string steamApps = Path.Combine(library, "steamapps");
                string game = Path.Combine(steamApps, "common", "DeepRace");
                Directory.CreateDirectory(game);
                File.WriteAllText(Path.Combine(steamApps, "appmanifest_9.acf"),
                    "\"AppState\"\n{\n\"name\" \"Deep Race\"\n\"installdir\" \"DeepRace\"\n}");
                File.WriteAllBytes(Path.Combine(game, "DeepRace.exe"), new byte[8]);

                DiscoveredGame result = Assert.Single(GameDiscoveryService.ScanSteamLibrary(root));

                Assert.Equal("DeepRace.exe", result.ExecutableName);
            }
            finally
            {
                TryDelete(root);
                TryDelete(library);
            }
        }

        [Fact]
        public void EpicScanUsesLaunchExecutableFromManifest()
        {
            string root = NewTempDirectory();
            try
            {
                string manifests = Path.Combine(root, "Manifests");
                string install = Path.Combine(root, "Moonfall");
                Directory.CreateDirectory(manifests);
                Directory.CreateDirectory(Path.Combine(install, "Binaries", "Win64"));
                string executable = Path.Combine(install, "Binaries", "Win64", "Moonfall.exe");
                File.WriteAllBytes(executable, new byte[12]);
                File.WriteAllText(Path.Combine(manifests, "moon.item"), $$"""
                    {
                      "DisplayName": "Moonfall",
                      "InstallLocation": "{{install.Replace("\\", "\\\\")}}",
                      "LaunchExecutable": "Binaries\\Win64\\Moonfall.exe"
                    }
                    """);

                DiscoveredGame result = Assert.Single(GameDiscoveryService.ScanEpicManifests(manifests));

                Assert.Equal("Moonfall.exe", result.ExecutableName);
                Assert.Equal("Epic Games", result.Library);
                Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void SteamScanRejectsBootstrapperAndFindsDeeperShippingExecutable()
        {
            string root = NewTempDirectory();
            try
            {
                string steamApps = Path.Combine(root, "steamapps");
                string game = Path.Combine(steamApps, "common", "Fortnite");
                string binaries = Path.Combine(game, "FortniteGame", "Binaries", "Win64");
                Directory.CreateDirectory(binaries);
                File.WriteAllText(Path.Combine(steamApps, "appmanifest_77.acf"),
                    "\"AppState\"\n{\n\"name\" \"Fortnite\"\n\"installdir\" \"Fortnite\"\n}");
                File.WriteAllBytes(Path.Combine(game, "FortniteBootstrapper.exe"), new byte[128]);
                File.WriteAllBytes(Path.Combine(binaries, "FortniteClient-Win64-Shipping.exe"), new byte[64]);

                DiscoveredGame result = Assert.Single(GameDiscoveryService.ScanSteamLibrary(root));

                Assert.Equal("FortniteClient-Win64-Shipping.exe", result.ExecutableName);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void SteamScanDropsUtilityLibraryEntries()
        {
            string root = NewTempDirectory();
            try
            {
                string steamApps = Path.Combine(root, "steamapps");
                string utility = Path.Combine(steamApps, "common", "Soundpad");
                Directory.CreateDirectory(utility);
                File.WriteAllText(Path.Combine(steamApps, "appmanifest_88.acf"),
                    "\"AppState\"\n{\n\"name\" \"Soundpad\"\n\"installdir\" \"Soundpad\"\n}");
                File.WriteAllBytes(Path.Combine(utility, "Soundpad.exe"), new byte[64]);

                Assert.Empty(GameDiscoveryService.ScanSteamLibrary(root));
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void EpicScanReplacesUnsafeBootstrapperWithShippingExecutable()
        {
            string root = NewTempDirectory();
            try
            {
                string manifests = Path.Combine(root, "Manifests");
                string install = Path.Combine(root, "Fortnite");
                string binaries = Path.Combine(install, "FortniteGame", "Binaries", "Win64");
                Directory.CreateDirectory(manifests);
                Directory.CreateDirectory(binaries);
                File.WriteAllBytes(Path.Combine(install, "FortniteBootstrapper.exe"), new byte[128]);
                string shipping = Path.Combine(binaries, "FortniteClient-Win64-Shipping.exe");
                File.WriteAllBytes(shipping, new byte[64]);
                File.WriteAllText(Path.Combine(manifests, "fortnite.item"), $$"""
                    {
                      "DisplayName": "Fortnite",
                      "InstallLocation": "{{install.Replace("\\", "\\\\")}}",
                      "LaunchExecutable": "FortniteBootstrapper.exe"
                    }
                    """);

                DiscoveredGame result = Assert.Single(GameDiscoveryService.ScanEpicManifests(manifests));

                Assert.Equal("FortniteClient-Win64-Shipping.exe", result.ExecutableName);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void SteamScanRecognizesAbbreviatedExecutableInWindowsBinaryFolder()
        {
            string root = NewTempDirectory();
            try
            {
                string steamApps = Path.Combine(root, "steamapps");
                string binaries = Path.Combine(
                    steamApps, "common", "Euro Truck Simulator 2", "bin", "win_x64");
                Directory.CreateDirectory(binaries);
                File.WriteAllText(Path.Combine(steamApps, "appmanifest_99.acf"),
                    "\"AppState\"\n{\n\"name\" \"Euro Truck Simulator 2\"\n\"installdir\" \"Euro Truck Simulator 2\"\n}");
                File.WriteAllBytes(Path.Combine(binaries, "eurotrucks2.exe"), new byte[64]);

                DiscoveredGame result = Assert.Single(GameDiscoveryService.ScanSteamLibrary(root));

                Assert.Equal("eurotrucks2.exe", result.ExecutableName);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void SteamScanHonorsCancellationBeforeTouchingLibrary()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                GameDiscoveryService.ScanSteamLibrary(
                    Path.Combine(Path.GetTempPath(), "does-not-need-to-exist"),
                    cancellation.Token));
        }

        [Fact]
        public void HdrCapabilityDetection_FindsStrongLocalConfigSignalWithoutGuessing()
        {
            string root = NewTempDirectory();
            try
            {
                string binaries = Path.Combine(root, "Binaries", "Win64");
                string config = Path.Combine(root, "Config");
                Directory.CreateDirectory(binaries);
                Directory.CreateDirectory(config);
                string executable = Path.Combine(binaries, "Arena.exe");
                File.WriteAllBytes(executable, new byte[32]);
                File.WriteAllText(Path.Combine(config, "DefaultEngine.ini"),
                    "r.HDR.EnableHDROutput=1\n");

                var detected = GameDiscoveryService.DetectHdrCapability(executable);

                Assert.Equal(GamerHdrCapability.Detected, detected.Capability);
                Assert.Contains("DefaultEngine.ini", detected.Evidence);

                File.Delete(Path.Combine(config, "DefaultEngine.ini"));
                File.WriteAllText(Path.Combine(config, "readme.txt"),
                    "This readme discusses HDR10 support.");
                var unknown = GameDiscoveryService.DetectHdrCapability(executable);
                Assert.Equal(GamerHdrCapability.Unknown, unknown.Capability);
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "GloamGameScanTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }
    }
}
