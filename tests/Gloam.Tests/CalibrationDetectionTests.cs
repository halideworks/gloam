using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Gloam.Core;
using Gloam.Core.Calibration;
using Gloam.ViewModels;
using Xunit;

namespace Gloam.Tests;

public class CalibrationDetectionTests
{
    [Fact]
    public async Task RefreshFailure_IsShownAndCanBeRetried()
    {
        using var instrument = new ColorimeterService(System.IO.Path.GetTempPath());
        var vm = new CalibrationSetupViewModel(new List<MonitorInfo>(), null,
            reusableColorimeterService: instrument);
        vm.InitializeInstrumentAsync = (_, _) => throw new InvalidOperationException("USB disconnected");
        var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(vm.RefreshColorimeterCommand);
        await command.ExecuteAsync(null);
        Assert.Contains("USB disconnected", vm.StatusText);
        Assert.False(vm.CanStart);
        Assert.True(command.CanExecute(null));
    }
    [Fact]
    public async Task ClosingSetup_CancelsDetectionAndPreventsLateUpdates()
    {
        using var instrument = new ColorimeterService(System.IO.Path.GetTempPath());
        var vm = new CalibrationSetupViewModel(new List<MonitorInfo>(), null,
            reusableColorimeterService: instrument);
        CancellationToken captured = default;
        int calls = 0;
        vm.InitializeInstrumentAsync = (_, token) =>
        {
            calls++;
            captured = token;
            return Task.Delay(Timeout.Infinite, token);
        };
        Task detection = vm.RefreshColorimeterAsync();
        Assert.True(captured.CanBeCanceled);
        Assert.False(vm.RefreshColorimeterCommand.CanExecute(null));
        string status = vm.StatusText;
        vm.StopDetection();
        await detection.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(captured.IsCancellationRequested);
        Assert.Equal(status, vm.StatusText);
        Assert.False(vm.CanStart);
        Assert.False(vm.RefreshColorimeterCommand.CanExecute(null));
        await vm.RefreshColorimeterAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OverlappingDetection_UsesOneInstrumentOperation()
    {
        using var instrument = new ColorimeterService(System.IO.Path.GetTempPath());
        var vm = new CalibrationSetupViewModel(new List<MonitorInfo>(), null,
            reusableColorimeterService: instrument);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        vm.InitializeInstrumentAsync = (_, _) => { calls++; return finish.Task; };
        Task first = vm.RefreshColorimeterAsync();
        try
        {
            await vm.RefreshColorimeterAsync();
            Assert.Equal(1, calls);
            Assert.False(vm.CanStart);
            Assert.False(vm.RefreshColorimeterCommand.CanExecute(null));
        }
        finally { finish.SetResult(); }
        await first;
        Assert.True(vm.RefreshColorimeterCommand.CanExecute(null));
    }

    [Fact]
    public async Task DetectionCancellation_ReportsTimeoutAndAllowsRetry()
    {
        using var instrument = new ColorimeterService(System.IO.Path.GetTempPath());
        var vm = new CalibrationSetupViewModel(new List<MonitorInfo>(), null,
            reusableColorimeterService: instrument);
        vm.InitializeInstrumentAsync = (_, _) => throw new OperationCanceledException();
        await vm.RefreshColorimeterAsync();
        Assert.Equal("Detection timed out", vm.StatusText);
        Assert.False(vm.CanStart);
        Assert.True(vm.RefreshColorimeterCommand.CanExecute(null));
        int calls = 0;
        vm.InitializeInstrumentAsync = (_, _) => { calls++; return Task.CompletedTask; };
        await vm.RefreshColorimeterAsync();
        Assert.Equal(1, calls);
        Assert.Equal("No colorimeter detected", vm.StatusText);
    }
}
