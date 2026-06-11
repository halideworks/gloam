using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HDRGammaController.Core;

namespace HDRGammaController.Cli
{
    /// <summary>
    /// Diagnostic dump tool for the LUT generator. Built as a regression harness for the
    /// headroom-blend rewrite: you can visualize any calibration scenario as an ASCII
    /// sparkline in seconds, or dump CSV for external plotting. See `--help` for usage.
    /// </summary>
    class Program
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
            Console.WriteLine("HDRGammaController.Cli — LUT generation diagnostic tool");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  cli lut      <gamma> <white> [options]   Dump one LUT");
            Console.WriteLine("  cli sweep    <gamma> <white>             Matrix of dimming/temp scenarios");
            Console.WriteLine("  cli compare  <gamma> <white> <A> <B>     Side-by-side diff of two scenarios");
            Console.WriteLine();
            Console.WriteLine("Gamma:  2.2 | 2.4 | default");
            Console.WriteLine("White:  nits (e.g. 80, 200, 480)");
            Console.WriteLine();
            Console.WriteLine("Options (lut):");
            Console.WriteLine("  --brightness N      0..100 (default 100)");
            Console.WriteLine("  --temp T            -50..+50 (default 0)");
            Console.WriteLine("  --tint T            -50..+50 (default 0)");
            Console.WriteLine("  --linear-dim        Use linear instead of perceptual dimming");
            Console.WriteLine("  --sdr               Generate the SDR path (decode-gamma-calibrate-encode)");
            Console.WriteLine("  --csv PATH          Also write a CSV file");
            Console.WriteLine("  --no-chart          Skip the ASCII sparkline");
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

        // ---------- Core dump ----------

        private static void DumpLut(LutOptions opts)
        {
            var lut = opts.IsSdr
                ? LutGenerator.GenerateLut(opts.Mode, opts.WhiteLevel, opts.Calibration, isHdr: false)
                : LutGenerator.GenerateLut(opts.Mode, opts.WhiteLevel, opts.Calibration, isHdr: true);

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

        private static void ParseCalOptions(string[] args, int start, LutOptions opts)
        {
            for (int i = start; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--brightness": opts.Calibration.Brightness = ParseDouble(args[++i]); break;
                    case "--temp":       opts.Calibration.Temperature = ParseDouble(args[++i]); break;
                    case "--tint":       opts.Calibration.Tint = ParseDouble(args[++i]); break;
                    case "--linear-dim": opts.Calibration.UseLinearBrightness = true; break;
                    case "--sdr":        opts.IsSdr = true; break;
                    case "--csv":        opts.CsvPath = args[++i]; break;
                    case "--no-chart":   opts.ShowChart = false; break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
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

        private class LutOptions
        {
            public GammaMode Mode { get; set; } = GammaMode.Gamma22;
            public double WhiteLevel { get; set; } = 200;
            public CalibrationSettings Calibration { get; } = new CalibrationSettings();
            public bool IsSdr { get; set; } = false;
            public string? CsvPath { get; set; }
            public bool ShowChart { get; set; } = true;
        }
    }
}
