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
using HDRGammaController.Services;

namespace HDRGammaController
{
    /// <summary>
    /// In-app browser for the DisplayCAL colorimeter corrections database: search by
    /// display name, pick a spectro-derived correction, and it lands in the corrections
    /// folder ready for the setup picker — no website round-trip.
    /// </summary>
    public sealed partial class CcssBrowserWindow : Window
    {
        private readonly string _saveFolder;
        private readonly string? _typeFilter;
        private readonly string _displayName;

        // A spectral/ccmx capture owns the instrument and a Topmost patch window; while it
        // runs, Search / Download-&-close / Cancel-close must not tear the window (or the
        // instrument session) out from under it.
        private bool _captureInProgress;

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
            InitializeComponent();
            Title = title ?? "Find Meter Correction - DisplayCAL Community Database";
            _query.Text = initialQuery;
            _status.Text = introText ??
                "Search the community database by display model. .ccss spectral samples are preferred for an i1 Display; choose one measured for your panel model when possible.";
            _createButton.ToolTip = "Measure this panel's R/G/B/W emission spectra with a connected spectrometer " +
                                    "(i1 Pro, ColorMunki Photo/Design, i1 Studio) and generate a .ccss for it.";
            _createMatrixButton.ToolTip = "Measure the same White/Red/Green/Blue patches with your reference spectrometer " +
                                          "and your everyday colorimeter in turn, then generate a .ccmx correction matrix " +
                                          "that maps the colorimeter's readings onto the spectrometer's for this exact panel.";
            if (_typeFilter == "ccss")
                _createMatrixButton.Visibility = Visibility.Collapsed;
            _deleteButton.ToolTip = "Delete the selected saved correction file from disk. Only files saved in " +
                                    "Gloam's corrections folder can be deleted (online results cannot).";
            _dedupeButton.ToolTip = "Delete redundant saved correction files whose contents are identical, " +
                                    "keeping one copy of each.";
            BrutalistChrome.Apply(this, title ?? "Find Meter Correction", BodyRoot);
            Loaded += async (_, _) => await SearchAsync();
        }

        private async void Query_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                await SearchAsync();
        }

        private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
        private void Download_Click(object sender, RoutedEventArgs e) => DownloadSelected();
        private async void CreateSpectral_Click(object sender, RoutedEventArgs e) => await CreateFromSpectrometerAsync();
        private async void CreateMatrix_Click(object sender, RoutedEventArgs e) => await CreateCorrectionMatrixAsync();
        private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();
        private async void Dedupe_Click(object sender, RoutedEventArgs e) => await RemoveDuplicatesAsync();
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_captureInProgress) return;
            DialogResult = false;
            Close();
        }

        private void Correction_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _downloadButton.IsEnabled = !_captureInProgress && _list.SelectedItem != null;
            _downloadButton.Content = _list.SelectedItem is CcssDatabaseClient.Entry { LocalPath: not null }
                ? "Use Saved"
                : "Download & Use";
            _deleteButton.IsEnabled = !_captureInProgress &&
                _list.SelectedItem is CcssDatabaseClient.Entry { LocalPath: not null };
        }

        private void Correction_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_list.SelectedItem != null) DownloadSelected();
        }

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
            ProbeOperationScope? probe = null;
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

                using var cts = new CancellationTokenSource();
                _status.Text = "Measuring emission spectra… watch the patch window.";
                probe = await ProbeOperationScope.StartAsync(new ProbeOperationScope.Options(
                    monitor,
                    colorimeter,
                    "Emission spectrum capture",
                    PatchSize: 400,
                    Session: ProbeOperationScope.SessionKind.Spectral,
                    ConfigurePatchWindow: window => window.SetColor(1, 1, 1),
                    CancellationToken: cts.Token));
                var patchWindow = probe.PatchWindow;

                var window = patchWindow; // non-null capture for the delegates
                var capture = new SpectralCaptureService(
                    rgb => { window.SetColor(rgb.R, rgb.G, rgb.B); return Task.CompletedTask; },
                    ct => colorimeter.MeasureSpectralAsync(ct))
                {
                    Progress = (done, total, name) =>
                        window.SetProgress(done, total, name, null, phase: $"Measuring {name} spectrum"),
                };

                var spectra = await capture.CaptureAsync(probe.Token);

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
                if (probe != null)
                    await probe.DisposeAsync();
                if (colorimeter != null)
                {
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
            ProbeOperationScope? probe = null;
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

                using var cts = new CancellationTokenSource();
                probe = await ProbeOperationScope.StartAsync(new ProbeOperationScope.Options(
                    monitor,
                    first,
                    $"Correction matrix · {firstInfo.Model}",
                    PatchSize: 400,
                    ConfigurePatchWindow: window => window.SetColor(1, 1, 1),
                    CancellationToken: cts.Token));
                var patchWindow = probe.PatchWindow;

                var window = patchWindow; // non-null capture for the delegates
                ColorimeterInfo? secondInfo = null;

                var capture = new MeterOffsetCaptureService(
                    rgb => { window.SetColor(rgb.R, rgb.G, rgb.B); return Task.CompletedTask; },
                    ct => MeasureXyzOnceAsync(first!, ct),
                    async ct =>
                    {
                        // Phase 1 done: release the first instrument's USB handle so the
                        // second instrument can be attached and detected.
                        await probe.EndSessionAsync();
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

                _status.Text = $"Phase 1: measuring with {firstInfo.Model}… watch the patch window.";
                var readings = await capture.CaptureAsync(probe.Token);
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
                if (probe != null)
                    await probe.DisposeAsync();
                if (first != null)
                {
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
