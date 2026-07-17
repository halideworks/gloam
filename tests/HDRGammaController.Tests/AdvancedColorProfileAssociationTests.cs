using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Interop;
using Xunit;

namespace HDRGammaController.Tests
{
    public sealed class AdvancedColorProfileAssociationTests
    {
        [Fact]
        public void ActivateInstalled_EnablesCurrentUserScope_SetsAndVerifiesExtendedDefault()
        {
            var platform = new FakeAdvancedColorPlatform { PerUserEnabled = false };
            var monitor = Monitor();

            bool success = AdvancedColorProfileAssociation.TryActivateInstalled(
                monitor, "Gloam safe.icm", out var receipt, out string? error, platform);

            Assert.True(success, error);
            Assert.NotNull(receipt);
            Assert.True(platform.PerUserEnabled);
            Assert.Equal(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                platform.SelectedScope);
            Assert.Equal("Gloam safe.icm", platform.CurrentDefault);
            Assert.Contains("Gloam safe.icm", platform.CurrentProfiles);
            Assert.True(platform.SetDefaultCalled);
        }

        [Fact]
        public void ActivateInstalled_VerificationFailure_RestoresPriorScopeAndAssociation()
        {
            var platform = new FakeAdvancedColorPlatform
            {
                PerUserEnabled = false,
                IgnoreDefaultWrites = true
            };
            platform.CurrentProfiles.Add("previous.icm");
            platform.CurrentDefault = "previous.icm";
            var monitor = Monitor();

            bool success = AdvancedColorProfileAssociation.TryActivateInstalled(
                monitor, "unverified.icm", out _, out string? error, platform);

            Assert.False(success);
            Assert.Contains("did not retain", error, StringComparison.OrdinalIgnoreCase);
            Assert.False(platform.PerUserEnabled);
            Assert.Equal(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_SYSTEM_WIDE,
                platform.SelectedScope);
            Assert.DoesNotContain("unverified.icm", platform.CurrentProfiles);
            Assert.Contains("previous.icm", platform.CurrentProfiles);
        }

        [Fact]
        public void ActivateInstalled_ProfileAlreadyInInactiveUserList_SelectsWithoutDuplicateAdd()
        {
            var platform = new FakeAdvancedColorPlatform { PerUserEnabled = false };
            platform.CurrentProfiles.Add("parked.icm");

            bool success = AdvancedColorProfileAssociation.TryActivateInstalled(
                Monitor(), "parked.icm", out _, out string? error, platform);

            Assert.True(success, error);
            Assert.Equal(0, platform.AddCalls);
            Assert.Equal("parked.icm", platform.CurrentDefault);
            Assert.True(platform.PerUserEnabled);
        }

        [Fact]
        public void RemoveCurrentUser_UsesOfficialListAndConfirmsRemoval()
        {
            var platform = new FakeAdvancedColorPlatform { PerUserEnabled = true };
            platform.CurrentProfiles.Add("Gloam old.icm");

            bool success = AdvancedColorProfileAssociation.TryRemoveCurrentUser(
                Monitor(), "Gloam old.icm", out string? error, platform);

            Assert.True(success, error);
            Assert.DoesNotContain("Gloam old.icm", platform.CurrentProfiles);
            Assert.True(platform.GetListCalls >= 2);
        }

        [Fact]
        public void VerifiedCurrentUserDefault_RejectsMatchingSystemDefault()
        {
            var platform = new FakeAdvancedColorPlatform
            {
                PerUserEnabled = false,
                SystemDefault = "Gloam safe.icm"
            };
            platform.SystemProfiles.Add("Gloam safe.icm");

            bool queried = AdvancedColorProfileAssociation.TryIsVerifiedCurrentUserDefault(
                Monitor(), "Gloam safe.icm", out bool active, out string? error, platform);

            Assert.True(queried, error);
            Assert.False(active);
        }

        private static MonitorInfo Monitor() => new()
        {
            DeviceName = @"\\.\DISPLAY1",
            MonitorDevicePath = @"MONITOR\TEST\INSTANCE",
            FriendlyName = "Test Display",
            IsHdrActive = true,
            HasDisplayConfigIds = true,
            DisplayConfigAdapterId = new Dxgi.LUID { LowPart = 1, HighPart = 2 },
            DisplayConfigSourceId = 3
        };
    }

