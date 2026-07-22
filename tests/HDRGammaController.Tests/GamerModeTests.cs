using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HDRGammaController.Core;
using HDRGammaController.Interop;
using HDRGammaController.ViewModels;
using Xunit;

namespace HDRGammaController.Tests
{
    public sealed class GamerModeTests
    {
        [Fact]
        public void CompetitivePreset_IsAStaticMonotonicVisibilityIntent()
        {
            GamerProfileRule profile = GamerPresetCatalog.Create(
                @"C:\Games\Arena\arena", GamerPictureIntent.CompetitiveClarity);

            Assert.Equal("arena.exe", profile.AppName);
            Assert.Equal(GammaMode.Gamma22, profile.GammaMode);
            Assert.True(profile.ShadowDetailStrength > 0);
            Assert.Equal(GamerNightPolicy.FollowSchedule, profile.NightPolicy);
            Assert.True(profile.GameplayLock);
        }

        [Fact]
        public void NightOpsPreset_UsesCircadianPolicyWithoutSacrificingVisibilityToe()
        {
            GamerProfileRule profile = GamerPresetCatalog.Create("night-game.exe", GamerPictureIntent.NightOps);

            Assert.Equal(GamerNightPolicy.NightOps, profile.NightPolicy);
            Assert.InRange(profile.NightOpsKelvin, 1900, 6500);
            Assert.InRange(profile.NightOpsStrength, 0.0, 1.0);
            Assert.True(profile.ShadowDetailStrength > 0);
            Assert.Equal(0.0, profile.NightOpsMelanopicCeiling);
        }

        [Fact]
        public void EditorChoices_RenderHumanLabels_AndDoseCeilingIsOptIn()
        {
            var editor = new GamerProfileEditorItem(
                GamerPresetCatalog.Create("arena.exe", GamerPictureIntent.NightOps));

            Assert.Equal("Accurate", editor.AvailablePictureIntents[0].ToString());
            Assert.Equal("Gamma 2.2", editor.AvailableGammaModes[0].ToString());
            Assert.False(editor.IsNightOpsMelanopicCeilingEnabled);

            editor.IsNightOpsMelanopicCeilingEnabled = true;
            Assert.Equal(10.0, editor.ToRule().NightOpsMelanopicCeiling);
            editor.IsNightOpsMelanopicCeilingEnabled = false;
            Assert.Equal(0.0, editor.ToRule().NightOpsMelanopicCeiling);
        }

        [Fact]
        public void LargeLibrary_FilterKeepsOneSelectedEditorAndDoesNotDuplicateProfiles()
        {
            var item = new GamerModeItem();
            for (int i = 0; i < 150; i++)
            {
                GamerProfileRule rule = GamerPresetCatalog.Create(
                    $"game{i:D3}.exe", GamerPictureIntent.CompetitiveClarity);
                rule.DisplayName = $"Game {i:D3}";
                item.Profiles.Add(new GamerProfileEditorItem(rule));
            }

            Assert.Equal(5, item.FilteredProfiles.Count);
            Assert.NotNull(item.SelectedProfile);

            item.ProfileSearchText = "Game 149";

            Assert.Equal(150, item.Profiles.Count);
            Assert.Equal("game149.exe", Assert.Single(item.FilteredProfiles).AppName);
            Assert.Same(item.FilteredProfiles[0], item.SelectedProfile);

            item.ProfileSearchText = string.Empty;
            item.UpdateProfileRecency("game149.exe", new DateTime(2026, 7, 17, 20, 0, 0, DateTimeKind.Utc));

            Assert.Equal(5, item.FilteredProfiles.Count);
            Assert.Equal("game149.exe", item.FilteredProfiles[0].AppName);
        }

        [Fact]
        public void Discovery_StartsUnselectedAndHidesGamesAlreadyInLibrary()
        {
            var item = new GamerModeItem();
            DiscoveredGame[] games =
            [
                new("Arena", "arena.exe", @"C:\Games\Arena\arena.exe", "Steam"),
                new("Moonfall", "moonfall.exe", @"C:\Games\Moonfall\moonfall.exe", "Epic Games")
            ];

            item.SetDiscoveryResults(games, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "arena.exe"
            });

            DiscoveredGameItem result = Assert.Single(item.DiscoveredGames);
            Assert.Equal("moonfall.exe", result.ExecutableName);
            Assert.False(result.IsSelected);
            Assert.Equal(0, item.SelectedDiscoveryCount);
        }

