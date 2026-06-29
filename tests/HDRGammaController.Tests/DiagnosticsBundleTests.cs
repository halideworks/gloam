using System;
using HDRGammaController.Core;
using Xunit;

namespace HDRGammaController.Tests
{
    public class DiagnosticsBundleTests
    {
        [Fact]
        public void SanitizeText_ReplacesUserScopedPathsAndUserName()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string input = $"{userProfile}\\Documents\\probe.txt\n{localAppData}\\Gloam\\app.log\nuser={Environment.UserName}";

            string result = DiagnosticsBundle.SanitizeText(input);

            Assert.DoesNotContain(userProfile, result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(localAppData, result, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(Environment.UserName))
                Assert.DoesNotContain(Environment.UserName, result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", result);
            Assert.Contains("%LOCALAPPDATA%", result);
        }
    }
}
