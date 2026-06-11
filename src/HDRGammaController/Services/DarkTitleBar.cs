using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Turns the native window caption dark via DWM. Only needed for windows that
    /// keep the standard chrome (the main windows draw their own title bars).
    /// </summary>
    public static class DarkTitleBar
    {
        private const int DwmwaUseImmersiveDarkMode = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void Apply(Window window)
        {
            window.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                int enabled = 1;
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
            };
        }
    }
}
