using System;
using System.IO;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Minimal app-wide logger. Always echoes to the console (visible when one is
    /// attached, e.g. CLI usage); once <see cref="Initialize"/> has been called it
    /// also appends to a size-capped file. The GUI is a WinExe whose console output
    /// is discarded by Windows, so the file sink is the only durable diagnostic
    /// channel for the tray app.
    /// </summary>
    public static class Log
    {
        private static readonly object Sync = new object();
        private static string? _filePath;

        private const long MaxBytes = 5_000_000;
        private const int MaxArchives = 3;

        /// <summary>Default log location under LocalApplicationData.</summary>
        public static string DefaultFilePath => Path.Combine(AppPaths.DataDir, "app.log");

        /// <summary>
        /// Enables the file sink. Safe to call once at startup; if the path is
        /// unusable the logger silently stays console-only.
        /// </summary>
        public static void Initialize(string? filePath = null)
        {
            lock (Sync)
            {
                try
                {
                    string path = filePath ?? DefaultFilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    LogFileRotator.RotateIfNeeded(path, MaxBytes, MaxArchives);
                    _filePath = path;
                }
                catch
                {
                    _filePath = null;
                }
            }
        }

        public static void Info(string message) => Write("INFO ", message);

        public static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            Console.WriteLine(line);
            lock (Sync)
            {
                if (_filePath == null) return;
                try
                {
                    LogFileRotator.RotateIfNeeded(_filePath, MaxBytes, MaxArchives);
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
                catch { /* logging must never take the app down */ }
            }
        }
    }
}
