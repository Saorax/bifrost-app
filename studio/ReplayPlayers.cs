using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BifrostStudio;

public sealed record KeptPlayer(string Dir, string Version, int ReplayVersion, IReadOnlyDictionary<string, long> Core)
{
    public string Bin => Path.Combine(Dir, "bin");
    public string Game => Path.Combine(Dir, "game");
    public bool HasPlayer => File.Exists(Path.Combine(Bin, ReplayRunner.RunnerFile)) && File.Exists(Path.Combine(Bin, "hl.exe"));

    public bool HasGame => Core.Count > 0 && Core.All(c =>
    {
        var f = new FileInfo(Path.Combine(Game, c.Key));
        return f.Exists && f.Length == c.Value;
    });
}

public sealed record ReplayPlayerPick(ReplayRunner Runner, string Game, string Version);

public static class ReplayPlayers
{
    public const string InfoName = "player.json";

    public static string? RootOverride { get; set; }

    public static string Root => RootOverride ?? Path.Combine(EngineKit.Work, "replay-players");

    static readonly Dictionary<string, int> Known = new()
    {
        ["10.05"] = 270, ["10.06"] = 270, ["10.07"] = 270, ["10.08"] = 270, ["10.09"] = 270, ["10.10"] = 270, ["10.11"] = 270,
        ["11.00"] = 271, ["11.01"] = 272,
    };

    public static int ReplayVersionOf(string? gameVersion)
    {
        if (string.IsNullOrEmpty(gameVersion)) return 0;
        var parts = gameVersion.Split('.');
        var key = parts.Length >= 2 ? parts[0] + "." + parts[1] : gameVersion;
        return Known.TryGetValue(key, out var v) ? v : 0;
    }

    public static string Shown(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? parts[0] + "." + parts[1] : version;
    }

