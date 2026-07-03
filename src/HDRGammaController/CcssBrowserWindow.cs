using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HDRGammaController.Core;
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
        private readonly Button _createButton;
        private readonly ListView _list;
        private readonly TextBlock _status;
        private readonly string _saveFolder;
        private readonly string? _typeFilter;
        private readonly string _displayName;

        /// <summary>Path of the downloaded correction file when the dialog returns true.</summary>
        public string? SavedPath { get; private set; }

        public CcssBrowserWindow(
            string initialQuery,
            string saveFolder,
            string? typeFilter = null,
            string? title = null,
            string? introText = null)
        {
            _saveFolder = saveFolder;
            _displayName = initialQuery?.Trim() ?? "";
            _typeFilter = string.IsNullOrWhiteSpace(typeFilter) ? null : typeFilter.Trim().ToLowerInvariant();
            Title = title ?? "Find Meter Correction - DisplayCAL Community Database";
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

            _downloadButton = MakeButton("Download & Use");
            _downloadButton.IsEnabled = false;
            _downloadButton.Click += (_, _) => DownloadSelected();

            _createButton = MakeButton("Create from spectrometer…");
            _createButton.Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x23)); // secondary, not accent
            _createButton.ToolTip = "Measure this panel's R/G/B/W emission spectra with a connected spectrometer " +
                                    "(i1 Pro, ColorMunki Photo/Design, i1 Studio) and generate a .ccss for it.";
            _createButton.Click += async (_, _) => await CreateFromSpectrometerAsync();

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
            grid.Columns.Add(Col("Source", nameof(CcssDatabaseClient.Entry.Source), 70));
            grid.Columns.Add(Col("Display", nameof(CcssDatabaseClient.Entry.Display), 250));
            grid.Columns.Add(Col("Type", nameof(CcssDatabaseClient.Entry.Type), 55));
            grid.Columns.Add(Col("Instrument", nameof(CcssDatabaseClient.Entry.Instrument), 130));
            grid.Columns.Add(Col("Measured with", nameof(CcssDatabaseClient.Entry.Reference), 150));
            grid.Columns.Add(Col("Created", nameof(CcssDatabaseClient.Entry.Created), 120));
            _list.View = grid;
            _list.SelectionChanged += (_, _) =>
            {
                _downloadButton.IsEnabled = _list.SelectedItem != null;
                _downloadButton.Content = _list.SelectedItem is CcssDatabaseClient.Entry { LocalPath: not null }
                    ? "Use Saved"
                    : "Download & Use";
            };
            _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem != null) DownloadSelected(); };

            _status = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB0, 0xBC)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Text = introText ??
                       "Search the community database by display model. .ccss (spectral sample) entries are " +
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
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_createButton, 1);
            Grid.SetColumn(_downloadButton, 2);
            Grid.SetColumn(closeButton, 3);
            _createButton.Margin = new Thickness(8, 0, 0, 0);
            _downloadButton.Margin = new Thickness(8, 0, 8, 0);
            bottomRow.Children.Add(_status);
            bottomRow.Children.Add(_createButton);
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
            Services.BrutalistChrome.Apply(this, title ?? "Find Meter Correction", root);

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
                var saved = CcssDatabaseClient.ListSaved(_saveFolder, _query.Text, _typeFilter);
                IReadOnlyList<CcssDatabaseClient.Entry> online = Array.Empty<CcssDatabaseClient.Entry>();
                Exception? onlineError = null;
                try
                {
                    online = await CcssDatabaseClient.SearchAsync(_query.Text, _typeFilter);
                }
                catch (Exception ex)
                {
                    onlineError = ex;
                }

                var results = CcssDatabaseClient.MergePreferSaved(saved, online);
                _list.ItemsSource = results;
                _status.Text = results.Count == 0
                    ? onlineError != null
                        ? $"No saved matches, and online search failed: {onlineError.Message}"
                        : "No matches. Try fewer words (e.g. just the panel model number), or leave empty to browse recent entries."
                    : _typeFilter == "ccss"
                        ? $"{results.Count} spectral sample(s) found, including saved local files. Pick one made for your exact panel or closest display technology."
                        : $"{results.Count} correction(s) found, including saved local files. .ccss entries work with any i1 Display; pick one made for your exact panel.";
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
                SavedPath = entry.LocalPath ?? CcssDatabaseClient.Save(entry, _saveFolder);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                _status.Text = $"Save failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Measures this panel's R/G/B/W emission spectra with a connected spectrometer
        /// and writes a .ccss into the corrections folder. On success the new file is
        /// selected as the dialog result — it immediately becomes the active correction
        /// for the caller's colorimeter, exactly like a downloaded entry.
        /// </summary>
        private async Task CreateFromSpectrometerAsync()
        {
            _createButton.IsEnabled = false;
            _status.Text = "Looking for a spectrometer…";

            ColorimeterService? colorimeter = null;
            PatchDisplayWindow? patchWindow = null;
            try
            {
                string? argyllBin = ArgyllPathFinder.FindArgyllBinPath();
                if (argyllBin == null)
                {
                    _status.Text = "ArgyllCMS was not found. Start a calibration once so Gloam downloads it, then retry.";
                    return;
                }

                colorimeter = new ColorimeterService(argyllBin);
                if (!await colorimeter.InitializeAsync())
                {
                    _status.Text = "No instrument detected. Connect the spectrometer and try again.";
                    return;
                }
                if (!colorimeter.ConnectedInstrumentIsSpectrometer)
                {
                    _status.Text = $"'{colorimeter.ConnectedColorimeter?.Model ?? "The connected instrument"}' is not a spectrometer. " +
                                   "Spectral capture needs an i1 Pro, ColorMunki Photo/Design or i1 Studio.";
                    return;
                }

                var monitor = PickMonitorForCapture();
                if (monitor == null)
                {
                    _status.Text = "No display found to show measurement patches on.";
                    return;
                }
                Log.Info($"CcssBrowserWindow: spectral capture starting on '{monitor.FriendlyName}' " +
                         $"with '{colorimeter.ConnectedColorimeter?.Model}'.");

                // Positioning phase: white square, draggable, Enter/double-click to start.
                patchWindow = new PatchDisplayWindow(monitor, patchSize: 400);
                patchWindow.SetColor(1, 1, 1);
                patchWindow.EnableDrag();
                patchWindow.SetProgress(0, SpectralCaptureService.Patches.Count * 3, "White", "Red",
                    phase: "Drag the square under the spectrometer, then press Enter");

                using var cts = new CancellationTokenSource();
                var positioned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                patchWindow.ContinueRequested += () => positioned.TrySetResult(true);
                patchWindow.AbortRequested += () => { positioned.TrySetResult(false); cts.Cancel(); };
                patchWindow.Closed += (_, _) => { positioned.TrySetResult(false); cts.Cancel(); };
                patchWindow.Show();

                if (!await positioned.Task)
                {
                    _status.Text = "Spectral capture cancelled.";
                    return;
                }
                patchWindow.DisableDrag();

                _status.Text = "Measuring emission spectra… watch the patch window.";
                await colorimeter.BeginSpectralSessionAsync(cts.Token);

                var window = patchWindow; // non-null capture for the delegates
                var capture = new SpectralCaptureService(
                    rgb => { window.SetColor(rgb.R, rgb.G, rgb.B); return Task.CompletedTask; },
                    ct => colorimeter.MeasureSpectralAsync(ct))
                {
                    Progress = (done, total, name) =>
                        window.SetProgress(done, total, name, null, phase: $"Measuring {name} spectrum"),
                };

                var spectra = await capture.CaptureAsync(cts.Token);
                await colorimeter.EndSpectralSessionAsync();

                string displayName = !string.IsNullOrWhiteSpace(_displayName) ? _displayName : monitor.FriendlyName;
                string path = CcssWriter.SaveToFolder(
                    displayName,
                    colorimeter.ConnectedColorimeter?.Model ?? "Spectrometer",
                    spectra,
                    _saveFolder);
                Log.Info($"CcssBrowserWindow: spectral capture finished, CCSS saved to {path}.");

                SavedPath = path;
                DialogResult = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                _status.Text = "Spectral capture cancelled.";
            }
            catch (Exception ex)
            {
                Log.Info($"CcssBrowserWindow: spectral capture failed: {ex.Message}");
                _status.Text = $"Spectral capture failed: {ex.Message}";
            }
            finally
            {
                try { patchWindow?.Close(); } catch { /* already closed */ }
                if (colorimeter != null)
                {
                    try { await colorimeter.EndSpectralSessionAsync(); } catch { /* session already gone */ }
                    colorimeter.Dispose();
                }
                _createButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// The monitor this browser window is on (patches show where the user already is);
        /// falls back to the first enumerated display.
        /// </summary>
        private MonitorInfo? PickMonitorForCapture()
        {
            var monitors = new MonitorManager().EnumerateMonitors();
            if (monitors.Count == 0) return null;
            try
            {
                var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
                foreach (var monitor in monitors)
                {
                    var b = monitor.MonitorBounds;
                    if (center.X >= b.Left && center.X < b.Right && center.Y >= b.Top && center.Y < b.Bottom)
                        return monitor;
                }
            }
            catch
            {
                // PointToScreen can throw before the window is fully sourced; fall through.
            }
            return monitors[0];
        }
    }
}
