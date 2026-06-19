using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController
{
    /// <summary>
    /// Dialog for guiding users through ArgyllCMS USB driver installation.
    /// </summary>
    public partial class DriverInstallDialog : Window
    {
        private bool _driverInstalled;

        /// <summary>
        /// Gets whether the user wants to retry calibration after driver installation.
        /// </summary>
        public bool ShouldRetry { get; private set; }

        /// <summary>
        /// Gets whether the driver was installed successfully.
        /// </summary>
        public bool DriverInstalled => _driverInstalled;

        public DriverInstallDialog()
        {
            InitializeComponent();
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
            ShouldRetry = false;
            DialogResult = false;
            Close();
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            // Check if driver installer is available
            if (!UsbDriverHelper.IsDriverInstallerAvailable())
            {
                ShowResult(false, "Driver installer not found",
                    "The ArgyllCMS USB driver installer could not be found. Please download ArgyllCMS first.");
                return;
            }

            // Switch to installing view
            ExplanationPanel.Visibility = Visibility.Collapsed;
            InitialButtons.Visibility = Visibility.Collapsed;
            InstallingPanel.Visibility = Visibility.Visible;

            try
            {
                // Launch the installer and wait for it to complete
                bool success = await UsbDriverHelper.LaunchDriverInstallerAsync();

                // We consider it successful if the installer ran (user may have installed or cancelled)
                // The real test is whether calibration works afterward
                _driverInstalled = true;
                ShowResult(true, "Driver installer completed",
                    "If you installed the driver for your colorimeter, click 'Retry Calibration' to continue.\n\n" +
                    "If the installer was cancelled or failed, click 'Close' and try again.");
            }
            catch (Exception ex)
            {
                ShowResult(false, "Failed to launch installer", ex.Message);
            }
        }

        private void ShowResult(bool success, string message, string detail)
        {
            InstallingPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;
            ResultButtons.Visibility = Visibility.Visible;

            if (success)
            {
                ResultIcon.Text = "OK";
                ResultIcon.Foreground = (Brush)FindResource("SuccessBrush");
                RetryButton.Visibility = Visibility.Visible;
            }
            else
            {
                ResultIcon.Text = "FAILED";
                ResultIcon.Foreground = (Brush)FindResource("ErrorBrush");
                RetryButton.Visibility = Visibility.Collapsed;
            }

            ResultText.Text = message;
            ResultDetailText.Text = detail;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            ShouldRetry = false;
            DialogResult = _driverInstalled;
            Close();
        }

        private void Retry_Click(object sender, RoutedEventArgs e)
        {
            ShouldRetry = true;
            DialogResult = true;
            Close();
        }
    }
}
