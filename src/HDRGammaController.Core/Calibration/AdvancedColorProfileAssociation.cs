using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HDRGammaController.Interop;

namespace HDRGammaController.Core.Calibration
{
    internal readonly record struct AdvancedColorDisplayIdentity(Dxgi.LUID AdapterId, uint SourceId);

    internal interface IAdvancedColorProfilePlatform
    {
        bool TryResolveDisplay(MonitorInfo monitor, out AdvancedColorDisplayIdentity identity);
        bool TryGetUsePerUserProfiles(string monitorDevicePath, out bool enabled);
        bool SetUsePerUserProfiles(string monitorDevicePath, bool enabled);
        int GetSelectedScope(AdvancedColorDisplayIdentity identity, out Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope);
        int GetDisplayList(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, AdvancedColorDisplayIdentity identity,
            out IReadOnlyList<string> profiles);
        int GetDisplayDefault(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, AdvancedColorDisplayIdentity identity,
            out string? profileName);
        int AddDisplayAssociation(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, string profileName,
            AdvancedColorDisplayIdentity identity, bool setAsDefault);
        int SetDisplayDefault(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, string profileName,
            AdvancedColorDisplayIdentity identity);
        int RemoveDisplayAssociation(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, string profileName,
            AdvancedColorDisplayIdentity identity);
        bool InstallColorProfile(string stagedPath);
        bool UninstallColorProfile(string profileName, bool delete);
        string ColorStoreDirectory { get; }
    }

    /// <summary>
    /// Owns the modern WCS Advanced Color association workflow. Windows keeps separate
    /// system and per-user lists and consults only the selected list; writing to an inactive
    /// current-user list reports success but applies no calibration. Every activation here
    /// therefore selects current-user mode, sets the Extended Display Color Mode default,
    /// and queries both the list and default back before reporting success.
    /// </summary>
    internal static class AdvancedColorProfileAssociation
    {
        internal static IAdvancedColorProfilePlatform Platform { get; set; } = new WindowsAdvancedColorProfilePlatform();

        internal sealed record ActivationReceipt(
            AdvancedColorDisplayIdentity Identity,
            bool PerUserWasEnabled,
            Wcs.WCS_PROFILE_MANAGEMENT_SCOPE PriorSelectedScope,
            string? PriorCurrentUserDefault,
            IReadOnlyList<string> PriorCurrentUserProfiles,
            string ActivatedProfile);

        internal static bool TryGetSelectedDefault(
            MonitorInfo monitor, out string? profileName, out string? error,
            IAdvancedColorProfilePlatform? platform = null)
        {
            platform ??= Platform;
            profileName = null;
            if (!platform.TryResolveDisplay(monitor, out var identity))
            {
                error = "Could not resolve this display's DisplayConfig identity.";
                return false;
            }

            int hr = platform.GetSelectedScope(identity, out var scope);
            if (hr != 0)
            {
                error = $"ColorProfileGetDisplayUserScope failed (HRESULT 0x{hr:X8}).";
                return false;
            }

            hr = platform.GetDisplayDefault(scope, identity, out profileName);
            if (hr != 0)
            {
                // A display may legitimately have no explicit default.
                profileName = null;
                error = null;
                return true;
            }

            error = null;
            return true;
        }

        internal static IReadOnlyList<string> GetCurrentUserProfiles(
            MonitorInfo monitor, IAdvancedColorProfilePlatform? platform = null)
        {
            platform ??= Platform;
            if (!platform.TryResolveDisplay(monitor, out var identity))
                return Array.Empty<string>();
            return platform.GetDisplayList(
                       Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                       identity, out var profiles) == 0
                ? profiles
                : Array.Empty<string>();
        }

