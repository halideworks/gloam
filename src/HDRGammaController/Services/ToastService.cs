using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using HDRGammaController.Core;
using HDRGammaController.ViewModels;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Abstraction over transient themed notifications. Core code raises intent via
    /// <see cref="TrayViewModel.NotificationRequested"/>; the app layer bridges to this
    /// service so Core never depends on WPF. Callers may run on any thread — the
    /// implementation marshals to the UI thread.
    /// </summary>
    public interface IToastService
    {
        /// <summary>Show a toast with an optional severity kind.</summary>
        void Show(string title, string message, ToastKind kind = ToastKind.Info);

        /// <summary>Show a toast with an inline action button.</summary>
        void Show(string title, string message, ToastKind kind, string actionLabel, Action action);

        /// <summary>Async variant for callers that want to await the toast's dismissal.</summary>
        Task ShowAsync(string title, string message, ToastKind kind = ToastKind.Info);
    }

    /// <summary>
    /// DI singleton that shows themed <see cref="ToastWindow"/>s. Coalesces: a new toast
    /// replaces a currently-showing one (rather than stacking) so a rapid burst of events
    /// (e.g. night-mode fade ticks) never floods the screen. All work is marshalled to the
    /// UI thread; <see cref="Show"/> returns immediately.
    /// </summary>
    public class ToastService : IToastService
    {
        // The single live toast window, if any. Replacing a toast dismisses the previous one
        // via the VM's DismissRequested event so its fade-out still plays cleanly.
        private ToastWindow? _current;

        public void Show(string title, string message, ToastKind kind = ToastKind.Info)
        {
            Post(() => ShowCore(title, message, kind, null, null));
        }

        public void Show(string title, string message, ToastKind kind, string actionLabel, Action action)
        {
            ICommand? cmd = action == null ? null : new RelayCommandAdapter(action);
            Post(() => ShowCore(title, message, kind, actionLabel, cmd));
        }

        public Task ShowAsync(string title, string message, ToastKind kind = ToastKind.Info)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                ShowCore(title, message, kind, null, null);
                // Toasts auto-dismiss; resolve the task when the current toast closes.
                // Capture the window instance: by the time Closed fires, _current may
                // already point at a NEWER toast (or be null), so unsubscribing via
                // _current was a no-op that leaked the handler on the closed window.
                var window = _current;
                if (window != null)
                {
                    void OnClosed(object? s, EventArgs e)
                    {
                        window.Closed -= OnClosed;
                        tcs.TrySetResult(null);
                    }
                    window.Closed += OnClosed;
                }
                else
                {
                    tcs.TrySetResult(null);
                }
            });
            return tcs.Task;
        }

        private void ShowCore(string title, string message, ToastKind kind,
            string? actionLabel, ICommand? actionCommand)
        {
            // Replace any currently-showing toast: ask it to dismiss, then drop our reference.
            // The old window's fade-out-then-close still runs to completion.
            if (_current != null)
            {
                try { _current.ViewModel.RequestDismiss(); }
                catch { /* window already closing */ }
            }

            var vm = new ToastViewModel
            {
                Title = title,
                Message = message,
                Kind = kind,
                ActionLabel = actionLabel,
                ActionCommand = actionCommand
            };

            var window = new ToastWindow(vm, actionCommand == null ? null : TimeSpan.FromSeconds(8));
            // Keep a weak handle so a window that closed on its own (timer) isn't replaced
            // against a stale reference.
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, window))
                    _current = null;
            };
            _current = window;

            try
            {
                window.Show();
            }
            catch (Exception ex)
            {
                // Never let a toast failure propagate — it's a notification, not a critical path.
                Log.Error($"ToastService: failed to show toast: {ex.Message}");
                _current = null;
            }
        }

        private static void Post(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                // App shutting down (Dispatcher gone) — drop the notification.
                return;
            }
            dispatcher.BeginInvoke(action);
        }

        /// <summary>Adapts a plain <see cref="Action"/> into an <see cref="ICommand"/>.</summary>
        private sealed class RelayCommandAdapter : ICommand
        {
            private readonly Action _action;
            public RelayCommandAdapter(Action action) => _action = action;
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _action();
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }
    }
}
