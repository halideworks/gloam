using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HDRGammaController
{
    /// <summary>
    /// Dark-themed Yes/No confirmation, replacing MessageBox where it would render
    /// stock chrome - and, on Topmost fullscreen windows (calibration), where a
    /// plain MessageBox can open BEHIND the window with no visible feedback.
    /// </summary>
    public sealed class ConfirmDialog : Window
    {
        private bool _confirmed;

        private ConfirmDialog(string title, string message, string confirmLabel, string? cancelLabel)
        {
            Title = title;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            MaxWidth = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Foreground = Brushes.White;

            Button MakeButton(string label, bool accent)
            {
                var b = new Button
                {
                    Content = label,
                    Padding = new Thickness(16, 7, 16, 7),
                    Margin = new Thickness(8, 0, 0, 0),
                    Background = new SolidColorBrush(accent ? Color.FromRgb(0xFF, 0x3C, 0x2F) : Color.FromRgb(0x3D, 0x3D, 0x3D)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    MinWidth = 80,
                };
                return b;
            }

            var confirm = MakeButton(confirmLabel, accent: true);
            confirm.Click += (_, _) => { _confirmed = true; DialogResult = true; Close(); };
            confirm.IsDefault = true;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0),
            };
            buttons.Children.Add(confirm);

            if (cancelLabel != null)
            {
                var cancel = MakeButton(cancelLabel, accent: false);
                cancel.Click += (_, _) => { DialogResult = false; Close(); };
                cancel.IsCancel = true;
                buttons.Children.Add(cancel);
            }
            else
            {
                // Informational dialog: the single OK button also answers Escape.
                confirm.IsCancel = true;
            }

            var display = Application.Current?.Resources["DisplayFont"] as FontFamily;
            var body = Application.Current?.Resources["BodyFont"] as FontFamily;

            var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            stack.Children.Add(new TextBlock
            {
                Text = title.ToUpperInvariant(),
                FontFamily = display,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10),
            });
            stack.Children.Add(new TextBlock
            {
                Text = message,
                FontFamily = body,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            });
            stack.Children.Add(buttons);

            // Dark-fixed brutalist: confirmations mostly appear over the dark calibration UI.
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(0),
                Child = stack,
            };

            // Merge the shared dark styles so the buttons get the templated look.
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Gloam;component/Themes/DarkControls.xaml", UriKind.Absolute),
            });
        }

        /// <summary>Shows the dialog modally over the owner and returns true when confirmed.</summary>
        public static bool Confirm(Window owner, string title, string message,
            string confirmLabel = "Yes", string cancelLabel = "No")
        {
            var dialog = new ConfirmDialog(title, message, confirmLabel, cancelLabel)
            {
                Owner = owner,
                // Match the owner so the dialog isn't buried under a Topmost
                // fullscreen window (the old MessageBox failure mode).
                Topmost = owner?.Topmost == true,
            };
            dialog.ShowDialog();
            return dialog._confirmed;
        }

        /// <summary>
        /// Shows an informational dialog (single OK button) modally over the owner.
        /// Replaces MessageBox for status reports, errors and multiline summaries.
        /// </summary>
        public static void Info(Window owner, string title, string message)
        {
            var dialog = new ConfirmDialog(title, message, "OK", cancelLabel: null)
            {
                Owner = owner,
                Topmost = owner?.Topmost == true,
                // Informational messages can be long multiline reports (e.g. the HDR
                // renderer validation) - give them more width before wrapping.
                MaxWidth = 640,
            };
            dialog.ShowDialog();
        }
    }
}