        [Fact]
        public void CinematicHdrPreset_PreservesReferencePqTransferFunction()
        {
            GamerProfileRule profile = GamerPresetCatalog.Create(
                "cinematic.exe", GamerPictureIntent.CinematicHdr);

            Assert.True(profile.OverrideGamma);
            Assert.Equal(GammaMode.WindowsDefault, profile.GammaMode);
            Assert.Equal(GamerHdrExpectation.RequireHdr, profile.HdrExpectation);
            Assert.Equal(0, profile.ShadowDetailStrength);
        }

        [Fact]
        public void ChangingIntent_ResetsSignalExpectationAndCustomPreservesEdits()
        {
            GamerProfileRule profile = GamerPresetCatalog.Create(
                "game.exe", GamerPictureIntent.CinematicHdr);

            GamerPresetCatalog.Apply(profile, GamerPictureIntent.NightOps);
            Assert.Equal(GamerHdrExpectation.Automatic, profile.HdrExpectation);
            Assert.Equal(GamerNightPolicy.NightOps, profile.NightPolicy);

            profile.ShadowDetailStrength = 0.73;
            profile.OverrideGamma = false;
            GamerPresetCatalog.Apply(profile, GamerPictureIntent.Custom);
            Assert.Equal(0.73, profile.ShadowDetailStrength);
            Assert.False(profile.OverrideGamma);
        }

        [Fact]
        public void GamerProfileSanitized_ClampsCorruptAndUnknownInputs()
        {
            var profile = new GamerProfileRule
            {
                AppName = @" C:\Games\bad ",
                DisplayName = new string('x', 200),
                PictureIntent = (GamerPictureIntent)999,
                DisplayScope = GamerDisplayScope.SpecificDisplay,
                MonitorDevicePath = " ",
                ShadowDetailStrength = double.NaN,
                ShadowDetailPivot = 9,
                NightOpsKelvin = -1,
                NightOpsStrength = double.PositiveInfinity,
                NightOpsMelanopicCeiling = 5000,
                PaperWhiteNits = -20,
                PeakNits = 50000,
                BlackLevelNits = double.NaN
            }.Sanitized();

            Assert.Equal("bad.exe", profile.AppName);
            Assert.Equal(80, profile.DisplayName.Length);
            Assert.Equal(GamerPictureIntent.CompetitiveClarity, profile.PictureIntent);
            Assert.Equal(GamerDisplayScope.WindowDisplays, profile.DisplayScope);
            Assert.Null(profile.MonitorDevicePath);
            Assert.Equal(0.0, profile.ShadowDetailStrength);
            Assert.Equal(0.25, profile.ShadowDetailPivot);
            Assert.Equal(1900, profile.NightOpsKelvin);
            Assert.Equal(0.55, profile.NightOpsStrength);
            Assert.Equal(1000, profile.NightOpsMelanopicCeiling);
            Assert.Equal(0, profile.PaperWhiteNits);
            Assert.Equal(10000, profile.PeakNits);
            Assert.Equal(0, profile.BlackLevelNits);
        }

        [Fact]
        public void ShadowVisibility_AnchorsBlackAndPivot_AndProtectsMidtones()
        {
            const double pivot = 0.10;
            Assert.Equal(0.0, GamerShadowVisibility.Apply(0.0, 1.0, pivot), 15);
            Assert.Equal(pivot, GamerShadowVisibility.Apply(pivot, 1.0, pivot), 15);
            Assert.Equal(0.50, GamerShadowVisibility.Apply(0.50, 1.0, pivot), 15);
            Assert.True(GamerShadowVisibility.Apply(0.025, 1.0, pivot) > 0.025);
        }

        [Fact]
        public void ShadowVisibility_IsStrictlyMonotonicAtMaximumStrength()
        {
            double previous = GamerShadowVisibility.Apply(0, 1.0);
            for (int i = 1; i <= 10000; i++)
            {
                double input = i / 10000.0;
                double current = GamerShadowVisibility.Apply(input, 1.0);
                Assert.True(current > previous,
                    $"Visibility curve stopped increasing at {input:F5}: {previous:F8} -> {current:F8}");
                previous = current;
            }
            Assert.True(GamerShadowVisibility.MinimumSlope(1.0) > 0);
        }

