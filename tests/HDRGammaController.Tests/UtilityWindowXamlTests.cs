using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace HDRGammaController.Tests;

public class UtilityWindowXamlTests
{
    [Fact]
    public void CcssBrowser_UsesXamlBodyInsideSharedChrome()
    {
        WpfTestHost.Run(() =>
        {
            var window = new CcssBrowserWindow(
                "Example panel",
                System.IO.Path.GetTempPath(),
                title: "Meter corrections");
            try
            {
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.IsType<Border>(window.Content);
                Assert.IsType<TextBox>(window.FindName("_query"));
                Assert.IsType<ListView>(window.FindName("_list"));
                Assert.IsType<Button>(window.FindName("_downloadButton"));
                Assert.Equal("Example panel", ((TextBox)window.FindName("_query")).Text);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
