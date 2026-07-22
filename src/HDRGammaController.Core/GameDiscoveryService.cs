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
        string Library,
        GamerHdrCapability HdrCapability = GamerHdrCapability.Unknown,
        string? HdrEvidence = null);

    public sealed record GameDiscoveryProgress(
        string Stage,
        int GamesFound,
        IReadOnlyList<DiscoveredGame> Games,
        bool UsedCache = false);

    /// <summary>
    /// Finds installed games from launcher metadata. It deliberately avoids a whole-drive
    /// executable crawl: discovery reads the small manifests launchers already maintain and
    /// never starts, patches, or injects into a game.
    /// </summary>
    public sealed class GameDiscoveryService
    {
        private sealed record CacheEntry(
            string Fingerprint,
            IReadOnlyList<DiscoveredGame> Games,
            DateTime CachedUtc);
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
        // Shared for the process: closing and reopening Game Lab should not throw away the
        // launcher manifest cache. Fingerprints still invalidate each source immediately.
        private static readonly object CacheLock = new();
        private static readonly Dictionary<string, CacheEntry> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<DiscoveredGame> Scan()
            => Scan(CancellationToken.None, null);

        public IReadOnlyList<DiscoveredGame> Scan(
            CancellationToken cancellationToken,
            IProgress<GameDiscoveryProgress>? progress = null)
        {
            var found = new List<DiscoveredGame>();

            foreach (string steamRoot in FindSteamRoots())
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool cached = TryAddCached(
                    found,
                    $"steam:{steamRoot}",
                    () => SteamFingerprint(steamRoot, cancellationToken),
                    () => ScanSteamLibrary(steamRoot, cancellationToken),
                    cancellationToken);
                Report(progress, $"Steam · {Path.GetFileName(steamRoot)}", found, cached);
            }

            string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(commonData))
            {
                string epicManifests = Path.Combine(
                    commonData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
                bool cached = TryAddCached(
                    found,
                    $"epic:{epicManifests}",
                    () => FileSetFingerprint(epicManifests, "*.item", SearchOption.TopDirectoryOnly, cancellationToken),
                    () => ScanEpicManifests(epicManifests, cancellationToken),
                    cancellationToken);
                Report(progress, "Epic Games", found, cached);
            }

            TryAdd(found, () => ScanGogRegistry(cancellationToken), cancellationToken);
            Report(progress, "GOG", found, false);
            ScanXboxLibraries(found, cancellationToken, progress);

            return Normalize(found);
        }

        private static IReadOnlyList<DiscoveredGame> Normalize(IEnumerable<DiscoveredGame> found) => found
                .Where(game => GamerExecutableSafety.IsSafeProfileTarget(
                    game.ExecutableName, game.DisplayName))
                .GroupBy(game => game.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        internal static IReadOnlyList<DiscoveredGame> ScanSteamLibrary(string steamRoot) =>
            ScanSteamLibrary(steamRoot, CancellationToken.None);

        internal static IReadOnlyList<DiscoveredGame> ScanSteamLibrary(
            string steamRoot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                string steamApps = Path.Combine(root, "steamapps");
                if (!Directory.Exists(steamApps)) continue;

                foreach (string manifest in Directory.EnumerateFiles(
                    steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string text;
                    try { text = File.ReadAllText(manifest); }
                    catch { continue; }

                    string name = ReadVdfValue(text, "name");
                    string installDir = ReadVdfValue(text, "installdir");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir))
                        continue;
                    if (IsNonGameLibraryEntry(name)) continue;

                    string gameDir = Path.Combine(steamApps, "common", installDir);
                    string? executable = FindLikelyExecutable(
                        gameDir, name, installDir, cancellationToken);
                    if (executable != null && GamerExecutableSafety.IsSafeProfileTarget(
                        Path.GetFileName(executable), name))
                        games.Add(Create(name, executable, "Steam"));
                }
            }
            return games;
        }

        internal static IReadOnlyList<DiscoveredGame> ScanEpicManifests(string manifestDirectory) =>
            ScanEpicManifests(manifestDirectory, CancellationToken.None);

        internal static IReadOnlyList<DiscoveredGame> ScanEpicManifests(
            string manifestDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var games = new List<DiscoveredGame>();
            if (!Directory.Exists(manifestDirectory)) return games;

            foreach (string manifest in Directory.EnumerateFiles(
                manifestDirectory, "*.item", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                            install, name, Path.GetFileName(install), cancellationToken) ?? string.Empty;
                    }
                    if (File.Exists(executable) && GamerExecutableSafety.IsSafeProfileTarget(
                            Path.GetFileName(executable), name))
                        games.Add(Create(name, executable, "Epic Games"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One damaged launcher manifest must not hide the rest of the library.
                }
            }
            return games;
        }

        internal static IReadOnlyList<DiscoveredGame> ScanXboxLibrary(string xboxGamesDirectory) =>
            ScanXboxLibrary(xboxGamesDirectory, CancellationToken.None);

        internal static IReadOnlyList<DiscoveredGame> ScanXboxLibrary(
            string xboxGamesDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var games = new List<DiscoveredGame>();
            if (!Directory.Exists(xboxGamesDirectory)) return games;

            foreach (string titleDir in Directory.EnumerateDirectories(xboxGamesDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Ignore titles whose metadata is unavailable or uses a newer schema.
                }
            }
            return games;
        }

        private static IReadOnlyList<DiscoveredGame> ScanGogRegistry(CancellationToken cancellationToken)
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
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? gamesKey = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                    if (gamesKey == null) continue;
                    foreach (string id in gamesKey.GetSubKeyNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Registry view or launcher may be absent.
                }
            }
            return games;
        }

        private void ScanXboxLibraries(
            List<DiscoveredGame> destination,
            CancellationToken cancellationToken,
            IProgress<GameDiscoveryProgress>? progress)
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                bool cached = TryAddCached(
                    destination,
                    $"xbox:{directory}",
                    () => XboxFingerprint(directory, cancellationToken),
                    () => ScanXboxLibrary(directory, cancellationToken),
                    cancellationToken);
                Report(progress, $"Xbox · {drive.Name}", destination, cached);
            }
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

        private static string? FindLikelyExecutable(
            string gameDir,
            string displayName,
            string installDir,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(gameDir)) return null;
            var candidates = new List<(string Path, int Depth)>();
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((gameDir, 0));
            const int candidateLimit = 500;
            while (queue.Count > 0 && candidates.Count < candidateLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                catch (Exception ex) when (ex is not OperationCanceledException) { }
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

        private static DiscoveredGame Create(string name, string executable, string library)
        {
            string fullPath = Path.GetFullPath(executable);
            (GamerHdrCapability capability, string? evidence) = DetectHdrCapability(fullPath);
            return new(name.Trim(), Path.GetFileName(fullPath), fullPath, library, capability, evidence);
        }

        private static readonly string[] HdrConfigTokens =
        [
            "bUseHDRDisplayOutput",
            "r.HDR.EnableHDROutput",
            "HDRDisplayOutput",
            "EnableHDR",
            "HDROutput",
            "HDR10",
            "scRGB",
            "HighDynamicRange"
        ];

        private static readonly HashSet<string> HdrConfigExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".cfg", ".conf", ".ini", ".json", ".toml", ".xml", ".yaml", ".yml"
            };

        /// <summary>
        /// Looks for strong, local HDR-rendering switches in a small bounded set of game
        /// configuration files. This never launches or modifies the title and deliberately
        /// returns Unknown instead of guessing from the renderer API or store category.
        /// </summary>
        public static (GamerHdrCapability Capability, string? Evidence) DetectHdrCapability(
            string executablePath)
        {
            try
            {
                string? executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
                if (string.IsNullOrWhiteSpace(executableDirectory) || !Directory.Exists(executableDirectory))
                    return (GamerHdrCapability.Unknown, null);

                var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string gameRoot = executableDirectory;
                string leaf = Path.GetFileName(gameRoot);
                if (leaf.Equals("win64", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("win32", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("x86", StringComparison.OrdinalIgnoreCase))
                {
                    string? parent = Directory.GetParent(gameRoot)?.FullName;
                    if (!string.IsNullOrWhiteSpace(parent)) gameRoot = parent;
                }
                leaf = Path.GetFileName(gameRoot);
                if (leaf.Equals("binaries", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("bin", StringComparison.OrdinalIgnoreCase))
                {
                    string? parent = Directory.GetParent(gameRoot)?.FullName;
                    if (!string.IsNullOrWhiteSpace(parent)) gameRoot = parent;
                }

                AddDirectory(executableDirectory);
                AddDirectory(gameRoot);
                AddDirectory(Path.Combine(gameRoot, "Config"));
                AddDirectory(Path.Combine(gameRoot, "Engine", "Config"));
                AddDirectory(Path.Combine(gameRoot, "Saved", "Config", "Windows"));
                AddDirectory(Path.Combine(gameRoot, "Saved", "Config", "WindowsNoEditor"));

                // Unreal projects commonly place Config below a project-named directory.
                // Inspect only a capped set of immediate children; never crawl the tree or
                // walk upward into a launcher directory containing unrelated games.
                try
                {
                    foreach (string child in Directory.EnumerateDirectories(gameRoot).Take(24))
                        AddDirectory(Path.Combine(child, "Config"));
                }
                catch { }

                int inspected = 0;
                foreach (string directory in directories.Take(48))
                {
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly); }
                    catch { continue; }

                    foreach (string file in files
                                 .Where(path => HdrConfigExtensions.Contains(Path.GetExtension(path)))
                                 .Take(40))
                    {
                        if (++inspected > 120) return (GamerHdrCapability.Unknown, null);
                        string fileName = Path.GetFileName(file);
                        if (fileName.Contains("readme", StringComparison.OrdinalIgnoreCase) ||
                            fileName.Contains("license", StringComparison.OrdinalIgnoreCase) ||
                            fileName.Contains("changelog", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string text;
                        try
                        {
                            var info = new FileInfo(file);
                            if (info.Length <= 0 || info.Length > 2 * 1024 * 1024) continue;
                            text = File.ReadAllText(file);
                        }
                        catch { continue; }

                        string? token = HdrConfigTokens.FirstOrDefault(candidate =>
                            text.Contains(candidate, StringComparison.OrdinalIgnoreCase));
                        if (token != null)
                            return (GamerHdrCapability.Detected,
                                $"{token} in {Path.GetFileName(file)}");
                    }
                }

                return (GamerHdrCapability.Unknown, null);

                void AddDirectory(string? path)
                {
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                        directories.Add(Path.GetFullPath(path));
                }
            }
            catch
            {
                return (GamerHdrCapability.Unknown, null);
            }
        }

        private static void AddExistingDirectory(ISet<string> roots, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                roots.Add(Path.GetFullPath(path));
        }

        private bool TryAddCached(
            List<DiscoveredGame> destination,
            string cacheKey,
            Func<string> fingerprint,
            Func<IReadOnlyList<DiscoveredGame>> scan,
            CancellationToken cancellationToken)
        {
            try
            {
                string currentFingerprint = fingerprint();
                lock (CacheLock)
                {
                    if (Cache.TryGetValue(cacheKey, out CacheEntry? cached) &&
                        string.Equals(cached.Fingerprint, currentFingerprint, StringComparison.Ordinal) &&
                        DateTime.UtcNow - cached.CachedUtc <= CacheLifetime &&
                        cached.Games.All(game => File.Exists(game.ExecutablePath)))
                    {
                        destination.AddRange(cached.Games);
                        return true;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<DiscoveredGame> games = scan();
                cancellationToken.ThrowIfCancellationRequested();
                lock (CacheLock)
                    Cache[cacheKey] = new CacheEntry(
                        currentFingerprint, games.ToArray(), DateTime.UtcNow);
                destination.AddRange(games);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Info($"GameDiscoveryService: source scan failed ({cacheKey}): {ex.Message}");
            }
            return false;
        }

        private static void TryAdd(
            List<DiscoveredGame> destination,
            Func<IReadOnlyList<DiscoveredGame>> scan,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.AddRange(scan());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Info($"GameDiscoveryService: source scan failed: {ex.Message}");
            }
        }

        private static void Report(
            IProgress<GameDiscoveryProgress>? progress,
            string stage,
            List<DiscoveredGame> found,
            bool cached)
        {
            if (progress == null) return;
            IReadOnlyList<DiscoveredGame> snapshot = Normalize(found);
            progress.Report(new GameDiscoveryProgress(stage, snapshot.Count, snapshot, cached));
        }

        private static string SteamFingerprint(string steamRoot, CancellationToken cancellationToken)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddExistingDirectory(roots, steamRoot);
            string libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
            {
                string text = File.ReadAllText(libraryFile);
                foreach (Match match in Regex.Matches(
                    text, "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                    AddExistingDirectory(roots, match.Groups["path"].Value.Replace("\\\\", "\\"));
            }

            var files = new List<string>();
            if (File.Exists(libraryFile)) files.Add(libraryFile);
            foreach (string root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string steamApps = Path.Combine(root, "steamapps");
                if (!Directory.Exists(steamApps)) continue;
                files.AddRange(Directory.EnumerateFiles(
                    steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly));
            }
            return FingerprintFiles(files, cancellationToken);
        }

        private static string FileSetFingerprint(
            string directory,
            string pattern,
            SearchOption option,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(directory)) return "missing";
            return FingerprintFiles(
                Directory.EnumerateFiles(directory, pattern, option), cancellationToken);
        }

        private static string XboxFingerprint(
            string xboxGamesDirectory,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(xboxGamesDirectory)) return "missing";
            var configs = new List<string>();
            foreach (string titleDirectory in Directory.EnumerateDirectories(xboxGamesDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string config = Path.Combine(titleDirectory, "Content", "MicrosoftGame.Config");
                if (File.Exists(config)) configs.Add(config);
            }
            return FingerprintFiles(configs, cancellationToken);
        }

        private static string FingerprintFiles(
            IEnumerable<string> paths,
            CancellationToken cancellationToken)
        {
            var hash = new HashCode();
            int count = 0;
            foreach (string path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(path);
                    hash.Add(path, StringComparer.OrdinalIgnoreCase);
                    hash.Add(info.Length);
                    hash.Add(info.LastWriteTimeUtc.Ticks);
                    count++;
                }
                catch { }
            }
            hash.Add(count);
            return hash.ToHashCode().ToString("X8");
        }
    }
}
