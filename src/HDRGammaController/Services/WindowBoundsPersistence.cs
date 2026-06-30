using System;
using System.Windows;
using HDRGammaController.Core;

namespace HDRGammaController.Services
{
    public static class WindowBoundsPersistence
    {
        private const double RecoveryVisibleEdge = 80;

        public static void Attach(Window window, SettingsManager? settingsManager, string key, Func<bool>? shouldSave = null)
        {
            if (settingsManager == null || string.IsNullOrWhiteSpace(key)) return;

            window.SourceInitialized += (_, _) => Restore(window, settingsManager, key);
            window.Closing += (_, _) =>
            {
                if (shouldSave?.Invoke() == false) return;
                Save(window, settingsManager, key);
            };
        }

        public static void CopyBounds(Window target, Window source)
            => Apply(target, Capture(source));

        private static void Restore(Window window, SettingsManager settingsManager, string key)
        {
            var saved = settingsManager.GetWindowBounds(key);
            if (saved == null) return;
            Apply(window, saved);
        }

        private static void Apply(Window window, WindowBoundsData saved)
        {
            if (!IsFinite(saved.Left) || !IsFinite(saved.Top) ||
                !IsFinite(saved.Width) || !IsFinite(saved.Height) ||
                saved.Width <= 0 || saved.Height <= 0)
            {
                return;
            }

            var desktop = VirtualDesktop;
            if (desktop.Width <= 0 || desktop.Height <= 0) return;

            double minWidth = Math.Max(window.MinWidth, 480);
            double minHeight = Math.Max(window.MinHeight, 360);
            double width = Clamp(saved.Width, minWidth, desktop.Width);
            double height = Clamp(saved.Height, minHeight, desktop.Height);

            double left = saved.Left;
            double top = saved.Top;

            bool mostlyOffscreen =
                left > desktop.Right - RecoveryVisibleEdge ||
                top > desktop.Bottom - RecoveryVisibleEdge ||
                left + width < desktop.Left + RecoveryVisibleEdge ||
                top + height < desktop.Top + RecoveryVisibleEdge;

            if (mostlyOffscreen)
            {
                left = desktop.Left + Math.Max(0, (desktop.Width - width) / 2);
                top = desktop.Top + Math.Max(0, (desktop.Height - height) / 2);
            }
            else
            {
                left = Clamp(left, desktop.Left, Math.Max(desktop.Left, desktop.Right - RecoveryVisibleEdge));
                top = Clamp(top, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - RecoveryVisibleEdge));
            }

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Width = width;
            window.Height = height;
            window.Left = left;
            window.Top = top;
        }

        private static WindowBoundsData Capture(Window window)
        {
            Rect bounds = window.WindowState == WindowState.Normal
                ? new Rect(
                    window.Left,
                    window.Top,
                    window.ActualWidth > 0 ? window.ActualWidth : window.Width,
                    window.ActualHeight > 0 ? window.ActualHeight : window.Height)
                : window.RestoreBounds;

            return new WindowBoundsData
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height
            };
        }

        private static void Save(Window window, SettingsManager settingsManager, string key)
        {
            var bounds = Capture(window);
            if (!IsFinite(bounds.Left) || !IsFinite(bounds.Top) ||
                !IsFinite(bounds.Width) || !IsFinite(bounds.Height) ||
                bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            settingsManager.SetWindowBounds(key, bounds);
        }

        private static Rect VirtualDesktop => new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        private static double Clamp(double value, double min, double max)
            => Math.Max(min, Math.Min(max, value));

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
