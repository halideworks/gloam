using HDRGammaController.Services;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Tests for the Run-key repair decision logic. Registry access itself is not
    /// unit-testable, so <see cref="StartupManager.IsRegisteredPathStale"/> and
    /// <see cref="StartupManager.ExtractExePath"/> are exercised as pure functions.
    /// </summary>
    public class StartupManagerTests
    {
        private const string CurrentExe = @"C:\Users\Dev\AppData\Local\GloamApp\current\Gloam.exe";

        [Theory]
        [InlineData("\"C:\\Users\\Dev\\AppData\\Local\\GloamApp\\current\\Gloam.exe\"")] // quoted, exact
        [InlineData(@"C:\Users\Dev\AppData\Local\GloamApp\current\Gloam.exe")]           // unquoted
        [InlineData("\"c:\\users\\dev\\appdata\\local\\gloamapp\\current\\gloam.exe\"")] // case differs
        [InlineData("  \"C:\\Users\\Dev\\AppData\\Local\\GloamApp\\current\\Gloam.exe\"  ")] // padded
        public void IsRegisteredPathStale_CurrentPath_IsNotStale(string registered)
        {
            Assert.False(StartupManager.IsRegisteredPathStale(registered, CurrentExe));
        }

        [Theory]
        [InlineData("\"C:\\Program Files\\HDR-Gamma-Adjust\\Gloam.exe\"")] // legacy install dir
        [InlineData(@"C:\Program Files\HDR-Gamma-Adjust\Gloam.exe")]       // legacy, unquoted
        [InlineData("\"C:\\Deleted\\Nowhere\\Gloam.exe\"")]                // nonexistent location
        [InlineData("\"C:\\Users\\Dev\\AppData\\Local\\GloamApp\\app-1.0.0\\Gloam.exe\"")] // old layout
        public void IsRegisteredPathStale_DifferentPath_IsStale(string registered)
        {
            Assert.True(StartupManager.IsRegisteredPathStale(registered, CurrentExe));
        }

        [Fact]
        public void IsRegisteredPathStale_QuotedPathWithArguments_ComparesExeOnly()
        {
            Assert.False(StartupManager.IsRegisteredPathStale(
                $"\"{CurrentExe}\" --autostart", CurrentExe));

            Assert.True(StartupManager.IsRegisteredPathStale(
                "\"C:\\Program Files\\HDR-Gamma-Adjust\\Gloam.exe\" --autostart", CurrentExe));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void IsRegisteredPathStale_MissingRegisteredValue_IsNotStale(string? registered)
        {
            // No Run value (startup disabled) means there is nothing to repair.
            Assert.False(StartupManager.IsRegisteredPathStale(registered, CurrentExe));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void IsRegisteredPathStale_UnknownCurrentPath_IsNotStale(string? currentExePath)
        {
            // If the current exe path cannot be determined, do not touch the registration.
            Assert.False(StartupManager.IsRegisteredPathStale($"\"{CurrentExe}\"", currentExePath));
        }

        [Theory]
        [InlineData("\"C:\\Apps\\Gloam.exe\"", @"C:\Apps\Gloam.exe")]
        [InlineData(@"C:\Apps\Gloam.exe", @"C:\Apps\Gloam.exe")]
        [InlineData("\"C:\\Apps\\Gloam.exe\" --flag", @"C:\Apps\Gloam.exe")]
        [InlineData("  \"C:\\Apps\\Gloam.exe\"  ", @"C:\Apps\Gloam.exe")]
        [InlineData("\"C:\\Apps\\Gloam.exe", @"C:\Apps\Gloam.exe")] // unterminated quote
        public void ExtractExePath_StripsQuotesAndArguments(string registryValue, string expected)
        {
            Assert.Equal(expected, StartupManager.ExtractExePath(registryValue));
        }
    }
}
