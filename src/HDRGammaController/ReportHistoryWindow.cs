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
    /// Browser for past calibration reports. Every calibration saves a JSON snapshot of
    /// its report (profile + the displayed accuracy summary) to the saved-reports folder;
    /// this window lists them and re-opens any of them as a read-only report.
    /// </summary>
    public sealed class ReportHistoryWindow : Window
    {
        private readonly ListView _list;
        private readonly TextBlock _status;

        private sealed record Row(string Monitor, string Date, string Grade, string AvgDeltaE,
            string Target, CalibrationProfile Profile, string FilePath, DateTime SortKey);

        public ReportHistoryWindow()
        {
            Title = "Past Calibration Reports";
            Width = 780;
            Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
            Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0));
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Gloam;component/Themes/DarkControls.xaml", UriKind.Absolute),
            });
            // Brutalist custom chrome (header + frame) is applied at the end of the ctor.

            _list = new ListView
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 0, 0, 10),
            };
            var grid = new GridView();
            grid.Columns.Add(Col("Monitor", nameof(Row.Monitor), 230));
            grid.Columns.Add(Col("Date", nameof(Row.Date), 130));
            grid.Columns.Add(Col("Grade", nameof(Row.Grade), 60));
            grid.Columns.Add(Col("Avg dE", nameof(Row.AvgDeltaE), 70));
            grid.Columns.Add(Col("Target", nameof(Row.Target), 190));
            _list.View = grid;
            _list.MouseDoubleClick += (_, _) => Open();

            Button Make(string label, RoutedEventHandler onClick, bool accent = false)
            {
                var b = new Button
                {
                    Content = label,
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(8, 0, 0, 0),
                    Background = new SolidColorBrush(accent ? Color.FromRgb(0xFF, 0x3C, 0x2F) : Color.FromRgb(0x3d, 0x3d, 0x3d)),
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
            for (int i = 0; i < 3; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var open = Make("Open", (_, _) => Open(), accent: true);
            var delete = Make("Delete…", (_, _) => Delete());
            var close = Make("Close", (_, _) => Close());
            Grid.SetColumn(open, 1);
            Grid.SetColumn(delete, 2);
            Grid.SetColumn(close, 3);
            buttons.Children.Add(_status);
            buttons.Children.Add(open);
            buttons.Children.Add(delete);
            buttons.Children.Add(close);

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_list, 0);
            Grid.SetRow(buttons, 1);
            root.Children.Add(_list);
            root.Children.Add(buttons);
            Services.BrutalistChrome.Apply(this, "Past Calibration Reports", root);

            Refresh();
        }

        private static GridViewColumn Col(string header, string property, double width) => new()
        {
            Header = header,
            Width = width,
            DisplayMemberBinding = new System.Windows.Data.Binding(property),
        };

        private void Refresh()
        {
            try
            {
                var rows = Directory.GetFiles(CalibrationProfile.GetReportsDirectory(), "*.json")
                    .Select(MakeRow)
                    .Where(r => r != null)
                    .Select(r => r!)
                    .OrderByDescending(r => r.SortKey)
                    .ToList();
                _list.ItemsSource = rows;
                _status.Text = rows.Count == 0
                    ? "No saved reports yet. Each calibration saves its report here automatically."
                    : $"{rows.Count} saved report(s).";
            }
            catch (Exception ex)
            {
                _status.Text = $"Could not list saved reports: {ex.Message}";
            }
        }

        /// <summary>One file, one row; corrupt or foreign JSON is skipped, never fatal.</summary>
        private static Row? MakeRow(string path)
        {
            try
            {
                var profile = CalibrationProfile.LoadFromFile(path);
                DateTime when = (profile.LastCalibratedAt ?? profile.ModifiedAt).ToLocalTime();
                double? avg = profile.ReportSummary?.AfterAvgDeltaE
                              ?? profile.ReportSummary?.AvgDeltaE
                              ?? profile.PostCalibrationDeltaE;
                return new Row(
                    profile.MonitorName,
                    when.ToString("yyyy-MM-dd HH:mm"),
                    GradeLabel(profile.QualityGrade),
                    avg is { } a ? $"{a:F2}" : "-",
                    profile.Target.Name,
                    profile,
                    path,
                    when);
            }
            catch (Exception ex)
            {
                Log.Info($"ReportHistoryWindow: skipping unreadable report {path}: {ex.Message}");
                return null;
            }
        }

        private static string GradeLabel(CalibrationGrade? grade) => grade switch
        {
            CalibrationGrade.APLus => "A+",
            CalibrationGrade.A => "A",
            CalibrationGrade.AMinus => "A-",
            CalibrationGrade.BPlus => "B+",
            CalibrationGrade.B => "B",
            CalibrationGrade.BMinus => "B-",
            CalibrationGrade.CPlus => "C+",
            CalibrationGrade.C => "C",
            CalibrationGrade.CMinus => "C-",
            CalibrationGrade.D => "D",
            CalibrationGrade.F => "F",
            _ => "-",
        };

        private Row? Selected => _list.SelectedItem as Row;

        private void Open()
        {
            if (Selected is not { } row) return;
            // No metrics/measurements/apply context: the report opens in its read-only
            // historical mode, fed entirely from the saved profile JSON. Not owned by this
            // window so it survives closing the browser.
            new CalibrationReportWindow(row.Profile).Show();
            _status.Text = $"Opened: {row.Monitor} ({row.Date})";
        }

        private void Delete()
        {
            if (Selected is not { } row) return;
            if (!ConfirmDialog.Confirm(this, "Delete Report",
                    $"Permanently delete the saved report for '{row.Monitor}' from {row.Date}?\n\n" +
                    "This cannot be undone. The installed calibration profile is not affected.",
                    confirmLabel: "Delete"))
                return;
            try
            {
                File.Delete(row.FilePath);
                _status.Text = $"Deleted: {row.Monitor} ({row.Date})";
            }
            catch (Exception ex)
            {
                _status.Text = $"Could not delete the report: {ex.Message}";
            }
            Refresh();
        }
    }
}