    public static IReadOnlyList<KeptPlayer> Kept()
    {
        var found = new List<KeptPlayer>();
        try
        {
            if (!Directory.Exists(Root)) return found;
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                try
                {
                    var o = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, InfoName)))!.AsObject();
                    var version = (string?)o["version"] ?? Path.GetFileName(dir);
                    var replay = o["replayVersion"]?.GetValue<int>() ?? ReplayVersionOf(version);
                    var core = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    if (o["core"] is JsonObject c)
                        foreach (var (k, v) in c)
                            if (v != null) core[k] = v.GetValue<long>();
                    found.Add(new KeptPlayer(dir, version, replay, core));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or NullReferenceException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }

    public static KeptPlayer? KeptFor(string version) =>
        Kept().FirstOrDefault(k => string.Equals(k.Version, version, StringComparison.OrdinalIgnoreCase));

    public static int LiveReplayVersion(string? game) => game == null ? 0 : ReplayVersionOf(EngineKit.InstalledVersion(game));

    public static (ReplayPlayerPick? Pick, string? Why) For(int replayVersion, string game, ReplayRunner live, bool current = true)
    {
        var liveVersion = EngineKit.InstalledVersion(game);
        var liveReplay = ReplayVersionOf(liveVersion);
        if (liveReplay == 0 ? current : replayVersion == liveReplay)
        {
            if (EngineKit.InstalledGameMatches(game, false)) return (new ReplayPlayerPick(live, game, liveVersion ?? ""), null);
            if (liveVersion != null && KeptFor(liveVersion) is { HasGame: true } k) return (new ReplayPlayerPick(live, k.Game, liveVersion), null);
            var v = liveVersion != null ? Shown(liveVersion) : "the installed";
            return (null, $"Steam updated Brawlhalla, but Bifrost is still on {v} and there's no saved copy of the {v} game files to play this with. "
                + "Replays from the new version will play once Bifrost's update for it is out.");
        }
        foreach (var k in Kept())
            if (k.ReplayVersion == replayVersion && k.HasPlayer && k.HasGame)
                return (new ReplayPlayerPick(new ReplayRunner(k.Dir, k.Bin), k.Game, k.Version), null);
        return (null, null);
    }

    static readonly SemaphoreSlim keeping = new(1, 1);

    static readonly string[] CopiedFolders = ["anims"];

    static bool CoreFile(string name) =>
        name.EndsWith(".swz", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "BrawlhallaAir.swf", StringComparison.OrdinalIgnoreCase);

    public static async Task<string?> KeepAsync(string game, ReplayRunner live, Action<string>? log = null)
    {
        if (!await keeping.WaitAsync(0)) return null;
        try
        {
            return await Task.Run(() => Keep(game, live, log));
        }
        finally
        {
            keeping.Release();
        }
    }

    static string? Keep(string game, ReplayRunner live, Action<string>? log)
    {
        if (EngineKit.InstalledVersion(game) is not { Length: > 0 } version) return null;
        if (!EngineKit.InstalledGameMatches(game, false) || EngineKit.InstalledGameFiles(game) is not { } fingerprint) return null;
        var dir = Path.Combine(Root, version);
        var kept = KeptFor(version);
        try
        {
            Directory.CreateDirectory(dir);
            if (kept is not { HasGame: true })
            {
                var target = Path.Combine(dir, "game");
                var partial = target + ".partial";
                if (Directory.Exists(partial)) DeleteTree(partial);
                log?.Invoke($"Keeping the {Shown(version)} game files for its replays in {target}");
                CopyGame(game, partial);
                if (Directory.Exists(target)) DeleteTree(target);
                Directory.Move(partial, target);
            }
            if (live.Prepared && (kept is not { HasPlayer: true } || File.GetLastWriteTimeUtc(Path.Combine(dir, "bin", ReplayRunner.RunnerFile)) < File.GetLastWriteTimeUtc(live.RunnerPath)))
            {
                var bin = Path.Combine(dir, "bin");
                Directory.CreateDirectory(bin);
                foreach (var f in Directory.EnumerateFiles(live.Bin))
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith("game-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".hl", StringComparison.OrdinalIgnoreCase)) continue;
                    File.Copy(f, Path.Combine(bin, name), true);
                }
            }
            if (kept is not { HasGame: true }) WriteInfo(dir, version, game, fingerprint);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("Keeping the replay player's files failed: " + e.Message);
            return e.Message;
        }
    }

    public static void WriteInfo(string dir, string version, string from, string? fingerprint)
    {
        var core = new JsonObject();
        var game = Path.Combine(dir, "game");
        foreach (var f in Directory.EnumerateFiles(game).Where(f => CoreFile(Path.GetFileName(f))))
            core[Path.GetFileName(f)] = new FileInfo(f).Length;
        foreach (var folder in CopiedFolders)
            if (Directory.Exists(Path.Combine(game, folder)))
                foreach (var f in Directory.EnumerateFiles(Path.Combine(game, folder), "*", SearchOption.AllDirectories))
                    core[Path.GetRelativePath(game, f).Replace(Path.DirectorySeparatorChar, '/')] = new FileInfo(f).Length;
        var info = new JsonObject
        {
            ["version"] = version, ["replayVersion"] = ReplayVersionOf(version), ["gameFiles"] = fingerprint,
            ["kept"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"), ["from"] = from, ["core"] = core,
        };
        File.WriteAllText(Path.Combine(dir, InfoName), info.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void CopyGame(string from, string to)
    {
        Directory.CreateDirectory(to);
        var link = SameDrive(from, to);
        foreach (var f in Directory.EnumerateFiles(from))
        {
            var name = Path.GetFileName(f);
            if (name.EndsWith(".bifrost-original", StringComparison.OrdinalIgnoreCase)) continue;
            var source = File.Exists(f + ".bifrost-original") ? f + ".bifrost-original" : f;
            Place(source, Path.Combine(to, name), link && !CoreFile(name));
        }
        foreach (var d in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(d);
            if (string.Equals(name, "bifrost-engine", StringComparison.OrdinalIgnoreCase)) continue;
            if (CopiedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) CopyTree(d, Path.Combine(to, name));
            else Junction(Path.Combine(to, name), d);
        }
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) Place(f, Path.Combine(to, Path.GetFileName(f)), false);
        foreach (var d in Directory.EnumerateDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static void Place(string from, string to, bool link)
    {
        if (link && OperatingSystem.IsWindows() && CreateHardLinkW(to, from, IntPtr.Zero)) return;
        File.Copy(from, to, true);
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
    }

    static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    static void Junction(string at, string target)
    {
        at = Path.GetFullPath(at);
        target = Path.GetFullPath(target);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(at, target);
            return;
        }
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", at, target }, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        });
        p?.WaitForExit();
        if (!Directory.Exists(at)) throw new IOException("the link to " + target + " couldn't be made");
    }

    static void DeleteTree(string dir)
    {
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d);
            else DeleteTree(d);
        }
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }
        Directory.Delete(dir);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr security);
}