        [Fact]
        public void ShadowVisibility_ComposesInsideLinearAdjustmentPipeline()
        {
            var neutral = ColorAdjustments.ApplyUserAdjustmentsLinear(
                0.03, 0.03, 0.03, CalibrationSettings.Default);
            var competitive = ColorAdjustments.ApplyUserAdjustmentsLinear(
                0.03, 0.03, 0.03,
                new CalibrationSettings { ShadowDetailStrength = 0.75, ShadowDetailPivot = 0.10 });
            var protectedMid = ColorAdjustments.ApplyUserAdjustmentsLinear(
                0.30, 0.30, 0.30,
                new CalibrationSettings { ShadowDetailStrength = 1.0, ShadowDetailPivot = 0.10 });

            Assert.True(competitive.R > neutral.R);
            Assert.Equal(competitive.R, competitive.G, 15);
            Assert.Equal(competitive.G, competitive.B, 15);
            Assert.Equal(0.30, protectedMid.R, 15);
        }

        [Fact]
        public void ShadowVisibility_PreservesDarkColorRatios()
        {
            var adjusted = ColorAdjustments.ApplyUserAdjustmentsLinear(
                0.04, 0.02, 0.01,
                new CalibrationSettings { ShadowDetailStrength = 0.8, ShadowDetailPivot = 0.10 });

            Assert.True(adjusted.R > 0.04);
            Assert.Equal(2.0, adjusted.R / adjusted.G, 12);
            Assert.Equal(2.0, adjusted.G / adjusted.B, 12);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GamerVisibilityLut_IsFiniteBoundedAndMonotonic(bool isHdr)
        {
            var lut = LutGenerator.GenerateLut(
                GammaMode.Gamma22,
                200.0,
                new CalibrationSettings { ShadowDetailStrength = 1.0, ShadowDetailPivot = 0.10 },
                isHdr);

            for (int i = 0; i < lut.R.Length; i++)
            {
                Assert.True(double.IsFinite(lut.R[i]));
                Assert.InRange(lut.R[i], 0.0, 1.0);
                if (i > 0)
                    Assert.True(lut.R[i] >= lut.R[i - 1] - 1e-12,
                        $"LUT became non-monotonic at {i}: {lut.R[i - 1]} -> {lut.R[i]}");
            }
        }

        [Fact]
        public void SignalDiagnostics_FlagsHdrMismatchLowBitDepthAndHeadroom()
        {
            var profile = GamerPresetCatalog.Create("hdrgame.exe", GamerPictureIntent.CinematicHdr);
            var monitor = new MonitorInfo
            {
                FriendlyName = "Test",
                IsHdrActive = false,
                DxgiColorSpace = 0,
                BitsPerColor = 2,
                SdrWhiteLevel = 200,
                HdrPeakNits = 220
            };

            var sdrFindings = GamerSignalDiagnostics.Evaluate(profile, monitor, null);
            Assert.Contains(sdrFindings, f => f.Code == "HDR_REQUIRED_INACTIVE" &&
                                                f.Severity == GamerDiagnosticSeverity.Critical);
            Assert.Contains(sdrFindings, f => f.Code == "DISPLAY_UNMEASURED");

            monitor.IsHdrActive = true;
            monitor.DxgiColorSpace = 12;
            var hdrFindings = GamerSignalDiagnostics.Evaluate(profile, monitor, null);
            Assert.Contains(hdrFindings, f => f.Code == "HDR_LOW_BIT_DEPTH");
            Assert.Contains(hdrFindings, f => f.Code == "HDR_HEADROOM_LOW");

            profile.GammaMode = GammaMode.Gamma24;
            hdrFindings = GamerSignalDiagnostics.Evaluate(profile, monitor, null);
            Assert.Contains(hdrFindings, f => f.Code == "HDR_GAMMA_OVERRIDE");
        }

        [Fact]
        public void SignalDiagnostics_DoesNotTreatStoredMhc2FilenameAsActive()
        {
            var profile = GamerPresetCatalog.Create("game.exe", GamerPictureIntent.Reference);
            var monitor = new MonitorInfo { MonitorDevicePath = "display-1" };
            var stored = new MonitorProfileData { Mhc2ProfileName = "gloam-test.icm" };

            var unverified = GamerSignalDiagnostics.Evaluate(profile, monitor, stored);
            Assert.Contains(unverified, finding =>
                finding.Code == "DISPLAY_CALIBRATION_UNVERIFIED");

            var active = GamerSignalDiagnostics.Evaluate(
                profile,
                monitor,
                stored,
                new GamerCalibrationStatus(true, true, true, "gloam-test.icm"));
            Assert.DoesNotContain(active, finding =>
                finding.Code is "DISPLAY_UNMEASURED" or
                    "DISPLAY_CALIBRATION_UNVERIFIED" or
                    "DISPLAY_CALIBRATION_INACTIVE");
        }

        [Fact]
        public void SettingsManager_GamerProfilesPersistAsDeepSanitizedSnapshots()
        {
            string oldData = AppPaths.DataDir;
            string oldRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), "GloamGamerTests", Guid.NewGuid().ToString("N"));
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(root, Path.Combine(root, "roaming"));
                var manager = new SettingsManager();
                var source = GamerPresetCatalog.Create("game.exe", GamerPictureIntent.NightOps);
                source.ExecutablePath = @"C:\Games\Arena\game.exe";
                manager.SetGamerProfiles(new[] { source });
                manager.SetGamerModeEnabled(true);
                var usedUtc = new DateTime(2026, 7, 17, 22, 30, 0, DateTimeKind.Utc);
                string? recencyEventApp = null;
                manager.GamerProfileUsed += (app, _) => recencyEventApp = app;
                manager.MarkGamerProfileUsed("GAME.EXE", usedUtc);

