namespace BifrostStudio;

public sealed record BifrostPlace(string Name, string Path, string Help);

public static class BifrostPlaces
{
    public static readonly TimeSpan LogsKept = TimeSpan.FromDays(14);
    public const string PendingStudioUpdate = "BifrostStudio.new.exe";

    public static IReadOnlyList<BifrostPlace> For(AppState state)
    {
        var paths = state.Paths;
        var game = state.Game;
        var list = new List<BifrostPlace>
        {
            new("Game data", paths?.Docs ?? BifrostFolders.Documents, "Settings, server sign-in, mod settings, screenshots and videos."),
            new("Replays", paths?.Replays ?? BifrostFolders.ReplaysRoot, "Your Brawlhalla replays. Replays only Bifrost makes go in the Bifrost folder inside."),
            new("Mods", paths?.Mods ?? BifrostFolders.Mods, "Content mods the game loads."),
        };
        if (paths != null) list.Add(new("Saves", paths.Storage, "The game's saves, key bindings and cloud copies."));
        if (paths != null) list.Add(new("Engine", paths.Engine, "The engine Bifrost runs on."));
        list.Add(new("Cache", (game == null ? null : EngineKit.CacheDir(game)) ?? BifrostFolders.Cache, "Shader and picture caches. Safe to clear, since the game builds them again."));
        list.Add(new("Build", BifrostFolders.Build, "Bifrost's kit and the build it made from your copy of the game. Don't share it."));
        list.Add(new("Downloads", BifrostFolders.Downloads, "Releases while they download. Clean up removes leftovers."));
        list.Add(new("Studio", BifrostFolders.Studio, "This app's own settings, sign-ins and setup library."));
        return list;
    }

    public static long Size(string path) => OperatingSystem.IsWindows() && DiskSize(path) is { } once ? once : Walk(path);

    static long? DiskSize(string root)
    {
        if (!Directory.Exists(root)) return null;
        var seen = new HashSet<long>();
        var stack = new Stack<string>();
        stack.Push(root);
        var buffer = new byte[64 * 1024];
        long total = 0;
        var opened = false;
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            using var handle = CreateFileW(dir, 1, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) continue;
            opened = true;
            var kind = 11;
            while (GetFileInformationByHandleEx(handle, kind, buffer, buffer.Length))
            {
                kind = 10;
                for (var at = 0; ; )
                {
                    var next = BitConverter.ToInt32(buffer, at);
                    var size = BitConverter.ToInt64(buffer, at + 40);
                    var attributes = BitConverter.ToUInt32(buffer, at + 56);
                    var nameLength = BitConverter.ToInt32(buffer, at + 60);
                    var id = BitConverter.ToInt64(buffer, at + 96);
                    var name = System.Text.Encoding.Unicode.GetString(buffer, at + 104, nameLength);
                    if (name != "." && name != ".." && (attributes & 0x400) == 0)
                    {
                        if ((attributes & 0x10) != 0) stack.Push(System.IO.Path.Combine(dir, name));
                        else if (seen.Add(id)) total += size;
                    }
                    if (next == 0) break;
                    at += next;
                }
            }
        }
        return opened ? total : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass, byte[] buffer, int size);

    static long Walk(string path)
    {
        long total = 0;
        var stack = new Stack<DirectoryInfo>();
        try
        {
            if (!Directory.Exists(path)) return 0;
            stack.Push(new DirectoryInfo(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (entry is FileInfo file) total += file.Length;
                    else if (entry is DirectoryInfo sub) stack.Push(sub);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    public static string? Open(string path)
    {
        if (!Directory.Exists(path) && AppPaths.InOldEngineStorage(path)) return AppPaths.OldStorageNote;
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Couldn't make {path}: {e.Message}";
        }
        Ui.Open(path);
        return null;
    }

    public static (int Files, long Bytes) CleanUp(string? engine, DateTime now)
    {
        var files = 0;
        long bytes = 0;
        void Remove(FileInfo f)
        {
            try
            {
                var size = f.Length;
                f.Delete();
                files++;
                bytes += size;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var f in Files(BifrostFolders.Downloads, SearchOption.TopDirectoryOnly))
            if (!f.Name.Equals(PendingStudioUpdate, StringComparison.OrdinalIgnoreCase)) Remove(f);
        foreach (var dir in new[] { BifrostFolders.Logs, engine == null ? null : System.IO.Path.Combine(engine, "logs") })
        {
            if (dir == null) continue;
            foreach (var f in Files(dir, SearchOption.AllDirectories))
                if (now - f.LastWriteTime > LogsKept) Remove(f);
        }
        return (files, bytes);
    }

    static IEnumerable<FileInfo> Files(string dir, SearchOption depth)
    {
        try
        {
            return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*", depth) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
