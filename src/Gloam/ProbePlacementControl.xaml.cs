using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Gloam
{
    /// <summary>
    /// Shared colorimeter-placement surface used by the initial calibration and every
    /// follow-up measurement window. It owns the target visual, drag/nudge behavior and
    /// instructions so those flows cannot drift into different placement experiences.
    /// </summary>
    public partial class ProbePlacementControl : UserControl
    {
        private bool _dragging;
        private Point _dragStart;
        private double _dragStartX;
        private double _dragStartY;

        public event RoutedEventHandler? BeginRequested;
        public event RoutedEventHandler? SecondaryRequested;

        public double OffsetX { get; private set; }
        public double OffsetY { get; private set; }

        public ProbePlacementControl()
        {
            InitializeComponent();
        }

        public void Configure(
            double patchSize,
            double offsetX,
            double offsetY,
            string? operationLabel = null,
            string secondaryLabel = "Back",
            bool showWindowedBanner = false)
        {
            PatchBorder.Width = Math.Clamp(patchSize, 120, 2000);
            PatchBorder.Height = Math.Clamp(patchSize, 120, 2000);
            OffsetX = offsetX;
            OffsetY = offsetY;
            ApplyOffset();

            OperationLabel.Text = operationLabel?.Trim().ToUpperInvariant() ?? string.Empty;
            OperationLabel.Visibility = string.IsNullOrEmpty(OperationLabel.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            SecondaryButton.Content = secondaryLabel;
            WindowedBanner.Visibility = showWindowedBanner
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public bool TryNudge(Key key, bool largerStep)
        {
            double step = largerStep ? 25 : 5;
            switch (key)
            {
                case Key.Left: OffsetX -= step; break;
                case Key.Right: OffsetX += step; break;
                case Key.Up: OffsetY -= step; break;
                case Key.Down: OffsetY += step; break;
                default: return false;
            }

            ApplyOffset();
            return true;
        }

        private void ApplyOffset()
        {
            PatchTransform.X = OffsetX;
            PatchTransform.Y = OffsetY;
        }

        private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || IsInsideButton(e.OriginalSource as DependencyObject))
                return;

            _dragging = true;
            _dragStart = e.GetPosition(this);
            _dragStartX = OffsetX;
            _dragStartY = OffsetY;
            // Capture on the SURFACE that carries the move/up handlers, not on the
            // UserControl. CaptureMouse() here captured `this`, and CaptureMode.Element
            // routes every following mouse event to the captured element alone — its
            // children are skipped. So the surface's MouseMove/MouseLeftButtonUp never
            // fired: the target would not drag, the capture was never released, and with
            // the control holding capture the Begin Measurement and Back buttons stopped
            // receiving clicks entirely. The window looked frozen while the app ran on.
            ((UIElement)sender).CaptureMouse();
            Focus();
        }

        private static bool IsInsideButton(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is Button) return true;
                element = element is Visual
                    ? VisualTreeHelper.GetParent(element)
                    : (element as FrameworkContentElement)?.Parent;
            }
            return false;
        }

        private void Surface_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point current = e.GetPosition(this);
            OffsetX = _dragStartX + current.X - _dragStart.X;
            OffsetY = _dragStartY + current.Y - _dragStart.Y;
            ApplyOffset();
        }

        private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }

        /// <summary>
        /// Anything can take the capture away mid-drag (a tray menu, alt-tab, a dialog
        /// opening on another thread). End the drag when that happens so the flag cannot
        /// latch true and leave the surface convinced it is still dragging.
        /// </summary>
        private void Surface_LostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

        private void Control_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (TryNudge(e.Key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
            {
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                BeginRequested?.Invoke(this, new RoutedEventArgs());
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SecondaryRequested?.Invoke(this, new RoutedEventArgs());
            }
        }

        private void BeginButton_Click(object sender, RoutedEventArgs e) =>
            BeginRequested?.Invoke(this, e);

        private void SecondaryButton_Click(object sender, RoutedEventArgs e) =>
            SecondaryRequested?.Invoke(this, e);
    }
}
