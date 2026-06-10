using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HDRGammaController.Core;

namespace HDRGammaController
{
    /// <summary>
    /// Patch window for the post-apply verify pass, styled to MATCH the main calibration
    /// measurement screen: same dark surround, the patch at the size and placement the user
    /// chose during calibration, and the same bottom progress overlay (bar + patch counter +
    /// patch name). Windows applies the installed MHC2 profile at the compositor, so what
    /// the probe sees through this window IS the corrected output.
    /// </summary>
    public sealed class PatchDisplayWindow : Window
    {
        // Palette mirrored from CalibrationWindow.xaml resources.
        private static readonly Brush Surround = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        private static readonly Brush OverlayBg = new SolidColorBrush(Color.FromArgb(0xCC, 0x1A, 0x1A, 0x1A));
        private static readonly Brush BarBg = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x08, 0x91, 0xb2));
        private static readonly Brush TextDim = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        private readonly Border _patch;
        private readonly ProgressBar _progress;
        private readonly TextBlock _patchInfo;
        private readonly TextBlock _phase;

        public PatchDisplayWindow(MonitorInfo monitor, double patchSize = 600, double offsetX = 0, double offsetY = 0)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Background = Surround;

            // Same monitor-bounds placement the calibration window uses (DXGI desktop rect).
            var b = monitor.MonitorBounds;
            if (b.Right > b.Left && b.Bottom > b.Top)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = b.Left;
                Top = b.Top;
                Width = b.Right - b.Left;
                Height = b.Bottom - b.Top;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Width = 800;
                Height = 600;
            }

            _patch = new Border
            {
                Width = patchSize,
                Height = patchSize,
                Background = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new TranslateTransform(offsetX, offsetY),
            };

            _progress = new ProgressBar
            {
                Height = 8,
                Minimum = 0,
                Maximum = 100,
                Background = BarBg,
                Foreground = Accent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 8),
            };
            _patchInfo = new TextBlock { Foreground = TextDim, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left };
            _phase = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                Text = "Verifying calibration",
            };

            var infoRow = new Grid();
            infoRow.Children.Add(_patchInfo);
            infoRow.Children.Add(_phase);

            var overlayContent = new StackPanel { MaxWidth = 600 };
            overlayContent.Children.Add(_progress);
            overlayContent.Children.Add(infoRow);

            var overlay = new Border
            {
                Background = OverlayBg,
                Padding = new Thickness(20, 16, 20, 16),
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = overlayContent,
            };

            var root = new Grid();
            root.Children.Add(_patch);
            root.Children.Add(overlay);
            Content = root;
        }

        public void SetColor(double r, double g, double b)
        {
            _patch.Background = new SolidColorBrush(Color.FromRgb(
                (byte)Math.Round(Math.Clamp(r, 0, 1) * 255),
                (byte)Math.Round(Math.Clamp(g, 0, 1) * 255),
                (byte)Math.Round(Math.Clamp(b, 0, 1) * 255)));
        }

        public void SetProgress(int current, int total, string patchName)
        {
            _progress.Value = total > 0 ? current * 100.0 / total : 0;
            _patchInfo.Text = $"Patch {current} of {total}";
            _phase.Text = $"Verifying — {patchName}";
        }
    }
}
