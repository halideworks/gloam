using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Wraps a code-built window in the app's custom chrome: a shared app frame,
    /// a dark header with the brand mark + display-font title, and a close glyph. Dark-fixed
    /// (these are calibration utility windows, consistent with the measurement UI). Replaces
    /// the stock OS title bar that <see cref="DarkTitleBar"/> only darkened.
    /// </summary>
    public static class BrutalistChrome
    {
        private static readonly Brush Frame    = new SolidColorBrush(Color.FromRgb(0x36, 0x42, 0x52));
        private static readonly Brush Bg       = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16));
        private static readonly Brush HeaderBg = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x23));
        private static readonly Brush Dim      = new SolidColorBrush(Color.FromRgb(0xA8, 0xB0, 0xBC));
        private static readonly Brush Accent   = new SolidColorBrush(Color.FromRgb(0xE3, 0x5F, 0x52));

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
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x55, 0x67)),
                BorderThickness = new Thickness(0, 0, 0, 1),
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
                FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFA)), VerticalAlignment = VerticalAlignment.Center,
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
            // Handle the Down event so it never bubbles to the header's DragMove().
            // DragMove() captures the mouse for a modal move loop, which would swallow
            // the Up event and make the close glyph inert.
            close.MouseLeftButtonDown += (_, e) => e.Handled = true;
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; win.Close(); };
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
                BorderThickness = new Thickness(1),
                Child = grid,
            };
        }
    }
}
