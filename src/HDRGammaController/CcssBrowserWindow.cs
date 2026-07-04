using System;
using System.IO;
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
        private readonly Button _createMatrixButton;
        private readonly Button _deleteButton;
        private readonly Button _dedupeButton;
        private readonly Button _closeButton;
        private readonly ListView _list;
        private readonly TextBlock _status;
        private readonly string _saveFolder;
        private readonly string? _typeFilter;
        private readonly string _displayName;

        // A spectral/ccmx capture owns the instrument and a Topmost patch window; while it
        // runs, Search / Download-&-close / Cancel-close must not tear the window (or the
        // instrument session) out from under it.
        private bool _captureInProgress;

        /// <summary>Path of the downloaded correction file when the dialog returns true.</summary>
        public string? SavedPath { get; private set; }

        // Accent style for the two primary actions (Search, Download & Use), built from the
        // Theme* tokens so it follows the light/dark swap. The normal/secondary buttons are
        // left to the reactive DarkControls implicit Button style. Its hover keeps the accent
        // fill (unlike the implicit template's grey ThemeHover) and just adds a themed ring.
        private static readonly Style PrimaryButtonStyle = (Style)System.Windows.Markup.XamlReader.Parse(
            @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                     xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                     TargetType='Button'>
                <Setter Property='Background' Value='{DynamicResource ThemeAccent}'/>
                <Setter Property='Foreground' Value='{DynamicResource ThemeOnAccent}'/>
                <Setter Property='FontFamily' Value='{DynamicResource DisplayFont}'/>
                <Setter Property='FontWeight' Value='Bold'/>
                <Setter Property='FontSize' Value='12'/>
                <Setter Property='Cursor' Value='Hand'/>
                <Setter Property='Padding' Value='14,6'/>
                <Setter Property='Template'>
                  <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                      <Border x:Name='Bd' Background='{TemplateBinding Background}'
                              BorderBrush='{DynamicResource ThemeAccent}' BorderThickness='1'
                              Padding='{TemplateBinding Padding}'>
                        <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property='IsMouseOver' Value='True'>
                          <Setter TargetName='Bd' Property='Opacity' Value='0.88'/>
                          <Setter TargetName='Bd' Property='BorderBrush' Value='{DynamicResource ThemeOnAccent}'/>
                        </Trigger>
                        <Trigger Property='IsEnabled' Value='False'>
                          <Setter TargetName='Bd' Property='Opacity' Value='0.40'/>
                        </Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>");

        // Type-column cell: a 1px chip so the decision-relevant .ccss/.ccmx distinction reads
        // at a glance — accent for ccss (the preferred spectral sample), ThemeAmber for ccmx.
        private static readonly DataTemplate TypeChipTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            @"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                            xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
                <Border BorderThickness='1' Padding='6,1' HorizontalAlignment='Left' VerticalAlignment='Center'>
                  <Border.Style>
                    <Style TargetType='Border'>
                      <Setter Property='BorderBrush' Value='{DynamicResource ThemeAccent}'/>
                      <Style.Triggers>
                        <DataTrigger Binding='{Binding Type}' Value='ccmx'>
                          <Setter Property='BorderBrush' Value='{DynamicResource ThemeAmber}'/>
                        </DataTrigger>
                      </Style.Triggers>
                    </Style>
                  </Border.Style>
                  <TextBlock Text='{Binding Type}' FontSize='11' FontWeight='Bold'
                             Foreground='{DynamicResource ThemeText}'/>
                </Border>
              </DataTemplate>");

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
            Height = 560;
            MinWidth = 760;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.SetResourceReference(BackgroundProperty, "ThemeBg");
            this.SetResourceReference(ForegroundProperty, "ThemeText");
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
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _query.SetResourceReference(BackgroundProperty, "ThemeSurface");
            _query.SetResourceReference(ForegroundProperty, "ThemeText");
            _query.SetResourceReference(Control.BorderBrushProperty, "ThemeBorder");
            _query.SetResourceReference(TextBox.CaretBrushProperty, "ThemeText");
            _query.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await SearchAsync(); };

            _searchButton = MakePrimaryButton("Search");
            _searchButton.Click += async (_, _) => await SearchAsync();

            _downloadButton = MakePrimaryButton("Download & Use");
            _downloadButton.IsEnabled = false;
            _downloadButton.Click += (_, _) => DownloadSelected();

            // Secondary actions — no inline colours; the reactive DarkControls implicit
            // Button style drives their fill/border/hover so they follow light/dark.
            _createButton = MakeButton("From spectrometer…");
            _createButton.ToolTip = "Measure this panel's R/G/B/W emission spectra with a connected spectrometer " +
                                    "(i1 Pro, ColorMunki Photo/Design, i1 Studio) and generate a .ccss for it.";
            _createButton.Click += async (_, _) => await CreateFromSpectrometerAsync();

            _createMatrixButton = MakeButton("Correction matrix…");
            _createMatrixButton.ToolTip = "Measure the same White/Red/Green/Blue patches with your reference spectrometer " +
                                          "and your everyday colorimeter in turn, then generate a .ccmx correction matrix " +
                                          "that maps the colorimeter's readings onto the spectrometer's for this exact panel.";
            _createMatrixButton.Click += async (_, _) => await CreateCorrectionMatrixAsync();
            if (_typeFilter == "ccss")
            {
                // A matrix correction is useless to callers that specifically want spectral
                // samples (e.g. the Ultra Night melanopic estimator).
                _createMatrixButton.Visibility = Visibility.Collapsed;
            }

            // Management of the saved-correction library. Both are available wherever this
            // browser is opened (calibration setup and the night-mode spectrum picker), so
            // corrections can be tidied from anywhere they're chosen. Delete acts on the
            // selected saved file; Remove duplicates cleans the whole corrections folder.
            _deleteButton = MakeButton("Delete");
            _deleteButton.IsEnabled = false;
            _deleteButton.ToolTip = "Delete the selected saved correction file from disk. Only files saved in " +
                                    "Gloam's corrections folder can be deleted (online results cannot).";
            _deleteButton.Click += async (_, _) => await DeleteSelectedAsync();

            _dedupeButton = MakeButton("Remove duplicates");
            _dedupeButton.ToolTip = "Delete redundant saved correction files whose contents are identical, " +
                                    "keeping one copy of each.";
            _dedupeButton.Click += async (_, _) => await RemoveDuplicatesAsync();

            _closeButton = MakeButton("Cancel");
            _closeButton.Click += (_, _) => { if (_captureInProgress) return; DialogResult = false; Close(); };

            _list = new ListView
            {
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 10, 0, 10),
            };
            _list.SetResourceReference(BackgroundProperty, "ThemeSurface");
            _list.SetResourceReference(ForegroundProperty, "ThemeText");
            _list.SetResourceReference(Control.BorderBrushProperty, "ThemeBorder");
            var grid = new GridView();
            grid.Columns.Add(Col("Source", nameof(CcssDatabaseClient.Entry.Source), 70));
            grid.Columns.Add(Col("Display", nameof(CcssDatabaseClient.Entry.Display), 250));
            grid.Columns.Add(ChipCol("Type", 64));
            grid.Columns.Add(Col("Instrument", nameof(CcssDatabaseClient.Entry.Instrument), 130));
            grid.Columns.Add(Col("Measured with", nameof(CcssDatabaseClient.Entry.Reference), 150));
            grid.Columns.Add(Col("Created", nameof(CcssDatabaseClient.Entry.Created), 120));
            _list.View = grid;
            _list.SelectionChanged += (_, _) =>
            {
                _downloadButton.IsEnabled = !_captureInProgress && _list.SelectedItem != null;
                _downloadButton.Content = _list.SelectedItem is CcssDatabaseClient.Entry { LocalPath: not null }
                    ? "Use Saved"
                    : "Download & Use";
                // Only saved files (those with a LocalPath) live on disk and can be deleted.
                _deleteButton.IsEnabled = !_captureInProgress
                    && _list.SelectedItem is CcssDatabaseClient.Entry { LocalPath: not null };
            };
            _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem != null) DownloadSelected(); };

            _status = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Text = introText ??
                       "Search the community database by display model. .ccss (spectral sample) entries are " +
                       "preferred for the i1 Display; ones measured with a spectro for YOUR panel model are best.",
                TextWrapping = TextWrapping.Wrap,
            };
            _status.SetResourceReference(ForegroundProperty, "ThemeTextDim");

            var topRow = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_searchButton, 1);
            _searchButton.Margin = new Thickness(8, 0, 0, 0);
            topRow.Children.Add(_query);
            topRow.Children.Add(_searchButton);

            // Bottom row: "make a new correction" (status + the two Create actions) reads on
            // the left, distinct from "commit selection / leave" (Download & Use + Cancel) on
            // the right. Grouping the Create actions lets the window drop back to its natural
            // ~760px instead of the old five-across 940px.
            _createMatrixButton.Margin = new Thickness(8, 0, 0, 0);
            var createRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            createRow.Children.Add(_createButton);
            createRow.Children.Add(_createMatrixButton);

            // Second action row: manage the saved library (distinct from the "make a new
            // correction" Create actions above).
            _dedupeButton.Margin = new Thickness(8, 0, 0, 0);
            var manageRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            manageRow.Children.Add(_deleteButton);
            manageRow.Children.Add(_dedupeButton);

            var leftPanel = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Bottom };
            leftPanel.Children.Add(_status);
            leftPanel.Children.Add(createRow);
            leftPanel.Children.Add(manageRow);

            _downloadButton.Margin = new Thickness(0, 0, 8, 0);
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 0, 0),
            };
            rightPanel.Children.Add(_downloadButton);
            rightPanel.Children.Add(_closeButton);

            var bottomRow = new Grid();
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(leftPanel, 0);
            Grid.SetColumn(rightPanel, 1);
            bottomRow.Children.Add(leftPanel);
            bottomRow.Children.Add(rightPanel);

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
            Padding = new Thickness(14, 6, 14, 6),
        };

        private static Button MakePrimaryButton(string content) => new()
        {
            Content = content,
            Style = PrimaryButtonStyle,
        };

        private static GridViewColumn Col(string header, string property, double width) => new()
        {
            Header = header,
            Width = width,
            DisplayMemberBinding = new System.Windows.Data.Binding(property),
        };

        private static GridViewColumn ChipCol(string header, double width) => new()
        {
            Header = header,
            Width = width,
            CellTemplate = TypeChipTemplate,
        };

        /// <summary>
        /// Reflects a running instrument capture across the window: it owns the instrument
        /// and a Topmost patch surface, so both Create actions and the Search / Download /
        /// Cancel controls that could close the window are disabled for its duration.
        /// </summary>
        private void SetCaptureBusy(bool busy)
        {
            _captureInProgress = busy;
            _searchButton.IsEnabled = !busy;
            _query.IsEnabled = !busy;
            _createButton.IsEnabled = !busy;
            _createMatrixButton.IsEnabled = !busy;
            _dedupeButton.IsEnabled = !busy;
            _deleteButton.IsEnabled = !busy
                && _list.SelectedItem is CcssDatabaseClient.Entry { LocalPath: not null };
            _closeButton.IsEnabled = !busy;
            _downloadButton.IsEnabled = !busy && _list.SelectedItem != null;
        }

        /// <summary>
        /// Deletes the selected saved correction file after a confirm, then refreshes the list.
        /// Online results have no local file and are filtered out by the button's enabled state.
        /// </summary>
        private async Task DeleteSelectedAsync()
        {
            if (_captureInProgress) return;
            if (_list.SelectedItem is not CcssDatabaseClient.Entry { LocalPath: { } path }) return;

            bool confirmed = ConfirmDialog.Confirm(
                this,
                "Delete correction",
                $"Delete this saved correction file from disk?\n\n{Path.GetFileName(path)}\n\nThis cannot be undone.",
                "Delete",
                "Cancel");
            if (!confirmed) return;

            try
            {
                CcssDatabaseClient.Delete(path);
                _status.Text = $"Deleted {Path.GetFileName(path)}.";
                await SearchAsync();
            }
            catch (Exception ex)
            {
                _status.Text = $"Delete failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Removes content-duplicate files from the corrections folder, then refreshes the list.
        /// The list already hides content-dupes, so this reclaims copies the user cannot see.
        /// </summary>
        private async Task RemoveDuplicatesAsync()
        {
            if (_captureInProgress) return;
            try
            {
                int removed = CcssDatabaseClient.RemoveDuplicates(_saveFolder);
                _status.Text = removed == 0
                    ? "No duplicate correction files found."
                    : $"Removed {removed} duplicate correction file(s).";
                await SearchAsync();
            }
            catch (Exception ex)
            {
                _status.Text = $"Cleanup failed: {ex.Message}";
            }
        }

        private async Task SearchAsync()
        {
            if (_captureInProgress) return;
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
                // A capture may not start during a search (its buttons are disabled), so it is
                // safe to unconditionally restore Search here.
                _searchButton.IsEnabled = true;
            }
        }

        private void DownloadSelected()
        {
            if (_captureInProgress) return;
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
            if (_captureInProgress) return;
            SetCaptureBusy(true);
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
                SetCaptureBusy(false);
            }
        }

        /// <summary>
        /// Measures the same W/R/G/B patches with a reference spectrometer and an everyday
        /// colorimeter in turn (either connected first) and writes a .ccmx correction
        /// matrix into the corrections folder. On success the new file is selected as the
        /// dialog result — it immediately becomes the active correction for the caller's
        /// colorimeter, exactly like a downloaded entry (spotread's -X takes .ccmx and
        /// .ccss alike).
        /// </summary>
        private async Task CreateCorrectionMatrixAsync()
        {
            if (_captureInProgress) return;
            SetCaptureBusy(true);
            _status.Text = "Looking for the first instrument…";

            ColorimeterService? first = null;
            ColorimeterService? second = null;
            PatchDisplayWindow? patchWindow = null;
            try
            {
                string? argyllBin = ArgyllPathFinder.FindArgyllBinPath();
                if (argyllBin == null)
                {
                    _status.Text = "ArgyllCMS was not found. Start a calibration once so Gloam downloads it, then retry.";
                    return;
                }

                first = new ColorimeterService(argyllBin);
                if (!await first.InitializeAsync())
                {
                    _status.Text = "No instrument detected. Connect either instrument (the other one comes after the swap) and try again.";
                    return;
                }
                var firstInfo = first.ConnectedColorimeter!;

                var monitor = PickMonitorForCapture();
                if (monitor == null)
                {
                    _status.Text = "No display found to show measurement patches on.";
                    return;
                }
                Log.Info($"CcssBrowserWindow: two-instrument CCMX capture starting on '{monitor.FriendlyName}' " +
                         $"with '{firstInfo.Model}' as the first instrument.");

                // Positioning phase: white square, draggable, Enter/double-click to start.
                patchWindow = new PatchDisplayWindow(monitor, patchSize: 400);
                patchWindow.SetColor(1, 1, 1);
                patchWindow.EnableDrag();

                using var cts = new CancellationTokenSource();
                var positioned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                patchWindow.ContinueRequested += () => positioned.TrySetResult(true);
                patchWindow.AbortRequested += () => { positioned.TrySetResult(false); cts.Cancel(); };
                patchWindow.Closed += (_, _) => { positioned.TrySetResult(false); cts.Cancel(); };
                patchWindow.Show();

                var window = patchWindow; // non-null capture for the delegates
                ColorimeterInfo? secondInfo = null;

                var capture = new MeterOffsetCaptureService(
                    rgb => { window.SetColor(rgb.R, rgb.G, rgb.B); return Task.CompletedTask; },
                    ct => MeasureXyzOnceAsync(first!, ct),
                    async ct =>
                    {
                        // Phase 1 done: release the first instrument's USB handle so the
                        // second instrument can be attached and detected.
                        await first!.EndMeasurementSessionAsync();
                        first.Dispose();

                        // Themed confirm instead of a stock Win32 MessageBox: the old MessageBox
                        // could open BEHIND the Topmost patch window and stall the flow. Drop the
                        // patch window's Topmost around the prompt so the dialog is clearly visible
                        // above the patch surface (where the user is looking), then restore it.
                        bool wasTopmost = window.Topmost;
                        window.Topmost = false;
                        bool proceed = ConfirmDialog.Confirm(window,
                            "Swap instruments",
                            $"Phase 1 with '{firstInfo.Model}' is complete.\n\n" +
                            "1. Remove it from the panel and unplug it.\n" +
                            "2. Connect the OTHER instrument and place it over the same measurement square.\n" +
                            "3. Start phase 2 when ready.\n\n" +
                            "Do not change display settings or let the panel sleep in between.",
                            "Start phase 2", "Cancel");
                        window.Topmost = wasTopmost;
                        if (!proceed) return false;

                        _status.Text = "Looking for the second instrument…";
                        second = new ColorimeterService(argyllBin);
                        if (!await second.InitializeAsync(ct))
                            throw new InvalidOperationException(
                                "No instrument was detected after the swap. Check the USB connection and retry.");
                        secondInfo = second.ConnectedColorimeter!;
                        if (secondInfo.IsSpectrometer == firstInfo.IsSpectrometer)
                            throw new InvalidOperationException(
                                $"A correction matrix needs ONE reference spectrometer and ONE colorimeter, but " +
                                $"'{firstInfo.Model}' and '{secondInfo.Model}' are both " +
                                $"{(firstInfo.IsSpectrometer ? "spectrometers" : "colorimeters")}. " +
                                "Swap in the other instrument type and retry.");

                        _status.Text = $"Phase 2: measuring with {secondInfo.Model}… watch the patch window.";
                        await second.BeginMeasurementSessionAsync(hdrMode: false, ct);
                        return true;
                    },
                    ct => MeasureXyzOnceAsync(second!, ct))
                {
                    Progress = (done, total, name, phase) =>
                        window.SetProgress(done, total, name, null, phase: $"{phase}: measuring {name}"),
                };

                patchWindow.SetProgress(0, capture.TotalReads, "White", "Red",
                    phase: $"Drag the square under the {firstInfo.Model}, then press Enter");

                if (!await positioned.Task)
                {
                    _status.Text = "Correction-matrix capture cancelled.";
                    return;
                }
                patchWindow.DisableDrag();

                _status.Text = $"Phase 1: measuring with {firstInfo.Model}… watch the patch window.";
                await first.BeginMeasurementSessionAsync(hdrMode: false, cts.Token);

                var readings = await capture.CaptureAsync(cts.Token);
                await second!.EndMeasurementSessionAsync();

                // The ccmx maps colorimeter XYZ -> spectrometer XYZ, whichever phase each
                // instrument was measured in.
                bool firstIsReference = firstInfo.IsSpectrometer;
                var colorimeterReadings = firstIsReference ? readings.SecondInstrument : readings.FirstInstrument;
                var referenceReadings = firstIsReference ? readings.FirstInstrument : readings.SecondInstrument;
                string colorimeterName = (firstIsReference ? secondInfo : firstInfo)?.Model ?? "Colorimeter";
                string referenceName = (firstIsReference ? firstInfo : secondInfo)?.Model ?? "Spectrometer";

                var matrix = CcmxWriter.SolveCorrectionMatrix(colorimeterReadings, referenceReadings, whiteIndex: 0);

                string displayName = !string.IsNullOrWhiteSpace(_displayName) ? _displayName : monitor.FriendlyName;
                string path = CcmxWriter.SaveToFolder(displayName, colorimeterName, referenceName, matrix, _saveFolder);
                Log.Info($"CcssBrowserWindow: CCMX capture finished, matrix saved to {path}.");

                SavedPath = path;
                DialogResult = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                _status.Text = "Correction-matrix capture cancelled.";
            }
            catch (Exception ex)
            {
                Log.Info($"CcssBrowserWindow: CCMX capture failed: {ex.Message}");
                _status.Text = $"Correction-matrix capture failed: {ex.Message}";
            }
            finally
            {
                try { patchWindow?.Close(); } catch { /* already closed */ }
                if (first != null)
                {
                    try { await first.EndMeasurementSessionAsync(); } catch { /* session already gone */ }
                    first.Dispose();
                }
                if (second != null)
                {
                    try { await second.EndMeasurementSessionAsync(); } catch { /* session already gone */ }
                    second.Dispose();
                }
                SetCaptureBusy(false);
            }
        }

        /// <summary>
        /// One raw XYZ reading on an open measurement session, surfacing failures as
        /// exceptions (MeasureAsync's MeasurementResult swallows them otherwise).
        /// </summary>
        private static async Task<CieXyz> MeasureXyzOnceAsync(
            ColorimeterService service, CancellationToken cancellationToken)
        {
            var result = await service.MeasureAsync(
                new ColorPatch { Name = "ccmx patch", DisplayRgb = new LinearRgb(0, 0, 0) },
                hdrMode: false,
                cancellationToken: cancellationToken);
            if (!result.IsValid)
                throw new InvalidOperationException(result.ErrorMessage ?? "Measurement failed.");
            return result.Xyz;
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
