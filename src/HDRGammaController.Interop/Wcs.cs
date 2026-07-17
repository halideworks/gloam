using System;
using System.Runtime.InteropServices;

namespace HDRGammaController.Interop
{
    public static class Wcs
    {
        public enum WCS_PROFILE_MANAGEMENT_SCOPE
        {
            WCS_PROFILE_MANAGEMENT_SCOPE_SYSTEM_WIDE,
            WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER
        }

        public enum COLORPROFILETYPE
        {
            CPT_ICC = 0
        }

        public enum COLORPROFILESUBTYPE
        {
            CPST_PERCEPTUAL = 0,
            CPST_RELATIVE_COLORIMETRIC = 1,
            CPST_SATURATION = 2,
            CPST_ABSOLUTE_COLORIMETRIC = 3,
            CPST_NONE = 4,
            CPST_RGB_WORKING_SPACE = 5,
            CPST_CUSTOM_WORKING_SPACE = 6,
            CPST_STANDARD_DISPLAY_COLOR_MODE = 7,
            CPST_EXTENDED_DISPLAY_COLOR_MODE = 8
        }

        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool WcsSetDefaultColorProfile(
            WCS_PROFILE_MANAGEMENT_SCOPE scope,
            string? pDeviceName,
            int cpt, // COLORPROFILETYPE
            int cpst, // COLORPROFILESUBTYPE
            uint dwProfileID,
            string pProfileName
        );

        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool WcsGetDefaultColorProfile(
            WCS_PROFILE_MANAGEMENT_SCOPE scope,
            string? pDeviceName,
            int cpt,
            int cpst,
            uint dwProfileID,
            int cbProfileName,
            [Out] char[] pProfileName
        );
        
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool WcsGetDefaultColorProfileSize(
            WCS_PROFILE_MANAGEMENT_SCOPE scope,
            string? pDeviceName,
            int cpt,
            int cpst,
            uint dwProfileID,
            out int pcbProfileName
        );
        
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool InstallColorProfile(
             string? pMachineName,
             string pProfileName
        );

        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool UninstallColorProfile(
             string? pMachineName,
             string pProfileName,
             bool bDelete
        );
        
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool AssociateColorProfileWithDevice(
            string? pMachineName,
            string pProfileFileName,
            string? pDeviceName
        );
        
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool DisassociateColorProfileFromDevice(
            string? pMachineName,
            string pProfileFileName,
            string? pDeviceName
        );
        
        /// <summary>
        /// Associates an installed profile with a display via DisplayConfig identifiers.
        /// With <paramref name="associateAsAdvancedColor"/> the profile lands in the
        /// ADVANCED COLOR association list (registry ICMProfileAC) — the list Windows
        /// actually consults while the display runs in HDR / Advanced Color. The classic
        /// AssociateColorProfileWithDevice API only touches the SDR list. Win10 20348+.
        /// Returns an HRESULT (0 = S_OK).
        /// </summary>
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        public static extern int ColorProfileAddDisplayAssociation(
            WCS_PROFILE_MANAGEMENT_SCOPE scope,
            string profileName,
            Dxgi.LUID targetAdapterID,
            uint sourceID,
            [MarshalAs(UnmanagedType.Bool)] bool setAsDefault,
            [MarshalAs(UnmanagedType.Bool)] bool associateAsAdvancedColor);

        /// <summary>Removes a display association added via the API above. HRESULT.</summary>
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        public static extern int ColorProfileRemoveDisplayAssociation(
            WCS_PROFILE_MANAGEMENT_SCOPE scope,
            string profileName,
            Dxgi.LUID targetAdapterID,
            uint sourceID,
            [MarshalAs(UnmanagedType.Bool)] bool dissociateAdvancedColor);

        /// <summary>Explicitly selects a display profile as the default for one color mode.</summary>
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        public static extern int ColorProfileSetDisplayDefaultAssociation(
            WCS_PROFILE_MANAGEMENT_SCOPE scope,
            string profileName,
            COLORPROFILETYPE profileType,
            COLORPROFILESUBTYPE profileSubType,
            Dxgi.LUID targetAdapterID,
            uint sourceID);

        /// <summary>
        /// Returns an array of LPWSTR pointers allocated by Windows. The outer allocation
        /// must be released with LocalFree after copying the strings.
        /// </summary>
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        public static extern int ColorProfileGetDisplayList(
            WCS_PROFILE_MANAGEMENT_SCOPE scope,
            Dxgi.LUID targetAdapterID,
            uint sourceID,
            out IntPtr profileList,
            out uint profileCount);

        /// <summary>Returns one LocalAlloc-backed LPWSTR profile filename.</summary>
        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        public static extern int ColorProfileGetDisplayDefault(
            WCS_PROFILE_MANAGEMENT_SCOPE scope,
            Dxgi.LUID targetAdapterID,
            uint sourceID,
            COLORPROFILETYPE profileType,
            COLORPROFILESUBTYPE profileSubType,
            out IntPtr profileName);

        [DllImport("mscms.dll")]
        public static extern int ColorProfileGetDisplayUserScope(
            Dxgi.LUID targetAdapterID,
            uint sourceID,
            out WCS_PROFILE_MANAGEMENT_SCOPE scope);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool WcsGetUsePerUserProfiles(
            string deviceName,
            uint deviceClass,
            [MarshalAs(UnmanagedType.Bool)] out bool usePerUserProfiles);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool WcsSetUsePerUserProfiles(
            string deviceName,
            uint deviceClass,
            [MarshalAs(UnmanagedType.Bool)] bool usePerUserProfiles);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>
        /// Resolves the Windows color store directory (normally
        /// %SystemRoot%\System32\spool\drivers\color) so profile filenames from the
        /// association lists can be opened for read-only inspection. pdwSize is in BYTES.
        /// </summary>
        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetColorDirectory(
            string? pMachineName,
            System.Text.StringBuilder? pBuffer,
            ref uint pdwSize);

        public const int CPT_ICC = 0;
        public const int CPST_PERCEPTUAL = 0;
        public const int CPST_RELATIVE_COLORIMETRIC = 1;
        public const int CPST_SATURATION = 2;
        public const int CPST_ABSOLUTE_COLORIMETRIC = 3;

        // ICC device class signature 'mntr'. Multi-character C constants are packed
        // most-significant byte first by the Windows SDK/MSVC headers.
        public const uint CLASS_MONITOR = 0x6D6E7472;
    }
}
