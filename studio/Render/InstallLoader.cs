using System.Collections.Concurrent;

namespace BifrostStudio.Render;

public sealed record ModLayer(string Id, string Folder, IReadOnlyDictionary<string, string> Assets);

public sealed class InstallLoader(string game, IReadOnlyList<ModLayer>? mods = null)
{
    readonly ConcurrentDictionary<string, Lazy<SwfData?>> swfs = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentQueue<string> problems = new();

    public string Game { get; } = game;

    public IReadOnlyList<ModLayer> Mods { get; } = [.. (mods ?? []).Where(m => Directory.Exists(m.Folder))];

    public IEnumerable<string> Problems => problems;

    public string? Resolve(string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains("..")) return null;
        foreach (var mod in Mods)
        {
            if (!mod.Assets.TryGetValue(relative.Replace('\\', '/'), out var file) || Path.IsPathRooted(file) || file.Split('/').Contains("..")) continue;
            var full = Path.Combine(mod.Folder, file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) return full;
        }
        foreach (var mod in Mods)
        {
            var prefix = "mods/" + mod.Id + "/";
            if (!relative.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.Combine(mod.Folder, relative[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) return full;
        }
        var inGame = Path.Combine(Game, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(inGame) ? inGame : null;
    }

    public SwfData? Swf(string swfPath) => swfs.GetOrAdd(swfPath.Replace('\\', '/'), key => new Lazy<SwfData?>(() => LoadSwf(key))).Value;

    SwfData? LoadSwf(string relative)
    {
        if (Resolve(relative) is not { } full) return null;
        try
        {
            using var file = File.OpenRead(full);
            return SwfData.Read(file);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problems.Enqueue(relative + ": " + e.Message);
            return null;
        }
    }

    public bool SwfExists(string swfPath) => Resolve(swfPath) != null;
}
