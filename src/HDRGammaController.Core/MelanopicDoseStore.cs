using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HDRGammaController.Core
{
    /// <summary>One melanopic sample in the nightly dose log.</summary>
    public sealed record MelanopicDoseSample
    {
        public DateTime TimestampUtc { get; init; }
        public string MonitorDevicePath { get; init; } = string.Empty;
        /// <summary>Melanopic EDI in mel-lux at the assumed viewing geometry.</summary>
        public double MelanopicEdiLux { get; init; }
        public double? EdiExpandedU { get; init; }
        /// <summary>Geometry-free melanopic reduction vs 6500K (fraction).</summary>
        public double ReductionFraction { get; init; }
        public int Kelvin { get; init; }
        public bool HasSpectra { get; init; }
    }

    /// <summary>
    /// Lightweight append-only store for the nightly melanopic dose curve: one JSONL file
    /// per calendar day under %LocalAppData%\Gloam\melanopic, one sample per applied-state
    /// change plus a slow keepalive. Tolerant reader (torn lines skipped), retention-rotated
    /// on rollover. Deliberately not a database — the dose curve needs at most a few hundred
    /// points per night.
    /// </summary>
    public static class MelanopicDoseStore
    {
        public const int DefaultRetentionDays = 90;

        private static readonly object WriteLock = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>Test seam: redirects the store away from %LocalAppData%.</summary>
        internal static string? DirectoryOverride;

        public static string GetDirectory()
        {
            string dir = DirectoryOverride ?? Path.Combine(AppPaths.DataDir, "melanopic");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string PathForDay(DateTime localDate)
            => Path.Combine(GetDirectory(), $"{localDate:yyyy-MM-dd}.jsonl");

        public static void Append(MelanopicDoseSample sample)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            string line = JsonSerializer.Serialize(sample, JsonOptions);
            lock (WriteLock)
            {
                File.AppendAllText(PathForDay(sample.TimestampUtc.ToLocalTime().Date), line + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }

        /// <summary>
        /// Loads all samples at or after <paramref name="sinceLocal"/>, oldest first
        /// (spans at most the two day-files the window can touch).
        /// </summary>
        public static IReadOnlyList<MelanopicDoseSample> LoadSince(DateTime sinceLocal)
        {
            var samples = new List<MelanopicDoseSample>();
            var sinceUtc = sinceLocal.Kind == DateTimeKind.Utc ? sinceLocal : sinceLocal.ToUniversalTime();
            for (DateTime day = sinceLocal.Date; day <= DateTime.Now.Date; day = day.AddDays(1))
            {
                string path = PathForDay(day);
                if (!File.Exists(path)) continue;
                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var sample = JsonSerializer.Deserialize<MelanopicDoseSample>(line, JsonOptions);
                        if (sample != null && sample.TimestampUtc >= sinceUtc)
                            samples.Add(sample);
                    }
                    catch (JsonException)
                    {
                        // Torn trailing line from a crash mid-append: skip, keep the rest.
                    }
                }
            }
            return samples.OrderBy(s => s.TimestampUtc).ToList();
        }

        /// <summary>
        /// Cumulative melanopic dose in mel-lx·hours via trapezoidal integration over the
        /// sample series (per monitor summed — callers label multi-monitor sums as the
        /// upper-bound estimate they are). Empty or single-sample series → 0.
        /// </summary>
        public static double IntegrateDose(IReadOnlyList<MelanopicDoseSample> samples)
        {
            if (samples == null || samples.Count < 2) return 0.0;

            double doseByMonitorSum = 0.0;
            foreach (var group in samples.GroupBy(s => s.MonitorDevicePath))
            {
                var ordered = group.OrderBy(s => s.TimestampUtc).ToList();
                for (int i = 1; i < ordered.Count; i++)
                {
                    double hours = (ordered[i].TimestampUtc - ordered[i - 1].TimestampUtc).TotalHours;
                    if (hours <= 0 || hours > 6) continue; // gap = screen off / sleep, don't integrate across it
                    double a = Math.Max(0, ordered[i - 1].MelanopicEdiLux);
                    double b = Math.Max(0, ordered[i].MelanopicEdiLux);
                    doseByMonitorSum += 0.5 * (a + b) * hours;
                }
            }
            return doseByMonitorSum;
        }

        /// <summary>Deletes day-files older than the retention window. Call at startup and
        /// on date rollover; failures are swallowed (retention is best-effort hygiene).</summary>
        public static void RotateRetention(int retentionDays = DefaultRetentionDays)
        {
            try
            {
                DateTime cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, retentionDays));
                foreach (string file in Directory.EnumerateFiles(GetDirectory(), "????-??-??.jsonl"))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (DateTime.TryParseExact(stem, "yyyy-MM-dd", null,
                            System.Globalization.DateTimeStyles.None, out var day) &&
                        day < cutoff)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"MelanopicDoseStore: retention rotation failed: {ex.Message}");
            }
        }
    }
}
