using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// One trust-check run appended to the per-monitor trend history. Schema-versioned and
    /// unknown-field-tolerant: newer builds may add fields, older files must keep loading.
    /// </summary>
    public sealed record TrustCheckEntry
    {
        public int SchemaVersion { get; init; } = 1;
        public DateTime TimestampUtc { get; init; }
        public string MonitorDevicePath { get; init; } = string.Empty;
        public string? ProfileId { get; init; }
        public string? ProfileName { get; init; }
        public bool HdrMode { get; init; }
        public string TargetName { get; init; } = string.Empty;
        public string? InstrumentModel { get; init; }
        public string? MeterCorrectionFile { get; init; }
        public double AvgDeltaE2000 { get; init; }
        public double WhiteDeltaE2000 { get; init; }
        public double WhiteCctK { get; init; }
        public double WhiteDuv { get; init; }
        public double WhiteNits { get; init; }
        public double BlackNits { get; init; }
        public double? U95DeltaE { get; init; }
        public IReadOnlyList<TrustCheck.PatchGrade> Patches { get; init; } = Array.Empty<TrustCheck.PatchGrade>();
    }

    /// <summary>
    /// Append-only per-monitor JSONL store for trust-check runs, under
    /// %LocalAppData%\Gloam\reports\trend. Corrupt lines are skipped on load (JSONL crash
    /// safety), and drift analysis honors measurement uncertainty: no alert fires unless the
    /// drift exceeds BOTH the combined U95 of the two runs and a practical floor — an alert
    /// the math can't stand behind is noise, not information.
    /// </summary>
    public static class TrustCheckHistory
    {
        /// <summary>Practical floors under which drift is not worth an alert even if it
        /// clears the uncertainty band (engineering choice, ~1 JND avg / a visible tint).</summary>
        public const double DeltaEDriftFloor = 1.0;
        public const double DuvDriftFloor = 0.003;

        private static readonly object WriteLock = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>Test seam: redirects the store away from %LocalAppData%.</summary>
        internal static string? TrendDirectoryOverride;

        public static string GetTrendDirectory()
        {
            string dir = TrendDirectoryOverride
                ?? Path.Combine(CalibrationProfile.GetReportsDirectory(), "trend");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetHistoryPath(string monitorDevicePath)
        {
            string safe = Sanitize(monitorDevicePath);
            return Path.Combine(GetTrendDirectory(), $"{safe}.jsonl");
        }

        public static void Append(TrustCheckEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            string line = JsonSerializer.Serialize(entry, JsonOptions);
            lock (WriteLock)
            {
                File.AppendAllText(GetHistoryPath(entry.MonitorDevicePath), line + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }

        /// <summary>Loads the monitor's history, oldest first; malformed lines are skipped.</summary>
        public static IReadOnlyList<TrustCheckEntry> Load(string monitorDevicePath)
        {
            string path = GetHistoryPath(monitorDevicePath);
            if (!File.Exists(path)) return Array.Empty<TrustCheckEntry>();

            var entries = new List<TrustCheckEntry>();
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<TrustCheckEntry>(line, JsonOptions);
                    if (entry != null) entries.Add(entry);
                }
                catch (JsonException)
                {
                    // A torn/corrupt trailing line (crash mid-append) must not poison the
                    // rest of the history.
                }
            }
            return entries;
        }

        public sealed record DriftVerdict(
            bool Alert,
            string Summary,
            double DeltaEDrift,
            double DuvDrift,
            double CombinedU95,
            TrustCheckEntry Baseline,
            TrustCheckEntry Latest);

        /// <summary>
        /// Compares the latest entry against its baseline — the FIRST entry recorded under
        /// the same installed profile (the baseline resets when the profile changes, because
        /// a recalibration legitimately moves every number). Returns null when there is no
        /// comparable pair.
        /// </summary>
        public static DriftVerdict? AnalyzeDrift(IReadOnlyList<TrustCheckEntry> history)
        {
            if (history == null || history.Count < 2) return null;

            var latest = history[^1];
            var baseline = history.FirstOrDefault(e =>
                e.ProfileId == latest.ProfileId && e.HdrMode == latest.HdrMode);
            if (baseline == null || ReferenceEquals(baseline, latest)) return null;

            double deltaEDrift = Math.Abs(latest.AvgDeltaE2000 - baseline.AvgDeltaE2000);
            double duvDrift = latest.WhiteDuv - baseline.WhiteDuv;

            // Two independent measurements: their uncertainties add in quadrature. Missing
            // U95 contributes zero (the floor still protects against noise-chasing).
            double combinedU95 = Math.Sqrt(
                Math.Pow(latest.U95DeltaE ?? 0.0, 2) + Math.Pow(baseline.U95DeltaE ?? 0.0, 2));

            bool deltaEAlert = deltaEDrift > Math.Max(DeltaEDriftFloor, combinedU95);
            bool duvAlert = Math.Abs(duvDrift) > DuvDriftFloor;
            bool alert = deltaEAlert || duvAlert;

            string summary = alert
                ? $"Drift since {baseline.TimestampUtc:yyyy-MM-dd}: avg ΔE {baseline.AvgDeltaE2000:F2} → " +
                  $"{latest.AvgDeltaE2000:F2}" +
                  (duvAlert ? $", white Duv shift {duvDrift:+0.0000;-0.0000}" : string.Empty) +
                  " — consider re-verifying or recalibrating."
                : $"Stable since {baseline.TimestampUtc:yyyy-MM-dd}: ΔE drift {deltaEDrift:F2} " +
                  $"within ±{Math.Max(DeltaEDriftFloor, combinedU95):F2}.";

            return new DriftVerdict(alert, summary, deltaEDrift, duvDrift, combinedU95, baseline, latest);
        }

        /// <summary>Pure reminder gate for the monthly toast.</summary>
        public static bool ShouldRemind(DateTime? lastCheckUtc, DateTime nowUtc, int intervalDays)
        {
            if (intervalDays <= 0) return false;
            if (lastCheckUtc == null) return true;
            return (nowUtc - lastCheckUtc.Value).TotalDays >= intervalDays;
        }

        /// <summary>Exports a monitor's history as CSV (for spreadsheets/support bundles).</summary>
        public static string BuildCsv(IReadOnlyList<TrustCheckEntry> history)
        {
            var sb = new StringBuilder();
            sb.AppendLine("timestamp_utc,profile_name,hdr,target,avg_de2000,white_de2000,white_cct_k,white_duv,white_nits,black_nits,u95_de");
            foreach (var e in history)
            {
                sb.AppendLine(string.Join(",",
                    e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                    Csv(e.ProfileName),
                    e.HdrMode,
                    Csv(e.TargetName),
                    N(e.AvgDeltaE2000), N(e.WhiteDeltaE2000), N(e.WhiteCctK), N(e.WhiteDuv),
                    N(e.WhiteNits), N(e.BlackNits),
                    e.U95DeltaE is { } u ? N(u) : string.Empty));
            }
            return sb.ToString();

            static string N(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
            static string Csv(string? v) =>
                string.IsNullOrEmpty(v) ? string.Empty
                : v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\""
                : v;
        }

        private static string Sanitize(string monitorDevicePath)
        {
            if (string.IsNullOrWhiteSpace(monitorDevicePath)) return "unknown-monitor";
            var sb = new StringBuilder(monitorDevicePath.Length);
            foreach (char c in monitorDevicePath)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            string safe = sb.ToString().Trim('_');
            return safe.Length == 0 ? "unknown-monitor" : safe.Length > 120 ? safe[..120] : safe;
        }
    }
}
