using System;
using HDRGammaController.Core;
using Microsoft.Win32;

namespace HDRGammaController.Services
{
    public static class ThemeDetector
    {
        /// <summary>
        /// Returns true if Windows is set to dark mode for apps.
        /// </summary>
        public static bool IsDarkMode()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        var value = key.GetValue("AppsUseLightTheme");
                        if (value is int intValue)
                        {
                            // 0 = dark mode, 1 = light mode
                            return intValue == 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"ThemeDetector: Failed to read theme: {ex.Message}");
            }
            
            // Default to light mode if we can't detect
            return false;
        }
    }
}
