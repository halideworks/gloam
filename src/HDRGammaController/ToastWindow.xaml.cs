using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HDRGammaController.Interop;
using HDRGammaController.ViewModels;

namespace HDRGammaController
{
    /// <summary>
    /// A transient, themed, non-activating toast overlay. View-lifecycle concerns only live
    /// here: bottom-right placement on the active monitor, the auto-dismiss timer, and the
    /// fade-out-then-close animation. All display state is owned by the bound
    /// <see cref="ToastViewModel"/> (MVVM); this class contains no business logic.
    ///
    /// The flag set (WindowStyle=None, AllowsTransparency, ShowInTaskbar=False, Topmost,
    /// ShowActivated=False) mirrors <c>DisplayIdentify.Flash</c>'s proven overlay pattern —
    /// the toast appears instantly and never steals focus.
    /// </summary>
    public partial class ToastWindow : Window
    {
        private readonly DispatcherTimer _dismissTimer;
        private readonly TimeSpan _duration;
        private bool _closing;

        public ToastViewModel ViewModel { get; }

        /// <param name="duration">How long the toast stays before fading out.</param>
        public ToastWindow(ToastViewModel viewModel, TimeSpan? duration = null)
        {
            InitializeComponent();
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = ViewModel;
            _duration = duration ?? TimeSpan.FromSeconds(2.5);

            // When the VM asks to be dismissed (action clicked, or replaced), close now.
            ViewModel.DismissRequested += OnDismissRequested;

            _dismissTimer = new DispatcherTimer { Interval = _duration };
            _dismissTimer.Tick += (_, _) =>
            {
                _dismissTimer.Stop();
                BeginFadeOutAndClose();
            };

            // Position AFTER the content has been measured, arranged, and rendered. At Loaded
            // (the old hook) SizeToContent hasn't resolved ActualWidth/Height yet, so the card
            // was placed as if ~3× narrower than it really is and spilled off the right edge.
            ContentRendered += (_, _) =>
            {
                PositionBottomRightOfActiveMonitor();
                _dismissTimer.Start();
            };
        }

        private void OnDismissRequested()
        {
            // Marshal to the UI thread — a bound command may run on a background thread.
            Dispatcher.BeginInvoke(new Action(BeginFadeOutAndClose));
        }

        private void BeginFadeOutAndClose()
        {
            if (_closing) return;
            _closing = true;
            _dismissTimer.Stop();

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
            fade.Completed += (_, _) => Close();
            Card.BeginAnimation(OpacityProperty, fade);
        }

        protected override void OnClosed(EventArgs e)
        {
            _dismissTimer.Stop();
            ViewModel.DismissRequested -= OnDismissRequested;
            base.OnClosed(e);
        }

        /// <summary>
        /// Places the toast at the bottom-right of the working area of the monitor under the
        /// cursor (or the primary monitor as a fallback), with a margin.
        ///
        /// Win32 monitor bounds (<see cref="User32.TryGetMonitorBounds"/>) come back in
        /// PHYSICAL pixels, but WPF's <see cref="Window.Left"/>/<see cref="Window.Top"/> take
        /// device-independent units (DIPs). On any non-100%-scaled display we must convert
        /// using the composition transform matrix (the authoritative pixels→DIPs mapping).
        /// </summary>
        private void PositionBottomRightOfActiveMonitor()
        {
            const double margin = 16;

            // Resolve the WORK area (desktop minus taskbar/appbars) of the monitor under the
            // cursor, falling back to the primary work area if the cursor can't be read.
            // TryGetMonitorBounds returns rcMonitor (full screen incl. taskbar) which would
            // sit the toast on top of the taskbar.
            Rect area = SystemParameters.WorkArea;
            if (User32.GetCursorPos(out var pt))
            {
                IntPtr hMon = User32.MonitorFromPoint(pt, User32.MONITOR_DEFAULTTOPRIMARY);
                if (User32.TryGetMonitorWorkArea(hMon, out var work))
                {
                    area = new Rect(work.Left, work.Top,
                        work.Right - work.Left, work.Bottom - work.Top);
                }
            }

            // Authoritative pixels→DIPs scale from the live composition target. Falls back to
            // 1.0 if the window isn't yet source-connected (rare, since ContentRendered fires late).
            double scaleX = 1.0, scaleY = 1.0;
            PresentationSource? src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                var m = src.CompositionTarget.TransformToDevice;
                scaleX = m.M11 > 0 ? m.M11 : 1.0;
                scaleY = m.M22 > 0 ? m.M22 : 1.0;
            }

            // Convert the physical-pixel work area to DIPs.
            double areaRightDip = area.Right / scaleX;
            double areaBottomDip = area.Bottom / scaleY;
            double areaLeftDip = area.Left / scaleX;
            double areaTopDip = area.Top / scaleY;

            // By ContentRendered time these are the final, fully-measured dimensions (DIPs).
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0) w = MaxWidth;
            if (h <= 0) h = 200;

            Left = areaRightDip - w - margin;
            Top = areaBottomDip - h - margin;
            if (Left < areaLeftDip) Left = areaLeftDip;
            if (Top < areaTopDip) Top = areaTopDip;
        }
    }
}
