using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HDRGammaController.Core;

namespace HDRGammaController.ViewModels
{
    public class DashboardItem : ObservableObject
    {
        private MonitorInfo _model = new MonitorInfo();
        private string _friendlyName = "";
        private string _badgeText = "";
        private Brush _badgeColor = Brushes.Gray;
        private GammaMode _currentGamma;
        private double _currentBrightness;
        private string _currentTemperatureText = "";
        private bool _isCalibrating;

        public MonitorInfo Model
        {
            get => _model;
            set => SetProperty(ref _model, value);
        }

        public string FriendlyName
        {
            get => _friendlyName;
            set => SetProperty(ref _friendlyName, value);
        }

        public string BadgeText
        {
            get => _badgeText;
            set => SetProperty(ref _badgeText, value);
        }

        public Brush BadgeColor
        {
            get => _badgeColor;
            set => SetProperty(ref _badgeColor, value);
        }

        public GammaMode CurrentGamma
        {
            get => _currentGamma;
            set => SetProperty(ref _currentGamma, value);
        }

        public double CurrentBrightness
        {
            get => _currentBrightness;
            set => SetProperty(ref _currentBrightness, value);
        }

        public string CurrentTemperatureText
        {
            get => _currentTemperatureText;
            set => SetProperty(ref _currentTemperatureText, value);
        }

        /// <summary>
        /// A calibration is measuring this display with corrections bypassed; the card
        /// fades its (inactive) settings and shows a badge.
        /// </summary>
        public bool IsCalibrating
        {
            get => _isCalibrating;
            set => SetProperty(ref _isCalibrating, value);
        }

        internal void UpdateFrom(DashboardItem updated)
        {
            Model = updated.Model;
            FriendlyName = updated.FriendlyName;
            BadgeText = updated.BadgeText;
            BadgeColor = updated.BadgeColor;
            CurrentGamma = updated.CurrentGamma;
            CurrentBrightness = updated.CurrentBrightness;
            CurrentTemperatureText = updated.CurrentTemperatureText;
            IsCalibrating = updated.IsCalibrating;
        }
    }
}
