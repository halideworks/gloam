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
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x3C, 0x2F));
        private static readonly Brush TextDim = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly Brush ButtonBg = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
        private static readonly Brush ButtonHoverBg = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        private readonly Border _patch;
        private readonly ProgressBar _progress;
        private readonly TextBlock _patchInfo;
        private readonly TextBlock _phase;
        private readonly TextBlock _percent;
        private readonly TextBlock _currentPatch;
        private readonly TextBlock _nextPatch;
        private readonly Grid _currentNextRow;
        private readonly StackPanel _controlsRow;
        private readonly Button _muteButton;
        private Action? _cancelRequested;

        /// <summary>Raised when the user presses Escape on the patch window (abort the sweep).</summary>
        public event Action? AbortRequested;

        /// <summary>
        /// Raised on Enter/Space or double-click — "I'm done positioning, continue." The
        /// patch window covers the whole monitor, so the confirmation has to live ON it.
        /// </summary>
        public event Action? ContinueRequested;

        /// <summary>Current patch placement offset from center, in pixels.</summary>
        public double OffsetX { get; private set; }
        public double OffsetY { get; private set; }

        private bool _dragEnabled;
        private bool _dragging;
        private Point _dragStart;
        private double _dragStartX, _dragStartY;

        /// <summary>
        /// Lets the user drag the patch to the probe position — the same interaction as the
        /// calibration window's positioning step. Read the result from OffsetX/OffsetY.
        /// </summary>
        public void EnableDrag()
        {
            if (_dragEnabled) return;
            _dragEnabled = true;
            Cursor = System.Windows.Input.Cursors.SizeAll;

            MouseLeftButtonDown += (_, e) =>
            {
                if (!_dragEnabled) return;
                _dragging = true;
                _dragStart = e.GetPosition(this);
                _dragStartX = OffsetX;
                _dragStartY = OffsetY;
                CaptureMouse();
            };
            MouseMove += (_, e) =>
            {
                if (!_dragging) return;
                var p = e.GetPosition(this);
                OffsetX = _dragStartX + (p.X - _dragStart.X);
                OffsetY = _dragStartY + (p.Y - _dragStart.Y);
                _patch.RenderTransform = new TranslateTransform(OffsetX, OffsetY);
            };
            MouseLeftButtonUp += (_, _) =>
            {
                if (!_dragging) return;
                _dragging = false;
                ReleaseMouseCapture();
            };
        }

        public void DisableDrag()
        {
            _dragEnabled = false;
            Cursor = null;
        }

        public PatchDisplayWindow(MonitorInfo monitor, double patchSize = 600, double offsetX = 0, double offsetY = 0)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            // Activated on show so Escape lands here without an extra click.
            ShowActivated = true;
            Topmost = true;
            Background = Surround;
            Focusable = true;
            // Hovering a taskbar icon mid-sweep triggers Aero Peek; without this the patch
            // fades to glass and the probe reads the desktop instead of the patch.
            Services.WindowTheme.ExcludeFromPeek(this);

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    e.Handled = true;
                    AbortRequested?.Invoke();
                }
                else if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
                {
                    e.Handled = true;
                    ContinueRequested?.Invoke();
                }
            };
            MouseDown += (_, _) => Focus();
            MouseDoubleClick += (_, _) => ContinueRequested?.Invoke();

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

            OffsetX = offsetX;
            OffsetY = offsetY;
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
            _percent = new TextBlock { Foreground = TextDim, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right };

            var infoRow = new Grid();
            infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_phase, 1);
            Grid.SetColumn(_percent, 2);
            infoRow.Children.Add(_patchInfo);
            infoRow.Children.Add(_phase);
            infoRow.Children.Add(_percent);

            // Current/Next patch row, mirroring the calibration measurement strip. Shown
            // only for sweeps that report a next patch (the verify and PQ-tracking loops).
            _currentPatch = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
            _nextPatch = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
            _currentNextRow = new Grid { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
            _currentNextRow.ColumnDefinitions.Add(new ColumnDefinition());
            _currentNextRow.ColumnDefinitions.Add(new ColumnDefinition());
            var currentStack = new StackPanel { Orientation = Orientation.Horizontal };
            currentStack.Children.Add(new TextBlock { Text = "Current: ", Foreground = TextDim, FontSize = 12 });
            currentStack.Children.Add(_currentPatch);
            var nextStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            nextStack.Children.Add(new TextBlock { Text = "Next: ", Foreground = TextDim, FontSize = 12 });
            nextStack.Children.Add(_nextPatch);
            Grid.SetColumn(nextStack, 1);
            _currentNextRow.Children.Add(currentStack);
            _currentNextRow.Children.Add(nextStack);

            // Cancel + mute, the same in-run controls the calibration screen offers. Hidden
            // until a sweep opts in via EnableSweepControls.
            var cancelButton = MakeSubtleButton("Cancel");
            cancelButton.Margin = new Thickness(0, 0, 8, 0);
            cancelButton.Click += (_, _) =>
            {
                if (_cancelRequested == null) return;
                if (ConfirmDialog.Confirm(this, "Cancel Verification",
                        "Cancel verification? Progress from this sweep will be lost.",
                        confirmLabel: "Yes", cancelLabel: "No"))
                    _cancelRequested?.Invoke();
            };
            _muteButton = MakeSubtleButton("Sound: On");
            _muteButton.ToolTip = "Mute or unmute capture and completion sounds";
            _muteButton.Click += (_, _) =>
            {
                CalibrationSounds.Muted = !CalibrationSounds.Muted;
                UpdateMuteLabel();
            };
            _controlsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            _controlsRow.Children.Add(cancelButton);
            _controlsRow.Children.Add(_muteButton);

            var overlayContent = new StackPanel { MaxWidth = 600 };
            overlayContent.Children.Add(_progress);
            overlayContent.Children.Add(infoRow);
            overlayContent.Children.Add(_currentNextRow);
            overlayContent.Children.Add(_controlsRow);

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

        /// <summary>
        /// A stray Win+D / taskbar-preview click can minimize the patch window mid-sweep,
        /// leaving the probe staring at the desktop. Snap straight back to Normal.
        /// </summary>
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                Core.Log.Info("PatchDisplayWindow: minimized during a sweep; restoring to keep the patch on screen.");
                WindowState = WindowState.Normal;
            }
        }

        /// <summary>
        /// The patch area in physical screen pixels (PointToScreen includes the drag
        /// transform and per-monitor DPI). Used to place the FP16 wire renderer exactly
        /// over the patch for the HDR PQ-tracking verify sweep. Only valid once shown.
        /// </summary>
        public Int32Rect GetPatchPixelRect()
        {
            var topLeft = _patch.PointToScreen(new Point(0, 0));
            var bottomRight = _patch.PointToScreen(new Point(_patch.ActualWidth, _patch.ActualHeight));
            return new Int32Rect(
                (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
                (int)Math.Round(bottomRight.X - topLeft.X), (int)Math.Round(bottomRight.Y - topLeft.Y));
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
            SetProgressBar(current, total);
            _phase.Text = $"Verifying - {patchName}";
            _currentNextRow.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Sweep-style progress with the calibration screen's Current/Next patch row.
        /// Pass null for <paramref name="nextPatchName"/> on the last patch.
        /// </summary>
        public void SetProgress(int current, int total, string patchName, string? nextPatchName,
            string phase = "Verifying calibration")
        {
            SetProgressBar(current, total);
            _phase.Text = phase;
            _currentPatch.Text = patchName;
            _nextPatch.Text = nextPatchName ?? "(last patch)";
            _currentNextRow.Visibility = Visibility.Visible;
        }

        private void SetProgressBar(int current, int total)
        {
            double pct = total > 0 ? current * 100.0 / total : 0;
            _progress.Value = pct;
            _percent.Text = $"{pct:F0}%";
            _patchInfo.Text = $"Patch {current} of {total}";
        }

        /// <summary>
        /// Shows the in-run Cancel and mute buttons on the bottom strip. The cancel
        /// callback is invoked only after the user confirms ("Cancel verification?"),
        /// and must trigger the same cancellation path as Escape (the sweep's CTS).
        /// </summary>
        public void EnableSweepControls(Action cancelRequested)
        {
            _cancelRequested = cancelRequested;
            _controlsRow.Visibility = Visibility.Visible;
            UpdateMuteLabel();
        }

        private void UpdateMuteLabel() =>
            _muteButton.Content = CalibrationSounds.Muted ? "Sound: Muted" : "Sound: On";

        /// <summary>Code-built twin of CalibrationWindow.xaml's SubtleButton style.</summary>
        private static Button MakeSubtleButton(string label)
        {
            var border = new System.Windows.FrameworkElementFactory(typeof(Border), "border");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BackgroundProperty, ButtonBg);
            border.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));
            var presenter = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new System.Windows.Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, ButtonHoverBg, "border"));
            template.Triggers.Add(hover);

            return new Button
            {
                Content = label,
                Template = template,
                Foreground = Brushes.White,
                Cursor = System.Windows.Input.Cursors.Hand,
                // Keep keyboard focus on the window so Escape/Enter still land there and
                // Space can't accidentally re-click a focused button mid-sweep.
                Focusable = false,
            };
        }
    }
}
