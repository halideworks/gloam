using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Applies the Windows immersive dark-mode title bar to a WPF window so the OS chrome
    /// matches the app's dark UI instead of rendering as a stock light bar.
    /// </summary>
    public static class WindowTheme
    {
        // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 10 2004+ / Windows 11; 19 on the
        // earlier 1809–1903 builds. We try the modern value first, then fall back.
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModePre20H1 = 19;
        private const int DwmwaDisallowPeek = 11;     // window won't trigger peek
        private const int DwmwaExcludedFromPeek = 12; // window stays opaque during Aero Peek

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>
        /// Enables the dark title bar for the given window. Safe to call before or after the
        /// HWND exists — if it isn't created yet, it hooks SourceInitialized and applies then.
        /// </summary>
        public static void UseDarkTitleBar(Window window)
        {
            if (window == null) return;

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                Apply(hwnd);
            }
            else
            {
                window.SourceInitialized += (_, _) =>
                    Apply(new WindowInteropHelper(window).Handle);
            }
        }

        private static void Apply(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int enabled = 1;
            // A non-zero HRESULT just means the attribute isn't supported on this build;
            // the fallback covers older builds, and either way it's purely cosmetic.
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModePre20H1, ref enabled, sizeof(int));
            }
        }

        /// <summary>
        /// Keeps a window opaque during Aero Peek (hovering a taskbar thumbnail or the
        /// Show-Desktop button) instead of letting the desktop show through. Essential for the
        /// calibration patch window — a peek mid-measurement would feed the probe the desktop,
        /// not the patch, and ruin the reading.
        /// </summary>
        public static void ExcludeFromPeek(Window window)
        {
            if (window == null) return;
            void Apply2(IntPtr hwnd)
            {
                if (hwnd == IntPtr.Zero) return;
                int on = 1;
                DwmSetWindowAttribute(hwnd, DwmwaExcludedFromPeek, ref on, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaDisallowPeek, ref on, sizeof(int));
            }
            var h = new WindowInteropHelper(window).Handle;
            if (h != IntPtr.Zero) Apply2(h);
            else window.SourceInitialized += (_, _) => Apply2(new WindowInteropHelper(window).Handle);
        }
    }
}
