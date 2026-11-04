using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BifrostStudio;

public static class GameFiles
{
    const string Original = ".bifrost-original";
    static readonly string[] Suffixes = [".swf", ".swz"];

    sealed record Entry(long Size, long Time, string Sha256);

    static readonly object Gate = new();
    static Dictionary<string, Entry>? cache;
    static string? lastGame, lastStamp, lastFingerprint;
    static SortedDictionary<string, string>? lastListing;

    static string CachePath => Path.Combine(EngineKit.Home, "game-files-cache.json");

    static bool GameFile(string name) => Suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    static SortedDictionary<string, string> Files(string game)
    {
        var found = new Dictionary<string, string>();
        foreach (var path in Directory.EnumerateFiles(game))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(Original, StringComparison.OrdinalIgnoreCase))
            {
                var baseName = name[..^Original.Length];
                if (GameFile(baseName)) found[baseName] = path;
            }
            else if (GameFile(name) && !found.ContainsKey(name))
            {
                found[name] = path;
            }
        }
        var sorted = new SortedDictionary<string, string>(Comparer<string>.Create((a, b) =>
        {
            var c = string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant());
            return c != 0 ? c : string.CompareOrdinal(a, b);
        }));
        foreach (var (k, v) in found) sorted[k] = v;
        return sorted;
    }

    public static string? Fingerprint(string? game)
    {
        lock (Gate)
        {
            if (Listing(game) is not { } listing) return null;
            if (listing == lastListing && lastFingerprint != null) return lastFingerprint;
            var lines = new StringBuilder();
            foreach (var (name, sha) in listing) lines.Append(name).Append('\t').Append(sha).Append('\n');
            lastFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString())));
            return lastFingerprint;
        }
    }

    public static List<string>? Differences(string? game, IReadOnlyDictionary<string, string> want)
    {
        if (Listing(game) is not { } listing) return null;
        var have = new Dictionary<string, string>(listing, StringComparer.OrdinalIgnoreCase);
        var differ = new List<string>();
        foreach (var (name, sha) in want)
            if (!have.TryGetValue(name, out var found)) differ.Add(name + " (missing)");
            else if (found != sha) differ.Add(name);
        return differ;
    }

    public static bool Core(string name)
    {
        var n = name.Replace(" (missing)", "");
        return n.Equals("BrawlhallaAir.swf", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".swz", StringComparison.OrdinalIgnoreCase);
    }

    public static string Describe(IReadOnlyList<string> names, int shown = 3) =>
        string.Join(", ", names.Take(shown)) + (names.Count > shown ? $" and {names.Count - shown} more" : "");

    static SortedDictionary<string, string>? Listing(string? game)
    {
        if (game == null || !Directory.Exists(game)) return null;
        lock (Gate)
        {
            try
            {
                var files = Files(game);
                if (files.Count == 0) return null;
                var stamp = new StringBuilder();
                foreach (var (name, path) in files)
                {
                    var info = new FileInfo(path);
                    stamp.Append(name).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }
                var stampText = stamp.ToString();
                if (game == lastGame && stampText == lastStamp && lastListing != null) return lastListing;
                cache ??= Load();
                var listing = new SortedDictionary<string, string>(files.Comparer);
                var changed = false;
                foreach (var (name, path) in files)
                {
                    var info = new FileInfo(path);
                    if (!cache.TryGetValue(path, out var e) || e.Size != info.Length || e.Time != info.LastWriteTimeUtc.Ticks)
                    {
                        using var stream = File.OpenRead(path);
                        e = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexStringLower(SHA256.HashData(stream)));
                        cache[path] = e;
                        changed = true;
                    }
                    listing[name] = e.Sha256;
                }
                if (changed) Save(cache);
                lastGame = game;
                lastStamp = stampText;
                lastListing = listing;
                lastFingerprint = null;
                return listing;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    static Dictionary<string, Entry> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath)) ?? []; }
        catch (Exception) { return []; }
    }

    static void Save(Dictionary<string, Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(entries));
        }
        catch (Exception) { }
    }
}
