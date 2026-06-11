using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reflection;
using HDRGammaController.Core;

namespace HDRGammaController.Services
{
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string ReleaseUrl { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public bool IsUpdateAvailable { get; set; }
        public DateTime PublishedAt { get; set; }
    }

    public class UpdateService
    {
        // The GitHub repo is being renamed for the Gloam rebrand. Until the rename
        // lands, the check 404s and logs - acceptable.
        private const string RepoOwner = "davidtorcivia";
        private const string RepoName = "gloam";

        // Rate limiting to prevent excessive API calls
        private static DateTime _lastCheckTime = DateTime.MinValue;
        private static UpdateInfo? _cachedResult = null;
        private static readonly TimeSpan MinCheckInterval = TimeSpan.FromMinutes(15);
        private const int MaxResponseSizeBytes = 1024 * 100; // 100KB max response

        public async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            // Rate limiting - return cached result if checked recently
            if (_cachedResult != null && DateTime.UtcNow - _lastCheckTime < MinCheckInterval)
            {
                Log.Info($"UpdateService: Returning cached result (last check was {(DateTime.UtcNow - _lastCheckTime).TotalMinutes:F1} minutes ago)");
                return _cachedResult;
            }

            var result = new UpdateInfo();

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Gloam", "1.0"));
                client.Timeout = TimeSpan.FromSeconds(30);

                // Get the release tagged 'latest' (our Auto-Build)
                // Note: The standard 'releases/latest' endpoint ONLY returns stable releases, not prereleases.
                // Since our auto-build is a prerelease tagged 'latest', we should fetch by tag.
                string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/tags/latest";

                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    // Fallback to latest stable release
                    url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
                    response = await client.GetAsync(url);
                }

                if (response.IsSuccessStatusCode)
                {
                    // Security: Check response size before reading
                    if (response.Content.Headers.ContentLength > MaxResponseSizeBytes)
                    {
                        Log.Error($"UpdateService: Response too large ({response.Content.Headers.ContentLength} bytes), skipping");
                        return result;
                    }

                    var json = await response.Content.ReadAsStringAsync();

                    // Additional size check for cases where Content-Length is not set
                    if (json.Length > MaxResponseSizeBytes)
                    {
                        Log.Error($"UpdateService: Response too large ({json.Length} chars), skipping");
                        return result;
                    }

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Security: Validate expected properties exist before accessing
                    if (!root.TryGetProperty("tag_name", out var tagNameProp) || tagNameProp.ValueKind != JsonValueKind.String)
                    {
                        Log.Error("UpdateService: Invalid response - missing tag_name");
                        return result;
                    }

                    string tagName = tagNameProp.GetString() ?? "";
                    string htmlUrl = root.TryGetProperty("html_url", out var htmlUrlProp) ? htmlUrlProp.GetString() ?? "" : "";
                    string publishedAtStr = root.TryGetProperty("published_at", out var publishedProp) ? publishedProp.GetString() ?? "" : "";
                    // Parse as UTC: the old default parse converted to local time while the
                    // build date was UTC, skewing the comparison by the timezone offset.
                    DateTime publishedAt = ParseUtc(publishedAtStr);

                    // GitHub freezes published_at when a release is first published.
                    // The CI workflow reuses the rolling 'latest' release and only swaps
                    // its assets, so published_at never moves again; the assets'
                    // updated_at is the only timestamp that tracks new builds.
                    DateTime releaseTimeUtc = publishedAt;
                    if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assetsProp.EnumerateArray())
                        {
                            if (asset.TryGetProperty("updated_at", out var updatedProp) && updatedProp.ValueKind == JsonValueKind.String)
                            {
                                DateTime assetUpdated = ParseUtc(updatedProp.GetString() ?? "");
                                if (assetUpdated > releaseTimeUtc) releaseTimeUtc = assetUpdated;
                            }

                            if (string.IsNullOrEmpty(result.DownloadUrl) &&
                                asset.TryGetProperty("browser_download_url", out var dlProp) && dlProp.ValueKind == JsonValueKind.String)
                            {
                                result.DownloadUrl = dlProp.GetString() ?? "";
                            }
                        }
                    }

                    result.Version = tagName;
                    result.ReleaseUrl = htmlUrl;
                    result.PublishedAt = releaseTimeUtc;

                    // The auto-build is a rolling 'latest' tag with no comparable semver,
                    // so "newer" means built/published after this binary was compiled.
                    DateTime buildDate = GetBuildTimeUtc(Assembly.GetExecutingAssembly());

                    // Allow 1 hour buffer for build server time differences
                    if (releaseTimeUtc > buildDate.AddHours(1))
                    {
                        result.IsUpdateAvailable = true;
                    }

                    Log.Info($"UpdateService: Release '{tagName}' time {releaseTimeUtc:u}, build time {buildDate:u}, update available: {result.IsUpdateAvailable}");

                    // Cache only successful checks; a transient failure should not
                    // suppress retries for the next 15 minutes.
                    _lastCheckTime = DateTime.UtcNow;
                    _cachedResult = result;
                }
                else
                {
                    Log.Error($"UpdateService: GitHub API returned {(int)response.StatusCode} {response.StatusCode} for {url}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"UpdateService: Update check failed: {ex.Message}");
            }

            return result;
        }
        
        private static DateTime ParseUtc(string value)
        {
            return DateTime.TryParse(value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed) ? parsed : DateTime.MinValue;
        }

        private static DateTime GetBuildTimeUtc(Assembly assembly)
        {
            // Preferred: the BuildTimestampUtc assembly metadata stamped at compile
            // time (see the csproj). Reliable across copies and CI builds.
            foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (attr.Key == "BuildTimestampUtc" &&
                    DateTime.TryParse(attr.Value,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var stamped))
                {
                    return stamped;
                }
            }

            // Fallback: file write time — fragile (any file copy refreshes it, which
            // suppresses update notifications until the next real build).
            try
            {
                string location = assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    return System.IO.File.GetLastWriteTimeUtc(location);
                }
            }
            catch {}

            return DateTime.MinValue;
        }
    }
}
