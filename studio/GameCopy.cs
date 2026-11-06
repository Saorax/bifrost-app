namespace BifrostStudio;

public static class GameCopy
{
    const string Backup = ".bifrost-original";
    const string Exe = "Brawlhalla.exe";

    static readonly string[] Skipped = [EngineKit.Marker, "steam_appid.txt"];

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
            throw new InvalidOperationException($"Not enough free space on {root} for a copy of Brawlhalla ({total / (1L << 30)} GB or more).");

        Directory.CreateDirectory(target);
        long done = 0;
        foreach (var (rel, source) in files)
        {
            cancel.ThrowIfCancellationRequested();
            var info = new FileInfo(source);
            if (!Current(rel, info, target))
            {
                var path = Path.Combine(target, rel);
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
    }

    static long Pending(Dictionary<string, string> files, string target) =>
        files.Where(f => !Current(f.Key, new FileInfo(f.Value), target)).Sum(f => new FileInfo(f.Value).Length);

    static bool Current(string rel, FileInfo source, string target)
    {
        var have = new FileInfo(rel.Equals(Exe, StringComparison.OrdinalIgnoreCase) && GameFinder.IsGame(target)
            ? EngineKit.OriginalExe(target) : Path.Combine(target, rel));
        return have.Exists && have.Length == source.Length && have.LastWriteTimeUtc == source.LastWriteTimeUtc;
    }

    public static void CarrySaves(string from, string target)
    {
        var engine = Path.Combine(target, "bifrost-engine");
        foreach (var name in new[] { "storage", "storage-official" })
        {
            var source = Path.Combine(EngineKit.EngineDir(from), name);
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