                source.AppName = "mutated.exe";
                GamerProfileRule snapshot = Assert.Single(manager.GamerProfiles);
                snapshot.AppName = "also-mutated.exe";

                var reloaded = new SettingsManager();
                Assert.True(reloaded.GamerModeEnabled);
                GamerProfileRule stored = Assert.Single(reloaded.GamerProfiles);
                Assert.Equal("game.exe", stored.AppName);
                Assert.Equal(Path.GetFullPath(@"C:\Games\Arena\game.exe"), stored.ExecutablePath);
                Assert.Equal(GamerPictureIntent.NightOps, stored.PictureIntent);
                Assert.Equal(usedUtc, stored.LastUsedUtc);
                Assert.Equal("GAME.EXE", recencyEventApp, ignoreCase: true);
                Assert.Equal(4, SettingsManager.CurrentSchemaVersion);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(oldData, oldRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void SettingsManager_UiSectionStatePersistsWithoutChangingReleaseSchema()
        {
            string oldData = AppPaths.DataDir;
            string oldRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), "GloamUiStateTests", Guid.NewGuid().ToString("N"));
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(root, Path.Combine(root, "roaming"));
                var manager = new SettingsManager();
                Assert.True(manager.GetUiSectionExpanded("Dashboard.GameLab", true));

                manager.SetUiSectionExpanded("Dashboard.GameLab", false);
                manager.SetUiSectionExpanded("Dashboard.CircadianAdvanced", true);

