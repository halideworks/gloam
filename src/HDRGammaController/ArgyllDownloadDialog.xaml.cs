using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController
{
    /// <summary>
    /// Styled dialog for downloading ArgyllCMS with progress feedback.
    /// </summary>
    public partial class ArgyllDownloadDialog : Window
    {
        private CancellationTokenSource? _cts;
        private bool _downloadSucceeded;

        /// <summary>
        /// Gets whether the download completed successfully.
        /// </summary>
        public bool DownloadSucceeded => _downloadSucceeded;

        public ArgyllDownloadDialog(string reason)
        {
            InitializeComponent();
            ReasonText.Text = reason;
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void Yes_Click(object sender, RoutedEventArgs e)
        {
            // Switch to progress view
            ConfirmationPanel.Visibility = Visibility.Collapsed;
            ConfirmationButtons.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            CancelDownloadButton.Visibility = Visibility.Visible;

            _cts = new CancellationTokenSource();

            try
            {
                var progress = new Progress<int>(percent =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        ProgressBar.Width = (ProgressPanel.ActualWidth > 0 ? ProgressPanel.ActualWidth : 380) * percent / 100;
                        ProgressDetailText.Text = $"{percent}%";

                        if (percent < 70)
                            ProgressStatusText.Text = "Downloading...";
                        else if (percent < 80)
                            ProgressStatusText.Text = "Verifying...";
                        else if (percent < 100)
                            ProgressStatusText.Text = "Extracting...";
                        else
                            ProgressStatusText.Text = "Complete!";
                    });
                });

                await ArgyllDownloader.DownloadAsync(_cts.Token, progress);
                _downloadSucceeded = true;

                // Show success
                ShowResult(true, "Download complete!", "ArgyllCMS has been installed and is ready to use.");
            }
            catch (OperationCanceledException)
            {
                // User cancelled via the button, or the window closed mid-download
                // (OnClosed cancels the CTS). In the latter case the dialog is gone -
                // there is nothing to show.
                if (IsVisible)
                    ShowResult(false, "Download cancelled", "You can try again later from the calibration setup.");
            }
            catch (ObjectDisposedException)
            {
                // Window closed and disposed the CTS while this continuation was still in
                // flight; swallow quietly.
            }
            catch (Exception ex)
            {
                // Download failed
                if (IsVisible)
                    ShowResult(false, "Download failed", ex.Message + "\n\nYou can install ArgyllCMS manually from argyllcms.com");
            }
        }

        private void ShowResult(bool success, string message, string detail)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            CancelDownloadButton.Visibility = Visibility.Collapsed;

            ResultPanel.Visibility = Visibility.Visible;
            CloseButton.Visibility = Visibility.Visible;

            if (success)
            {
                ResultIcon.Text = "OK";
                ResultIcon.Foreground = (Brush)FindResource("SuccessBrush");
            }
            else
            {
                ResultIcon.Text = "FAILED";
                ResultIcon.Foreground = (Brush)FindResource("ErrorBrush");
            }

            ResultText.Text = message;
            ResultDetailText.Text = detail;
        }

        private void CancelDownload_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = _downloadSucceeded;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            // Cancel BEFORE disposing: closing the window mid-download (Alt+F4) must stop
            // the transfer instead of leaving it running headless, and the orphaned
            // Yes_Click continuation must observe a cancellation rather than touching a
            // disposed CTS.
            try { _cts?.Cancel(); }
            catch (ObjectDisposedException) { /* already torn down */ }
            _cts?.Dispose();
            base.OnClosed(e);
        }
    }
}
