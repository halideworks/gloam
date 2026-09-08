using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Gloam.Core.Calibration;
using Xunit;

namespace Gloam.Tests;

public class ReportHistoryWindowTests
{
    [Fact]
    public void Selection_ControlsAvailabilityOfReportActions()
    {
        string path = Path.Combine(CalibrationProfile.GetReportsDirectory(), $"history-test-{System.Guid.NewGuid():N}.json");
        new CalibrationProfile
        {
            MonitorName = "Test panel", MonitorDevicePath = "test", Target = StandardTargets.SrgbGamma22
        }.SaveToFile(path);
        try
        {
            WpfTestHost.Run(() =>
            {
                var window = new ReportHistoryWindow();
                try
                {
                    var children = Descendants(window).ToList();
                    var list = children.OfType<ListView>().Single();
                    var open = children.OfType<Button>().Single(b => Equals(b.Content, "Open"));
                    var delete = children.OfType<Button>().Single(b => Equals(b.Content, "Delete…"));
                    Assert.Equal(SelectionMode.Single, list.SelectionMode);
                    Assert.False(open.IsEnabled);
                    Assert.False(delete.IsEnabled);
                    list.SelectedIndex = 0;
                    Assert.True(open.IsEnabled);
                    Assert.True(delete.IsEnabled);
                    list.SelectedIndex = -1;
                    Assert.False(open.IsEnabled);
                    Assert.False(delete.IsEnabled);
                }
                finally { window.Close(); }
            });
        }
        finally { File.Delete(path); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
