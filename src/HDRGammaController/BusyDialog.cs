using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HDRGammaController
{
    /// <summary>
    /// Small dark wait dialog with an animated sweep bar, for operations that take a
    /// few seconds with no other visible feedback (e.g. calibration shutdown: the
    /// in-flight read finishes, spotread releases the meter, display state restores).
    /// Not user-closable; the caller closes it when the work completes.
    /// </summary>
    public sealed class BusyDialog : Window
    {
        private BusyDialog(string message)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Foreground = Brushes.White;

            var text = new TextBlock
            {
                Text = message,
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            // Animated sweep: a short accent bar gliding across a dark track.
            const double trackWidth = 260;
            const double runnerWidth = 70;
            var transform = new TranslateTransform();
            var runner = new Border
            {
                Width = runnerWidth,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = transform,
            };
            var track = new Border
            {
                Width = trackWidth,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Margin = new Thickness(0, 16, 0, 0),
                ClipToBounds = true,
                Child = runner,
            };

            var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
            stack.Children.Add(text);
            stack.Children.Add(track);

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x3F)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = stack,
            };

            Loaded += (_, _) =>
            {
                var sweep = new DoubleAnimation
                {
                    From = -runnerWidth,
                    To = trackWidth,
                    Duration = TimeSpan.FromSeconds(1.2),
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                transform.BeginAnimation(TranslateTransform.XProperty, sweep);
            };

            // Not user-closable: ignore Alt+F4 until the caller closes it.
            Closing += (_, e) => e.Cancel = !_allowClose;
        }

        private bool _allowClose;

        /// <summary>Opens the dialog non-blocking over the owner; close with Dismiss().</summary>
        public static BusyDialog Open(Window owner, string message)
        {
            var dialog = new BusyDialog(message)
            {
                Owner = owner,
                // Match the owner so the dialog isn't buried under a Topmost fullscreen window.
                Topmost = owner?.Topmost == true,
            };
            dialog.Show();
            return dialog;
        }

        /// <summary>Closes the dialog (the only way it closes).</summary>
        public void Dismiss()
        {
            _allowClose = true;
            Close();
        }
    }
}
