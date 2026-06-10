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

        public const int CPT_ICC = 0;
        public const int CPST_PERCEPTUAL = 0;
        public const int CPST_RELATIVE_COLORIMETRIC = 1;
        public const int CPST_SATURATION = 2;
        public const int CPST_ABSOLUTE_COLORIMETRIC = 3;
        
        // Advanced Color specific subtype for SDR-in-HDR
        // This is not officially constant, usually 0 or implicit
    }
}
