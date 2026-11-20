namespace BifrostStudio;

public static class GameCopy
{
    const string Backup = ".bifrost-original";
    const string Exe = "Brawlhalla.exe";
    public const string CopyMarker = "bifrost-copy.txt";

    static readonly string[] Skipped = [EngineKit.Marker, "steam_appid.txt", GameFinder.LauncherAside, CopyMarker];

    public static bool IsCopy(string? game) =>
        game != null && !GameFinder.IsSteamInstall(game)
        && (InstallPlace.IsOwnFolder(game) || File.Exists(Path.Combine(game, CopyMarker)));

    static string ExeTarget(string target)
    {
        try { return GameFinder.IsGame(target) ? EngineKit.OriginalExe(target) : Path.Combine(target, Exe); }
        catch (InvalidOperationException) { return Path.Combine(target, Exe); }
    }

    public static Task MakeAsync(string steam, string target, Action<double> progress, CancellationToken cancel) =>
        Task.Run(() => Mirror(steam, target, progress, cancel), cancel);

    static void Mirror(string steam, string target, Action<double> progress, CancellationToken cancel)
    {
        if (!GameFinder.IsGame(steam)) throw new InvalidOperationException($"{steam} has no Brawlhalla to copy.");
        var engine = Path.GetFullPath(EngineKit.EngineDir(steam)).TrimEnd('\\', '/');
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(steam, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFullPath(file).StartsWith(engine + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(steam, file);
            if (Skipped.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;
            if (rel.Split(Path.DirectorySeparatorChar)[0].Equals("bifrost-engine", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.EndsWith(Backup, StringComparison.OrdinalIgnoreCase))
                files[rel[..^Backup.Length]] = file;
            else if (!files.ContainsKey(rel))
                files[rel] = file;
        }
        files[Exe] = EngineKit.OriginalExe(steam);

        var total = files.Values.Sum(f => new FileInfo(f).Length);
        var root = Path.GetPathRoot(Path.GetFullPath(target));
        if (root != null && new DriveInfo(root).AvailableFreeSpace < Pending(files, target))
            throw new InvalidOperationException($"Not enough free space on {root} to copy Brawlhalla. It needs {total / (1L << 30)} GB or more.");

        Directory.CreateDirectory(target);
        long done = 0;
        foreach (var (rel, source) in files)
        {
            cancel.ThrowIfCancellationRequested();
            var info = new FileInfo(source);
            if (!Current(rel, info, target))
            {
                var path = rel.Equals(Exe, StringComparison.OrdinalIgnoreCase) ? ExeTarget(target) : Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
                File.Copy(source, path, true);
            }
            done += info.Length;
            progress(total == 0 ? 1 : (double)done / total);
        }
        foreach (var old in Directory.EnumerateFiles(target).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".swf" or ".swz"))
            if (!files.ContainsKey(Path.GetFileName(old))) File.Delete(old);
        GameFinder.MarkSteamApp(target);
        File.WriteAllText(Path.Combine(target, CopyMarker), "A copy of Steam's Brawlhalla made by the Bifrost app." + Environment.NewLine);
    }

    static long Pending(Dictionary<string, string> files, string target) =>
        files.Where(f => !Current(f.Key, new FileInfo(f.Value), target)).Sum(f => new FileInfo(f.Value).Length);

    static bool Current(string rel, FileInfo source, string target)
    {
        var have = new FileInfo(rel.Equals(Exe, StringComparison.OrdinalIgnoreCase) && GameFinder.IsGame(target)
            ? EngineKit.OriginalExe(target) : Path.Combine(target, rel));
        return have.Exists && have.Length == source.Length && have.LastWriteTimeUtc == source.LastWriteTimeUtc;
    }

    public static string? Unused(string? game)
    {
        var copy = BifrostFolders.Game;
        if (game == null || InstallPlace.IsOwnFolder(game) || !Directory.Exists(copy)) return null;
        try { return Directory.EnumerateFileSystemEntries(copy).Any() ? copy : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string? CantDelete(string copy)
    {
        static bool Inside(string? path, string root) =>
            path != null && (Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(
                Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var kept = new[] { Path.Combine(copy, "bifrost-engine"), EngineKit.SavesDir(copy), EngineKit.Storage(copy), EngineKit.Storage(copy, true) };
        if (kept.Any(dir => Inside(dir, copy) && Directory.Exists(dir)))
            return "The copy still has Bifrost's engine or saves in it, so it wasn't deleted. Open it and move them out first.";
        return null;
    }

    public static async Task DeleteInstallAsync(string copy)
    {
        if (!IsCopy(copy)) throw new InvalidOperationException("That's not a separate install, so it wasn't deleted.");
        if (CantDelete(copy) is { } why) throw new InvalidOperationException(why);
        var engine = EngineKit.EngineDir(copy);
        var saves = new[] { EngineKit.Storage(copy), EngineKit.Storage(copy, true), Path.Combine(engine, "storage"), Path.Combine(engine, "storage-official") };
        var home = Path.GetFullPath(BifrostFolders.Home).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var ownEngine = Directory.Exists(engine) && Path.GetFullPath(engine).StartsWith(home, StringComparison.OrdinalIgnoreCase)
                        && !saves.Any(s => Directory.Exists(s) && (Path.GetFullPath(s) + Path.DirectorySeparatorChar).StartsWith(
                            Path.GetFullPath(engine).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        await DeleteAsync(copy);
        if (ownEngine) await DeleteAsync(engine);
    }

    public static Task DeleteAsync(string copy) => Task.Run(() =>
    {
        var walk = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(copy, "*", walk))
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(copy, true);
    });

    public static void CarrySaves(string from, string target)
    {
        var engine = FolderMigration.EngineTarget(target, carried: true);
        foreach (var name in new[] { "storage", "storage-official" })
        {
            var source = EngineKit.Storage(from, name == "storage-official");
            var into = Path.Combine(engine, name);
            if (!Directory.Exists(source) || Directory.Exists(into)) continue;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var path = Path.Combine(into, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(file, path);
            }
        }
    }
}
