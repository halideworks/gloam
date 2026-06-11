using HDRGammaController.Core;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HDRGammaController.ViewModels
{
    public class AppExclusionItem : ObservableObject
    {
        public const string Placeholder = "Select running app...";

        public ObservableCollection<AppExclusionRule> ExcludedApps { get; } = new ObservableCollection<AppExclusionRule>();
        public ObservableCollection<string> RunningApps { get; } = new ObservableCollection<string>();

        private string _newAppText = Placeholder;
        public string NewAppText
        {
            get => _newAppText;
            set => SetProperty(ref _newAppText, value);
        }
    }
}
