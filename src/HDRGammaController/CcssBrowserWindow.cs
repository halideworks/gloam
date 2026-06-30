using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController
{
    /// <summary>
    /// In-app browser for the DisplayCAL colorimeter corrections database: search by
    /// display name, pick a spectro-derived correction, and it lands in the corrections
    /// folder ready for the setup picker — no website round-trip.
    /// </summary>
    public sealed class CcssBrowserWindow : Window
    {
        private readonly TextBox _query;
        private readonly Button _searchButton;
        private readonly Button _downloadButton;
        private readonly ListView _list;
        private readonly TextBlock _status;
        private readonly string _saveFolder;

        /// <summary>Path of the downloaded correction file when the dialog returns true.</summary>
        public string? SavedPath { get; private set; }

        public CcssBrowserWindow(string initialQuery, string saveFolder)
        {
            _saveFolder = saveFolder;
            Title = "Find Meter Correction - DisplayCAL Community Database";
            Width = 760;
            Height = 520;
            MinWidth = 760;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16));
            Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFA));
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Gloam;component/Themes/DarkControls.xaml", UriKind.Absolute),
            });
            // Brutalist custom chrome (header + frame) is applied at the end of the ctor.

            _query = new TextBox
            {
                Text = initialQuery,
                FontSize = 13,
                FontFamily = Application.Current?.Resources["BodyFont"] as FontFamily,
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x23)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFA)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x55, 0x67)),
                BorderThickness = new Thickness(1),
                CaretBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFA)),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _query.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await SearchAsync(); };

            _searchButton = MakeButton("Search");
            _searchButton.Click += async (_, _) => await SearchAsync();

            _downloadButton = MakeButton("Download && Use");
            _downloadButton.IsEnabled = false;
            _downloadButton.Click += (_, _) => DownloadSelected();

            var closeButton = MakeButton("Cancel");
            closeButton.Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x23)); // secondary, not accent
            closeButton.Click += (_, _) => { DialogResult = false; Close(); };

            _list = new ListView
            {
                Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x23)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFA)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x55, 0x67)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 10, 0, 10),
            };
            var grid = new GridView();
            grid.Columns.Add(Col("Display", nameof(CcssDatabaseClient.Entry.Display), 250));
            grid.Columns.Add(Col("Type", nameof(CcssDatabaseClient.Entry.Type), 55));
            grid.Columns.Add(Col("Instrument", nameof(CcssDatabaseClient.Entry.Instrument), 130));
            grid.Columns.Add(Col("Measured with", nameof(CcssDatabaseClient.Entry.Reference), 150));
            grid.Columns.Add(Col("Created", nameof(CcssDatabaseClient.Entry.Created), 120));
            _list.View = grid;
            _list.SelectionChanged += (_, _) => _downloadButton.IsEnabled = _list.SelectedItem != null;
            _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem != null) DownloadSelected(); };

            _status = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB0, 0xBC)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Text = "Search the community database by display model. .ccss (spectral sample) entries are " +
                       "preferred for the i1 Display; ones measured with a spectro for YOUR panel model are best.",
                TextWrapping = TextWrapping.Wrap,
            };

            var topRow = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_searchButton, 1);
            _searchButton.Margin = new Thickness(8, 0, 0, 0);
            topRow.Children.Add(_query);
            topRow.Children.Add(_searchButton);

            var bottomRow = new Grid();
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_downloadButton, 1);
            Grid.SetColumn(closeButton, 2);
            _downloadButton.Margin = new Thickness(8, 0, 8, 0);
            bottomRow.Children.Add(_status);
            bottomRow.Children.Add(_downloadButton);
            bottomRow.Children.Add(closeButton);

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(topRow, 0);
            Grid.SetRow(_list, 1);
            Grid.SetRow(bottomRow, 2);
            root.Children.Add(topRow);
            root.Children.Add(_list);
            root.Children.Add(bottomRow);
            Services.BrutalistChrome.Apply(this, "Find Meter Correction", root);

            Loaded += async (_, _) => await SearchAsync();
        }

        private static Button MakeButton(string content) => new()
        {
            Content = content,
            Padding = new Thickness(14, 5, 14, 5),
            Background = new SolidColorBrush(Color.FromRgb(0xE3, 0x5F, 0x52)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0x5F, 0x52)),
            BorderThickness = new Thickness(1),
        };

        private static GridViewColumn Col(string header, string property, double width) => new()
        {
            Header = header,
            Width = width,
            DisplayMemberBinding = new System.Windows.Data.Binding(property),
        };

        private async Task SearchAsync()
        {
            _searchButton.IsEnabled = false;
            _status.Text = "Searching…";
            try
            {
                var results = await CcssDatabaseClient.SearchAsync(_query.Text);
                _list.ItemsSource = results;
                _status.Text = results.Count == 0
                    ? "No matches. Try fewer words (e.g. just the panel model number), or leave empty to browse recent entries."
                    : $"{results.Count} correction(s) found. .ccss entries work with any i1 Display; pick one made for your exact panel.";
            }
            catch (Exception ex)
            {
                _status.Text = ex.Message;
            }
            finally
            {
                _searchButton.IsEnabled = true;
            }
        }

        private void DownloadSelected()
        {
            if (_list.SelectedItem is not CcssDatabaseClient.Entry entry) return;
            try
            {
                SavedPath = CcssDatabaseClient.Save(entry, _saveFolder);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                _status.Text = $"Save failed: {ex.Message}";
            }
        }
    }
}
