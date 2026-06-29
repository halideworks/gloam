using System;
using System.IO;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Shared size-based log rotation. Logging is support evidence, so every file sink
    /// should preserve recent archives instead of truncating or deleting the active log.
    /// </summary>
    public static class LogFileRotator
    {
        public static void RotateIfNeeded(string path, long maxBytes, int maxArchives)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Log path is required.", nameof(path));
            if (maxBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes), "Maximum log size must be non-negative.");

            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= maxBytes) return;

            if (maxArchives <= 0)
            {
                File.Delete(path);
                return;
            }

            for (int i = maxArchives; i >= 1; i--)
            {
                string source = i == 1 ? path : $"{path}.{i - 1}";
                string dest = $"{path}.{i}";
                if (!File.Exists(source)) continue;
                if (i == maxArchives && File.Exists(dest))
                    File.Delete(dest);
                File.Move(source, dest, overwrite: true);
            }
        }
    }
}
