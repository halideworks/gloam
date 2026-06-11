using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController
{
    /// <summary>
    /// TEMPORARY diagnostic harness - strip after the HDR-range math is confirmed.
    ///
    /// One probe run answers every open question gating HDR-range calibration:
    ///   1. Does the FP16 scRGB swapchain create and report colorspace support?
    ///   2. PIPELINE PARITY - does the installed MHC2 profile apply to FP16 surfaces
    ///      identically to 8-bit SDR windows? (FP16 at SDR-white nits must match the SDR
    ///      window's white in luminance AND chromaticity.)
    ///   3. RANGE - do values above 1.0 scRGB reach the panel (no SDR-slider clamp)?
    ///   4. SDR MAPPING CURVE - is Windows' SDR-in-HDR linearization piecewise sRGB or
    ///      pure gamma 2.2? (Our HdrMhc2LutBuilder assumes sRGB; this measures it.)
    ///      Requires this monitor's gamma mode = Windows Default and night mode off, or
    ///      the GPU ramp contaminates the tone shape.
    ///   5. PQ TRACKING - FP16 patches at known nits give the panel's PQ response at
    ///      EXACT wire positions (no mapping assumption at all).
    /// </summary>
    public sealed class HdrSanityCheckWindow : Window
    {
        private readonly MonitorInfo _monitor;
        private readonly ColorimeterService _colorimeter;
        private readonly SettingsManager? _settings;
        private readonly TextBox _output;
        private readonly Button _runButton;

        public HdrSanityCheckWindow(MonitorInfo monitor, ColorimeterService colorimeter, SettingsManager? settings)
        {
            _monitor = monitor;
            _colorimeter = colorimeter;
            _settings = settings;

            Title = $"HDR Sanity Check (temporary) - {monitor.FriendlyName}";
            Width = 780;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
            Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0));

            _output = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3f, 0x3f, 0x3f)),
                Margin = new Thickness(0, 0, 0, 10),
                Text = "Place the probe on the display, then press Run.\r\n\r\n" +
                       "For the SDR-mapping check, set this monitor's gamma to Windows Default and turn " +
                       "night mode off first (the renderer checks are insensitive to the ramp; the " +
                       "mapping-curve check is not).",
            };

            _runButton = new Button
            {
                Content = "Run Sanity Check",
                Padding = new Thickness(16, 7, 16, 7),
                Background = new SolidColorBrush(Color.FromRgb(0x08, 0x91, 0xb2)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            _runButton.Click += async (_, _) => await RunAsync();

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_output, 0);
            Grid.SetRow(_runButton, 1);
            root.Children.Add(_output);
            root.Children.Add(_runButton);
            Content = root;
        }

        private void Say(string line)
        {
            _output.AppendText(line + "\r\n");
            _output.ScrollToEnd();
            Log.Info($"HdrSanityCheck: {line}");
        }

        private async Task RunAsync()
        {
            if (!_monitor.IsHdrActive)
            {
                MessageBox.Show("This monitor is not in HDR mode. Enable HDR and reopen this window.",
                    "HDR Sanity Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _runButton.IsEnabled = false;
            _output.Clear();
            PatchDisplayWindow? surround = null;
            HdrPatchRenderer? renderer = null;
            var patch = new ColorPatch { Name = "Sanity", DisplayRgb = new LinearRgb(1, 1, 1), Category = PatchCategory.General };

            async Task<CieXyz> Measure(int settleMs = 1200)
            {
                await Task.Delay(settleMs);
                var m = await _colorimeter.MeasureAsync(patch, hdrMode: true);
                return m.Xyz;
            }

            try
            {
                var prefs = _settings?.GetMonitorProfile(_monitor.MonitorDevicePath);
                if (prefs?.GammaMode is { } gm && gm != GammaMode.WindowsDefault)
                    Say($"WARNING: gamma mode is {gm}, not Windows Default - the SDR-mapping check below will be contaminated.");

                Say($"Monitor: {_monitor.FriendlyName}  SDR white {_monitor.SdrWhiteLevel:F0} nits  panel peak {_monitor.HdrPeakNits:F0} nits");
                Say(new string('-', 86));

                // Surround + patch geometry (600px patch, centered).
                surround = new PatchDisplayWindow(_monitor);
                surround.Show();
                surround.SetColor(0, 0, 0);
                var b = _monitor.MonitorBounds;
                const int size = 600;
                int px = b.Left + (b.Right - b.Left - size) / 2;
                int py = b.Top + (b.Bottom - b.Top - size) / 2;

                await _colorimeter.BeginMeasurementSessionAsync(hdrMode: true);

                // ---- 1. Renderer creation -------------------------------------------------
                Say("[1] Creating FP16 scRGB swapchain…");
                renderer = new HdrPatchRenderer(px, py, size, size);
                Say($"    created OK; CheckColorSpaceSupport(scRGB) = {(renderer.ScRgbSupported ? "SUPPORTED" : "NOT REPORTED (continuing - SetColorSpace1 accepted it)")}");

                // ---- 2. Pipeline parity ---------------------------------------------------
                Say("[2] Pipeline parity: SDR window white vs FP16 at the same nits…");
                renderer.Dispose(); renderer = null;            // SDR window visible first
                surround.SetColor(1, 1, 1);
                surround.SetProgress(1, 5, "SDR white");
                var sdrWhite = await Measure(1500);
                Say($"    SDR window white : {sdrWhite.Y,7:F1} nits  ({sdrWhite.ToChromaticity().X:F4}, {sdrWhite.ToChromaticity().Y:F4})");

                surround.SetColor(0, 0, 0);
                renderer = new HdrPatchRenderer(px, py, size, size);
                renderer.PresentNits(sdrWhite.Y, sdrWhite.Y, sdrWhite.Y);
                surround.SetProgress(2, 5, "FP16 at SDR white");
                var fp16Same = await Measure(1500);
                double ratio = fp16Same.Y / Math.Max(sdrWhite.Y, 1e-6);
                double dx = Math.Abs(fp16Same.ToChromaticity().X - sdrWhite.ToChromaticity().X);
                double dy = Math.Abs(fp16Same.ToChromaticity().Y - sdrWhite.ToChromaticity().Y);
                bool parity = ratio is > 0.90 and < 1.10 && dx < 0.006 && dy < 0.006;
                Say($"    FP16 same level  : {fp16Same.Y,7:F1} nits  ({fp16Same.ToChromaticity().X:F4}, {fp16Same.ToChromaticity().Y:F4})  ratio {ratio:F3}  Δxy ({dx:F4},{dy:F4})");
                Say($"    PARITY: {(parity ? "PASS - profile applies to FP16 identically" : "FAIL - FP16 takes a different pipeline path!")}");

                // ---- 3. Range ---------------------------------------------------------------
                double high = Math.Min(sdrWhite.Y * 2.0, _monitor.HdrPeakNits > 50 ? _monitor.HdrPeakNits * 0.9 : sdrWhite.Y * 2.0);
                Say($"[3] Range: FP16 at {high:F0} nits (briefly bright)…");
                renderer.PresentNits(high, high, high);
                surround.SetProgress(3, 5, $"FP16 at {high:F0} nits");
                var fp16High = await Measure(1500);
                bool range = fp16High.Y > sdrWhite.Y * 1.4;
                Say($"    measured: {fp16High.Y,7:F1} nits (requested {high:F0})");
                Say($"    RANGE: {(range ? "PASS - above-SDR-white emission works" : "FAIL - clamped at/near SDR white!")}");

                // ---- 4. SDR mapping curve ---------------------------------------------------
                Say("[4] Windows SDR-in-HDR mapping: measuring SDR window at 25/50/75% signal…");
                renderer.Dispose(); renderer = null;
                double srgbErr = 0, g22Err = 0;
                foreach (double v in new[] { 0.25, 0.50, 0.75 })
                {
                    surround.SetColor(v, v, v);
                    surround.SetProgress(4, 5, $"SDR {v:P0}");
                    var m = await Measure();
                    double measured = m.Y / Math.Max(sdrWhite.Y, 1e-6);
                    double srgbPred = TransferFunctions.SrgbEotf(v);
                    double g22Pred = Math.Pow(v, 2.2);
                    srgbErr += Math.Abs(measured - srgbPred) / srgbPred;
                    g22Err += Math.Abs(measured - g22Pred) / g22Pred;
                    Say($"    v={v:F2}: measured {measured:F4} of white  |  sRGB predicts {srgbPred:F4}  |  γ2.2 predicts {g22Pred:F4}");
                }
                Say(srgbErr < g22Err
                    ? $"    MAPPING: piecewise sRGB fits better (err {srgbErr:F3} vs {g22Err:F3}) - HdrMhc2LutBuilder assumption CONFIRMED"
                    : $"    MAPPING: gamma 2.2 fits better (err {g22Err:F3} vs {srgbErr:F3}) - HdrMhc2LutBuilder assumption WRONG, switch its linearization");

                // ---- 5. PQ tracking at exact wire positions ---------------------------------
                Say("[5] PQ tracking via FP16 (exact wire positions, no mapping assumption)…");
                surround.SetColor(0, 0, 0);
                renderer = new HdrPatchRenderer(px, py, size, size);
                foreach (double nits in new[] { sdrWhite.Y * 0.25, sdrWhite.Y * 0.5, sdrWhite.Y, high })
                {
                    renderer.PresentNits(nits, nits, nits);
                    surround.SetProgress(5, 5, $"FP16 {nits:F0} nits");
                    var m = await Measure();
                    Say($"    requested {nits,6:F1} nits -> measured {m.Y,6:F1} nits  ({m.Y / nits:P1} of request)");
                }

                Say(new string('-', 86));
                Say(parity && range
                    ? "VERDICT: HDR-range patches are trustworthy on this system. Ready to build HDR-range calibration."
                    : "VERDICT: NOT ready - see failures above before building on this path.");
            }
            catch (Exception ex)
            {
                Say($"FAILED: {ex.Message}");
                Log.Info($"HdrSanityCheck exception: {ex}");
            }
            finally
            {
                try { await _colorimeter.EndMeasurementSessionAsync(); } catch { }
                renderer?.Dispose();
                surround?.Close();
                _runButton.IsEnabled = true;
            }
        }
    }
}
