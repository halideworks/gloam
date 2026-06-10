using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController
{
    /// <summary>
    /// Housekeeping for the calibration profiles this app has installed for a monitor.
    /// Every calibration leaves its .icm in the Windows color store by design (so a known-
    /// good calibration is never destroyed); this window lists them and lets the user
    /// activate an older one, deactivate, or delete outright.
    /// </summary>
    public sealed class ProfileManagerWindow : Window
    {
        private static readonly string ColorStore = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");

        private readonly MonitorInfo _monitor;
        private readonly SettingsManager? _settings;
        private readonly ListView _list;
        private readonly TextBlock _status;

        private sealed record Row(string Name, string Modified, string SizeKb, string State);

        public ProfileManagerWindow(MonitorInfo monitor, SettingsManager? settings)
        {
            _monitor = monitor;
            _settings = settings;

            Title = $"Calibration Profiles - {monitor.FriendlyName}";
            Width = 720;
            Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
            Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0));

            _list = new ListView
            {
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3f, 0x3f, 0x3f)),
                Margin = new Thickness(0, 0, 0, 10),
            };
            var grid = new GridView();
            grid.Columns.Add(Col("Profile", nameof(Row.Name), 360));
            grid.Columns.Add(Col("Created", nameof(Row.Modified), 130));
            grid.Columns.Add(Col("Size", nameof(Row.SizeKb), 60));
            grid.Columns.Add(Col("State", nameof(Row.State), 90));
            _list.View = grid;

            Button Make(string label, RoutedEventHandler onClick, bool accent = false)
            {
                var b = new Button
                {
                    Content = label,
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(8, 0, 0, 0),
                    Background = new SolidColorBrush(accent ? Color.FromRgb(0x08, 0x91, 0xb2) : Color.FromRgb(0x3d, 0x3d, 0x3d)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                };
                b.Click += onClick;
                return b;
            }

            _status = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };

            var buttons = new Grid();
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var activate = Make("Activate", (_, _) => Activate(), accent: true);
            var deactivate = Make("Deactivate", (_, _) => Deactivate());
            var delete = Make("Delete…", (_, _) => Delete());
            var close = Make("Close", (_, _) => Close());
            Grid.SetColumn(activate, 1);
            Grid.SetColumn(deactivate, 2);
            Grid.SetColumn(delete, 3);
            Grid.SetColumn(close, 4);
            buttons.Children.Add(_status);
            buttons.Children.Add(activate);
            buttons.Children.Add(deactivate);
            buttons.Children.Add(delete);
            buttons.Children.Add(close);

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_list, 0);
            Grid.SetRow(buttons, 1);
            root.Children.Add(_list);
            root.Children.Add(buttons);
            Content = root;

            Refresh();
        }

        private static GridViewColumn Col(string header, string property, double width) => new()
        {
            Header = header,
            Width = width,
            DisplayMemberBinding = new System.Windows.Data.Binding(property),
        };

        private string? ActiveProfileName =>
            _settings?.GetMonitorProfile(_monitor.MonitorDevicePath)?.Mhc2ProfileName;

        private void Refresh()
        {
            try
            {
                string prefix = _monitor.FriendlyName.Trim() + " - ";
                var rows = Directory.GetFiles(ColorStore, "*.icm")
                    .Concat(Directory.GetFiles(ColorStore, "*.icc"))
                    .Select(p => new FileInfo(p))
                    .Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => new Row(
                        f.Name,
                        f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        $"{f.Length / 1024} KB",
                        string.Equals(f.Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase) ? "ACTIVE" : ""))
                    .ToList();
                _list.ItemsSource = rows;
                _status.Text = rows.Count == 0
                    ? "No calibration profiles found for this monitor."
                    : $"{rows.Count} profile(s) in the Windows color store for this monitor.";
            }
            catch (Exception ex)
            {
                _status.Text = $"Could not list profiles: {ex.Message}";
            }
        }

        private Row? Selected => _list.SelectedItem as Row;

        private void Activate()
        {
            if (Selected is not { } row) return;
            string? previous = ActiveProfileName;
            if (CalibrationProfileInstaller.Reenable(_monitor, row.Name, _monitor.IsHdrActive))
            {
                if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, row.Name, StringComparison.OrdinalIgnoreCase))
                    CalibrationProfileInstaller.Disable(_monitor, previous);
                _settings?.SetMhc2Calibration(_monitor.MonitorDevicePath, row.Name);
                _status.Text = $"Activated: {row.Name}";
            }
            else
            {
                _status.Text = "Windows refused the association - is the monitor active and in the matching SDR/HDR mode?";
            }
            Refresh();
        }

        private void Deactivate()
        {
            if (Selected is not { } row) return;
            CalibrationProfileInstaller.Disable(_monitor, row.Name);
            if (string.Equals(row.Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                _settings?.SetMhc2Calibration(_monitor.MonitorDevicePath, null);
            _status.Text = $"Deactivated: {row.Name} (file kept in the color store).";
            Refresh();
        }

        private void Delete()
        {
            if (Selected is not { } row) return;
            if (MessageBox.Show($"Permanently delete '{row.Name}' from the color store?\n\nThis cannot be undone.",
                    "Delete Profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            CalibrationProfileInstaller.Uninstall(_monitor, row.Name);
            if (string.Equals(row.Name, ActiveProfileName, StringComparison.OrdinalIgnoreCase))
                _settings?.SetMhc2Calibration(_monitor.MonitorDevicePath, null);
            _status.Text = $"Deleted: {row.Name}";
            Refresh();
        }
    }
}
