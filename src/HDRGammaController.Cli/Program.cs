using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Cli
{
    /// <summary>
    /// Headless diagnostics for the color pipeline. It can dump LUTs, run CI-friendly LUT
    /// invariants, compare scenarios, and inspect night-mode / Ultra Night multipliers without
    /// starting the tray app or touching display state.
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            try
            {
                return args[0].ToLowerInvariant() switch
                {
                    "lut" => RunLut(args),
                    "sweep" => RunSweep(args),
                    "compare" => RunCompare(args),
                    "check-lut" => RunCheckLut(args),
                    "night" => RunNight(args),
                    "golden-ingest" => RunGoldenIngest(args),
                    // Back-compat: old `Cli 2.2 200 [out.csv]` form still works
                    _ => RunLegacy(args)
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                if (Environment.GetEnvironmentVariable("HDRCLI_TRACE") == "1")
                    Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Gloam CLI - headless color pipeline diagnostics");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  cli lut      <gamma> <white> [options]   Dump one LUT");
            Console.WriteLine("  cli sweep    <gamma> <white>             Matrix of dimming/temp scenarios");
            Console.WriteLine("  cli compare  <gamma> <white> <A> <B>     Side-by-side diff of two scenarios");
            Console.WriteLine("  cli check-lut <gamma> <white> [options]  Validate LUT invariants, exit non-zero on failure");
            Console.WriteLine("  cli night    <kelvin> [options]          Inspect night-mode multipliers");
            Console.WriteLine("  cli golden-ingest <fixtureDir> [options] Create/refresh a golden-sample regression fixture");
            Console.WriteLine();
            Console.WriteLine("Gamma:  2.2 | 2.4 | default");
            Console.WriteLine("White:  nits (e.g. 80, 200, 480)");
            Console.WriteLine();
            Console.WriteLine("Options (lut/check-lut):");
            Console.WriteLine("  --brightness N      0..100 (default 100)");
            Console.WriteLine("  --temp T            temperature scale, extended for night mode");
            Console.WriteLine("  --temp-k K          temperature as Kelvin");
            Console.WriteLine("  --tint T            -50..+50 (default 0)");
            Console.WriteLine("  --algorithm NAME    perceptual | ultra | accurate | classic");
            Console.WriteLine("  --strength N        perceptual strength 0..1");
            Console.WriteLine("  --ccss PATH         .ccss spectral sample for Ultra Night");
            Console.WriteLine("  --linear-dim        Use linear instead of perceptual dimming");
            Console.WriteLine("  --sdr               Generate the SDR path (decode-gamma-calibrate-encode)");
            Console.WriteLine("  --csv PATH          Also write a CSV file");
            Console.WriteLine("  --json PATH|-       Write machine-readable output, or '-' for stdout");
            Console.WriteLine("  --no-chart          Skip the ASCII sparkline");
            Console.WriteLine();
            Console.WriteLine("Options (night):");
            Console.WriteLine("  --algorithm NAME    perceptual | ultra | accurate | classic");
            Console.WriteLine("  --basis NAME        srgb | rec2020");
            Console.WriteLine("  --strength N        perceptual strength 0..1");
            Console.WriteLine("  --ccss PATH         .ccss spectral sample for Ultra Night");
            Console.WriteLine("  --json PATH|-       Write machine-readable output, or '-' for stdout");
            Console.WriteLine();
            Console.WriteLine("Scenario shorthand (compare): 'b=50,t=-20' means brightness 50, temp -20");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  cli sweep 2.2 200");
            Console.WriteLine("  cli lut 2.2 200 --brightness 50 --csv dim50.csv");
            Console.WriteLine("  cli compare 2.2 200 b=100 b=50");
        }

        // ---------- Legacy path (backward compat with the tiny original CLI) ----------

        private static int RunLegacy(string[] args)
        {
            if (args.Length < 2)
            {
                PrintHelp();
                return 1;
            }
            var opts = new LutOptions
            {
                Mode = ParseGamma(args[0]),
                WhiteLevel = ParseDouble(args[1]),
                CsvPath = args.Length >= 3 ? args[2] : null,
                ShowChart = false
            };
            DumpLut(opts);
            return 0;
        }

        // ---------- Subcommands ----------

        private static int RunLut(string[] args)
        {
            if (args.Length < 3) throw new ArgumentException("lut requires <gamma> <white>");
            var opts = new LutOptions
            {
                Mode = ParseGamma(args[1]),
                WhiteLevel = ParseDouble(args[2])
            };
            ParseCalOptions(args, 3, opts);
            DumpLut(opts);
            return 0;
        }

        private static int RunSweep(string[] args)
        {
            if (args.Length < 3) throw new ArgumentException("sweep requires <gamma> <white>");
            var mode = ParseGamma(args[1]);
            var white = ParseDouble(args[2]);

            Console.WriteLine($"Sweep: gamma {GammaLabel(mode)}, SDR white {white} nits\n");

            Console.WriteLine("-- Brightness (perceptual) --");
            foreach (var b in new[] { 100.0, 75.0, 50.0, 25.0 })
            {
                var opts = new LutOptions { Mode = mode, WhiteLevel = white };
                opts.Calibration.Brightness = b;
                PrintScenarioRow($"brightness={b,3:0}", opts);
            }

            Console.WriteLine("\n-- Temperature (warming) --");
            foreach (var t in new[] { 0.0, -10.0, -20.0, -30.0, -50.0 })
            {
                var opts = new LutOptions { Mode = mode, WhiteLevel = white };
                opts.Calibration.Temperature = t;
                PrintScenarioRow($"temp={t,4:0}     ", opts);
            }

            Console.WriteLine("\n-- Combined (night-mode-like) --");
            foreach (var (b, t) in new[] { (100.0, 0.0), (75.0, -20.0), (50.0, -35.0), (30.0, -50.0) })
            {
                var opts = new LutOptions { Mode = mode, WhiteLevel = white };
                opts.Calibration.Brightness = b;
                opts.Calibration.Temperature = t;
                PrintScenarioRow($"b={b,3:0} t={t,4:0}", opts);
            }

            Console.WriteLine("\nColumns below chart: L=lowest, M=mid (index 512), H=peak (1023)");
            return 0;
        }

        private static int RunCompare(string[] args)
        {
            if (args.Length < 5) throw new ArgumentException("compare requires <gamma> <white> <A> <B>");
            var mode = ParseGamma(args[1]);
            var white = ParseDouble(args[2]);
            var optsA = new LutOptions { Mode = mode, WhiteLevel = white };
            var optsB = new LutOptions { Mode = mode, WhiteLevel = white };
            ApplyScenarioShorthand(optsA.Calibration, args[3]);
            ApplyScenarioShorthand(optsB.Calibration, args[4]);

            var lutA = GenerateGreyLut(optsA);
            var lutB = GenerateGreyLut(optsB);

            Console.WriteLine($"Compare: gamma {GammaLabel(mode)}, SDR white {white} nits");
            Console.WriteLine($"  A: {args[3]}   B: {args[4]}\n");

            Console.WriteLine("A " + Sparkline(lutA));
            Console.WriteLine("B " + Sparkline(lutB));
            Console.WriteLine("Δ " + SparklineSigned(lutA, lutB));

            double maxDelta = 0, peakDelta = lutB[1023] - lutA[1023];
            for (int i = 0; i < lutA.Length; i++)
            {
                double d = Math.Abs(lutA[i] - lutB[i]);
                if (d > maxDelta) maxDelta = d;
            }
            Console.WriteLine();
            Console.WriteLine($"Max |Δ|       : {maxDelta:F4}");
            Console.WriteLine($"Peak A / B    : {lutA[1023]:F4} / {lutB[1023]:F4}  (delta {peakDelta:+0.0000;-0.0000})");
            Console.WriteLine($"Midpoint A / B: {lutA[512]:F4} / {lutB[512]:F4}");
            return 0;
        }

        private static int RunCheckLut(string[] args)
        {
            if (args.Length < 3) throw new ArgumentException("check-lut requires <gamma> <white>");
            var opts = new LutOptions
            {
                Mode = ParseGamma(args[1]),
                WhiteLevel = ParseDouble(args[2]),
                ShowChart = false
            };
            ParseCalOptions(args, 3, opts);

            var lut = opts.IsSdr
                ? LutGenerator.GenerateLut(opts.Mode, opts.WhiteLevel, opts.Calibration, isHdr: false)
                : LutGenerator.GenerateLut(opts.Mode, opts.WhiteLevel, opts.Calibration, isHdr: true);

            var stats = new[]
            {
                AnalyzeChannel("R", lut.R),
                AnalyzeChannel("G", lut.G),
                AnalyzeChannel("B", lut.B),
                AnalyzeChannel("Grey", lut.Grey)
            };
            var failures = stats.SelectMany(s => s.Failures.Select(f => $"{s.Name}: {f}")).ToList();

            var report = new
            {
                command = "check-lut",
                gamma = GammaLabel(opts.Mode),
                sdrWhiteNits = opts.WhiteLevel,
                path = opts.IsSdr ? "sdr" : "hdr",
                calibration = DescribeCalibration(opts.Calibration),
                passed = failures.Count == 0,
                failures,
                channels = stats
            };

            if (opts.JsonPath == "-")
            {
                WriteJson(opts.JsonPath, report);
            }
            else
            {
                Console.WriteLine(failures.Count == 0 ? "PASS: LUT invariants hold." : "FAIL: LUT invariants broke.");
                foreach (var failure in failures)
                    Console.WriteLine("  " + failure);
                foreach (var s in stats)
                {
                    Console.WriteLine(
                        $"  {s.Name,-4} min={s.Min:F6} max={s.Max:F6} max_step={s.MaxPositiveStep:F6} " +
                        $"max_drop={s.MaxNegativeStep:F6}");
                }
                if (opts.JsonPath != null) WriteJson(opts.JsonPath, report);
            }

            return failures.Count == 0 ? 0 : 2;
        }

        private static int RunNight(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("night requires <kelvin>");
            var opts = new NightOptions { Kelvin = (int)Math.Round(ParseDouble(args[1])) };
            ParseNightOptions(args, 2, opts);

            opts.Kelvin = NightModeSettings.ClampKelvin(opts.Kelvin);
            var coefficients = CcssMelanopicEstimator.TryLoad(opts.CcssPath);
            var multipliers = GetNightMultipliers(opts, coefficients);
            var melanopic = EstimateRelativeMelanopic(multipliers, coefficients);

            var report = new
            {
                command = "night",
                kelvin = opts.Kelvin,
                algorithm = opts.Algorithm.ToString(),
                basis = opts.Basis.ToString(),
                perceptualStrength = opts.PerceptualStrength,
                ccss = coefficients?.SourceName,
                multipliers = new
                {
                    red = multipliers.R,
                    green = multipliers.G,
                    blue = multipliers.B
                },
                channelReduction = new
                {
                    red = 1.0 - multipliers.R,
                    green = 1.0 - multipliers.G,
                    blue = 1.0 - multipliers.B
                },
                relativeMelanopicPerLuminance = melanopic
            };

            if (opts.JsonPath == "-")
            {
                WriteJson(opts.JsonPath, report);
                return 0;
            }

            Console.WriteLine($"Night: {opts.Kelvin}K, {opts.Algorithm}, {opts.Basis}");
            if (opts.Algorithm == NightModeAlgorithm.Perceptual)
                Console.WriteLine($"  strength: {opts.PerceptualStrength:F2}");
            Console.WriteLine($"  multipliers: R={multipliers.R:F6} G={multipliers.G:F6} B={multipliers.B:F6}");
            Console.WriteLine($"  channel cut: R={(1.0 - multipliers.R):P1} G={(1.0 - multipliers.G):P1} B={(1.0 - multipliers.B):P1}");
            if (melanopic.HasValue)
                Console.WriteLine($"  relative melanopic/luminance: {melanopic.Value:F3}x D65 white estimate");
            else if (!string.IsNullOrWhiteSpace(opts.CcssPath))
                Console.WriteLine("  melanopic estimate: unavailable; CCSS could not be parsed");
            if (opts.JsonPath != null) WriteJson(opts.JsonPath, report);
            return 0;
        }

        // ---------- Golden-sample ingest ----------

        /// <summary>
        /// Ingests a recorded measurement set into a golden-sample regression fixture, or
        /// regenerates an existing fixture's baseline. Regeneration is the SANCTIONED way to
        /// accept numeric pipeline changes: the baseline diff shows up in git review instead
        /// of tests silently rewriting their own expectations.
        /// </summary>
        private static int RunGoldenIngest(string[] args)
        {
            if (args.Length < 2)
                throw new ArgumentException(
                    "golden-ingest requires <fixtureDir> [--native <csv> --verification <csv> " +
                    "--target <name> --panel <label> [--hdr] [--sdr-white N] [--instrument <model>] [--meter-correction]]");

            string fixtureDir = args[1];
            string manifestPath = Path.Combine(fixtureDir, "manifest.json");

            string? nativeCsv = null, verificationCsv = null, targetName = null,
                panelLabel = null, instrument = null;
            bool hdrMode = false, meterCorrection = false;
            double sdrWhite = 200.0;

            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--native": nativeCsv = RequireValue(args, ref i, args[i]); break;
                    case "--verification": verificationCsv = RequireValue(args, ref i, args[i]); break;
                    case "--target": targetName = RequireValue(args, ref i, args[i]); break;
                    case "--panel": panelLabel = RequireValue(args, ref i, args[i]); break;
                    case "--instrument": instrument = RequireValue(args, ref i, args[i]); break;
                    case "--sdr-white": sdrWhite = ParseDouble(RequireValue(args, ref i, args[i])); break;
                    case "--hdr": hdrMode = true; break;
                    case "--meter-correction": meterCorrection = true; break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }

            GoldenSampleManifest manifest;
            if (nativeCsv != null || verificationCsv != null)
            {
                // Creation mode: copy the recordings in and write a fresh manifest.
                if (nativeCsv == null || verificationCsv == null || targetName == null || panelLabel == null)
                    throw new ArgumentException(
                        "Creating a fixture needs --native, --verification, --target and --panel.");

                Directory.CreateDirectory(fixtureDir);
                File.Copy(nativeCsv, Path.Combine(fixtureDir, "native-measurements.csv"), overwrite: true);
                File.Copy(verificationCsv, Path.Combine(fixtureDir, "verification-measurements.csv"), overwrite: true);
                manifest = new GoldenSampleManifest
                {
                    PanelLabel = panelLabel,
                    TargetName = targetName,
                    HdrMode = hdrMode,
                    SdrWhiteNits = sdrWhite,
                    InstrumentModel = instrument,
                    HasMeterCorrection = meterCorrection,
                };
                manifest.Save(manifestPath);
            }
            else
            {
                if (!File.Exists(manifestPath))
                    throw new ArgumentException(
                        $"No manifest at {manifestPath} - pass --native/--verification/--target/--panel to create the fixture.");
                manifest = GoldenSampleManifest.Load(manifestPath);
            }

            var target = StandardTargets.GetByName(manifest.TargetName)
                ?? throw new ArgumentException($"Unknown calibration target '{manifest.TargetName}'.");

            var native = MeasurementCsvImporter.Load(Path.Combine(fixtureDir, manifest.NativeCsv));
            var verification = MeasurementCsvImporter.Load(Path.Combine(fixtureDir, manifest.VerificationCsv));

            var baseline = GoldenSampleBaseline.Compute(
                native.Measurements, verification.Measurements, target,
                manifest.HdrMode, manifest.SdrWhiteNits,
                manifest.ToUncertaintyContext(verification.Measurements));
            baseline.Save(Path.Combine(fixtureDir, "baseline.json"));

            Console.WriteLine($"Golden fixture '{manifest.PanelLabel}' at {fixtureDir}");
            Console.WriteLine($"  target: {target.Name}  hdr: {manifest.HdrMode}  sdr-white: {manifest.SdrWhiteNits} nits");
            Console.WriteLine($"  native rows: {native.Measurements.Count}  verification rows: {verification.Measurements.Count}");
            Console.WriteLine($"  characterization: peak {baseline.PeakLuminanceNits:F1} nits, black {baseline.BlackLevelNits:F3} nits, gamma {baseline.MeasuredGamma:F3}");
            Console.WriteLine($"  verify: avg dE {baseline.AverageDeltaE:F3}, max {baseline.MaxDeltaE:F3}" +
                              (baseline.UncertaintyExpandedU is { } u ? $" (U95 +/- {u:F3})" : string.Empty));
            if (baseline.HdrLutSamples != null)
                Console.WriteLine($"  hdr lut: black {baseline.HdrMeasuredBlackNits:F3}, peak {baseline.HdrMeasuredPeakNits:F1}, wire-exact {baseline.HdrWireExact}");
            Console.WriteLine("  wrote baseline.json");
            return 0;
        }

        // ---------- Core dump ----------

        private static void DumpLut(LutOptions opts)
        {
            var lut = opts.IsSdr
                ? LutGenerator.GenerateLut(opts.Mode, opts.WhiteLevel, opts.Calibration, isHdr: false)
                : LutGenerator.GenerateLut(opts.Mode, opts.WhiteLevel, opts.Calibration, isHdr: true);

            var report = new
            {
                command = "lut",
                gamma = GammaLabel(opts.Mode),
                sdrWhiteNits = opts.WhiteLevel,
                path = opts.IsSdr ? "sdr" : "hdr",
                calibration = DescribeCalibration(opts.Calibration),
                samples = SamplePoints(lut.Grey.Length).Select(i => new
                {
                    index = i,
                    inputSignal = i / 1023.0,
                    inputNits = TransferFunctions.PqEotf(i / 1023.0),
                    outputSignal = lut.Grey[i],
                    outputNits = TransferFunctions.PqEotf(lut.Grey[i])
                }).ToArray(),
                channels = new[]
                {
                    AnalyzeChannel("R", lut.R),
                    AnalyzeChannel("G", lut.G),
                    AnalyzeChannel("B", lut.B),
                    AnalyzeChannel("Grey", lut.Grey)
                }
            };

            if (opts.JsonPath == "-")
            {
                WriteJson(opts.JsonPath, report);
                return;
            }

            Console.WriteLine($"LUT: gamma {GammaLabel(opts.Mode)}, SDR white {opts.WhiteLevel} nits, " +
                              $"brightness {opts.Calibration.Brightness:0}, temp {opts.Calibration.Temperature:0}, tint {opts.Calibration.Tint:0}" +
                              (opts.IsSdr ? " [SDR path]" : string.Empty));

            if (opts.ShowChart)
            {
                Console.WriteLine("R " + Sparkline(lut.R));
                Console.WriteLine("G " + Sparkline(lut.G));
                Console.WriteLine("B " + Sparkline(lut.B));
            }

            Console.WriteLine();
            Console.WriteLine($"{"idx",4} {"in_sig",8} {"in_nits",10} {"out_sig(grey)",14} {"out_nits(grey)",16}");
            foreach (int i in SamplePoints(lut.Grey.Length))
            {
                double inSig = i / 1023.0;
                double inNits = TransferFunctions.PqEotf(inSig);
                double outSig = lut.Grey[i];
                double outNits = TransferFunctions.PqEotf(outSig);
                Console.WriteLine($"{i,4} {inSig,8:F4} {inNits,10:F2} {outSig,14:F4} {outNits,16:F2}");
            }

            if (opts.CsvPath != null)
            {
                WriteCsv(opts.CsvPath, lut);
                Console.WriteLine($"\nWrote {opts.CsvPath}");
            }

            if (opts.JsonPath != null)
            {
                WriteJson(opts.JsonPath, report);
            }
        }

        private static void PrintScenarioRow(string label, LutOptions opts)
        {
            var lut = GenerateGreyLut(opts);
            double L = lut[0], M = lut[512], H = lut[1023];
            Console.WriteLine($"  {label,-22} {Sparkline(lut)}  L={L:F2} M={M:F2} H={H:F2}");
        }

        private static double[] GenerateGreyLut(LutOptions opts)
        {
            var lut = opts.IsSdr
                ? LutGenerator.GenerateLut(opts.Mode, opts.WhiteLevel, opts.Calibration, isHdr: false)
                : LutGenerator.GenerateLut(opts.Mode, opts.WhiteLevel, opts.Calibration, isHdr: true);
            return lut.Grey;
        }

        // ---------- ASCII rendering ----------

        // Braille-block bar chars with 8 vertical steps. Unicode range 0x2581..0x2588.
        private static readonly char[] Blocks = { ' ', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

        private static string Sparkline(double[] lut, int width = 64)
        {
            // Map the 1024-point LUT onto `width` columns, taking the mean of each slice.
            var sb = new StringBuilder(width);
            double step = lut.Length / (double)width;
            for (int c = 0; c < width; c++)
            {
                int start = (int)(c * step);
                int end = Math.Min(lut.Length, (int)((c + 1) * step));
                double sum = 0;
                int n = 0;
                for (int i = start; i < end; i++) { sum += lut[i]; n++; }
                double v = n > 0 ? sum / n : 0;
                int idx = Math.Clamp((int)Math.Round(v * (Blocks.Length - 1)), 0, Blocks.Length - 1);
                sb.Append(Blocks[idx]);
            }
            return sb.ToString();
        }

        private static string SparklineSigned(double[] a, double[] b, int width = 64)
        {
            // Signed delta: '.' near zero, '-' for B<A, '+' for B>A, uppercase for larger diffs.
            var sb = new StringBuilder(width);
            double step = a.Length / (double)width;
            for (int c = 0; c < width; c++)
            {
                int start = (int)(c * step);
                int end = Math.Min(a.Length, (int)((c + 1) * step));
                double sum = 0;
                int n = 0;
                for (int i = start; i < end; i++) { sum += (b[i] - a[i]); n++; }
                double d = n > 0 ? sum / n : 0;
                double mag = Math.Abs(d);
                char ch = mag < 0.005 ? '·'
                        : mag < 0.02 ? (d < 0 ? '-' : '+')
                        : mag < 0.08 ? (d < 0 ? '=' : '#')
                        : (d < 0 ? '▼' : '▲');
                sb.Append(ch);
            }
            return sb.ToString();
        }

        // ---------- Helpers ----------

        private static IEnumerable<int> SamplePoints(int len)
        {
            // Spread that hits the SDR region densely, the SDR/HDR boundary (around index 520
            // at 200 nits) specifically, and a couple headroom samples.
            yield return 0;
            yield return 64;
            yield return 128;
            yield return 256;
            yield return 384;
            yield return 448;
            yield return 512;
            yield return 576;
            yield return 640;
            yield return 768;
            yield return 896;
            yield return len - 1;
        }

        private static void WriteCsv(string path, (double[] R, double[] G, double[] B, double[] Grey) lut)
        {
            using var w = new StreamWriter(path);
            w.WriteLine("index,input_signal,input_nits,out_r,out_g,out_b,out_grey");
            for (int i = 0; i < lut.Grey.Length; i++)
            {
                double inSig = i / 1023.0;
                double inNits = TransferFunctions.PqEotf(inSig);
                w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1:G8},{2:G8},{3:G8},{4:G8},{5:G8},{6:G8}",
                    i, inSig, inNits, lut.R[i], lut.G[i], lut.B[i], lut.Grey[i]));
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        private static void WriteJson(string path, object payload)
        {
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            if (path == "-")
            {
                Console.WriteLine(json);
                return;
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, json + Environment.NewLine);
            Console.WriteLine($"Wrote {path}");
        }

        private static object DescribeCalibration(CalibrationSettings calibration) => new
        {
            calibration.Brightness,
            calibration.UseLinearBrightness,
            calibration.Temperature,
            kelvin = ColorAdjustments.TemperatureScaleToKelvin(calibration.Temperature),
            calibration.TemperatureOffset,
            calibration.Tint,
            algorithm = calibration.Algorithm.ToString(),
            calibration.PerceptualStrength,
            calibration.UseUltraWarmMode,
            calibration.NightModeCcssPath
        };

        private static ChannelStats AnalyzeChannel(string name, double[] values)
        {
            var failures = new List<string>();
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            double maxPositiveStep = 0.0;
            double maxNegativeStep = 0.0;
            double previous = double.NaN;

            for (int i = 0; i < values.Length; i++)
            {
                double value = values[i];
                if (!double.IsFinite(value))
                {
                    failures.Add($"non-finite value at index {i}");
                    continue;
                }
                if (value < -1e-9 || value > 1.0 + 1e-9)
                    failures.Add($"out-of-range value {value:G17} at index {i}");

                min = Math.Min(min, value);
                max = Math.Max(max, value);

                if (double.IsFinite(previous))
                {
                    double step = value - previous;
                    if (step >= 0)
                    {
                        maxPositiveStep = Math.Max(maxPositiveStep, step);
                    }
                    else
                    {
                        maxNegativeStep = Math.Max(maxNegativeStep, -step);
                        if (step < -1e-7)
                            failures.Add($"non-monotonic drop {step:G17} at index {i - 1}->{i}");
                    }
                }
                previous = value;
            }

            if (values.Length == 0)
                failures.Add("empty channel");

            return new ChannelStats(
                name,
                double.IsPositiveInfinity(min) ? double.NaN : min,
                double.IsNegativeInfinity(max) ? double.NaN : max,
                maxPositiveStep,
                maxNegativeStep,
                failures);
        }

        private static GammaMode ParseGamma(string s) => s.ToLowerInvariant() switch
        {
            "2.2" => GammaMode.Gamma22,
            "2.4" => GammaMode.Gamma24,
            "default" or "srgb" or "windows" => GammaMode.WindowsDefault,
            _ => throw new ArgumentException($"Unknown gamma mode: {s}")
        };

        private static string GammaLabel(GammaMode m) => m switch
        {
            GammaMode.Gamma22 => "2.2",
            GammaMode.Gamma24 => "2.4",
            _ => "default"
        };

        private static double ParseDouble(string s)
        {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new ArgumentException($"Invalid number: {s}");
            return v;
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{option} requires a value");
            index++;
            return args[index];
        }

        private static NightModeAlgorithm ParseAlgorithm(string s) => s.Trim().ToLowerInvariant() switch
        {
            "perceptual" or "default" => NightModeAlgorithm.Perceptual,
            "ultra" or "ultranight" or "ultra-night" or "amber" => NightModeAlgorithm.UltraNight,
            "accurate" or "cie" or "cie1931" or "cie-1931" => NightModeAlgorithm.AccurateCIE1931,
            "classic" or "standard" or "helland" => NightModeAlgorithm.Standard,
            _ => throw new ArgumentException($"Unknown night algorithm: {s}")
        };

        private static NightBasis ParseBasis(string s) => s.Trim().ToLowerInvariant() switch
        {
            "srgb" or "sdr" or "rec709" or "rec.709" => NightBasis.Srgb,
            "rec2020" or "rec.2020" or "2020" or "hdr" => NightBasis.Rec2020,
            _ => throw new ArgumentException($"Unknown night basis: {s}")
        };

        private static void ParseCalOptions(string[] args, int start, LutOptions opts)
        {
            for (int i = start; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--brightness":
                        opts.Calibration.Brightness = ParseDouble(RequireValue(args, ref i, args[i]));
                        break;
                    case "--temp":
                        opts.Calibration.Temperature = ParseDouble(RequireValue(args, ref i, args[i]));
                        break;
                    case "--temp-k":
                        opts.Calibration.Temperature = (ParseDouble(RequireValue(args, ref i, args[i])) - 6500.0) / 70.0;
                        break;
                    case "--offset":
                    case "--temp-offset":
                        opts.Calibration.TemperatureOffset = ParseDouble(RequireValue(args, ref i, args[i]));
                        break;
                    case "--tint":
                        opts.Calibration.Tint = ParseDouble(RequireValue(args, ref i, args[i]));
                        break;
                    case "--algorithm":
                        opts.Calibration.Algorithm = ParseAlgorithm(RequireValue(args, ref i, args[i]));
                        break;
                    case "--strength":
                    case "--perceptual-strength":
                        opts.Calibration.PerceptualStrength = NightModeSettings.ClampPerceptualStrength(
                            ParseDouble(RequireValue(args, ref i, args[i])));
                        break;
                    case "--ultra-warm":
                        opts.Calibration.UseUltraWarmMode = true;
                        break;
                    case "--ccss":
                        opts.Calibration.NightModeCcssPath = RequireValue(args, ref i, args[i]);
                        opts.Calibration.NightMelanopicCoefficients = CcssMelanopicEstimator.TryLoad(opts.Calibration.NightModeCcssPath);
                        break;
                    case "--linear-dim":
                        opts.Calibration.UseLinearBrightness = true;
                        break;
                    case "--sdr":
                        opts.IsSdr = true;
                        break;
                    case "--csv":
                        opts.CsvPath = RequireValue(args, ref i, args[i]);
                        break;
                    case "--json":
                        opts.JsonPath = RequireValue(args, ref i, args[i]);
                        break;
                    case "--no-chart":
                        opts.ShowChart = false;
                        break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
        }

        private static void ParseNightOptions(string[] args, int start, NightOptions opts)
        {
            for (int i = start; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--algorithm":
                    case "--profile":
                        opts.Algorithm = ParseAlgorithm(RequireValue(args, ref i, args[i]));
                        break;
                    case "--basis":
                        opts.Basis = ParseBasis(RequireValue(args, ref i, args[i]));
                        break;
                    case "--strength":
                    case "--perceptual-strength":
                        opts.PerceptualStrength = NightModeSettings.ClampPerceptualStrength(
                            ParseDouble(RequireValue(args, ref i, args[i])));
                        break;
                    case "--ccss":
                        opts.CcssPath = RequireValue(args, ref i, args[i]);
                        break;
                    case "--json":
                        opts.JsonPath = RequireValue(args, ref i, args[i]);
                        break;
                    default:
                        throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
        }

        private static (double R, double G, double B) GetNightMultipliers(
            NightOptions opts,
            NightMelanopicCoefficients? coefficients)
        {
            return opts.Algorithm switch
            {
                NightModeAlgorithm.AccurateCIE1931 => ColorAdjustments.GetAccurateMultipliers(opts.Kelvin, opts.Basis),
                NightModeAlgorithm.UltraNight => ColorAdjustments.GetUltraNightMultipliers(opts.Kelvin, coefficients, opts.Basis),
                NightModeAlgorithm.Standard => ColorAdjustments.GetStandardMultipliers(opts.Kelvin),
                _ => ColorAdjustments.GetPerceptualMultipliers(opts.Kelvin, opts.PerceptualStrength, opts.Basis)
            };
        }

        private static double? EstimateRelativeMelanopic(
            (double R, double G, double B) multipliers,
            NightMelanopicCoefficients? coefficients)
        {
            if (coefficients == null) return null;

            double neutralMelanopic = coefficients.RedMelanopic + coefficients.GreenMelanopic + coefficients.BlueMelanopic;
            double neutralLuminance = coefficients.RedLuminance + coefficients.GreenLuminance + coefficients.BlueLuminance;
            double shiftedMelanopic =
                multipliers.R * coefficients.RedMelanopic +
                multipliers.G * coefficients.GreenMelanopic +
                multipliers.B * coefficients.BlueMelanopic;
            double shiftedLuminance =
                multipliers.R * coefficients.RedLuminance +
                multipliers.G * coefficients.GreenLuminance +
                multipliers.B * coefficients.BlueLuminance;

            if (neutralMelanopic <= 0 || neutralLuminance <= 0 || shiftedLuminance <= 0)
                return null;

            return (shiftedMelanopic / shiftedLuminance) / (neutralMelanopic / neutralLuminance);
        }

        /// <summary>Parses `b=50,t=-20,tint=5` style shorthand into a CalibrationSettings.</summary>
        private static void ApplyScenarioShorthand(CalibrationSettings cal, string spec)
        {
            foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) throw new ArgumentException($"Bad scenario piece: {part}");
                double v = ParseDouble(kv[1]);
                switch (kv[0].Trim().ToLowerInvariant())
                {
                    case "b": case "brightness": cal.Brightness = v; break;
                    case "t": case "temp":       cal.Temperature = v; break;
                    case "tint":                 cal.Tint = v; break;
                    default: throw new ArgumentException($"Unknown scenario key: {kv[0]}");
                }
            }
        }

        private sealed class LutOptions
        {
            public GammaMode Mode { get; set; } = GammaMode.Gamma22;
            public double WhiteLevel { get; set; } = 200;
            public CalibrationSettings Calibration { get; } = new CalibrationSettings();
            public bool IsSdr { get; set; }
            public string? CsvPath { get; set; }
            public string? JsonPath { get; set; }
            public bool ShowChart { get; set; } = true;
        }

        private sealed class NightOptions
        {
            public int Kelvin { get; set; } = NightModeSettings.DefaultNightKelvin;
            public NightModeAlgorithm Algorithm { get; set; } = NightModeAlgorithm.Perceptual;
            public NightBasis Basis { get; set; } = NightBasis.Srgb;
            public double PerceptualStrength { get; set; } = ColorAdjustments.DefaultPerceptualStrength;
            public string? CcssPath { get; set; }
            public string? JsonPath { get; set; }
        }

        private sealed record ChannelStats(
            string Name,
            double Min,
            double Max,
            double MaxPositiveStep,
            double MaxNegativeStep,
            IReadOnlyList<string> Failures);
    }
}
