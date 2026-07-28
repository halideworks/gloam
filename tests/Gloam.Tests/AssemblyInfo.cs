using System;
using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestProcessBootstrap
{
    /// <summary>
    /// WPF's native theme loader reads WINDIR directly. Some isolated test runners expose
    /// only SystemRoot, causing otherwise-valid UI tests to fail before a Window is created.
    /// Normalize the standard aliases once for the whole test process.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDIR")))
        {
            string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(systemRoot))
                Environment.SetEnvironmentVariable("WINDIR", systemRoot);
        }
    }
}
