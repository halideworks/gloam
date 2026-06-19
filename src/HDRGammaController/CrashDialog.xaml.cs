using System;
using System.Windows;
using HDRGammaController.ViewModels;

namespace HDRGammaController
{
    /// <summary>
    /// A themed, modal crash/error dialog (MVVM: display state lives on
    /// <see cref="CrashDialogViewModel"/>). Shown from the unhandled-exception handler to
    /// give the user a visible Continue/Exit choice instead of silently swallowing the error.
    /// </summary>
    public partial class CrashDialog : Window
    {
        public CrashDialogViewModel ViewModel { get; }

        public CrashDialog(CrashDialogViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = ViewModel;

            // The VM raises intent; the window translates it into DialogResult + Close, and
            // marshals the clipboard write to the UI thread (clipboard access must be STA).
            ViewModel.ContinueRequested += () => Close(dialogResult: false);
            ViewModel.ExitRequested += () => Close(dialogResult: true);
            ViewModel.CopyDetailsRequested += details =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { Clipboard.SetText(details); }
                    catch { /* clipboard locked — best effort */ }
                }));
        }

        private void Close(bool? dialogResult)
        {
            try
            {
                DialogResult = dialogResult;
                Close();
            }
            catch (InvalidOperationException)
            {
                // Close() before the window was shown (ShowDialog not yet entered) — just hide.
            }
        }

        /// <summary>
        /// Shows the dialog modally on the owner (or centered on screen) and returns true
        /// if the user chose Exit, false if they chose Continue. Rate-limiting against
        /// exception loops is the caller's responsibility (App keeps a once-per-session flag).
        /// </summary>
        public static bool? Show(Window? owner, string title, string message, string? details)
        {
            var vm = new CrashDialogViewModel
            {
                Title = title,
                Message = message,
                Details = details ?? string.Empty
            };

            var dialog = new CrashDialog(vm);
            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            try
            {
                return dialog.ShowDialog();
            }
            catch
            {
                // If the dialog itself throws (catastrophic state), fall back to the stock
                // WPF MessageBox so the user still gets *some* feedback.
                MessageBox.Show(
                    owner,
                    message + (string.IsNullOrWhiteSpace(details) ? "" : "\n\n" + details),
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return true;
            }
        }
    }
}
