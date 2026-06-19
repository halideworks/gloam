using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HDRGammaController.ViewModels
{
    /// <summary>
    /// Severity / intent of a toast. Drives the accent stripe colour shown by the view.
    /// </summary>
    public enum ToastKind
    {
        /// <summary>Neutral informational message (white stripe).</summary>
        Info,
        /// <summary>Success confirmation (white stripe).</summary>
        Success,
        /// <summary>Warning — something the user may want to address (amber stripe).</summary>
        Warning,
        /// <summary>Error — an operation failed (accent-red stripe).</summary>
        Error
    }

    /// <summary>
    /// Display state for a transient themed toast notification. Pure view-model: holds the
    /// title/message/kind and an optional action; the <see cref="ToastWindow"/> owns the
    /// auto-dismiss timer and fade animation as view-lifecycle concerns. This keeps the VM
    /// unit-testable and free of WPF timer/Dispatcher dependencies.
    /// </summary>
    public class ToastViewModel : ObservableObject
    {
        private string _title = string.Empty;
        private string _message = string.Empty;
        private ToastKind _kind = ToastKind.Info;
        private string? _actionLabel;
        private bool _hasAction;

        /// <summary>Short uppercase title shown in the display font.</summary>
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        /// <summary>Body message shown in the body font.</summary>
        public string Message
        {
            get => _message;
            set => SetProperty(ref _message, value);
        }

        /// <summary>Severity, driving the accent stripe colour.</summary>
        public ToastKind Kind
        {
            get => _kind;
            set => SetProperty(ref _kind, value);
        }

        /// <summary>Label for the optional inline action button. Null/empty hides the button.</summary>
        public string? ActionLabel
        {
            get => _actionLabel;
            set
            {
                if (SetProperty(ref _actionLabel, value))
                    HasAction = !string.IsNullOrWhiteSpace(value);
            }
        }

        /// <summary>True when an action button should be shown.</summary>
        public bool HasAction
        {
            get => _hasAction;
            private set => SetProperty(ref _hasAction, value);
        }

        /// <summary>Command bound to the optional action button.</summary>
        public ICommand? ActionCommand { get; set; }

        /// <summary>
        /// Raised (by the window) when the toast should be dismissed — either by the
        /// auto-dismiss timer, a click, or because a newer toast replaced this one. The
        /// view subscribes to close itself.
        /// </summary>
        public event Action? DismissRequested;

        /// <summary>Command invoked by the action button; runs the action then dismisses.</summary>
        public ICommand InvokeActionCommand { get; }

        public ToastViewModel()
        {
            InvokeActionCommand = new RelayCommand(() =>
            {
                try { ActionCommand?.Execute(null); }
                finally { DismissRequested?.Invoke(); }
            });
        }

        internal void RequestDismiss() => DismissRequested?.Invoke();
    }
}
