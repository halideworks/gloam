using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HDRGammaController.Interop
{
    public static class User32
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;

            public void Initialize() => cb = Marshal.SizeOf(this);
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        public const int MONITOR_DEFAULTTONULL = 0;
        public const int MONITOR_DEFAULTTOPRIMARY = 1;
        public const int MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public Dxgi.RECT rcMonitor;
            public Dxgi.RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>
        /// Desktop bounds of a live HMONITOR. Lets callers match a current monitor
        /// handle against bounds captured at enumeration time, since HMONITOR values
        /// go stale across display-configuration changes.
        /// </summary>
        public static bool TryGetMonitorBounds(IntPtr hMonitor, out Dxgi.RECT bounds)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref mi))
            {
                bounds = mi.rcMonitor;
                return true;
            }
            bounds = default;
            return false;
        }
        
        public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
        public const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

        // WinEventHook
        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out Dxgi.RECT lpRect);

        public const uint WINEVENT_OUTOFCONTEXT = 0;
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B; // Optional
    }

    /// <summary>
    /// Safe wrappers for User32 P/Invoke calls with proper error handling.
    /// </summary>
    public static class User32Safe
    {
        /// <summary>
        /// Attempts to register a hotkey. Returns false if registration fails.
        /// </summary>
        public static bool TryRegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk, out int errorCode)
        {
            if (User32.RegisterHotKey(hWnd, id, fsModifiers, vk))
            {
                errorCode = 0;
                return true;
            }
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }

        /// <summary>
        /// Registers a hotkey or throws Win32Exception on failure.
        /// </summary>
        public static void RegisterHotKeyOrThrow(IntPtr hWnd, int id, uint fsModifiers, uint vk)
        {
            if (!User32.RegisterHotKey(hWnd, id, fsModifiers, vk))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Failed to register hotkey (id={id}, modifiers=0x{fsModifiers:X}, vk=0x{vk:X})");
            }
        }

        /// <summary>
        /// Gets the window rect or returns false on failure.
        /// </summary>
        public static bool TryGetWindowRect(IntPtr hWnd, out Dxgi.RECT rect, out int errorCode)
        {
            if (User32.GetWindowRect(hWnd, out rect))
            {
                errorCode = 0;
                return true;
            }
            errorCode = Marshal.GetLastWin32Error();
            rect = default;
            return false;
        }
    }
}
