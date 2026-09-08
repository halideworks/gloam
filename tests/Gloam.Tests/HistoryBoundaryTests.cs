using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Gloam.Core;
using Gloam.Core.Calibration;
using Xunit;

namespace Gloam.Tests;

public sealed class HistoryBoundaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GloamHistoryBoundary", Guid.NewGuid().ToString("N"));
    public HistoryBoundaryTests()
    {
        Directory.CreateDirectory(_dir);
        MelanopicDoseStore.DirectoryOverride = _dir;
        TrustCheckHistory.TrendDirectoryOverride = _dir;
    }

    [Fact]
    public void UtcQuery_VisitsPreviousLocalCalendarDay()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test UTC-4", TimeSpan.FromHours(-4), "Test", "Test");
        var since = new DateTime(2026, 9, 8, 1, 0, 0, DateTimeKind.Utc);
        var sample = new MelanopicDoseSample { TimestampUtc = since.AddMinutes(10), MelanopicEdiLux = 5 };
        File.WriteAllText(Path.Combine(_dir, "2026-09-07.jsonl"), JsonSerializer.Serialize(sample));
        var result = MelanopicDoseStore.LoadSince(since, new DateTime(2026, 9, 7, 22, 0, 0), zone);
        Assert.Equal(sample, Assert.Single(result));
    }

    [Fact]
    public void DoseFiles_StayReadableAfterCultureChanges()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var sample = new MelanopicDoseSample { TimestampUtc = DateTime.UtcNow, MelanopicEdiLux = 5 };
            MelanopicDoseStore.Append(sample);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(sample, Assert.Single(MelanopicDoseStore.LoadSince(sample.TimestampUtc.ToLocalTime())));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("monitor:a", "monitor/a")]
    [InlineData("monitor-a", "MONITOR-A")]
    public void MonitorIdentity_IsolatesDistinctPathsAndIgnoresCase(string a, string b)
    {
        bool same = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(same, string.Equals(TrustCheckHistory.GetHistoryPath(a),
            TrustCheckHistory.GetHistoryPath(b), StringComparison.OrdinalIgnoreCase));
        TrustCheckHistory.Append(new TrustCheckEntry { MonitorDevicePath = a, AvgDeltaE2000 = 0.5 });
        if (same) Assert.Single(TrustCheckHistory.Load(b));
        else Assert.Empty(TrustCheckHistory.Load(b));
    }

    [Fact]
    public void LoadedHistory_IsChronological()
    {
        var earlier = new TrustCheckEntry { MonitorDevicePath = "test", TimestampUtc = new DateTime(2026, 1, 1), AvgDeltaE2000 = 0.5 };
        var later = earlier with { TimestampUtc = earlier.TimestampUtc.AddDays(30), AvgDeltaE2000 = 3 };
        TrustCheckHistory.Append(later);
        TrustCheckHistory.Append(earlier);
        var result = TrustCheckHistory.Load("test");
        Assert.Equal(2, result.Count);
        Assert.Equal(earlier.TimestampUtc, result[0].TimestampUtc);
        Assert.Equal(later.TimestampUtc, result[1].TimestampUtc);
    }

    [Fact]
    public void Drift_UsesChronologicalBaselineAndLatest()
    {
        var earlier = new TrustCheckEntry { MonitorDevicePath = "test", TimestampUtc = new DateTime(2026, 1, 1), AvgDeltaE2000 = 0.5 };
        var later = earlier with { TimestampUtc = earlier.TimestampUtc.AddDays(30), AvgDeltaE2000 = 3 };
        var result = TrustCheckHistory.AnalyzeDrift(new[] { later, earlier });
        Assert.NotNull(result);
        Assert.Same(earlier, result.Baseline);
        Assert.Same(later, result.Latest);
    }

    [Fact]
    public void LegacyHistory_IsPreservedAndFilteredByMonitor()
    {
        var own = new TrustCheckEntry { MonitorDevicePath = "monitor:a", TimestampUtc = new DateTime(2026, 1, 1) };
        var other = own with { MonitorDevicePath = "monitor/a" };
        File.WriteAllLines(Path.Combine(_dir, "monitor_a.jsonl"), new[] {
            JsonSerializer.Serialize(own), JsonSerializer.Serialize(other) });
        TrustCheckHistory.Append(own with { TimestampUtc = own.TimestampUtc.AddDays(1) });
        var result = TrustCheckHistory.Load("monitor:a");
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("monitor:a", e.MonitorDevicePath));
        Assert.Single(TrustCheckHistory.Load("monitor/a"));
    }

    [Fact]
    public void LongMonitorIds_DoNotCollide()
    {
        string prefix = new string('a', 150);
        Assert.NotEqual(TrustCheckHistory.GetHistoryPath(prefix + "1"),
            TrustCheckHistory.GetHistoryPath(prefix + "2"));
    }

    public void Dispose()
    {
        MelanopicDoseStore.DirectoryOverride = null;
        TrustCheckHistory.TrendDirectoryOverride = null;
        Directory.Delete(_dir, recursive: true);
    }
}
