using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;
using Xunit;

namespace HDRGammaController.Tests;

public class VerificationCoordinatorTests
{
    [Fact]
    public async Task RunAsync_SequencesPresentationDelayAndMeasurement()
    {
        var patches = new[]
        {
            new ColorPatch { Name = "White", DisplayRgb = new LinearRgb(1, 1, 1) },
            new ColorPatch { Name = "Gray", DisplayRgb = new LinearRgb(0.5, 0.5, 0.5) },
        };
        var events = new List<string>();
        var delays = new List<TimeSpan>();

        var result = await VerificationCoordinator.RunAsync(
            new VerificationCoordinator.Config(
                patches,
                StandardTargets.SrgbGamma22,
                NativeMeasurements: null,
                ShowPatch: patch => events.Add($"show:{patch.Name}"),
                ReportProgress: (current, total, patch, next) =>
                    events.Add($"progress:{current}/{total}:{next?.Name ?? "end"}"),
                MeasureAsync: (patch, _) =>
                {
                    events.Add($"measure:{patch.Name}");
                    return Task.FromResult(new MeasurementResult
                    {
                        Patch = patch,
                        Xyz = patch.Name == "White"
                            ? new CieXyz(95.047, 100, 108.883)
                            : new CieXyz(19.0, 20, 21.8),
                        IsValid = true,
                    });
                },
                MeasurementCaptured: () => events.Add("captured"),
                DelayAsync: (duration, _) =>
                {
                    delays.Add(duration);
                    events.Add("delay");
                    return Task.CompletedTask;
                }),
            CancellationToken.None);

        Assert.Equal(2, result.Measurements.Count);
        Assert.Null(result.Activation);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), delays[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(500), delays[1]);
        Assert.Equal(new[]
        {
            "progress:1/2:Gray", "show:White", "delay", "measure:White", "captured",
            "progress:2/2:end", "show:Gray", "delay", "measure:Gray", "captured",
        }, events);
    }

    [Fact]
    public async Task RunAsync_StopsBeforePresentingWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            VerificationCoordinator.RunAsync(
                new VerificationCoordinator.Config(
                    new[] { new ColorPatch { Name = "White", DisplayRgb = new LinearRgb(1, 1, 1) } },
                    StandardTargets.SrgbGamma22,
                    NativeMeasurements: null,
                    ShowPatch: _ => throw new InvalidOperationException("must not present"),
                    ReportProgress: (_, _, _, _) => throw new InvalidOperationException("must not report"),
                    MeasureAsync: (_, _) => throw new InvalidOperationException("must not measure")),
                cancellation.Token));
    }
}
