using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Client for the DisplayCAL colorimeter corrections database
    /// (colorimetercorrections.displaycal.net) — community-contributed, spectro-derived
    /// spectral samples (.ccss) and correction matrices (.ccmx) that make three-filter
    /// colorimeters read specific panels truthfully. The API is the one DisplayCAL itself
    /// uses: GET with ?get=1&amp;type=ccss&amp;display=&lt;pattern&gt;&amp;json=1, where each JSON entry
    /// embeds the complete correction file content in its "cgats" field.
    /// </summary>
    public static class CcssDatabaseClient
    {
        private const string BaseUrl = "https://colorimetercorrections.displaycal.net/";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            // The server rejects generic/bot user agents with 403.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Gloam/1.0");
            return client;
        }

        public sealed record Entry(
            string Type,         // "ccss" or "ccmx"
            string Display,
            string Manufacturer,
            string Instrument,
            string Reference,    // the spectro the correction was made with
            string Created,
            string Cgats,        // full correction file content
            string? LocalPath = null)
        {
            public string Source => string.IsNullOrEmpty(LocalPath) ? "Online" : "Saved";
        }

        /// <summary>
        /// Searches the database. <paramref name="displayQuery"/> is matched as a substring
        /// (wrapped in wildcards). Queries both ccss and ccmx unless a type is given.
        /// </summary>
        public static async Task<IReadOnlyList<Entry>> SearchAsync(
            string displayQuery, string? type = null, CancellationToken cancellationToken = default)
        {
            string pattern = string.IsNullOrWhiteSpace(displayQuery) ? "*" : $"*{displayQuery.Trim()}*";
            var types = type != null ? new[] { type } : new[] { "ccss", "ccmx" };

            var results = new List<Entry>();
            Exception? lastError = null;
            foreach (string t in types)
            {
                // instrument is REQUIRED for type=ccmx (the server answers 400 without it,
                // since matrices are instrument-specific); harmless wildcard for ccss.
                string url = $"{BaseUrl}?get=1&type={t}&display={Uri.EscapeDataString(pattern)}" +
                             $"&instrument={Uri.EscapeDataString("*")}&json=1";
                try
                {
                    string body = await Http.GetStringAsync(url, cancellationToken);
                    results.AddRange(Parse(body, t));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // One type failing must not kill the whole search.
                    lastError = ex;
                }
            }
            if (results.Count == 0 && lastError != null)
                throw new InvalidOperationException(
                    $"Corrections database query failed ({lastError.Message}). Check the internet connection.", lastError);
            return results
                .OrderByDescending(e => e.Created, StringComparer.Ordinal)
                .ToList();
        }

        public static IReadOnlyList<Entry> ListSaved(string folder, string displayQuery, string? type = null)
        {
            var results = new List<Entry>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return results;

            string query = displayQuery?.Trim() ?? "";
            var types = type != null ? new[] { type } : new[] { "ccss", "ccmx" };
            foreach (string t in types)
            {
                string pattern = t.Equals("ccmx", StringComparison.OrdinalIgnoreCase) ? "*.ccmx" : "*.ccss";
                foreach (string path in Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string cgats = File.ReadAllText(path);
                        if (!CgatsValidator.Validate(cgats, t).IsValid)
                            continue;

                        var entry = ParseLocal(path, cgats, t);
                        if (query.Length > 0 &&
                            !entry.Display.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(path).Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        results.Add(entry);
                    }
                    catch
                    {
                        // Local correction folders may contain hand-copied files. Ignore bad ones here;
                        // the setup preflight still validates selected files with an explicit error.
                    }
                }
            }

            return Deduplicate(results).ToList();
        }

        public static IReadOnlyList<Entry> MergePreferSaved(IEnumerable<Entry> saved, IEnumerable<Entry> online)
            => Deduplicate(saved.Concat(online)).ToList();

        private static IEnumerable<Entry> Parse(string json, string type)
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement list = doc.RootElement;
            if (list.ValueKind == JsonValueKind.Object)
            {
                // Tolerate wrapper objects: {"result": [...]}, {"results": [...]}, etc.
                foreach (var key in new[] { "result", "results", "entries", "data" })
                {
                    if (list.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Array)
                    {
                        list = inner;
                        break;
                    }
                }
            }
            if (list.ValueKind != JsonValueKind.Array) yield break;

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string Get(string name) =>
                    item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? "" : "";

                string cgats = Get("cgats");
                if (string.IsNullOrWhiteSpace(cgats)) continue;
                // Structural validation: a correction file is CGATS text (CCMX/CCSS) with
                // balanced data blocks. The old check (a bare "BEGIN_DATA" substring) let
                // truncated or hostile bodies through to spotread's parser; this validates
                // the full skeleton the same way Save() will before writing to disk.
                if (!CgatsValidator.Validate(cgats, type).IsValid) continue;

                yield return new Entry(
                    Type: Get("type") is { Length: > 0 } tt ? tt : type,
                    Display: Get("display"),
                    Manufacturer: Get("manufacturer"),
                    Instrument: Get("instrument"),
                    Reference: Get("reference"),
                    Created: Get("created"),
                    Cgats: cgats);
            }
        }

        private static Entry ParseLocal(string path, string cgats, string type)
        {
            string GetKeyword(string keyword)
            {
                foreach (string raw in cgats.Split('\n'))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Require a word boundary after the keyword so "DISPLAY" does not also
                    // match "DISPLAY_TYPE_REFRESH" (benign today only because DISPLAY happens
                    // to be emitted first): the keyword must be followed by whitespace or the
                    // opening quote of its value, or be the whole line.
                    if (line.Length > keyword.Length)
                    {
                        char next = line[keyword.Length];
                        if (!char.IsWhiteSpace(next) && next != '"')
                            continue;
                    }

                    string value = line[keyword.Length..].Trim();
                    if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                        value = value[1..^1];
                    return value;
                }
                return "";
            }

            string display = GetKeyword("DISPLAY");
            if (string.IsNullOrWhiteSpace(display))
                display = Path.GetFileNameWithoutExtension(path);

            string reference = GetKeyword("REFERENCE");
            if (string.IsNullOrWhiteSpace(reference))
                reference = "saved file";

            return new Entry(
                Type: type,
                Display: display,
                Manufacturer: "",
                Instrument: GetKeyword("INSTRUMENT"),
                Reference: reference,
                Created: GetKeyword("CREATED"),
                Cgats: cgats,
                LocalPath: path);
        }

        /// <summary>
        /// Writes the entry's correction content into <paramref name="folder"/> with a
        /// descriptive, sanitized filename. Returns the saved path.
        /// </summary>
        /// <exception cref="InvalidDataException">The entry's CGATS body fails structural
        /// validation and is therefore not written to disk.</exception>
        public static string Save(Entry entry, string folder)
        {
            // Validate before touching disk: this content came from a third-party,
            // community-contributed database and is handed verbatim to spotread. A malformed
            // body is never useful and shouldn't be persisted as a "correction".
            var validation = CgatsValidator.Validate(entry.Cgats, entry.Type);
            if (!validation.IsValid)
                throw new InvalidDataException(
                    $"Correction file for '{entry.Display}' failed validation and was not saved: {validation.Error}");

            Directory.CreateDirectory(folder);
            string? existing = FindExistingContentMatch(folder, entry);
            if (existing != null)
                return existing;

            string name = $"{entry.Display} - {entry.Reference}".Trim(' ', '-');
            if (string.IsNullOrWhiteSpace(name)) name = "correction";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
            name = string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (name.Length > 80) name = name[..80].Trim();

            string ext = entry.Type.Equals("ccmx", StringComparison.OrdinalIgnoreCase) ? ".ccmx" : ".ccss";
            string path = Path.Combine(folder, name + ext);
            int n = 2;
            while (File.Exists(path))
                path = Path.Combine(folder, $"{name} ({n++}){ext}");

            File.WriteAllText(path, entry.Cgats);
            return path;
        }

        private static string? FindExistingContentMatch(string folder, Entry entry)
        {
            string ext = entry.Type.Equals("ccmx", StringComparison.OrdinalIgnoreCase) ? ".ccmx" : ".ccss";
            string wanted = NormalizeCgats(entry.Cgats);

            foreach (string path in Directory.GetFiles(folder, "*" + ext, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (NormalizeCgats(File.ReadAllText(path)) == wanted)
                        return path;
                }
                catch
                {
                    // Ignore unreadable files; Save will write a new one if needed.
                }
            }

            return null;
        }

        private static IEnumerable<Entry> Deduplicate(IEnumerable<Entry> entries)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries
                         .OrderByDescending(e => !string.IsNullOrEmpty(e.LocalPath))
                         .ThenByDescending(e => e.Created, StringComparer.Ordinal))
            {
                string key = NormalizeCgats(entry.Cgats);
                if (key.Length == 0)
                    key = $"{entry.Type}|{entry.Display}|{entry.Reference}";
                if (seen.Add(key))
                    yield return entry;
            }
        }

        private static string NormalizeCgats(string value) =>
            string.Join("\n", (value ?? "")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.TrimEnd())
                .Where(line => line.Length > 0));
    }
}