                var reloaded = new SettingsManager();
                Assert.False(reloaded.GetUiSectionExpanded("Dashboard.GameLab", true));
                Assert.True(reloaded.GetUiSectionExpanded("Dashboard.CircadianAdvanced", false));
                Assert.Equal(4, SettingsManager.CurrentSchemaVersion);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(oldData, oldRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void Schema3_DefaultNightOpsCeiling_MigratesToOff()
        {
            string oldData = AppPaths.DataDir;
            string oldRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), "GloamGamerMigrationTests", Guid.NewGuid().ToString("N"));
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(root, Path.Combine(root, "roaming"));
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "settings.json"), """
                    {
                      "SchemaVersion": 3,
                      "GamerModeEnabled": true,
                      "GamerProfiles": [
                        {
                          "AppName": "arena.exe",
                          "PictureIntent": "NightOps",
                          "NightPolicy": "NightOps",
                          "NightOpsMelanopicCeiling": 10.0
                        }
                      ]
                    }
                    """);

                var manager = new SettingsManager();

                Assert.Equal(0.0, Assert.Single(manager.GamerProfiles).NightOpsMelanopicCeiling);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(oldData, oldRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void NormalizeGamerProfiles_LastDuplicateWinsAndInvalidRowsDrop()
        {
            var normalized = SettingsManager.NormalizeGamerProfiles(new[]
            {
                new GamerProfileRule { AppName = " " },
                GamerPresetCatalog.Create("GAME.EXE", GamerPictureIntent.Reference),
                GamerPresetCatalog.Create("game", GamerPictureIntent.NightOps)
            });

            GamerProfileRule stored = Assert.Single(normalized);
            Assert.Equal("game.exe", stored.AppName);
            Assert.Equal(GamerPictureIntent.NightOps, stored.PictureIntent);
        }

        [Theory]
        [InlineData("explorer.exe", "Windows")]
        [InlineData("CefSharp.BrowserSubprocess.exe", "YUR")]
        [InlineData("FortniteBootstrapper.exe", "Fortnite")]
        [InlineData("acServer.exe", "Assetto Corsa Dedicated Server")]
        [InlineData("vrpathreg.exe", "SteamVR")]
        public void GamerExecutableSafety_RejectsNonGameOwners(string executable, string title)
        {
            Assert.False(GamerExecutableSafety.IsSafeProfileTarget(executable, title));
        }

        [Fact]
        public void NormalizeGamerProfiles_RemovesUnsafeLegacyTargets()
        {
            var normalized = SettingsManager.NormalizeGamerProfiles(new[]
            {
                GamerPresetCatalog.Create("arena.exe", GamerPictureIntent.Reference),
                new GamerProfileRule { AppName = "explorer.exe", DisplayName = "Windows Explorer" },
                new GamerProfileRule { AppName = "XSOverlay.exe", DisplayName = "XSOverlay" }
            });

            GamerProfileRule stored = Assert.Single(normalized);
            Assert.Equal("arena.exe", stored.AppName);
        }

        [Fact]
        public void NightOpsSettings_UsePerceptualSpectrumPolicyAndDeepCopySchedule()
        {
            var source = new NightModeSettings
            {
                Algorithm = NightModeAlgorithm.UltraNight,
                PreserveLuminance = true,
                Schedule = new()
                {
                    new NightModeSchedulePoint { TargetKelvin = 2700 }
                }
            };
            GamerProfileRule profile = GamerPresetCatalog.Create("night.exe", GamerPictureIntent.NightOps);

            NightModeSettings resolved = GammaApplyService.BuildNightOpsSettings(source, profile);

            Assert.Equal(NightModeAlgorithm.Perceptual, resolved.Algorithm);
            Assert.False(resolved.PreserveLuminance);
            Assert.Equal(profile.NightOpsStrength, resolved.PerceptualStrength);
            Assert.Equal(profile.NightOpsMelanopicCeiling, resolved.MelanopicEdiCeiling);
            Assert.NotSame(source.Schedule, resolved.Schedule);
            Assert.NotSame(source.Schedule[0], resolved.Schedule[0]);
        }

        [Fact]
        public void ActiveGamerSession_DuplicateForegroundEventPreservesGameplayLockKelvin()
        {
            string oldData = AppPaths.DataDir;
            string oldRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), "GloamGamerSessionTests", Guid.NewGuid().ToString("N"));
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(root, Path.Combine(root, "roaming"));
                var settings = new SettingsManager();
                using var night = new NightModeService(new NightModeSettings());
                night.UpdateSettings(new NightModeSettings
                {
                    Enabled = true,
                    ManualOverrideEnabled = true,
                    TemperatureKelvin = 3000
                });
                using var apply = new GammaApplyService(new DispwinRunner(), settings, night);
                var monitor = new MonitorInfo
                {
                    HMonitor = (IntPtr)42,
                    MonitorDevicePath = "display-1",
                    FriendlyName = "Panel"
                };
                GamerProfileRule profile = GamerPresetCatalog.Create("game.exe", GamerPictureIntent.CompetitiveClarity);

                Assert.True(apply.UpdateActiveGamerSessions(new[] { new GamerSessionAssignment(monitor, profile) }));
                GamerSessionSnapshot first = Assert.Single(apply.ActiveGamerSessions);
                Assert.Equal(3000, first.LockedKelvin);
                Assert.Equal(GammaMode.Gamma22, first.EffectiveGamma);
                Assert.Equal(profile.ShadowDetailStrength, first.ShadowDetailStrength);

                night.UpdateSettings(new NightModeSettings
                {
                    Enabled = true,
                    ManualOverrideEnabled = true,
                    TemperatureKelvin = 5000
                });
                Assert.False(apply.UpdateActiveGamerSessions(new[] { new GamerSessionAssignment(monitor, profile.Clone()) }));
                GamerSessionSnapshot duplicate = Assert.Single(apply.ActiveGamerSessions);
                Assert.Equal(first.StartedUtc, duplicate.StartedUtc);
                Assert.Equal(3000, duplicate.LockedKelvin);

                GamerProfileRule unlocked = profile.Clone();
                unlocked.GameplayLock = false;
                Assert.True(apply.UpdateActiveGamerSessions(new[] { new GamerSessionAssignment(monitor, unlocked) }));
                Assert.Equal(5000, Assert.Single(apply.ActiveGamerSessions).LockedKelvin);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(oldData, oldRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void ActiveGamerSession_AllowsOnlyOneExecutableOwner()
        {
            string oldData = AppPaths.DataDir;
            string oldRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), "GloamSingleGamerSessionTests", Guid.NewGuid().ToString("N"));
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(root, Path.Combine(root, "roaming"));
                var settings = new SettingsManager();
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(new DispwinRunner(), settings, night);
                var left = new MonitorInfo { HMonitor = (IntPtr)11, MonitorDevicePath = "left", FriendlyName = "Left" };
                var right = new MonitorInfo { HMonitor = (IntPtr)22, MonitorDevicePath = "right", FriendlyName = "Right" };
                GamerProfileRule first = GamerPresetCatalog.Create("firstgame.exe", GamerPictureIntent.Reference);
                GamerProfileRule second = GamerPresetCatalog.Create("secondgame.exe", GamerPictureIntent.NightOps);

                Assert.True(apply.UpdateActiveGamerSessions(new[]
                {
                    new GamerSessionAssignment(left, first),
                    new GamerSessionAssignment(right, second)
                }));

                GamerSessionSnapshot active = Assert.Single(apply.ActiveGamerSessions);
                Assert.Equal("firstgame.exe", active.AppName);
                Assert.Equal("firstgame.exe", apply.ActiveGamerOwner);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(oldData, oldRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void ActiveGamerSession_RebuildsDiagnosticsWhenCalibrationActivationChanges()
        {
            string oldData = AppPaths.DataDir;
            string oldRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), "GloamGamerDiagnosticTests", Guid.NewGuid().ToString("N"));
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(root, Path.Combine(root, "roaming"));
                var settings = new SettingsManager();
                settings.SetMhc2Calibration("display-1", "gloam-test.icm");
                bool calibrationActive = false;
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(
                    new DispwinRunner(),
                    settings,
                    night,
                    (_, _) => new GamerCalibrationStatus(
                        true, calibrationActive, true, "gloam-test.icm"));
                var monitor = new MonitorInfo
                {
                    HMonitor = (IntPtr)41,
                    MonitorDevicePath = "display-1",
                    FriendlyName = "Panel"
                };
                var profile = GamerPresetCatalog.Create("game.exe", GamerPictureIntent.Reference);

                Assert.True(apply.UpdateActiveGamerSessions(new[]
                    { new GamerSessionAssignment(monitor, profile) }));
                Assert.Contains(Assert.Single(apply.ActiveGamerSessions).Diagnostics,
                    finding => finding.Code == "DISPLAY_CALIBRATION_INACTIVE");

                calibrationActive = true;
                Assert.True(apply.UpdateActiveGamerSessions(new[]
                    { new GamerSessionAssignment(monitor, profile.Clone()) }));
                Assert.DoesNotContain(Assert.Single(apply.ActiveGamerSessions).Diagnostics,
                    finding => finding.Code.StartsWith("DISPLAY_CALIBRATION_", StringComparison.Ordinal));
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(oldData, oldRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void GamerModeCoordinator_LatestForegroundWinsAndPauseClearsImmediately()
        {
            string oldData = AppPaths.DataDir;
            string oldRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), "GloamGamerCoordinatorTests", Guid.NewGuid().ToString("N"));
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(root, Path.Combine(root, "roaming"));
                var settings = new SettingsManager();
                string firstPath = Path.GetFullPath(@"C:\Games\First\first.exe");
                string secondPath = Path.GetFullPath(@"C:\Games\Second\second.exe");
                var first = GamerPresetCatalog.Create("first.exe", GamerPictureIntent.Reference);
                first.ExecutablePath = firstPath;
                var second = GamerPresetCatalog.Create("second.exe", GamerPictureIntent.NightOps);
                second.ExecutablePath = secondPath;
                Assert.True(settings.TrySetGamerProfiles(new[] { first, second }));
                Assert.True(settings.TrySetGamerModeEnabled(true));
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(
                    new DispwinRunner(), settings, night,
                    (_, _) => GamerCalibrationStatus.None);
                var monitor = new MonitorInfo
                {
                    HMonitor = (IntPtr)51,
                    MonitorDevicePath = "display-1",
                    FriendlyName = "Panel",
                    MonitorBounds = new Dxgi.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }
                };
                using var coordinator = new GamerModeCoordinator(
                    settings, apply, () => new[] { monitor });

                coordinator.ObserveForeground("first.exe", firstPath, monitor.MonitorBounds);
                coordinator.ObserveForeground("second.exe", secondPath, monitor.MonitorBounds);

                Assert.True(coordinator.WaitForIdle(TimeSpan.FromSeconds(3)));
                Assert.Equal("second.exe", Assert.Single(coordinator.ActiveSessions).AppName);

                Assert.True(coordinator.TrySetEnabled(false));
                Assert.Empty(coordinator.ActiveSessions);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(oldData, oldRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void GamerModeCoordinator_MonitorProfileChangeRefreshesActiveDiagnostics()
        {
            string oldData = AppPaths.DataDir;
            string oldRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), "GloamGamerProfileRefreshTests", Guid.NewGuid().ToString("N"));
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(root, Path.Combine(root, "roaming"));
                var settings = new SettingsManager();
                string executablePath = Path.GetFullPath(@"C:\Games\Arena\arena.exe");
                var profile = GamerPresetCatalog.Create("arena.exe", GamerPictureIntent.Reference);
                profile.ExecutablePath = executablePath;
                Assert.True(settings.TrySetGamerProfiles(new[] { profile }));
                Assert.True(settings.TrySetGamerModeEnabled(true));
                settings.SetMhc2Calibration("display-1", "gloam-test.icm");
                bool calibrationActive = false;
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(
                    new DispwinRunner(), settings, night,
                    (_, _) => new GamerCalibrationStatus(
                        true, calibrationActive, true, "gloam-test.icm"));
                var monitor = new MonitorInfo
                {
                    HMonitor = (IntPtr)61,
                    MonitorDevicePath = "display-1",
                    FriendlyName = "Panel",
                    MonitorBounds = new Dxgi.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }
                };
                using var coordinator = new GamerModeCoordinator(
                    settings, apply, () => new[] { monitor });
                coordinator.ObserveForeground("arena.exe", executablePath, monitor.MonitorBounds);
                Assert.True(coordinator.WaitForIdle(TimeSpan.FromSeconds(3)));
                Assert.Contains(Assert.Single(coordinator.ActiveSessions).Diagnostics,
                    finding => finding.Code == "DISPLAY_CALIBRATION_INACTIVE");

                calibrationActive = true;
                settings.SetMhc2Calibration("display-1", "gloam-test.icm");

                Assert.DoesNotContain(Assert.Single(coordinator.ActiveSessions).Diagnostics,
                    finding => finding.Code == "DISPLAY_CALIBRATION_INACTIVE");
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(oldData, oldRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void GamerWindowScope_TargetsOnlyIntersectingMonitor()
        {
            GamerProfileRule profile = GamerPresetCatalog.Create("game.exe", GamerPictureIntent.Reference);
            profile.DisplayScope = GamerDisplayScope.WindowDisplays;
            var left = new MonitorInfo
            {
                MonitorBounds = new Dxgi.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }
            };
            var right = new MonitorInfo
            {
                MonitorBounds = new Dxgi.RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 }
            };
            var window = new Dxgi.RECT { Left = 2100, Top = 100, Right = 3500, Bottom = 900 };

            Assert.False(GamerModeCoordinator.TargetsMonitor(profile, left, window, 2));
            Assert.True(GamerModeCoordinator.TargetsMonitor(profile, right, window, 2));
        }

        [Fact]
        public void GamerProfile_PathBoundTargetRejectsSameNameFromAnotherInstall()
        {
            GamerProfileRule profile = GamerPresetCatalog.Create("game.exe", GamerPictureIntent.Reference);
            profile.ExecutablePath = @"C:\Games\Arena\game.exe";

            Assert.True(GamerModeCoordinator.MatchesForeground(
                profile, "GAME.EXE", @"C:\Games\Arena\game.exe"));
            Assert.False(GamerModeCoordinator.MatchesForeground(
                profile, "game.exe", @"D:\Other\game.exe"));
            Assert.False(GamerModeCoordinator.MatchesForeground(
                profile, "game.exe", null));
        }
    }
}
