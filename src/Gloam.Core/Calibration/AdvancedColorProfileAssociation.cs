using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Gloam.Interop;

namespace Gloam.Core.Calibration
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
    /// and reads the default back before reporting success.
    ///
    /// LIST MEMBERSHIP IS NOT A VALID TEST (Windows 11 25H2, build 26200.8973).
    /// ColorProfileGetDisplayList returns S_OK with a count of ZERO for both scopes even
    /// when the association is live: measured directly against a real display, the profile
    /// was written to ICMProfileAC in the registry, ColorProfileGetDisplayDefault read it
    /// straight back, and the list was still empty. Requiring the profile to appear in that
    /// list made every HDR install fail verification, roll back a perfectly good
    /// association, and report "Windows did not retain the requested Advanced Color
    /// profile" — which is how a working calibration became uninstallable after the 23H2
    /// to 25H2 update. The DEFAULT association in the scope Windows consults is the
    /// authoritative signal: if our profile is that default, it is associated by
    /// definition. The list is still read for diagnostics, never to fail an operation.
    /// </summary>
    internal static class AdvancedColorProfileAssociation
    {
        /// <summary>HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS).</summary>
        private const int HResultAlreadyExists = unchecked((int)0x800700B7);

        /// <summary>
        /// "There was nothing there" HRESULTs: FILE_NOT_FOUND, PATH_NOT_FOUND and
        /// NOT_FOUND. Removing an association that does not exist is a success, not a fault.
        /// </summary>
        private static bool IsNotFound(int hr) =>
            hr == unchecked((int)0x80070002) ||
            hr == unchecked((int)0x80070003) ||
            hr == unchecked((int)0x80070490);

        internal static IAdvancedColorProfilePlatform Platform { get; set; } = new WindowsAdvancedColorProfilePlatform();

        internal sealed record ActivationReceipt(
            AdvancedColorDisplayIdentity Identity,
            bool PerUserWasEnabled,
            Wcs.WCS_PROFILE_MANAGEMENT_SCOPE PriorSelectedScope,
            string? PriorCurrentUserDefault,
            IReadOnlyList<string> PriorCurrentUserProfiles,
            string ActivatedProfile)
        {
            /// <summary>
            /// True when THIS activation created the association (as opposed to finding it
            /// already present). Rollback removes only what it added; the prior-profiles
            /// list can no longer answer that question on 25H2.
            /// </summary>
            internal bool AddedAssociation { get; init; }
        }

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

            hr = platform.GetDisplayDefault(currentScope, identity, out string? activeDefault);
            if (hr != 0)
            {
                error = null;
                return true;
            }

            // Default only: see the 25H2 note on this class. Being the consulted scope's
            // Extended Display Color Mode default IS the association.
            isActive = string.Equals(activeDefault, profileName, StringComparison.OrdinalIgnoreCase);
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

            // Re-activation commonly finds the profile already associated. The list can no
            // longer tell us (see the 25H2 note), so always attempt the add and treat
            // ERROR_ALREADY_EXISTS as success — the correct follow-up either way is to
            // select it as the HDR default.
            hr = platform.AddDisplayAssociation(currentScope, profileName, identity, setAsDefault: true);
            bool addedByUs = hr == 0;
            if (hr == HResultAlreadyExists)
                hr = 0;
            if (hr == 0)
                hr = platform.SetDisplayDefault(currentScope, profileName, identity);
            if (hr != 0)
            {
                pending = pending with { AddedAssociation = addedByUs };
                TryRollback(monitor, pending, platform, out _);
                error = $"Windows refused the Advanced Color default (HRESULT 0x{hr:X8}).";
                return false;
            }
            pending = pending with { AddedAssociation = addedByUs };

            if (platform.GetDisplayDefault(currentScope, identity, out string? installedDefault) != 0 ||
                !string.Equals(installedDefault, profileName, StringComparison.OrdinalIgnoreCase))
            {
                TryRollback(monitor, pending, platform, out _);
                error = "Windows did not retain the requested Advanced Color profile as the active HDR default.";
                return false;
            }

            // Diagnostics only. An empty list alongside a correct default is the expected
            // 25H2 shape, and must never fail the install.
            if (platform.GetDisplayList(currentScope, identity, out var installedProfiles) == 0 &&
                !installedProfiles.Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                Log.Info(
                    $"AdvancedColorProfileAssociation: ColorProfileGetDisplayList reported {installedProfiles.Count} " +
                    $"profile(s) and did not include '{profileName}', but it IS the active Extended Display Color Mode " +
                    "default. Trusting the default (expected on Windows 11 25H2).");
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

            // Remove only an association this activation actually created. The prior-list
            // snapshot cannot be used for this decision any more (see the 25H2 note): it
            // comes back empty, which would make rollback tear down a pre-existing
            // association that was never ours to touch.
            if (receipt.AddedAssociation)
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

            // No list precheck: it comes back empty on 25H2 (see the class note), which
            // turned every retire into a silent no-op and left stale associations behind.
            // Ask Windows to remove it and treat "there was nothing to remove" as success.
            int hr = platform.RemoveDisplayAssociation(scope, profileName, identity);
            if (hr is not 0 and not HResultAlreadyExists && !IsNotFound(hr))
            {
                error = $"ColorProfileRemoveDisplayAssociation failed (HRESULT 0x{hr:X8}).";
                return false;
            }

            // Confirm via the default, not the list: a retired profile must no longer be the
            // Extended Display Color Mode default. An empty list proves nothing either way.
            if (platform.GetDisplayDefault(scope, identity, out string? stillDefault) == 0 &&
                string.Equals(stillDefault, profileName, StringComparison.OrdinalIgnoreCase))
            {
                error = "Windows still reports the retired profile as the Advanced Color default.";
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
