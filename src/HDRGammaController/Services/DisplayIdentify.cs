using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HDRGammaController.Core;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Flashes a large "this is the display" overlay on a specific monitor, like the
    /// Windows "Identify displays" feature. Used in the calibration flow so the user knows
    /// exactly which physical screen is about to be calibrated — essential when two
    /// identical displays are attached.
    /// </summary>
    public static class DisplayIdentify
    {
        /// <summary>
        /// Briefly shows the monitor's index + name centered on that physical display.
        /// </summary>
        public static void Flash(MonitorInfo monitor, int index, TimeSpan? duration = null)
        {
            if (monitor == null) return;
            var bounds = monitor.MonitorBounds;
            double w = bounds.Right - bounds.Left;
            double h = bounds.Bottom - bounds.Top;
            if (w <= 0 || h <= 0) return;

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                IsHitTestVisible = false,
                // Physical-pixel bounds → device-independent units. Good enough to land the
                // overlay squarely on the right monitor; exact DPI scaling isn't critical here.
                Left = bounds.Left,
                Top = bounds.Top,
                Width = w,
                Height = h,
            };

            var display = Application.Current?.Resources["DisplayFont"] as FontFamily;

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x00, 0x00, 0x00)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3C, 0x2F)),
                BorderThickness = new Thickness(4),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(48, 36, 48, 36),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = index.ToString(),
                FontFamily = display,
                FontSize = 180,
                FontWeight = FontWeights.ExtraBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x3C, 0x2F)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(monitor.FriendlyName) ? "Display" : monitor.FriendlyName,
                FontFamily = display,
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            });
            stack.Children.Add(new TextBlock
            {
                Text = "calibration target",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            card.Child = stack;
            window.Content = card;

            window.Show();

            var timer = new DispatcherTimer { Interval = duration ?? TimeSpan.FromSeconds(2.0) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { window.Close(); } catch { }
            };
            timer.Start();
        }
    }
}
