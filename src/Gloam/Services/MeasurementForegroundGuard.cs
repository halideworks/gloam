using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Gloam.Core;

namespace Gloam.Services
{
    /// <summary>
    /// Keeps a measurement surface in front while the probe is reading it.
    ///
    /// The colorimeter reads whatever pixels are physically on the panel, so anything that
    /// covers the patch mid-read (alt-tab, another app taking the foreground, a topmost
    /// notification) is silently measured AS the patch. That does not fail loudly; it
    /// quietly poisons a reading that then flows into the profile. Topmost alone is not
    /// enough: another topmost window, or a shell switcher, still lands on top of us.
    ///
    /// While measuring, this re-inserts the window into the topmost band and pulls it back
    /// to the foreground whenever it loses activation to ANOTHER PROCESS. Windows owned by
    /// this app are deliberately left alone: the calibration flow opens modal dialogs
    /// (Night Light prompt, driver install, confirmations) over exactly this window, and
    /// yanking focus off them would make them unusable.
    /// </summary>
    internal sealed class MeasurementForegroundGuard
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        private readonly Window _window;
        private readonly Func<bool> _isMeasuring;
        private DateTime _lastLogUtc = DateTime.MinValue;

        /// <summary>How many times the patch had to be pulled back during this run.</summary>
        internal int Recoveries { get; private set; }

        private MeasurementForegroundGuard(Window window, Func<bool> isMeasuring)
        {
            _window = window;
            _isMeasuring = isMeasuring;
            window.Deactivated += OnDeactivated;
            window.Closed += (_, _) => window.Deactivated -= OnDeactivated;
        }

        internal static MeasurementForegroundGuard Attach(Window window, Func<bool> isMeasuring) =>
            new(window, isMeasuring);

        /// <summary>
        /// Takes the foreground deliberately, for the moment a measurement starts. The
        /// fullscreen patch is positioned with SWP_NOACTIVATE so it lands on the target
        /// monitor without stealing focus during setup, which means it may not own the
        /// foreground at all when the run begins.
        /// </summary>
        internal void ClaimForeground()
        {
            AssertTopmost();
            _window.Activate();
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            if (!_isMeasuring()) return;

            // Deactivated fires before the other window has finished coming up. Re-assert
            // once the switch has settled, or we race it and lose.
            _window.Dispatcher.BeginInvoke(new Action(Reassert), DispatcherPriority.Background);
        }

        private void Reassert()
        {
            if (!_isMeasuring() || ForegroundBelongsToThisApp()) return;

            AssertTopmost();
            bool activated = _window.Activate();
            Recoveries++;

            // Throttled: if something is genuinely fighting us for the foreground this can
            // fire many times a second, and a flooded log is a useless log.
            var now = DateTime.UtcNow;
            if (now - _lastLogUtc < TimeSpan.FromSeconds(2)) return;
            _lastLogUtc = now;
            Log.Info(
                $"MeasurementForegroundGuard: the measurement surface lost the foreground and was pulled back " +
                $"(activate={activated}, {Recoveries} recovery/recoveries so far). Readings taken while it was " +
                "covered may be contaminated.");
        }

        private void AssertTopmost()
        {
            _window.Topmost = true;
            IntPtr hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private static bool ForegroundBelongsToThisApp()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            // A zero thread id means the window died between the two calls; treat that as
            // "not ours" so the guard re-asserts rather than standing down on a stale answer.
            uint threadId = GetWindowThreadProcessId(foreground, out uint pid);
            return threadId != 0 && pid == (uint)Environment.ProcessId;
        }
    }
}
