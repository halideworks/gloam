using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Wraps a code-built window in the app's brutalist custom chrome: a hard white frame,
    /// a dark header with the brand mark + display-font title, and a close glyph. Dark-fixed
    /// (these are calibration utility windows, consistent with the measurement UI). Replaces
    /// the stock OS title bar that <see cref="DarkTitleBar"/> only darkened.
    /// </summary>
    public static class BrutalistChrome
    {
        private static readonly Brush Frame    = Brushes.White;
        private static readonly Brush Bg       = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
        private static readonly Brush HeaderBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        private static readonly Brush Dim      = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly Brush Accent   = new SolidColorBrush(Color.FromRgb(0xFF, 0x3C, 0x2F));

        public static void Apply(Window win, string title, UIElement content)
        {
            win.WindowStyle = WindowStyle.None;
            win.AllowsTransparency = true;
            win.Background = Brushes.Transparent;
            if (win.ResizeMode == ResizeMode.CanResize)
                win.ResizeMode = ResizeMode.CanResizeWithGrip;

            var display = Application.Current?.Resources["DisplayFont"] as FontFamily;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header / drag bar
            var header = new Border
            {
                Background = HeaderBg,
                BorderBrush = Frame,
                BorderThickness = new Thickness(0, 0, 0, 3),
            };
            header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) win.DragMove(); };

            var hg = new Grid { Margin = new Thickness(16, 0, 8, 0) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            brand.Children.Add(new BrandMark
            {
                Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center, GapBrush = HeaderBg,
            });
            brand.Children.Add(new TextBlock
            {
                Text = "GLOAM", FontFamily = display, FontWeight = FontWeights.ExtraBold,
                FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            });
            brand.Children.Add(new Border { Width = 1, Height = 14, Background = Dim, Opacity = 0.6, Margin = new Thickness(10, 0, 10, 0) });
            brand.Children.Add(new TextBlock
            {
                Text = title.ToUpperInvariant(), FontFamily = display, FontWeight = FontWeights.Bold,
                FontSize = 11, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center,
            });
            hg.Children.Add(brand);

            var close = new TextBlock
            {
                Text = "✕", Foreground = Dim, FontSize = 15, Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(8, 4, 8, 4),
            };
            close.MouseLeftButtonUp += (_, _) => win.Close();
            close.MouseEnter += (_, _) => close.Foreground = Accent;
            close.MouseLeave += (_, _) => close.Foreground = Dim;
            hg.Children.Add(close);

            header.Child = hg;
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            if (content is FrameworkElement fe) Grid.SetRow(fe, 1);
            grid.Children.Add(content);

            win.Content = new Border
            {
                Background = Bg,
                BorderBrush = Frame,
                BorderThickness = new Thickness(2),
                Child = grid,
            };
        }
    }
}
