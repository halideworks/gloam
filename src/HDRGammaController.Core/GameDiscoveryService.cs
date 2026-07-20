using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace HDRGammaController.Core
{
    public sealed record DiscoveredGame(
        string DisplayName,
        string ExecutableName,
        string ExecutablePath,
        string Library);

    /// <summary>
    /// Finds installed games from launcher metadata. It deliberately avoids a whole-drive
    /// executable crawl: discovery reads the small manifests launchers already maintain and
    /// never starts, patches, or injects into a game.
    /// </summary>
    public sealed class GameDiscoveryService
    {
        public IReadOnlyList<DiscoveredGame> Scan()
        {
            var found = new List<DiscoveredGame>();

            foreach (string steamRoot in FindSteamRoots())
                TryAdd(found, () => ScanSteamLibrary(steamRoot));

            string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(commonData))
            {
                TryAdd(found, () => ScanEpicManifests(Path.Combine(
                    commonData, "Epic", "EpicGamesLauncher", "Data", "Manifests")));
            }

            TryAdd(found, ScanGogRegistry);
            TryAdd(found, ScanXboxLibraries);

            return found
                .Where(game => GamerExecutableSafety.IsSafeProfileTarget(
                    game.ExecutableName, game.DisplayName))
                .GroupBy(game => game.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        internal static IReadOnlyList<DiscoveredGame> ScanSteamLibrary(string steamRoot)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddExistingDirectory(roots, steamRoot);

            string libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
            {
                string text = File.ReadAllText(libraryFile);
                foreach (Match match in Regex.Matches(
                    text, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                {
                    string path = match.Groups["path"].Value.Replace("\\\\", "\\");
                    AddExistingDirectory(roots, path);
                }
            }

            var games = new List<DiscoveredGame>();
            foreach (string root in roots)
            {
                string steamApps = Path.Combine(root, "steamapps");
                if (!Directory.Exists(steamApps)) continue;

                foreach (string manifest in Directory.EnumerateFiles(
                    steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                {
                    string text;
                    try { text = File.ReadAllText(manifest); }
                    catch { continue; }

                    string name = ReadVdfValue(text, "name");
                    string installDir = ReadVdfValue(text, "installdir");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir))
                        continue;
                    if (IsNonGameLibraryEntry(name)) continue;

                    string gameDir = Path.Combine(steamApps, "common", installDir);
                    string? executable = FindLikelyExecutable(gameDir, name, installDir);
                    if (executable != null && GamerExecutableSafety.IsSafeProfileTarget(
                        Path.GetFileName(executable), name))
                        games.Add(Create(name, executable, "Steam"));
                }
            }
            return games;
        }

        internal static IReadOnlyList<DiscoveredGame> ScanEpicManifests(string manifestDirectory)
        {
            var games = new List<DiscoveredGame>();
            if (!Directory.Exists(manifestDirectory)) return games;

            foreach (string manifest in Directory.EnumerateFiles(
                manifestDirectory, "*.item", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using JsonDocument json = JsonDocument.Parse(File.ReadAllText(manifest));
                    JsonElement root = json.RootElement;
                    string name = ReadJsonString(root, "DisplayName");
                    string install = ReadJsonString(root, "InstallLocation");
                    string launch = ReadJsonString(root, "LaunchExecutable");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(launch)) continue;

                    string executable = Path.IsPathRooted(launch) ? launch : Path.Combine(install, launch);
                    if (!File.Exists(executable) || !GamerExecutableSafety.IsSafeProfileTarget(
                            Path.GetFileName(executable), name))
                    {
                        executable = FindLikelyExecutable(
                            install, name, Path.GetFileName(install)) ?? string.Empty;
                    }
                    if (File.Exists(executable) && GamerExecutableSafety.IsSafeProfileTarget(
                            Path.GetFileName(executable), name))
                        games.Add(Create(name, executable, "Epic Games"));
                }
                catch
                {
                    // One damaged launcher manifest must not hide the rest of the library.
                }
            }
            return games;
        }

        internal static IReadOnlyList<DiscoveredGame> ScanXboxLibrary(string xboxGamesDirectory)
        {
            var games = new List<DiscoveredGame>();
            if (!Directory.Exists(xboxGamesDirectory)) return games;

            foreach (string titleDir in Directory.EnumerateDirectories(xboxGamesDirectory))
            {
                string content = Path.Combine(titleDir, "Content");
                string config = Path.Combine(content, "MicrosoftGame.Config");
                if (!File.Exists(config)) continue;
                try
                {
                    XDocument document = XDocument.Load(config);
                    XElement? executableNode = document.Descendants()
                        .FirstOrDefault(element => element.Attribute("Executable") != null);
                    string? relative = executableNode?.Attribute("Executable")?.Value;
                    string name = document.Descendants()
                        .Select(element => element.Attribute("Name")?.Value)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                        ?? Path.GetFileName(titleDir);
                    if (string.IsNullOrWhiteSpace(relative)) continue;
                    string executable = Path.Combine(content, relative);
                    if (File.Exists(executable) && GamerExecutableSafety.IsSafeProfileTarget(
                        Path.GetFileName(executable), name))
                        games.Add(Create(name, executable, "Xbox"));
                }
                catch
                {
                    // Ignore titles whose metadata is unavailable or uses a newer schema.
                }
            }
            return games;
        }

        private static IReadOnlyList<DiscoveredGame> ScanGogRegistry()
        {
            var games = new List<DiscoveredGame>();
            foreach ((RegistryHive hive, RegistryView view) in new[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Registry64),
                (RegistryHive.CurrentUser, RegistryView.Registry32)
            })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? gamesKey = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                    if (gamesKey == null) continue;
                    foreach (string id in gamesKey.GetSubKeyNames())
                    {
                        using RegistryKey? gameKey = gamesKey.OpenSubKey(id);
                        string name = gameKey?.GetValue("gameName") as string ?? string.Empty;
                        string install = gameKey?.GetValue("path") as string ?? string.Empty;
                        string executable = gameKey?.GetValue("exe") as string ?? string.Empty;
                        if (!Path.IsPathRooted(executable)) executable = Path.Combine(install, executable);
                        if (!string.IsNullOrWhiteSpace(name) && File.Exists(executable) &&
                            GamerExecutableSafety.IsSafeProfileTarget(Path.GetFileName(executable), name))
                            games.Add(Create(name, executable, "GOG"));
                    }
                }
                catch
                {
                    // Registry view or launcher may be absent.
                }
            }
            return games;
        }

        private static IReadOnlyList<DiscoveredGame> ScanXboxLibraries()
        {
            var games = new List<DiscoveredGame>();
            foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
            {
                try { games.AddRange(ScanXboxLibrary(Path.Combine(drive.RootDirectory.FullName, "XboxGames"))); }
                catch { }
            }
            return games;
        }

        private static IEnumerable<string> FindSteamRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((RegistryHive hive, RegistryView view, string keyName) in new[]
            {
                (RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Valve\Steam"),
                (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam"),
                (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam")
            })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? key = baseKey.OpenSubKey(keyName);
                    string? path = key?.GetValue("SteamPath") as string
                        ?? key?.GetValue("InstallPath") as string;
                    AddExistingDirectory(roots, path);
                }
                catch { }
            }

            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            AddExistingDirectory(roots, Path.Combine(programFilesX86, "Steam"));
            return roots;
        }

        private static string? FindLikelyExecutable(string gameDir, string displayName, string installDir)
        {
            if (!Directory.Exists(gameDir)) return null;
            var candidates = new List<(string Path, int Depth)>();
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((gameDir, 0));
            const int candidateLimit = 500;
            while (queue.Count > 0 && candidates.Count < candidateLimit)
            {
                (string directory, int depth) = queue.Dequeue();
                try
                {
                    candidates.AddRange(Directory.EnumerateFiles(directory, "*.exe")
                        .Take(candidateLimit - candidates.Count)
                        .Select(path => (path, depth)));
                    if (depth >= 4) continue;
                    foreach (string child in Directory.EnumerateDirectories(directory).Take(80))
                        queue.Enqueue((child, depth + 1));
                }
                catch { }
            }

            string gameKey = NormalizeForMatch(displayName);
            string installKey = NormalizeForMatch(installDir);
            return candidates
                .Where(candidate => !IsExcludedExecutable(candidate.Path))
                .Select(candidate => (candidate.Path,
                    Score: ScoreExecutable(candidate.Path, candidate.Depth, gameKey, installKey)))
                .Where(candidate => candidate.Score >= 90)
                .OrderByDescending(candidate => candidate.Score)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();
        }

        private static int ScoreExecutable(string path, int depth, string gameKey, string installKey)
        {
            string fileKey = NormalizeForMatch(Path.GetFileNameWithoutExtension(path));
            int score = 100 - depth * 15;
            if (fileKey == installKey) score += 160;
            if (fileKey == gameKey) score += 160;
            if (installKey.Length >= 4 && (fileKey.Contains(installKey) || installKey.Contains(fileKey))) score += 70;
            if (gameKey.Length >= 4 && (fileKey.Contains(gameKey) || gameKey.Contains(fileKey))) score += 70;
            string directoryKey = Path.GetDirectoryName(path)?.ToLowerInvariant() ?? string.Empty;
            if (directoryKey.Contains("binaries") || directoryKey.Contains("shipping") ||
                directoryKey.Contains("win64") || directoryKey.Contains("win_x64") ||
                directoryKey.Contains("win_x86")) score += 35;
            try { score += (int)Math.Min(new FileInfo(path).Length / (20L * 1024 * 1024), 40); }
            catch { }
            return score;
        }

        private static bool IsExcludedExecutable(string path)
        {
            return !GamerExecutableSafety.IsSafeProfileTarget(Path.GetFileName(path));
        }

        private static bool IsNonGameLibraryEntry(string name)
        {
            return !GamerExecutableSafety.IsSafeProfileTarget("game.exe", name);
        }

        private static string ReadVdfValue(string text, string key)
        {
            Match match = Regex.Match(text,
                $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"(?<value>[^\\\"]*)\\\"",
                RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["value"].Value : string.Empty;
        }

        private static string ReadJsonString(JsonElement root, string property) =>
            root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static string NormalizeForMatch(string value) =>
            new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        private static DiscoveredGame Create(string name, string executable, string library) =>
            new(name.Trim(), Path.GetFileName(executable), Path.GetFullPath(executable), library);

        private static void AddExistingDirectory(ISet<string> roots, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                roots.Add(Path.GetFullPath(path));
        }

        private static void TryAdd(List<DiscoveredGame> destination, Func<IReadOnlyList<DiscoveredGame>> scan)
        {
            try { destination.AddRange(scan()); }
            catch { }
        }
    }
}
