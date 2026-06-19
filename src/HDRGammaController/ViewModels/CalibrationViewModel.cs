using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HDRGammaController.ViewModels
{
    /// <summary>
    /// Display state for CalibrationWindow. The measurement orchestration itself stays
    /// in the window (it is entangled with window sizing, the HDR wire renderer and
    /// modal dialogs); this holds everything the XAML renders so the code-behind never
    /// touches controls directly.
    /// </summary>
    public class CalibrationViewModel : ObservableObject
    {
        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public static readonly Brush SuccessBrush = Frozen(0x4C, 0xAF, 0x50);
        public static readonly Brush WarningBrush = Frozen(0xFF, 0xA5, 0x00);
        public static readonly Brush ErrorBrush = Frozen(0xF4, 0x43, 0x36);

        #region Setup panel

        private bool _isFullScreenMode = true;
        public bool IsFullScreenMode
        {
            get => _isFullScreenMode;
            set
            {
                if (SetProperty(ref _isFullScreenMode, value))
                {
                    OnPropertyChanged(nameof(IsWindowedMode));
                }
            }
        }

        public bool IsWindowedMode
        {
            get => !_isFullScreenMode;
            set => IsFullScreenMode = !value;
        }

        private bool _alwaysOnTop = true;
        public bool AlwaysOnTop { get => _alwaysOnTop; set => SetProperty(ref _alwaysOnTop, value); }

        private bool _soundOnCompletion = true;
        public bool SoundOnCompletion { get => _soundOnCompletion; set => SetProperty(ref _soundOnCompletion, value); }

        private bool _soundOnCapture = true;
        public bool SoundOnCapture { get => _soundOnCapture; set => SetProperty(ref _soundOnCapture, value); }

        /// <summary>
        /// Pre-checks "Detailed verification" in the report window, so the automatic
        /// post-apply verify runs the extended sweep without another click.
        /// </summary>
        private bool _runDetailedVerify;
        public bool RunDetailedVerify { get => _runDetailedVerify; set => SetProperty(ref _runDetailedVerify, value); }

        private bool _showBypassWarning;
        public bool ShowBypassWarning { get => _showBypassWarning; set => SetProperty(ref _showBypassWarning, value); }

        private string _patchCountText = "";
        public string PatchCountText { get => _patchCountText; set => SetProperty(ref _patchCountText, value); }

        private string _estimatedTimeText = "";
        public string EstimatedTimeText { get => _estimatedTimeText; set => SetProperty(ref _estimatedTimeText, value); }

        #endregion

        #region Colorimeter status

        private Brush _colorimeterBrush = WarningBrush;
        public Brush ColorimeterBrush { get => _colorimeterBrush; set => SetProperty(ref _colorimeterBrush, value); }

        private string _colorimeterStatusText = "Checking...";
        public string ColorimeterStatusText { get => _colorimeterStatusText; set => SetProperty(ref _colorimeterStatusText, value); }

        private string _colorimeterModelText = "";
        public string ColorimeterModelText { get => _colorimeterModelText; set => SetProperty(ref _colorimeterModelText, value); }

        private bool _canStart;
        public bool CanStart { get => _canStart; set => SetProperty(ref _canStart, value); }

        #endregion

        #region Panels and banners

        private bool _isSetupVisible = true;
        public bool IsSetupVisible { get => _isSetupVisible; set => SetProperty(ref _isSetupVisible, value); }

        private bool _isPositioningVisible;
        public bool IsPositioningVisible { get => _isPositioningVisible; set => SetProperty(ref _isPositioningVisible, value); }

        private bool _isMeasurementVisible;
        public bool IsMeasurementVisible { get => _isMeasurementVisible; set => SetProperty(ref _isMeasurementVisible, value); }

        private bool _showPositioningWindowedBanner;
        public bool ShowPositioningWindowedBanner { get => _showPositioningWindowedBanner; set => SetProperty(ref _showPositioningWindowedBanner, value); }

        private bool _showWindowedModeBanner;
        public bool ShowWindowedModeBanner { get => _showWindowedModeBanner; set => SetProperty(ref _showWindowedModeBanner, value); }

        #endregion

        #region Measurement progress

        private double _progressPercent;
        private string? _progressLabelOverride;

        public double ProgressPercent
        {
            get => _progressPercent;
            set
            {
                _progressLabelOverride = null;
                SetProperty(ref _progressPercent, value);
                OnPropertyChanged(nameof(ProgressPercentText));
            }
        }

        public string ProgressPercentText => _progressLabelOverride ?? $"{_progressPercent:F0}%";

        /// <summary>Free-form override for the percent label ("Generating...").</summary>
        public void SetProgressLabel(string label)
        {
            _progressLabelOverride = label;
            OnPropertyChanged(nameof(ProgressPercentText));
        }

        private string _patchInfoText = "";
        public string PatchInfoText { get => _patchInfoText; set => SetProperty(ref _patchInfoText, value); }

        private string _phaseText = "";
        public string PhaseText { get => _phaseText; set => SetProperty(ref _phaseText, value); }

        private string _currentPatchText = "";
        public string CurrentPatchText { get => _currentPatchText; set => SetProperty(ref _currentPatchText, value); }

        private string _nextPatchText = "";
        public string NextPatchText { get => _nextPatchText; set => SetProperty(ref _nextPatchText, value); }

        private string _timeInfoText = "";
        public string TimeInfoText { get => _timeInfoText; set => SetProperty(ref _timeInfoText, value); }

        #endregion

        #region Pause / mute

        private bool _isPauseOverlayVisible;
        public bool IsPauseOverlayVisible { get => _isPauseOverlayVisible; set => SetProperty(ref _isPauseOverlayVisible, value); }

        private string _pauseButtonText = "Pause";
        public string PauseButtonText { get => _pauseButtonText; set => SetProperty(ref _pauseButtonText, value); }

        private bool _isPauseEnabled = true;
        public bool IsPauseEnabled { get => _isPauseEnabled; set => SetProperty(ref _isPauseEnabled, value); }

        private string _resumeCountdownText = "Click Resume to continue";
        public string ResumeCountdownText { get => _resumeCountdownText; set => SetProperty(ref _resumeCountdownText, value); }

        private string _muteButtonText = "Sound: On";
        public string MuteButtonText { get => _muteButtonText; set => SetProperty(ref _muteButtonText, value); }

        private bool _isCancelEnabled = true;
        public bool IsCancelEnabled { get => _isCancelEnabled; set => SetProperty(ref _isCancelEnabled, value); }

        #endregion

        #region Completion overlay

        private bool _isCompletionVisible;
        public bool IsCompletionVisible { get => _isCompletionVisible; set => SetProperty(ref _isCompletionVisible, value); }

        private string _completionIcon = "OK";
        public string CompletionIcon { get => _completionIcon; set => SetProperty(ref _completionIcon, value); }

        private Brush _completionIconBrush = SuccessBrush;
        public Brush CompletionIconBrush { get => _completionIconBrush; set => SetProperty(ref _completionIconBrush, value); }

        private string _completionTitle = "";
        public string CompletionTitle { get => _completionTitle; set => SetProperty(ref _completionTitle, value); }

        private string _completionMessage = "";
        public string CompletionMessage { get => _completionMessage; set => SetProperty(ref _completionMessage, value); }

        private bool _isViewReportVisible;
        public bool IsViewReportVisible { get => _isViewReportVisible; set => SetProperty(ref _isViewReportVisible, value); }

        private bool _showDisplayModeOptions;
        public bool ShowDisplayModeOptions { get => _showDisplayModeOptions; set => SetProperty(ref _showDisplayModeOptions, value); }

        private bool _isViewCalibrationOnly = true;
        public bool IsViewCalibrationOnly
        {
            get => _isViewCalibrationOnly;
            set
            {
                if (SetProperty(ref _isViewCalibrationOnly, value))
                {
                    OnPropertyChanged(nameof(IsViewWithPreviousSettings));
                }
            }
        }

        public bool IsViewWithPreviousSettings
        {
            get => !_isViewCalibrationOnly;
            set => IsViewCalibrationOnly = !value;
        }

        #endregion

        /// <summary>Sets the colorimeter status card in one call.</summary>
        public void SetColorimeterStatus(Brush brush, string status, string model, bool canStart)
        {
            ColorimeterBrush = brush;
            ColorimeterStatusText = status;
            ColorimeterModelText = model;
            CanStart = canStart;
        }
    }
}
