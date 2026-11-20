namespace BifrostStudio;

public sealed record AppPaths(string Game, string Engine, string Storage, string? Docs, string? Replays, string Mods, IReadOnlyList<string> Sol)
{
    public string? GameReplays { get; init; }

    public static AppPaths For(string game, string engine)
    {
        if (EngineKit.SavesDir(game) != null)
        {
            var saves = EngineKit.Storage(game);
            return new AppPaths(game, engine, saves, BifrostFolders.Documents, BifrostFolders.ReplaysRoot, ModsFolder(game), SolFiles(saves))
                { GameReplays = BifrostFolders.Replays };
        }
        var storage = Path.Combine(engine, "storage");
        return new AppPaths(game, engine, storage, Newest(storage, "Documents", "Bifrost"), BifrostFolders.ReplaysRoot, ModsFolder(game), SolFiles(storage))
            { GameReplays = BifrostFolders.ReplaysRoot };
    }

    public const string OldStorageNote = "Your older engine hasn't made that folder yet. It shows up once the game saves something there, and updating Bifrost moves everything to the new folders.";

    public static bool InOldEngineStorage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("BhPort", StringComparison.OrdinalIgnoreCase) || part.StartsWith("BhPort-", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static string ModsFolder(string game) =>
        EngineKit.SavesDir(game) != null || Directory.Exists(BifrostFolders.Mods) ? BifrostFolders.Mods : Path.Combine(game, "mods");

    public static string? Newest(string storage, params string[] tail)
    {
        try
        {
            return Directory.Exists(storage)
                ? Directory.GetDirectories(storage).Select(d => Path.Combine(new[] { d }.Concat(tail).ToArray())).Where(Directory.Exists)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static IReadOnlyList<string> SolFiles(string storage)
    {
        var shared = Path.Combine(storage, "#SharedObjects");
        try
        {
            return Directory.Exists(shared)
                ? Directory.GetFiles(shared, "*bifrost_*.sol", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    public string? DocsFile(string name) => Docs == null ? null : DocsLayout.File(Docs, name);
}