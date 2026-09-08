using System;
using System.IO;
using Gloam.Core;
using Gloam.Core.Calibration;
using Xunit;

namespace Gloam.Tests;

public sealed class FilePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GloamFilePersistence", Guid.NewGuid().ToString("N"));
    public FilePersistenceTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedReplacement_LeavesNoTemporaryFiles(int writer)
    {
        string path = Path.Combine(_directory, "destination.json");
        Directory.CreateDirectory(path);
        var failure = Record.Exception(() => Write(writer, path));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SavingProfile_DoesNotOverwriteExistingSidecar(int writer)
    {
        string path = Path.Combine(_directory, "profile.json");
        File.WriteAllText(path + ".tmp", "belongs to another operation");
        Write(writer, path);
        Assert.Equal("belongs to another operation", File.ReadAllText(path + ".tmp"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void OversizedReport_IsRejectedBeforeJsonParsing()
    {
        string path = Path.Combine(_directory, "large.json");
        using (var file = File.Create(path)) file.SetLength(16L * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => CalibrationProfile.LoadFromFile(path));
    }

    [Fact]
    public void BoundedRead_EnforcesDecodedCharacterLimit()
    {
        string path = Path.Combine(_directory, "characters.json");
        File.WriteAllText(path, new string('x', 101));
        Assert.Throws<InvalidDataException>(() => TextFileStore.ReadBounded(path, 1024, 100));
        Assert.Equal(new string('x', 101), TextFileStore.ReadBounded(path, 1024, 101));
    }

    [Fact]
    public void BoundedRead_PreservesUtf16BomSupport()
    {
        string path = Path.Combine(_directory, "unicode.json");
        File.WriteAllText(path, "Unicode report", System.Text.Encoding.Unicode);
        Assert.Equal("Unicode report", TextFileStore.ReadBounded(path, 1024, 100));
    }

    [Theory]
    [InlineData("123456", 100, 5)]
    [InlineData("\u03b1\u03b2\u03b3", 5, 100)]
    public void OversizedWrite_PreservesExistingFile(string text, long bytes, int characters)
    {
        string path = Path.Combine(_directory, "existing.json");
        File.WriteAllText(path, "original");
        Assert.Throws<InvalidDataException>(() => TextFileStore.WriteAtomic(path, text, bytes, characters));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    private static void Write(int writer, string path)
    {
        switch (writer)
        {
            case 0: SettingsPersistence.WriteAtomic(path, "{}"); break;
            case 1:
                new CalibrationProfile
                {
                    MonitorName = "Panel", MonitorDevicePath = "panel", Target = StandardTargets.SrgbGamma22
                }.SaveToFile(path);
                break;
            case 2: new DisplayCalibrationProfile().SaveToFile(path); break;
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
