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
                if (game == lastGame && stampText == lastStamp) return lastFingerprint;
                cache ??= Load();
                var lines = new StringBuilder();
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
                    lines.Append(name).Append('\t').Append(e.Sha256).Append('\n');
                }
                if (changed) Save(cache);
                lastGame = game;
                lastStamp = stampText;
                lastFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString())));
                return lastFingerprint;
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
