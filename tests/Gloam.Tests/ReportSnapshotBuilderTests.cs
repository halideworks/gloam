using System;
using Gloam.Core.Calibration;
using Gloam.Services;
using Xunit;

namespace Gloam.Tests;

public class ReportSnapshotBuilderTests
{
    [Fact]
    public void NewReportsFromSameNamedDisplaysAtSameTime_HaveDistinctPaths()
    {
        var a = new CalibrationProfile { MonitorDevicePath = "a", MonitorName = "Panel", Target = StandardTargets.SrgbGamma22 };
        var b = new CalibrationProfile { MonitorDevicePath = "b", MonitorName = "Panel", Target = StandardTargets.SrgbGamma22 };
        var time = new DateTime(2026, 9, 7, 12, 0, 0);
        Assert.NotEqual(ReportSnapshotBuilder.BuildPath(a, time), ReportSnapshotBuilder.BuildPath(b, time));
    }
}
