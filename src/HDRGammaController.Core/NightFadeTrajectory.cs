using System;
using System.Collections.Concurrent;
using System.Threading;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Arc-length parameterization of a night-mode fade over a representative colour
    /// ensemble. Mired interpolation remains the geometric path; this class changes only
    /// its clock so equal wall-time increments produce approximately equal whole-screen
    /// CAM16-UCS movement. Tables are immutable, bounded and cached by transition settings.
    /// </summary>
    public static class NightFadeTrajectory
    {
        private const int Samples = 33;
        private const int MaxCacheEntries = 128;
        private readonly record struct Key(
            int Start, int End, NightModeAlgorithm Algorithm, int Strength,
            bool UltraWarm, bool Preserve);

        private sealed record Table(double[] Mired, double[] Arc);
        private static readonly ConcurrentDictionary<Key, Lazy<Table>> Cache = new();
        private static readonly object CacheTrimLock = new();

        // Neutrals dominate adaptation; primaries and memory-like colours prevent an
        // apparently smooth white fade from moving saturated UI colours in visible jumps.
        private static readonly (LinearRgb Rgb, double Weight)[] Patches =
        {
            (new LinearRgb(1.0, 1.0, 1.0), 5.0),
            (new LinearRgb(0.18, 0.18, 0.18), 4.0),
            (new LinearRgb(0.03, 0.03, 0.03), 2.0),
            (new LinearRgb(0.75, 0.24, 0.16), 1.5),
            (new LinearRgb(0.18, 0.50, 0.92), 1.0),
            (new LinearRgb(0.15, 0.75, 0.28), 1.0),
            (new LinearRgb(0.70, 0.38, 0.22), 1.5),
        };

        public static int InterpolateKelvin(
            int startKelvin,
            int endKelvin,
            double progress,
            NightModeAlgorithm algorithm,
            double perceptualStrength,
            bool useUltraWarmMode,
            bool preserveLuminance)
        {
            progress = double.IsFinite(progress) ? Math.Clamp(progress, 0.0, 1.0) : 1.0;
            if (startKelvin <= 0 || endKelvin <= 0 || startKelvin == endKelvin)
                return endKelvin;
            if (progress <= 0.0) return startKelvin;
            if (progress >= 1.0) return endKelvin;

            var key = new Key(
                startKelvin, endKelvin, algorithm,
                (int)Math.Round(Math.Clamp(perceptualStrength, 0.0, 1.0) * 1000.0),
                useUltraWarmMode, preserveLuminance);
            if (Cache.Count >= MaxCacheEntries)
            {
                lock (CacheTrimLock)
                {
                    if (Cache.Count >= MaxCacheEntries) Cache.Clear();
                }
            }
            // Lazy closes the thundering-herd race: multiple monitor/fade services asking
            // for the same new transition build one immutable arc table, not one per thread.
            var table = Cache.GetOrAdd(key, static k => new Lazy<Table>(
                () => Build(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

            double target = progress * table.Arc[^1];
            int hi = Array.BinarySearch(table.Arc, target);
            if (hi >= 0) return Kelvin(table.Mired[hi]);
            hi = ~hi;
            if (hi <= 0) return Kelvin(table.Mired[0]);
            if (hi >= table.Arc.Length) return Kelvin(table.Mired[^1]);
            int lo = hi - 1;
            double span = table.Arc[hi] - table.Arc[lo];
            double f = span > 1e-12 ? (target - table.Arc[lo]) / span : 0.0;
            return Kelvin(table.Mired[lo] + (table.Mired[hi] - table.Mired[lo]) * f);
        }

        internal static int CacheCount => Cache.Count;

        private static Table Build(Key key)
        {
            var mired = new double[Samples];
            var arc = new double[Samples];
            double start = 1e6 / key.Start;
            double end = 1e6 / key.End;
            for (int i = 0; i < Samples; i++)
                mired[i] = start + (end - start) * i / (Samples - 1.0);

            var previous = AppearanceVector(Kelvin(mired[0]), key);
            for (int i = 1; i < Samples; i++)
            {
                var current = AppearanceVector(Kelvin(mired[i]), key);
                double segment2 = 0.0;
                for (int p = 0; p < current.Length; p++)
                {
                    double d = Cam16Ucs.DeltaEPrime(previous[p].Jab, current[p].Jab);
                    segment2 += previous[p].Weight * d * d;
                }
                arc[i] = arc[i - 1] + Math.Sqrt(Math.Max(segment2, 1e-12));
                previous = current;
            }

            // Degenerate algorithm/settings: retain ordinary mired timing.
            if (!double.IsFinite(arc[^1]) || arc[^1] <= 1e-8)
                for (int i = 0; i < Samples; i++) arc[i] = i / (Samples - 1.0);
            return new Table(mired, arc);
        }

        private static (Cam16Ucs.JabPrime Jab, double Weight)[] AppearanceVector(int kelvin, Key key)
        {
            var result = new (Cam16Ucs.JabPrime, double)[Patches.Length * 2];
            double scale = (kelvin - 6500) / 70.0;
            var white = new CieXyz(ColorMath.D65White.X * 100.0, 100.0, ColorMath.D65White.Z * 100.0);
            var conditions = Cam16Ucs.DisplayConditions(white);

            int index = 0;
            foreach (NightBasis basis in new[] { NightBasis.Srgb, NightBasis.Rec2020 })
            {
                var gains = ColorAdjustments.GetTemperatureMultipliers(
                    scale, key.Algorithm, key.UltraWarm, key.Strength / 1000.0, null, basis);
                if (key.Preserve && key.Algorithm != NightModeAlgorithm.UltraNight)
                {
                    // One global trajectory drives SDR and HDR. Price the maximum-headroom
                    // variant here; JndPacedFade separately guards the unpreserved path.
                    gains = ColorAdjustments.RescaleToConstantLuminance(gains, basis, 2.0, 1.0);
                }

                foreach (var patch in Patches)
                {
                    var rgb = new LinearRgb(
                        patch.Rgb.R * gains.R,
                        patch.Rgb.G * gains.G,
                        patch.Rgb.B * gains.B);
                    var xyz = basis == NightBasis.Rec2020
                        ? ColorMath.LinearRec2020ToXyz(rgb)
                        : ColorMath.LinearSrgbToXyz(rgb);
                    var absolute = new CieXyz(xyz.X * 100.0, xyz.Y * 100.0, xyz.Z * 100.0);
                    result[index++] = (Cam16Ucs.ToJabPrime(absolute, conditions), patch.Weight);
                }
            }
            return result;
        }

        private static int Kelvin(double mired) =>
            (int)Math.Round(1e6 / Math.Clamp(mired, 100.0, 1000.0));
    }
}