        internal static bool TryIsVerifiedCurrentUserDefault(
            MonitorInfo monitor, string profileName, out bool isActive, out string? error,
            IAdvancedColorProfilePlatform? platform = null)
        {
            platform ??= Platform;
            isActive = false;
            profileName = Path.GetFileName((profileName ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(profileName) || string.IsNullOrWhiteSpace(monitor.MonitorDevicePath))
            {
                error = "A display and profile filename are required.";
                return false;
            }

            if (!platform.TryResolveDisplay(monitor, out var identity))
            {
                error = "Could not resolve this display's DisplayConfig identity.";
                return false;
            }

            if (!platform.TryGetUsePerUserProfiles(monitor.MonitorDevicePath, out bool perUserEnabled))
            {
                error = "Windows did not report the display's color-profile scope.";
                return false;
            }

            int hr = platform.GetSelectedScope(identity, out var selectedScope);
            if (hr != 0)
            {
                error = $"ColorProfileGetDisplayUserScope failed (HRESULT 0x{hr:X8}).";
                return false;
            }

            var currentScope = Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER;
            if (!perUserEnabled || selectedScope != currentScope)
            {
                error = null;
                return true;
            }

            hr = platform.GetDisplayList(currentScope, identity, out var profiles);
            if (hr != 0)
            {
                error = $"ColorProfileGetDisplayList failed (HRESULT 0x{hr:X8}).";
                return false;
            }

            hr = platform.GetDisplayDefault(currentScope, identity, out string? activeDefault);
            if (hr != 0)
            {
                error = null;
                return true;
            }

            isActive = profiles.Contains(profileName, StringComparer.OrdinalIgnoreCase) &&
                       string.Equals(activeDefault, profileName, StringComparison.OrdinalIgnoreCase);
            error = null;
            return true;
        }

        internal static bool TryActivateInstalled(
            MonitorInfo monitor, string profileName, out ActivationReceipt? receipt, out string? error,
            IAdvancedColorProfilePlatform? platform = null)
        {
            platform ??= Platform;
            receipt = null;
            profileName = Path.GetFileName((profileName ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(profileName))
            {
                error = "A profile filename is required.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(monitor.MonitorDevicePath) ||
                !platform.TryResolveDisplay(monitor, out var identity))
            {
                error = "Could not resolve this display's identity for Advanced Color.";
                return false;
            }

            if (!platform.TryGetUsePerUserProfiles(monitor.MonitorDevicePath, out bool wasPerUser))
            {
                error = "Windows did not report the display's color-profile scope.";
                return false;
            }

            int hr = platform.GetSelectedScope(identity, out var priorScope);
            if (hr != 0)
            {
                error = $"ColorProfileGetDisplayUserScope failed (HRESULT 0x{hr:X8}).";
                return false;
            }

            var currentScope = Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER;
            platform.GetDisplayList(currentScope, identity, out var priorProfiles);
            platform.GetDisplayDefault(currentScope, identity, out string? priorDefault);
            priorProfiles ??= Array.Empty<string>();

            var pending = new ActivationReceipt(identity, wasPerUser, priorScope, priorDefault,
                priorProfiles.ToArray(), profileName);

            if (!wasPerUser && !platform.SetUsePerUserProfiles(monitor.MonitorDevicePath, true))
            {
                error = "Windows refused to enable per-user color profiles for this display.";
                return false;
            }

            if (!platform.TryGetUsePerUserProfiles(monitor.MonitorDevicePath, out bool nowPerUser) || !nowPerUser ||
                platform.GetSelectedScope(identity, out var selectedScope) != 0 || selectedScope != currentScope)
            {
                TryRollback(monitor, pending, platform, out _);
                error = "Windows did not switch this display to the per-user color-profile list.";
                return false;
            }

            // Re-activation commonly finds the profile already parked in an inactive
            // current-user list. Adding it again can return ERROR_ALREADY_EXISTS; in that
            // case the correct operation is simply to select it as the HDR default.
            hr = priorProfiles.Contains(profileName, StringComparer.OrdinalIgnoreCase)
                ? 0
                : platform.AddDisplayAssociation(currentScope, profileName, identity, setAsDefault: true);
            if (hr == 0)
                hr = platform.SetDisplayDefault(currentScope, profileName, identity);
            if (hr != 0)
            {
                TryRollback(monitor, pending, platform, out _);
                error = $"Windows refused the Advanced Color default (HRESULT 0x{hr:X8}).";
                return false;
            }

            if (platform.GetDisplayList(currentScope, identity, out var installedProfiles) != 0 ||
                !installedProfiles.Contains(profileName, StringComparer.OrdinalIgnoreCase) ||
                platform.GetDisplayDefault(currentScope, identity, out string? installedDefault) != 0 ||
                !string.Equals(installedDefault, profileName, StringComparison.OrdinalIgnoreCase))
            {
                TryRollback(monitor, pending, platform, out _);
                error = "Windows did not retain the requested Advanced Color profile as the active HDR default.";
                return false;
            }

            receipt = pending;
            Log.Info($"AdvancedColorProfileAssociation: verified current-user Extended Display Color Mode default '{profileName}' for {monitor.FriendlyName}.");
            error = null;
            return true;
        }

        internal static bool TryRollback(
            MonitorInfo monitor, ActivationReceipt receipt, IAdvancedColorProfilePlatform platform,
            out string? error)
        {
            var currentScope = Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER;
            var failures = new List<string>();

            if (!receipt.PriorCurrentUserProfiles.Contains(
                    receipt.ActivatedProfile, StringComparer.OrdinalIgnoreCase))
            {
                int removeHr = platform.RemoveDisplayAssociation(
                    currentScope, receipt.ActivatedProfile, receipt.Identity);
                if (removeHr != 0)
                    failures.Add($"remove new profile HRESULT 0x{removeHr:X8}");
            }

            if (!string.IsNullOrWhiteSpace(receipt.PriorCurrentUserDefault))
            {
                int addHr = platform.AddDisplayAssociation(currentScope, receipt.PriorCurrentUserDefault,
                    receipt.Identity, setAsDefault: true);
                int setHr = addHr == 0
                    ? platform.SetDisplayDefault(currentScope, receipt.PriorCurrentUserDefault, receipt.Identity)
                    : addHr;
                if (setHr != 0)
                    failures.Add($"restore previous default HRESULT 0x{setHr:X8}");
            }

            if (!receipt.PerUserWasEnabled &&
                !platform.SetUsePerUserProfiles(monitor.MonitorDevicePath, false))
                failures.Add("restore system-wide profile scope");

            error = failures.Count == 0 ? null : string.Join("; ", failures);
            return failures.Count == 0;
        }

        internal static bool TryRemoveCurrentUser(
            MonitorInfo monitor, string profileName, out string? error,
            IAdvancedColorProfilePlatform? platform = null)
        {
            platform ??= Platform;
            if (!platform.TryResolveDisplay(monitor, out var identity))
            {
                error = "Could not resolve this display's identity.";
                return false;
            }

            var scope = Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER;
            if (platform.GetDisplayList(scope, identity, out var profiles) != 0 ||
                !profiles.Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                error = null;
                return true;
            }

            int hr = platform.RemoveDisplayAssociation(scope, profileName, identity);
            if (hr != 0)
            {
                error = $"ColorProfileRemoveDisplayAssociation failed (HRESULT 0x{hr:X8}).";
                return false;
            }

            if (platform.GetDisplayList(scope, identity, out profiles) == 0 &&
                profiles.Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                error = "Windows still reports the retired profile in the Advanced Color list.";
                return false;
            }

            error = null;
            return true;
        }

        internal static bool VerifyInstalledProfile(
            string stagedPath, string profileName, IAdvancedColorProfilePlatform platform, out string? error)
        {
            string installedPath = Path.Combine(platform.ColorStoreDirectory, Path.GetFileName(profileName));
            if (!File.Exists(installedPath))
            {
                error = "Windows reported profile installation without placing the file in the color store.";
                return false;
            }

            byte[] stagedHash = SHA256.HashData(File.ReadAllBytes(stagedPath));
            byte[] installedHash = SHA256.HashData(File.ReadAllBytes(installedPath));
            if (!stagedHash.SequenceEqual(installedHash))
            {
                error = "A different profile already exists under the requested filename.";
                return false;
            }

            error = null;
            return true;
        }
    }

    internal sealed class WindowsAdvancedColorProfilePlatform : IAdvancedColorProfilePlatform
    {
        public string ColorStoreDirectory
        {
            get
            {
                uint bytes = 0;
                Wcs.GetColorDirectory(null, null, ref bytes);
                if (bytes > 0)
                {
                    var buffer = new StringBuilder((int)(bytes / 2) + 1);
                    if (Wcs.GetColorDirectory(null, buffer, ref bytes))
                        return buffer.ToString();
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "spool", "drivers", "color");
            }
        }

        public bool TryResolveDisplay(MonitorInfo monitor, out AdvancedColorDisplayIdentity identity)
        {
            if (DisplayConfig.TryGetPathForGdiName(monitor.DeviceName,
                    out var freshAdapter, out uint freshSource, out _))
            {
                identity = new AdvancedColorDisplayIdentity(freshAdapter, freshSource);
                return true;
            }
            if (monitor.HasDisplayConfigIds)
            {
                identity = new AdvancedColorDisplayIdentity(
                    monitor.DisplayConfigAdapterId, monitor.DisplayConfigSourceId);
                return true;
            }
            identity = default;
            return false;
        }

        public bool TryGetUsePerUserProfiles(string monitorDevicePath, out bool enabled) =>
            Wcs.WcsGetUsePerUserProfiles(monitorDevicePath, Wcs.CLASS_MONITOR, out enabled);

        public bool SetUsePerUserProfiles(string monitorDevicePath, bool enabled) =>
            Wcs.WcsSetUsePerUserProfiles(monitorDevicePath, Wcs.CLASS_MONITOR, enabled);

        public int GetSelectedScope(AdvancedColorDisplayIdentity identity,
            out Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope) =>
            Wcs.ColorProfileGetDisplayUserScope(identity.AdapterId, identity.SourceId, out scope);

        public int GetDisplayList(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope,
            AdvancedColorDisplayIdentity identity, out IReadOnlyList<string> profiles)
        {
            IntPtr list = IntPtr.Zero;
            try
            {
                int hr = Wcs.ColorProfileGetDisplayList(scope, identity.AdapterId, identity.SourceId,
                    out list, out uint count);
                if (hr != 0)
                {
                    profiles = Array.Empty<string>();
                    return hr;
                }

                var result = new List<string>((int)Math.Min(count, 4096));
                for (uint i = 0; i < count && i < 4096; i++)
                {
                    IntPtr namePtr = Marshal.ReadIntPtr(list, checked((int)i * IntPtr.Size));
                    string? name = Marshal.PtrToStringUni(namePtr);
                    if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
                }
                profiles = result;
                return 0;
            }
            finally
            {
                if (list != IntPtr.Zero) Wcs.LocalFree(list);
            }
        }

        public int GetDisplayDefault(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope,
            AdvancedColorDisplayIdentity identity, out string? profileName)
        {
            IntPtr name = IntPtr.Zero;
            try
            {
                int hr = Wcs.ColorProfileGetDisplayDefault(scope, identity.AdapterId, identity.SourceId,
                    Wcs.COLORPROFILETYPE.CPT_ICC,
                    Wcs.COLORPROFILESUBTYPE.CPST_EXTENDED_DISPLAY_COLOR_MODE, out name);
                profileName = hr == 0 && name != IntPtr.Zero ? Marshal.PtrToStringUni(name) : null;
                return hr;
            }
            finally
            {
                if (name != IntPtr.Zero) Wcs.LocalFree(name);
            }
        }

        public int AddDisplayAssociation(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, string profileName,
            AdvancedColorDisplayIdentity identity, bool setAsDefault) =>
            Wcs.ColorProfileAddDisplayAssociation(scope, profileName, identity.AdapterId,
                identity.SourceId, setAsDefault, associateAsAdvancedColor: true);

        public int SetDisplayDefault(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, string profileName,
            AdvancedColorDisplayIdentity identity) =>
            Wcs.ColorProfileSetDisplayDefaultAssociation(scope, profileName,
                Wcs.COLORPROFILETYPE.CPT_ICC,
                Wcs.COLORPROFILESUBTYPE.CPST_EXTENDED_DISPLAY_COLOR_MODE,
                identity.AdapterId, identity.SourceId);

        public int RemoveDisplayAssociation(Wcs.WCS_PROFILE_MANAGEMENT_SCOPE scope, string profileName,
            AdvancedColorDisplayIdentity identity) =>
            Wcs.ColorProfileRemoveDisplayAssociation(scope, profileName, identity.AdapterId,
                identity.SourceId, dissociateAdvancedColor: true);

        public bool InstallColorProfile(string stagedPath) => Wcs.InstallColorProfile(null, stagedPath);

        public bool UninstallColorProfile(string profileName, bool delete) =>
            Wcs.UninstallColorProfile(null, profileName, delete);
    }
}
