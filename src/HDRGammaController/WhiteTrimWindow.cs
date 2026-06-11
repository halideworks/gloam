using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HDRGammaController
{
    /// <summary>
    /// Visual white trim: a draggable mid-gray field the user holds next to their reference
    /// display while nudging the calibration target white point in CIE xy. Every nudge
    /// rebuilds and reinstalls the profile (the report window orchestrates that), so the
    /// whole screen updates live. Closes the metameric gap that instruments cannot — and
    /// bakes the result INTO the profile instead of leaving it on the GPU ramp.
    /// Arrow keys work too: ←/→ green↔magenta, ↑/↓ cooler↔warmer.
    /// </summary>
    public sealed class WhiteTrimWindow : Window
    {
        private const double Step = 0.0010; // CIE xy per nudge — ~0.5 ΔE, comfortably visible

        private readonly TextBlock _readout;
        private double _dx, _dy;

        /// <summary>Raised with the CUMULATIVE (Δx, Δy) after each nudge.</summary>
        public event Action<double, double>? TrimChanged;

        /// <summary>(Δx, Δy) at Done; null if cancelled.</summary>
        public (double Dx, double Dy)? Result { get; private set; }

        public WhiteTrimWindow()
        {
            Title = "Visual White Trim";
            Width = 720;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0xBC, 0xBC, 0xBC)); // ~50% gray field
            WindowStyle = WindowStyle.SingleBorderWindow;
            // The gray field is the measurement surface here (visually, against a reference
            // display); keep it opaque if a taskbar hover triggers Aero Peek mid-comparison.
            Services.WindowTheme.ExcludeFromPeek(this);

            _readout = new TextBlock
            {
                FontSize = 13,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
            };

            Button Make(string label, double dx, double dy)
            {
                var b = new Button
                {
                    Content = label,
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(4, 0, 4, 0),
                    Background = new SolidColorBrush(Color.FromRgb(0x2d, 0x2d, 0x2d)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                };
                b.Click += (_, _) => Nudge(dx, dy);
                return b;
            }

            var nudges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            nudges.Children.Add(Make("< Green", 0, +Step));
            nudges.Children.Add(Make("Magenta >", 0, -Step));
            nudges.Children.Add(new Border { Width = 16 });
            nudges.Children.Add(Make("Warmer", +Step, +Step * 0.35));
            nudges.Children.Add(Make("Cooler", -Step, -Step * 0.35));
            nudges.Children.Add(new Border { Width = 16 });
            nudges.Children.Add(Make("Reset", double.NaN, double.NaN));

            var done = new Button
            {
                Content = "Done — keep this white",
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(4, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x08, 0x91, 0xb2)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
            };
            done.Click += (_, _) => { Result = (_dx, _dy); DialogResult = true; Close(); };

            var cancel = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(4, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x3d, 0x3d, 0x3d)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
            };
            cancel.Click += (_, _) => { DialogResult = false; Close(); };

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(done);
            actions.Children.Add(cancel);

            var hint = new TextBlock
            {
                Text = "Compare this gray against your reference display, then nudge until they match. " +
                       "Each step rebuilds the profile live (~1 second). Arrow keys: Left/Right green/magenta, Up/Down cooler/warmer.",
                FontSize = 12,
                Foreground = Brushes.Black,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(24, 0, 24, 10),
            };

            var controls = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 22) };
            controls.Children.Add(hint);
            controls.Children.Add(_readout);
            controls.Children.Add(nudges);
            controls.Children.Add(actions);

            var root = new Grid();
            root.Children.Add(controls);
            Content = root;

            PreviewKeyDown += (_, e) =>
            {
                switch (e.Key)
                {
                    case Key.Left: Nudge(0, +Step); e.Handled = true; break;
                    case Key.Right: Nudge(0, -Step); e.Handled = true; break;
                    case Key.Up: Nudge(-Step, -Step * 0.35); e.Handled = true; break;
                    case Key.Down: Nudge(+Step, +Step * 0.35); e.Handled = true; break;
                    case Key.Escape: DialogResult = false; Close(); break;
                }
            };

            UpdateReadout();
        }

        private void Nudge(double dx, double dy)
        {
            if (double.IsNaN(dx)) { _dx = 0; _dy = 0; }
            else { _dx += dx; _dy += dy; }
            UpdateReadout();
            TrimChanged?.Invoke(_dx, _dy);
        }

        private void UpdateReadout() =>
            _readout.Text = $"Target white offset: Δx {_dx:+0.0000;-0.0000;0}  Δy {_dy:+0.0000;-0.0000;0}";
    }
}
