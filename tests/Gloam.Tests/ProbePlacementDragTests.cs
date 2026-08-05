using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// The placement surface drags the probe target and hosts the Begin/Back buttons.
    /// Mouse capture has to land on the element that carries the move/up handlers: WPF's
    /// default <see cref="CaptureMode.Element"/> delivers every subsequent mouse event to
    /// the captured element ALONE, skipping its children. Capturing the UserControl instead
    /// of the surface stranded the drag — the surface never saw the move or the button-up,
    /// so the target would not move, the capture was never released, and while the control
    /// held capture the placement buttons stopped receiving clicks. The calibration window
    /// looked locked up while the rest of the app kept running.
    /// </summary>
    public sealed class ProbePlacementDragTests
    {
        [Fact]
        public void PressAndRelease_ReleasesCapture_SoThePlacementButtonsStayLive()
        {
            WpfTestHost.Run(() =>
            {
                var (window, control, surface) = ShowPlacementSurface();
                try
                {
                    Press(surface);

                    // Capture must sit on the surface itself. Anything higher (the
                    // UserControl) routes past the handlers that end the drag.
                    Assert.Same(surface, Mouse.Captured);

                    // Route the button-up the way WPF does under capture: to the captured
                    // element. If capture landed too high this reaches a handler-less
                    // element, the drag never ends, and the capture leaks.
                    ReleaseOnCapturedElement();

                    Assert.Null(Mouse.Captured);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Fact]
        public void CaptureStolenMidDrag_EndsTheDrag_InsteadOfLatchingItOn()
        {
            WpfTestHost.Run(() =>
            {
                var (window, control, surface) = ShowPlacementSurface();
                try
                {
                    Press(surface);
                    Assert.Same(surface, Mouse.Captured);

                    // A tray menu, alt-tab or dialog can take the capture away mid-drag.
                    Mouse.Capture(null);
                    Assert.Null(Mouse.Captured);

                    // The next press must start a fresh drag and re-capture rather than
                    // finding the surface still convinced it was dragging.
                    Press(surface);
                    Assert.Same(surface, Mouse.Captured);

                    ReleaseOnCapturedElement();
                    Assert.Null(Mouse.Captured);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        private static (Window Window, ProbePlacementControl Control, IInputElement Surface) ShowPlacementSurface()
        {
            var control = new ProbePlacementControl();
            var window = new Window
            {
                Content = control,
                Width = 800,
                Height = 600,
                // Off-screen and invisible: the control only needs a live PresentationSource
                // for CaptureMouse to succeed.
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
                Opacity = 0,
            };
            window.Show();
            WpfTestHost.Pump(TimeSpan.FromMilliseconds(50));

            control.Configure(400, 0, 0, operationLabel: "Calibration");

            // Drive layout explicitly rather than waiting for the render pipeline. Another
            // WPF test in this assembly creates an Application and shuts it down, and after
            // that a window shown on a fresh thread never gets laid out on its own, leaving
            // a zero-size surface that CaptureMouse silently refuses to capture.
            control.Measure(new Size(window.Width, window.Height));
            control.Arrange(new Rect(0, 0, window.Width, window.Height));
            control.UpdateLayout();

            var surface = control.FindName("PlacementSurface") as Grid;
            Assert.NotNull(surface);
            Assert.True(surface!.ActualWidth > 0 && surface.ActualHeight > 0,
                "the placement surface must be laid out before mouse capture can be tested");
            return (window, control, surface);
        }

        private static void Press(IInputElement surface) =>
            ((UIElement)surface).RaiseEvent(
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                });

        private static void ReleaseOnCapturedElement() =>
            ((UIElement)Mouse.Captured!).RaiseEvent(
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                });
    }
}
