using System;
using System.Runtime.InteropServices;

namespace HDRGammaController.Interop
{
    /// <summary>
    /// Minimal QueryDisplayConfig interop: enough to map a GDI device name
    /// ("\\.\DISPLAY2") to its DisplayConfig adapter LUID + source id (required by the
    /// Advanced Color profile-association APIs) and to read the real SDR white level of an
    /// HDR display (the brightness Windows renders SDR content at — needed to place
    /// SDR-measured patches on the PQ signal axis).
    /// </summary>
    public static class DisplayConfig
    {
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public Dxgi.LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public Dxgi.LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            // DISPLAYCONFIG_RATIONAL — MUST be two uint32 fields, not a long: a long forces
            // 8-byte alignment, inflating the struct from 48 to 56 bytes, and
            // QueryDisplayConfig rejects the whole call with ERROR_INVALID_PARAMETER (87).
            public uint refreshRateNumerator;
            public uint refreshRateDenominator;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // DISPLAYCONFIG_MODE_INFO is a 64-byte tagged union; we never read modes, but
        // QueryDisplayConfig requires a correctly-sized buffer for them.
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO { }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public int size;
            public Dxgi.LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            // Bitfield union: bit0 advancedColorSupported, bit1 advancedColorEnabled,
            // bit2 wideColorEnforced, bit3 advancedColorForceDisabled.
            public uint value;
            public uint colorEncoding;     // DISPLAYCONFIG_COLOR_ENCODING
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel; // in units of 1/1000 of 80 nits: nits = value / 1000 * 80
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags,
            ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        /// <summary>
        /// Finds the active DisplayConfig path whose GDI source name matches
        /// <paramref name="gdiDeviceName"/> (e.g. "\\.\DISPLAY2").
        /// </summary>
        public static bool TryGetPathForGdiName(
            string gdiDeviceName, out Dxgi.LUID adapterId, out uint sourceId, out uint targetId)
        {
            adapterId = default; sourceId = 0; targetId = 0;
            if (string.IsNullOrEmpty(gdiDeviceName)) return false;

            // The display set can change between sizing and querying — retry on
            // ERROR_INSUFFICIENT_BUFFER (122) instead of failing the resolution.
            DISPLAYCONFIG_PATH_INFO[] paths;
            uint pathCount;
            int attempts = 0;
            while (true)
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out uint modeCount) != 0)
                    return false;
                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                int err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (err == 0) break;
                if (err != 122 /* ERROR_INSUFFICIENT_BUFFER */ || ++attempts >= 3) return false;
            }

            for (int i = 0; i < pathCount; i++)
            {
                var req = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref req) != 0) continue;
                if (!string.Equals(req.viewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;

                adapterId = paths[i].sourceInfo.adapterId;
                sourceId = paths[i].sourceInfo.id;
                targetId = paths[i].targetInfo.id;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Reads the display's Advanced Color state (DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
        /// queried per TARGET like the SDR white level). On Windows 11 22H2+
        /// advancedColorEnabled is also set when SDR "Auto Color Management" (ACM) is on —
        /// callers combine it with the display's HDR state to tell the two apart.
        /// Returns false when the state cannot be determined.
        /// </summary>
        public static bool TryGetAdvancedColorInfo(
            string gdiDeviceName,
            out bool advancedColorSupported,
            out bool advancedColorEnabled,
            out bool advancedColorForceDisabled)
        {
            advancedColorSupported = advancedColorEnabled = advancedColorForceDisabled = false;
            if (!TryGetPathForGdiName(gdiDeviceName, out var adapterId, out _, out uint targetId))
                return false;
            var req = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                    size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                    adapterId = adapterId,
                    id = targetId,
                }
            };
            if (DisplayConfigGetDeviceInfo(ref req) != 0) return false;
            advancedColorSupported = (req.value & 0x1) != 0;
            advancedColorEnabled = (req.value & 0x2) != 0;
            advancedColorForceDisabled = (req.value & 0x8) != 0;
            return true;
        }

        /// <summary>
        /// The nit level Windows renders SDR (non-HDR) content at on an HDR display — the
        /// "SDR content brightness" slider. Returns null if unavailable (e.g. SDR mode).
        /// </summary>
        public static double? TryGetSdrWhiteLevelNits(string gdiDeviceName)
        {
            if (!TryGetPathForGdiName(gdiDeviceName, out var adapterId, out _, out uint targetId))
                return null;
            var req = new DISPLAYCONFIG_SDR_WHITE_LEVEL
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
                    size = Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                    adapterId = adapterId,
                    id = targetId, // SDR white level is queried per TARGET
                }
            };
            if (DisplayConfigGetDeviceInfo(ref req) != 0) return null;
            if (req.SDRWhiteLevel == 0) return null;
            return req.SDRWhiteLevel / 1000.0 * 80.0;
        }
    }
}
