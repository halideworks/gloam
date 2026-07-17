using HDRGammaController.Core;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HDRGammaController.ViewModels
{
    public class AppExclusionItem : ObservableObject
    {
        public ObservableCollection<AppExclusionRule> ExcludedApps { get; } = new ObservableCollection<AppExclusionRule>();
        public ObservableCollection<string> RunningApps { get; } = new ObservableCollection<string>();

        // Keep placeholder copy out of the bound value. An actual placeholder string in an
        // editable ComboBox gets appended to typed input and can accidentally be persisted
        // as an executable name.
        private string _newAppText = string.Empty;
        public string NewAppText
        {
            get => _newAppText;
            set => SetProperty(ref _newAppText, value);
        }
    }
}
