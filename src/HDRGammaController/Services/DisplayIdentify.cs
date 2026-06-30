using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        /// Briefly shows the monitor's index + name on that physical display.
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

            var resources = Application.Current?.Resources;
            var display = resources?["DisplayFont"] as FontFamily;
            var body = resources?["BodyFont"] as FontFamily;
            var surface = BrushFromResource("ThemeSurface", Color.FromRgb(0x17, 0x1C, 0x23), 0xF2);
            var border = BrushFromResource("ThemeBorder", Color.FromRgb(0x46, 0x55, 0x67));
            var accent = BrushFromResource("ThemeAccent", Color.FromRgb(0xE3, 0x5F, 0x52));
            var text = BrushFromResource("ThemeText", Color.FromRgb(0xF4, 0xF7, 0xFA));
            var dim = BrushFromResource("ThemeTextDim", Color.FromRgb(0xA8, 0xB0, 0xBC));

            var card = new Border
            {
                Background = surface,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(24, 18, 28, 20),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(36),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 18,
                    ShadowDepth = 4,
                    Opacity = 0.35
                }
            };
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = index.ToString(),
                FontFamily = display,
                FontSize = 92,
                FontWeight = FontWeights.ExtraBold,
                Foreground = accent,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new Border
            {
                Width = 1,
                Height = 58,
                Background = border,
                Margin = new Thickness(18, 0, 18, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock
            {
                Text = "Calibration target",
                FontFamily = display,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
            });
            labels.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(monitor.FriendlyName) ? "Display" : monitor.FriendlyName,
                FontFamily = body,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = text,
                MaxWidth = Math.Max(240, Math.Min(520, w - 120)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0),
            });
            labels.Children.Add(new TextBlock
            {
                Text = "This display will be measured",
                FontFamily = body,
                FontSize = 12,
                Foreground = dim,
                Margin = new Thickness(0, 2, 0, 0),
            });
            row.Children.Add(labels);
            stack.Children.Add(row);
            stack.Children.Add(new TextBlock
            {
                Text = "If this is the wrong display, choose another monitor",
                FontFamily = body,
                FontSize = 11,
                Foreground = dim,
                Margin = new Thickness(0, 12, 0, 0),
            });
            card.Child = stack;
            window.Content = card;

            window.Show();

            var timer = new DispatcherTimer { Interval = duration ?? TimeSpan.FromSeconds(2.8) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(450))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                fade.Completed += (_, _) =>
                {
                    try { window.Close(); } catch { }
                };
                window.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            timer.Start();
        }

        private static SolidColorBrush BrushFromResource(string key, Color fallback, byte? alpha = null)
        {
            var color = (Application.Current?.Resources[key] as SolidColorBrush)?.Color ?? fallback;
            if (alpha.HasValue) color.A = alpha.Value;
            return new SolidColorBrush(color);
        }
    }
}
