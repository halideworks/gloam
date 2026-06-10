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
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOACTIVATE = 0x0010;

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

            // Place via raw Win32 pixels once the HWND exists. Feeding the DXGI pixel rect
            // into WPF's DIP-based Left/Width on a hidden window gets reinterpreted against
            // the wrong monitor's DPI on mixed-DPI setups — the window (and so the patch)
            // came out a different physical size than the calibration pass.
            var b = monitor.MonitorBounds;
            bool haveBounds = b.Right > b.Left && b.Bottom > b.Top;
            if (haveBounds)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                SourceInitialized += (_, _) =>
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    SetWindowPos(hwnd, HWND_TOPMOST, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, SWP_NOACTIVATE);
                };
                // Rough WPF placement so the window first materializes on the right monitor
                // (and therefore the right DPI context) before the pixel-exact SetWindowPos.
                Left = b.Left;
                Top = b.Top;
                Width = 200;
                Height = 200;
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
