using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HDRGammaController.ViewModels
{
    public class ActionViewModel : ObservableObject
    {
        private string _header;
        public string Header
        {
            get => _header;
            set => SetProperty(ref _header, value);
        }

        public ICommand? Command { get; }
        public bool IsSeparator { get; }

        /// <summary>
        /// Keep the tray menu open when this item is clicked, so gamma modes
        /// can be swapped quickly without reopening the menu.
        /// </summary>
        public bool StaysOpenOnClick { get; }

        public ActionViewModel(string header, ICommand? command, bool staysOpenOnClick = false)
        {
            _header = header;
            Command = command;
            IsSeparator = false;
            StaysOpenOnClick = staysOpenOnClick;
        }

        public System.Collections.IEnumerable? SubItems => null;

        public ActionViewModel(bool isSeparator)
        {
            IsSeparator = true;
            _header = string.Empty;
            Command = null!;
        }
    }
}
