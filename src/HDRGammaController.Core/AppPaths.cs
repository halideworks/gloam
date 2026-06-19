using System;
using System.Collections.Generic;
using System.IO;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Central authority for the app's data directories. The user-facing brand is
    /// "Gloam"; internal namespaces and project names keep HDRGammaController.
    /// Every on-disk path the app owns must route through this class so the folder
    /// name lives in exactly one place.
    /// </summary>
    public static class AppPaths
    {
        /// <summary>User-facing data folder name (the brand).</summary>
        public const string DataFolderName = "Gloam";

        /// <summary>Pre-rebrand folder name, kept only for the one-time migration.</summary>
        public const string LegacyDataFolderName = "HDRGammaController";

        /// <summary>
        /// %LocalAppData%\Gloam - settings.json, app.log, reports, corrections,
        /// downloaded Argyll, extracted ICM templates.
        /// </summary>
        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataFolderName);

        /// <summary>%AppData%\Gloam - roaming data (calibration profile JSON).</summary>
        public static string RoamingDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            DataFolderName);

        /// <summary>
        /// One-time rebrand migration: if the old HDRGammaController data folder exists
        /// and the Gloam folder does not, move it (copy fallback if the move fails).
        /// Covers both the local and roaming roots. Must run before anything touches
        /// the new locations - including the log file, which lives under
        /// <see cref="DataDir"/> - so outcomes are returned as messages for the caller
        /// to log once the log sink is up.
        /// </summary>
        public static IReadOnlyList<string> MigrateLegacyData()
        {
            var messages = new List<string>();
            MigrateRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), messages);
            MigrateRoot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), messages);
            return messages;
        }

        private static void MigrateRoot(string root, List<string> messages)
        {
            try
            {
                if (string.IsNullOrEmpty(root)) return;
                string oldDir = Path.Combine(root, LegacyDataFolderName);
                string newDir = Path.Combine(root, DataFolderName);
                if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;

                try
                {
                    Directory.Move(oldDir, newDir);
                    messages.Add($"AppPaths: Migrated data folder {oldDir} -> {newDir}");
                }
                catch (Exception moveEx)
                {
                    // A locked file (e.g. another process tailing the old log) makes
                    // Directory.Move fail wholesale; fall back to a copy and leave the
                    // old folder behind rather than half-moving it.
                    CopyDirectory(oldDir, newDir);
                    messages.Add(
                        $"AppPaths: Move failed ({moveEx.Message}); copied {oldDir} -> {newDir}, old folder left in place");
                }
            }
            catch (Exception ex)
            {
                messages.Add($"AppPaths: Data folder migration failed under {root}: {ex.Message}");
            }
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            foreach (string dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}
