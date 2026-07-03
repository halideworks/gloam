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
        private readonly Button _activateButton;

        // The last operation/refresh message; selection changes overlay it with a
        // multi-select hint and restore it when the selection drops back to 0 or 1.
        private string _baseStatus = "";

        private sealed record Row(string Name, string Modified, string SizeKb, string State);

        public ProfileManagerWindow(MonitorInfo monitor, SettingsManager? settings)
        {
            _monitor = monitor;
            _settings = settings;

            Title = $"Calibration Profiles - {monitor.FriendlyName}";
            Width = 720;
            Height = 460;
            MinWidth = 720;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.SetResourceReference(BackgroundProperty, "ThemeBg");
            this.SetResourceReference(ForegroundProperty, "ThemeText");
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Gloam;component/Themes/DarkControls.xaml", UriKind.Absolute),
            });
            // Brutalist custom chrome (header + frame) is applied at the end of the ctor.

            _list = new ListView
            {
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 10),
                SelectionMode = SelectionMode.Extended,
            };
            _list.SetResourceReference(BackgroundProperty, "ThemeSurface");
            _list.SetResourceReference(ForegroundProperty, "ThemeText");
            _list.SetResourceReference(Control.BorderBrushProperty, "ThemeBorder");
            _list.SelectionChanged += (_, _) => UpdateSelectionUi();
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
                    BorderThickness = new Thickness(1),
                };
                b.SetResourceReference(BackgroundProperty, accent ? "ThemeAccent" : "ThemeSurface");
                b.SetResourceReference(ForegroundProperty, accent ? "ThemeOnAccent" : "ThemeText");
                b.SetResourceReference(Control.BorderBrushProperty, accent ? "ThemeAccent" : "ThemeBorder");
                b.Click += onClick;
                return b;
            }

            _status = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            _status.SetResourceReference(ForegroundProperty, "ThemeTextDim");

            var buttons = new Grid();
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var activate = _activateButton = Make("Activate", (_, _) => ActivateSelectedProfile(), accent: true);
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
            Services.BrutalistChrome.Apply(this, "Calibration Profiles", root);

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

        private MonitorProfileData? ActiveMonitorProfile =>
            _settings?.GetMonitorProfile(_monitor.MonitorDevicePath);

        private void Refresh()
        {
            try
            {
                string prefix = CalibrationProfileInstaller.BuildProfileNamePrefix(_monitor);
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
                SetStatus(rows.Count == 0
                    ? "No calibration profiles found for this monitor."
                    : $"{rows.Count} profile(s) in the Windows color store for this monitor.");
            }
            catch (Exception ex)
            {
                SetStatus($"Could not list profiles: {ex.Message}");
            }
        }

        private Row? Selected => _list.SelectedItem as Row;

        private System.Collections.Generic.List<Row> SelectedRows => _list.SelectedItems.Cast<Row>().ToList();

        /// <summary>Records the operation/refresh message and re-renders the status line.</summary>
        private void SetStatus(string text)
        {
            _baseStatus = text;
            UpdateSelectionUi();
        }

        /// <summary>
        /// Activate targets exactly one profile (Windows has one active association per
        /// monitor), so it is disabled unless a single row is selected; Deactivate and
        /// Delete operate on the whole selection.
        /// </summary>
        private void UpdateSelectionUi()
        {
            if (_activateButton is null) return; // ctor ordering guard
            int count = _list.SelectedItems.Count;
            _activateButton.IsEnabled = count == 1;
            _status.Text = count > 1
                ? $"{count} profiles selected. Deactivate and Delete apply to all of them; Activate needs a single selection."
                : _baseStatus;
        }

        private void ActivateSelectedProfile()
        {
            if (_list.SelectedItems.Count != 1 || Selected is not { } row) return;
            string? previous = ActiveProfileName;
            var saved = ActiveMonitorProfile;
            string? previousDefault = CalibrationProfileInstaller.SelectPreviousProfileBackup(
                CalibrationProfileInstaller.GetCurrentDefaultProfile(_monitor, _monitor.IsHdrActive),
                previous,
                saved?.PreviousColorProfileName);

            if (CalibrationProfileInstaller.Reenable(_monitor, row.Name, _monitor.IsHdrActive))
            {
                if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, row.Name, StringComparison.OrdinalIgnoreCase))
                    CalibrationProfileInstaller.Disable(_monitor, previous);
                _settings?.SetMhc2Calibration(
                    _monitor.MonitorDevicePath,
                    row.Name,
                    previousDefault,
                    _monitor.IsHdrActive);
                SetStatus($"Activated: {row.Name}");
            }
            else
            {
                SetStatus("Windows refused the association - is the monitor active and in the matching SDR/HDR mode?");
            }
            Refresh();
        }

        private void Deactivate()
        {
            var rows = SelectedRows;
            if (rows.Count == 0) return;
            string? activeBefore = ActiveProfileName;
            bool touchedActive = false;
            string restoreMessage = "";
            foreach (var row in rows)
            {
                bool wasActive = string.Equals(row.Name, activeBefore, StringComparison.OrdinalIgnoreCase);
                CalibrationProfileInstaller.Disable(_monitor, row.Name);
                if (wasActive)
                {
                    touchedActive = true;
                    restoreMessage = RestorePreviousProfileIfAvailable();
                    _settings?.SetMhc2Calibration(_monitor.MonitorDevicePath, null);
                }
            }
            if (!string.IsNullOrEmpty(restoreMessage))
                SetStatus(restoreMessage);
            else
            {
                SetStatus(rows.Count == 1
                    ? $"Deactivated: {rows[0].Name} (file kept in the color store)."
                    : $"Deactivated {rows.Count} profiles (files kept in the color store)." +
                      (touchedActive ? " No previous Windows profile was recorded to restore." : ""));
            }
            Refresh();
        }

        private void Delete()
        {
            var rows = SelectedRows;
            if (rows.Count == 0) return;
            string message = rows.Count == 1
                ? $"Permanently delete '{rows[0].Name}' from the color store?\n\nThis cannot be undone."
                : $"Permanently delete these {rows.Count} profiles from the color store?\n\n" +
                  string.Join("\n", rows.Select(r => r.Name)) +
                  "\n\nThis cannot be undone.";
            if (!ConfirmDialog.Confirm(this, rows.Count == 1 ? "Delete Profile" : "Delete Profiles",
                    message, "Delete", "Cancel"))
                return;
            string? activeBefore = ActiveProfileName;
            string restoreMessage = "";
            foreach (var row in rows)
            {
                bool wasActive = string.Equals(row.Name, activeBefore, StringComparison.OrdinalIgnoreCase);
                CalibrationProfileInstaller.Uninstall(_monitor, row.Name);
                if (wasActive)
                {
                    restoreMessage = RestorePreviousProfileIfAvailable();
                    _settings?.SetMhc2Calibration(_monitor.MonitorDevicePath, null);
                }
            }
            if (!string.IsNullOrEmpty(restoreMessage))
                SetStatus(restoreMessage);
            else
                SetStatus(rows.Count == 1 ? $"Deleted: {rows[0].Name}" : $"Deleted {rows.Count} profiles.");
            Refresh();
        }

        private string RestorePreviousProfileIfAvailable()
        {
            var profile = ActiveMonitorProfile;
            string? previous = profile?.PreviousColorProfileName;
            if (string.IsNullOrWhiteSpace(previous))
                return "";

            bool hdrMode = profile?.PreviousColorProfileHdrMode ?? _monitor.IsHdrActive;
            if (CalibrationProfileInstaller.RestoreDefaultProfile(_monitor, previous, hdrMode))
            {
                _settings?.ClearMhc2PreviousColorProfile(_monitor.MonitorDevicePath);
                return $"Restored previous Windows color profile: {previous}";
            }

            return $"Gloam profile disabled, but Windows refused to restore previous profile: {previous}";
        }
    }
}
