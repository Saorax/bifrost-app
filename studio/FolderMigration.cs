namespace BifrostStudio;

public static class FolderMigration
{
    public static string LogFile => BifrostFolders.StudioFile("folders-moved.txt");

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(BifrostFolders.Studio);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static int MoveStudioFiles()
    {
        var from = BifrostFolders.OldStudio;
        if (!Directory.Exists(from)) return 0;
        var moved = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList())
            {
                var to = Path.Combine(BifrostFolders.Studio, Path.GetRelativePath(from, file));
                if (File.Exists(to)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(file, to);
                moved++;
            }
            RemoveEmpty(from);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Studio's files: {e.Message}");
        }
        if (moved > 0) Log($"moved {moved} of Studio's files from {from} to {BifrostFolders.Studio}");
        return moved;
    }

    static void RemoveEmpty(string dir)
    {
        foreach (var sub in Directory.GetDirectories(dir)) RemoveEmpty(sub);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    public static int PruneEngineIndex()
    {
        var index = BifrostFolders.EngineIndex;
        try
        {
            if (!File.Exists(index)) return 0;
            var lines = File.ReadAllLines(index).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var kept = lines.Where(l => File.Exists(Path.Combine(l, EngineKit.Marker))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (kept.Count == lines.Count) return 0;
            if (kept.Count == 0) File.Delete(index);
            else File.WriteAllText(index, string.Join("\n", kept) + "\n");
            foreach (var gone in lines.Except(kept, StringComparer.OrdinalIgnoreCase))
                Log($"dropped {gone} from engines.txt (no engine there)");
            return lines.Count - kept.Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"engines.txt: {e.Message}");
            return 0;
        }
    }

    public delegate bool JunctionMaker(string link, string target);

    public static int MoveBuildFolders(JunctionMaker? makeJunction = null)
    {
        var moved = 0;
        foreach (var (from, to) in new[]
                 {
                     (BifrostFolders.OldWork, BifrostFolders.Work),
                     (BifrostFolders.OldKit, BifrostFolders.Kit),
                     (BifrostFolders.OldOfficialLayer, BifrostFolders.OfficialLayer),
                 })
        {
            if (!Directory.Exists(from) || Directory.Exists(to)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Directory.Move(from, to);
                moved++;
                Log($"moved {from} to {to}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log($"{from} stays for now (in use?): {e.Message}");
            }
        }
        var cache = BifrostFolders.OldGameFilesCache;
        if (File.Exists(cache) && !File.Exists(BifrostFolders.GameFilesCache))
        {
            try
            {
                Directory.CreateDirectory(BifrostFolders.Cache);
                File.Move(cache, BifrostFolders.GameFilesCache);
                moved++;
                Log($"moved {cache} to {BifrostFolders.GameFilesCache}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log($"{cache} stays for now: {e.Message}");
            }
        }
        var relinked = RelinkWork(EngineKit.Work, makeJunction ?? MakeJunction);
        if (relinked > 0) Log($"pointed {relinked} links in {EngineKit.Work} at {BifrostFolders.Kit}");
        return moved;
    }

    public static string? JunctionTarget(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not { } target) return null;
            if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
            return Path.GetFullPath(target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    static string? Under(string path, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd('\\', '/');
        var p = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return "";
        return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? p[(r.Length + 1)..] : null;
    }

    public static int RelinkWork(string work, JunctionMaker makeJunction)
    {
        if (!Directory.Exists(work) || Directory.Exists(BifrostFolders.OldKit) || !Directory.Exists(BifrostFolders.Kit)) return 0;
        var relinked = 0;
        var stack = new Stack<string>();
        stack.Push(work);
        while (stack.Count > 0)
        {
            string[] subs;
            try { subs = Directory.GetDirectories(stack.Pop()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sub in subs)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(sub); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    stack.Push(sub);
                    continue;
                }
                if (JunctionTarget(sub) is not { } target || Under(target, BifrostFolders.OldKit) is not { } rest) continue;
                var fresh = rest.Length == 0 ? BifrostFolders.Kit : Path.Combine(BifrostFolders.Kit, rest);
                if (!Directory.Exists(fresh)) continue;
                try
                {
                    Directory.Delete(sub);
                    if (makeJunction(sub, fresh)) relinked++;
                    else Log($"couldn't link {sub} to {fresh}; the next install makes it again");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log($"couldn't relink {sub}: {e.Message}");
                }
            }
        }
        return relinked;
    }

    public static bool MakeJunction(string link, string target)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(info);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static readonly string[] InGameFolder = ["bifrost-engine", EngineKit.Marker, "Brawlhalla.exe.bifrost-original"];

    public static IReadOnlyList<string> LeftInGameFolder(string game) =>
        InGameFolder.Where(n => File.Exists(Path.Combine(game, n)) || Directory.Exists(Path.Combine(game, n))).ToList();

    public static bool EngineInGameFolder(string game) =>
        EngineKit.EngineInstalled(game) && Same(EngineKit.EngineDir(game), Path.Combine(game, "bifrost-engine"));

    public static readonly string[] Carried = ["storage", "storage-official", "logs"];

    public static string EngineTarget(string game, bool carried = false)
    {
        for (var n = 1; ; n++)
        {
            var dir = n == 1 ? BifrostFolders.Engine : BifrostFolders.Engine + "-" + n;
            ClearVulkanCopy(dir);
            if (!Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any()) return dir;
            if (carried && Directory.EnumerateFileSystemEntries(dir).All(e => Carried.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase)))
                return dir;
        }
    }

    public static void ClearVulkanCopy(string dir)
    {
        try
        {
            if (!Directory.Exists(dir) || Directory.EnumerateDirectories(dir).Any()) return;
            var files = Directory.GetFiles(dir);
            if (files.Length == 0 || !files.All(f => Path.GetFileName(f) is BifrostFolders.LauncherVulkanCopy or BifrostFolders.OldLauncherVulkanCopy)) return;
            foreach (var f in files) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static async Task<string?> MoveEngineOutAsync(string? game, bool gameRunning, Func<string, string, Task> move)
    {
        if (game == null || gameRunning || !EngineInGameFolder(game)) return null;
        var target = EngineTarget(game);
        Log($"moving the engine out of {game} to {target}");
        try
        {
            await move(game, target);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log($"the engine stays in {game} for now: {e.Message}");
            return null;
        }
        var left = LeftInGameFolder(game);
        Log(left.Count == 0 ? $"the engine is in {target}; nothing of Bifrost's is left in {game} but its Brawlhalla.exe and mods"
            : $"the engine is in {target}; still in {game}: {string.Join(", ", left)}");
        return target;
    }
}
