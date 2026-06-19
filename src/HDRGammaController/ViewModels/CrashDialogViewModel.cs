using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HDRGammaController.ViewModels
{
    /// <summary>
    /// Display state for the themed, modal crash/error dialog shown from the unhandled-exception
    /// handler. Holds the message and (collapse-by-default) exception details, plus the
    /// Continue/Exit/Copy commands. The commands are wired by the dialog to set its
    /// <see cref="Window.DialogResult"/> and close.
    /// </summary>
    public class CrashDialogViewModel : ObservableObject
    {
        private string _title = "Something went wrong";
        private string _message = string.Empty;
        private string _details = string.Empty;
        private bool _areDetailsVisible;
        private bool _hasDetails;

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        public string Message
        {
            get => _message;
            set => SetProperty(ref _message, value);
        }

        /// <summary>Exception type + message + stack (text). Empty when none was provided.</summary>
        public string Details
        {
            get => _details;
            set
            {
                if (SetProperty(ref _details, value))
                    HasDetails = !string.IsNullOrWhiteSpace(value);
            }
        }

        public bool HasDetails
        {
            get => _hasDetails;
            private set => SetProperty(ref _hasDetails, value);
        }

        /// <summary>True when the details panel is expanded.</summary>
        public bool AreDetailsVisible
        {
            get => _areDetailsVisible;
            set => SetProperty(ref _areDetailsVisible, value);
        }

        public ICommand ContinueCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand ToggleDetailsCommand { get; }
        public ICommand CopyDetailsCommand { get; }

        /// <summary>Raised when the user picks Continue. The dialog closes with DialogResult=false.</summary>
        public event Action? ContinueRequested;
        /// <summary>Raised when the user picks Exit. The dialog closes with DialogResult=true.</summary>
        public event Action? ExitRequested;
        /// <summary>Raised to copy the details to the clipboard; the view marshals to the UI thread.</summary>
        public event Action<string>? CopyDetailsRequested;

        public CrashDialogViewModel()
        {
            ContinueCommand = new RelayCommand(() => ContinueRequested?.Invoke());
            ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
            ToggleDetailsCommand = new RelayCommand(() => AreDetailsVisible = !AreDetailsVisible);
            CopyDetailsCommand = new RelayCommand(() =>
            {
                if (!string.IsNullOrWhiteSpace(Details))
                    CopyDetailsRequested?.Invoke(Details);
            });
        }
    }
}
