using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Wraps a code-built window in the app's custom chrome: a shared app frame,
    /// a themed header with the brand mark + display-font title, a light/dark toggle,
    /// and a close glyph. The chrome follows the app-wide light/dark swap
    /// (<see cref="BrutalistTheme"/>): every brush is sourced from the current Theme*
    /// tokens via {DynamicResource}-equivalent resource references, so it re-resolves
    /// when the palette is swapped. Replaces the stock OS title bar that
    /// <see cref="DarkTitleBar"/> only darkened.
    /// </summary>
    public static class BrutalistChrome
    {
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

            // Header / drag bar. Surface + bottom border follow the theme swap live.
            var header = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
            header.SetResourceReference(Border.BackgroundProperty, "ThemeSurface");
            header.SetResourceReference(Border.BorderBrushProperty, "ThemeBorder");
            header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) win.DragMove(); };

            var hg = new Grid { Margin = new Thickness(16, 0, 8, 0) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var mark = new BrandMark
            {
                Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            // The disc "gap" reads as a hole in the header, so match the header surface.
            mark.SetResourceReference(BrandMark.GapBrushProperty, "ThemeSurface");
            brand.Children.Add(mark);

            var brandText = new TextBlock
            {
                Text = "GLOAM", FontFamily = display, FontWeight = FontWeights.ExtraBold,
                FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            };
            brandText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeText");
            brand.Children.Add(brandText);

            var separator = new Border { Width = 1, Height = 14, Opacity = 0.6, Margin = new Thickness(10, 0, 10, 0) };
            separator.SetResourceReference(Border.BackgroundProperty, "ThemeTextDim");
            brand.Children.Add(separator);

            var titleText = new TextBlock
            {
                Text = title.ToUpperInvariant(), FontFamily = display, FontWeight = FontWeights.Bold,
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextDim");
            brand.Children.Add(titleText);
            hg.Children.Add(brand);

            // Window controls: light/dark toggle then close, right-aligned. Mirrors the
            // XAML pattern-A windows (Dashboard/Settings) that pair a ◐/◑ toggle with ✕.
            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var toggle = MakeGlyph(BrutalistTheme.IsDark ? "◐" : "◑", 15);
            toggle.ToolTip = "Toggle light / dark";
            // Down must not bubble to the header DragMove(): DragMove captures the mouse
            // for a modal move loop and swallows the Up, making the glyph inert.
            toggle.MouseLeftButtonDown += (_, e) => e.Handled = true;
            toggle.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                BrutalistTheme.Toggle();
                toggle.Text = BrutalistTheme.IsDark ? "◐" : "◑";
            };
            controls.Children.Add(toggle);

            var close = MakeGlyph("✕", 15);
            // Same DragMove-swallow fix as the toggle; preserves the committed behaviour.
            close.MouseLeftButtonDown += (_, e) => e.Handled = true;
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; win.Close(); };
            controls.Children.Add(close);
            hg.Children.Add(controls);

            header.Child = hg;
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            if (content is FrameworkElement fe) Grid.SetRow(fe, 1);
            grid.Children.Add(content);

            var frame = new Border { BorderThickness = new Thickness(1), Child = grid };
            frame.SetResourceReference(Border.BackgroundProperty, "ThemeBg");
            frame.SetResourceReference(Border.BorderBrushProperty, "ThemeWindowFrame");
            win.Content = frame;

            // The theme swap re-resolves every SetResourceReference above, so the chrome
            // recolors on its own. We still subscribe to keep the toggle glyph in sync when
            // some OTHER window (or the tray) flips the theme, and unsubscribe on close to
            // avoid leaking the window through the static event.
            void OnThemeChanged() => toggle.Text = BrutalistTheme.IsDark ? "◐" : "◑";
            BrutalistTheme.Changed += OnThemeChanged;
            win.Closed += (_, _) => BrutalistTheme.Changed -= OnThemeChanged;
        }

        /// <summary>An icon glyph that reads as dim and turns accent on hover, following the theme.</summary>
        private static TextBlock MakeGlyph(string text, double fontSize)
        {
            var glyph = new TextBlock
            {
                Text = text, FontSize = fontSize, Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 4, 8, 4),
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextDim");
            glyph.MouseEnter += (_, _) => glyph.SetResourceReference(TextBlock.ForegroundProperty, "ThemeAccent");
            glyph.MouseLeave += (_, _) => glyph.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextDim");
            return glyph;
        }
    }
}