    internal sealed class FakeAdvancedColorPlatform : IAdvancedColorProfilePlatform
    {
        public bool PerUserEnabled { get; set; }
        public bool IgnoreDefaultWrites { get; set; }
        public bool FailRemove { get; set; }
        public bool SetDefaultCalled { get; private set; }
        public int GetListCalls { get; private set; }
        public int AddCalls { get; private set; }
        public HashSet<string> CurrentProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SystemProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? CurrentDefault { get; set; }
        public string? SystemDefault { get; set; }
        public string ColorStoreDirectory { get; set; } = Path.GetTempPath();
        public Wcs.WCS_PROFILE_MANAGEMENT_SCOPE SelectedScope => PerUserEnabled
            ? Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER
            : Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_SYSTEM_WIDE;

        public bool TryResolveDisplay(MonitorInfo monitor, out AdvancedColorDisplayIdentity identity)
        {
            identity = new AdvancedColorDisplayIdentity(
                monitor.DisplayConfigAdapterId, monitor.DisplayConfigSourceId);
            return monitor.HasDisplayConfigIds;
        }

        public bool TryGetUsePerUserProfiles(string monitorDevicePath, out bool enabled)
        {
            enabled = PerUserEnabled;
            return true;
        }

        public bool SetUsePerUserProfiles(string monitorDevicePath, bool enabled)
        {
            PerUserEnabled = enabled;
            return true;
        }

        public int GetSelectedScope(AdvancedColorDisplayIdentity identity,
            out Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope)
        {
            scope = SelectedScope;
            return 0;
        }

        public int GetDisplayList(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope,
            AdvancedColorDisplayIdentity identity, out IReadOnlyList<string> profiles)
        {
            GetListCalls++;
            profiles = (scope == Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER
                ? CurrentProfiles
                : SystemProfiles).ToArray();
            return 0;
        }

        public int GetDisplayDefault(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope,
            AdvancedColorDisplayIdentity identity, out string? profileName)
        {
            profileName = scope == Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER
                ? CurrentDefault
                : SystemDefault;
            return profileName == null ? unchecked((int)0x80070490) : 0;
        }

        public int AddDisplayAssociation(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, string profileName,
            AdvancedColorDisplayIdentity identity, bool setAsDefault)
        {
            AddCalls++;
            Profiles(scope).Add(profileName);
            if (setAsDefault && !IgnoreDefaultWrites) SetDefault(scope, profileName);
            return 0;
        }

        public int SetDisplayDefault(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, string profileName,
            AdvancedColorDisplayIdentity identity)
        {
            SetDefaultCalled = true;
            if (!IgnoreDefaultWrites) SetDefault(scope, profileName);
            return 0;
        }

        public int RemoveDisplayAssociation(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, string profileName,
            AdvancedColorDisplayIdentity identity)
        {
            if (FailRemove) return unchecked((int)0x80004005);
            var profiles = Profiles(scope);
            bool removed = profiles.Remove(profileName);
            if (scope == Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER &&
                string.Equals(CurrentDefault, profileName, StringComparison.OrdinalIgnoreCase))
                CurrentDefault = null;
            if (scope == Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_SYSTEM_WIDE &&
                string.Equals(SystemDefault, profileName, StringComparison.OrdinalIgnoreCase))
                SystemDefault = null;
            return removed ? 0 : unchecked((int)0x80070490);
        }

        public bool InstallColorProfile(string stagedPath)
        {
            Directory.CreateDirectory(ColorStoreDirectory);
            string destination = Path.Combine(ColorStoreDirectory, Path.GetFileName(stagedPath));
            if (File.Exists(destination)) return false;
            File.Copy(stagedPath, destination);
            return true;
        }

        public bool UninstallColorProfile(string profileName, bool delete)
        {
            string path = Path.Combine(ColorStoreDirectory, Path.GetFileName(profileName));
            if (delete && File.Exists(path)) File.Delete(path);
            return true;
        }

        private HashSet<string> Profiles(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope) =>
            scope == Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER
                ? CurrentProfiles
                : SystemProfiles;

        private void SetDefault(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, string profileName)
        {
            if (scope == Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER)
                CurrentDefault = profileName;
            else
                SystemDefault = profileName;
        }
    }
}
