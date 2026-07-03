using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HDRGammaController
{
    public enum WhiteToolAction
    {
        None,
        ReanchorWhite,
        VisualTrim,
        ValidateHdrRenderer
    }

    /// <summary>Styled chooser for calibration report white tools.</summary>
    public sealed class WhiteToolsDialog : Window
    {
        public WhiteToolAction SelectedAction { get; private set; } = WhiteToolAction.None;

        public WhiteToolsDialog(bool canMeasure, bool canTrim, bool canValidateHdr)
        {
            Title = "White Tools";
            Width = 560;
            Height = 450;
            MinWidth = 520;
            MinHeight = 430;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            this.SetResourceReference(BackgroundProperty, "ThemeBg");
            this.SetResourceReference(ForegroundProperty, "ThemeText");
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Gloam;component/Themes/DarkControls.xaml", UriKind.Absolute),
            });

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var intro = new TextBlock
            {
                Text = "Choose a white-point operation for this freshly generated profile.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };
            intro.SetResourceReference(ForegroundProperty, "ThemeTextDim");
            root.Children.Add(intro);

            var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            Grid.SetRow(stack, 1);
            root.Children.Add(stack);

            stack.Children.Add(MakeOption(
                "Re-anchor white",
                "Measure only white again, rebuild the profile around the fresh reading, then reinstall and verify. Best for panel warm-up drift.",
                "Measure",
                canMeasure,
                WhiteToolAction.ReanchorWhite));
            stack.Children.Add(MakeOption(
                "Visual white trim",
                "Nudge target white by eye against a reference display. The offset is baked into the installed profile.",
                "Trim",
                canTrim,
                WhiteToolAction.VisualTrim));
            stack.Children.Add(MakeOption(
                "Validate HDR patch renderer",
                "Run the FP16 scRGB renderer probe check before trusting HDR-range measurements on this system.",
                "Validate",
                canValidateHdr,
                WhiteToolAction.ValidateHdrRenderer));

            var close = new Button
            {
                Content = "Cancel",
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 92,
                Margin = new Thickness(0, 14, 0, 0)
            };
            close.Click += (_, _) => { DialogResult = false; Close(); };
            Grid.SetRow(close, 2);
            root.Children.Add(close);

            Services.BrutalistChrome.Apply(this, "White Tools", root);
        }

        private UIElement MakeOption(string title, string body, string action, bool enabled, WhiteToolAction result)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var copy = new StackPanel { Margin = new Thickness(12, 10, 16, 10) };
            var optionTitle = new TextBlock
            {
                Text = title,
                FontFamily = Application.Current?.Resources["DisplayFont"] as FontFamily,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
            };
            optionTitle.SetResourceReference(ForegroundProperty, "ThemeText");
            copy.Children.Add(optionTitle);
            var optionBody = new TextBlock
            {
                Text = body,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            optionBody.SetResourceReference(ForegroundProperty, "ThemeTextDim");
            copy.Children.Add(optionBody);

            var button = new Button
            {
                Content = action,
                IsEnabled = enabled,
                MinWidth = 94,
                Margin = new Thickness(0, 10, 12, 10),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = enabled ? null : "Unavailable for this report state."
            };
            button.SetResourceReference(BackgroundProperty, "ThemeAccent");
            button.SetResourceReference(ForegroundProperty, "ThemeOnAccent");
            button.SetResourceReference(Control.BorderBrushProperty, "ThemeAccent");
            button.Click += (_, _) =>
            {
                SelectedAction = result;
                DialogResult = true;
                Close();
            };

            grid.Children.Add(copy);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                Child = grid,
                Opacity = enabled ? 1.0 : 0.58
            };
            card.SetResourceReference(Border.BackgroundProperty, "ThemeSurface");
            card.SetResourceReference(Border.BorderBrushProperty, "ThemeBorder");
            return card;
        }
    }
}
