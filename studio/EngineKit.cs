using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace BifrostStudio;

public sealed class EngineKit(Project project)
{
    public const string Marker = "bifrost-engine.json";

    public static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bifrost");

    static string Downloaded => Path.Combine(Home, "kit");
    static string OfficialLayer => Path.Combine(Home, "official");
    static string Work => Path.Combine(Home, "engine-work");

    public static void Clean(bool kit, bool officialLayer)
    {
        foreach (var dir in new[] { Work, kit ? Downloaded : null, officialLayer ? OfficialLayer : null })
            if (dir != null && Directory.Exists(dir)) DeleteTree(dir);
    }

    static void DeleteTree(string dir)
    {
        foreach (var sub in Directory.GetDirectories(dir))
        {
            if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(sub);
            else DeleteTree(sub);
        }
        foreach (var file in Directory.GetFiles(dir))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        Directory.Delete(dir);
    }

    string? lastFailure;

    public string? Folder => File.Exists(Path.Combine(Downloaded, "player_engine.py")) ? Downloaded : null;

    public string? Python => Folder is { } kit && Path.Combine(kit, "python", "python.exe") is var python && File.Exists(python)
        ? python : null;

    public static string? KitRelease(string? kit)
    {
        try { return kit == null ? null : (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(kit, "kit.json")))?["release"]; }
        catch (Exception) { return null; }
    }

    static JsonNode? ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null; }
        catch (Exception) { return null; }
    }

    static JsonNode? ReadMarker(string game) => ReadJson(MarkerPath(game));

    static string? LauncherTail(string game)
    {
        try
        {
            using var exe = File.OpenRead(Path.Combine(game, "Brawlhalla.exe"));
            if (exe.Length < 9) return null;
            var tail = new byte[8];
            exe.Seek(-8, SeekOrigin.End);
            exe.ReadExactly(tail);
            if (System.Text.Encoding.ASCII.GetString(tail, 4, 4) != "BFEN") return null;
            var size = BitConverter.ToInt32(tail, 0);
            if (size <= 0 || size > exe.Length - 8) return null;
            var text = new byte[size];
            exe.Seek(-8 - size, SeekOrigin.End);
            exe.ReadExactly(text);
            return Path.GetFullPath(System.Text.Encoding.UTF8.GetString(text));
        }
        catch (Exception) { return null; }
    }

    static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static string EngineDir(string game)
    {
        if (LauncherTail(game) is { } tail) return tail;
        if (ReadJson(Path.Combine(game, Marker)) is { } legacy)
            return (string?)legacy["engine_dir"] is { Length: > 0 } dir ? Path.GetFullPath(dir) : Path.Combine(game, "bifrost-engine");
        try
        {
            foreach (var line in File.ReadAllLines(Path.Combine(Home, "engines.txt")))
            {
                if (line.Trim() is not { Length: > 0 } folder) continue;
                if ((string?)ReadJson(Path.Combine(folder, Marker))?["game"] is { } recorded && SameFolder(recorded, game))
                    return Path.GetFullPath(folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Path.Combine(game, "bifrost-engine");
    }

    static string MarkerPath(string game) =>
        EngineDir(game) is var engine && !SameFolder(engine, Path.Combine(game, "bifrost-engine"))
            ? Path.Combine(engine, Marker) : Path.Combine(game, Marker);

    static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string? Installed(string game) => ReadMarker(game) is { } m && Has(game, Path.Combine("bin", "game-dx11.hl"))
        ? (string?)m["release"] ?? "" : null;

    public static string? OfficialInstalled(string game) =>
        ReadMarker(game)?["official"] is { } o && Has(game, Path.Combine("official", "game-dx11.hl")) ? (string?)o["version"] ?? "" : null;

    static bool Has(string game, string file) => File.Exists(Path.Combine(EngineDir(game), file));

    public static bool EngineInstalled(string game) => ReadMarker(game) != null;

    static string LegacyModeFile(string game) => Path.Combine(EngineDir(game), "mode.txt");

    public static bool ReadOfficialMode(string game)
    {
        if ((string?)ReadMarker(game)?["mode"] is { } mode) return mode == "official";
        try { return File.ReadAllText(LegacyModeFile(game)).Trim() == "official"; }
        catch (Exception) { return false; }
    }

    public static void WriteMode(string game, bool official)
    {
        if (ReadMarker(game) is not JsonObject marker) return;
        marker["mode"] = official ? "official" : "bifrost";
        try
        {
            File.WriteAllText(MarkerPath(game), marker.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            File.Delete(LegacyModeFile(game));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static bool LauncherOk(string game)
    {
        try
        {
            if ((string?)ReadMarker(game)?["stub_sha256"] is not { } stub) return true;
            return FileSha256(Path.Combine(game, "Brawlhalla.exe")) == stub;
        }
        catch (Exception) { return true; }
    }

    public static long DownloadSize(Release? release) =>
        release != null && KitRelease(Downloaded) != release.Name ? release.Kit.Size : 0;

    public static bool GameMatches(string game, Release? release)
    {
        if (release?.GameFileList is { } list) return GameFiles.Differences(game, list) is { } d && !d.Any(GameFiles.Core);
        if (release?.GameFiles is { } files) return GameFiles.Fingerprint(game) == files;
        if (release?.SwfSha256 is not { } want) return true;
        try
        {
            var original = Path.Combine(game, "BrawlhallaAir.swf.bifrost-original");
            return FileSha256(File.Exists(original) ? original : Path.Combine(game, "BrawlhallaAir.swf")) == want;
        }
        catch (Exception) { return false; }
    }

    public static bool InstalledGameMatches(string game, bool official)
    {
        var want = official ? (string?)ReadMarker(game)?["official"]?["game_files"] : (string?)ReadMarker(game)?["game_files"];
        return want == null || GameFiles.Fingerprint(game) == want;
    }

    public static bool OfficialMatches(string game, OfficialRelease? official) =>
        official?.GameFileList is { } list ? GameFiles.Differences(game, list) is { } d && !d.Any(GameFiles.Core)
            : official != null && official.GameFiles != null && GameFiles.Fingerprint(game) == official.GameFiles;

    public static string? FilesNote(string? game, IReadOnlyDictionary<string, string>? list)
    {
        if (game == null || list == null) return null;
        if (GameFiles.Differences(game, list) is not { } differ)
            return "Can't read Brawlhalla's files: close anything that has them open (or an antivirus scan) and try again.";
        var core = differ.Where(GameFiles.Core).ToList();
        if (core.Count > 0)
            return $"Brawlhalla's game code isn't the version this release is for (changed: {GameFiles.Describe(core)}). "
                   + "If Steam just updated, a new release comes soon; if not, verify the game files in Steam.";
        return differ.Count > 0
            ? $"Not Steam's current files: {GameFiles.Describe(differ)} (a mod, or a skipped Steam update). "
              + "The build doesn't use them; if they look wrong in game, verify the game files in Steam."
            : null;
    }

    public static bool OfficialOnOldKit(string game) =>
        OfficialInstalled(game) != null && (string?)ReadMarker(game)?["official"]?["kit"] != KitRelease(Downloaded);

    async Task EnsureAsync(Asset? asset, string folder, Action<double, double, long> download, Action unpacking, CancellationToken cancel)
    {
        if (asset == null) return;
        var file = Path.Combine(Home, "downloads", asset.Name);
        var clock = Stopwatch.StartNew();
        await Releases.DownloadAsync(asset, file, new Progress<double>(p =>
            download(p, clock.Elapsed.TotalSeconds > 0.5 ? p * asset.Size / clock.Elapsed.TotalSeconds : 0, asset.Size)), cancel);
        unpacking();
        var restart = folder == Downloaded && HostedServer.Running;
        if (restart) await HostedServer.StopAsync();
        try { await Task.Run(() => Unpack(file, folder), cancel); }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("A file in Bifrost's kit is in use. Close Brawlhalla and anything started from the app, then try again.");
        }
        finally
        {
            if (restart && Python is { } python) HostedServer.Start(python);
        }
        File.Delete(file);
    }

    public static string? OfficialLayerRelease()
    {
        try { return (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(OfficialLayer, "official.json")))?["version"]; }
        catch (Exception) { return null; }
    }

    public static long OfficialDownloadSize(OfficialRelease? official, Release? kitRelease) =>
        (official != null && OfficialLayerRelease() != official.Name ? official.Package.Size : 0) + DownloadSize(kitRelease);

    public async Task InstallOfficialAsync(string game, OfficialRelease official, Release? kitRelease, Action<double, double, long> download,
                                           Action unpacking, Action<int> step, Action<string> line, CancellationToken cancel,
                                           bool fresh = false)
    {
        if (FilesNote(game, official.GameFileList) is { } note) line(note);
        if (!OfficialMatches(game, official))
            throw new InvalidOperationException("Official isn't available for this Brawlhalla version yet. It will update for it soon.");
        if (DownloadSize(kitRelease) > 0) await EnsureAsync(kitRelease!.Kit, Downloaded, download, unpacking, cancel);
        if (OfficialLayerRelease() != official.Name) await EnsureAsync(official.Package, OfficialLayer, download, unpacking, cancel);
        var kit = Folder ?? throw new InvalidOperationException("The engine kit is missing: install Bifrost's kit first.");
        var code = await RunDriverAsync(kit, ["official", "--game", game, "--work", Work, "--layer", OfficialLayer], text =>
        {
            line(text);
            if (Steps.TryParse(text) is var (n, _, _)) step(n);
        }, cancel, fresh);
        if (code != 0) throw new InvalidOperationException(Steps.Failure(lastFailure));
    }

    public async Task RemoveOfficialAsync(string game, Action<string> log, bool purge = false)
    {
        var kit = Folder ?? throw new InvalidOperationException("The engine kit is missing.");
        string[] args = ["uninstall-official", "--game", game, "--work", Work, .. purge ? new[] { "--purge" } : []];
        if (await RunDriverAsync(kit, args, log, CancellationToken.None) != 0)
            throw new InvalidOperationException(lastFailure ?? "Couldn't remove it.");
    }

    public async Task InstallAsync(string game, Release? release, Action<double, double, long> download, Action unpacking,
                                   Action<int> step, Action<string> line, CancellationToken cancel, bool fresh = false)
    {
        if (FilesNote(game, release?.GameFileList) is { } note) line(note);
        if (!GameMatches(game, release))
            throw new InvalidOperationException("This Brawlhalla version isn't supported yet. Bifrost will update for it soon.");
        if (DownloadSize(release) > 0) await EnsureAsync(release!.Kit, Downloaded, download, unpacking, cancel);
        var kit = Folder ?? throw new InvalidOperationException("No Bifrost to install yet.");
        var code = await RunDriverAsync(kit, ["install", "--game", game, "--work", Work], text =>
        {
            line(text);
            if (Steps.TryParse(text) is var (n, _, _)) step(n);
        }, cancel, fresh);
        if (code != 0) throw new InvalidOperationException(Steps.Failure(lastFailure));
        if (HostedServer.Downloaded) AddOwnServer(game);
    }

    public static void AddOwnServer(string game)
    {
        var storage = Path.Combine(EngineDir(game), "storage");
        var app = Directory.Exists(storage) ? Directory.GetDirectories(storage, "BhPort*").FirstOrDefault() : null;
        if (app == null) return;
        var path = Path.Combine(app, "Documents", "Bifrost", "bh-mod-settings.json");
        try
        {
            var settings = (File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null) ?? new JsonObject();
            var servers = settings["servers"] as JsonArray ?? new JsonArray();
            var port = HostedServer.Port;
            var own = servers.OfType<JsonObject>().FirstOrDefault(s => (string?)s["name"] == "My server (this PC)");
            if (own != null && own["port"]?.GetValue<int>() == port) return;
            if (own != null) own["port"] = port;
            else servers.Add(new JsonObject { ["name"] = "My server (this PC)", ["host"] = "127.0.0.1", ["port"] = port });
            settings["servers"] = servers;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    public static bool Original(string game)
    {
        try { return ReadMarker(game)?["original"]?.GetValue<bool>() == true; }
        catch (Exception) { return false; }
    }

    public async Task SwapLauncherAsync(string game, bool original, Action<string> log)
    {
        var kit = Folder ?? throw new InvalidOperationException("Bifrost's files are missing.");
        string[] args = [original ? "original" : "launcher", "--game", game, "--work", Work];
        if (await RunDriverAsync(kit, args, log, CancellationToken.None) != 0)
            throw new InvalidOperationException(lastFailure ?? "Couldn't swap Brawlhalla.exe.");
    }

    public async Task RemoveAsync(string game, Action<string> log, bool purge = false)
    {
        var kit = Folder ?? throw new InvalidOperationException("Bifrost's files are missing.");
        string[] args = ["uninstall", "--game", game, "--work", Work, .. purge ? new[] { "--purge" } : []];
        if (await RunDriverAsync(kit, args, log, CancellationToken.None) != 0)
            throw new InvalidOperationException(lastFailure ?? "Couldn't remove it.");
    }

    public async Task RemoveEverythingAsync(string? game, Action<string> log)
    {
        if (game != null && Installed(game) != null) await RemoveAsync(game, log, purge: true);
        if (Folder is { } kit && Directory.Exists(Work))
            await RunDriverAsync(kit, ["clean", "--game", game ?? Home, "--work", Work], log, CancellationToken.None);
        foreach (var dir in new[] { Downloaded, OfficialLayer, Path.Combine(Home, "downloads"), Path.Combine(Home, "server"), Downloaded + ".new" })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    static void Unpack(string zip, string folder)
    {
        var fresh = folder + ".new";
        if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
        ZipFile.ExtractToDirectory(zip, fresh);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.Move(fresh, folder);
    }

    async Task<int> RunDriverAsync(string kit, string[] args, Action<string> line, CancellationToken cancel, bool fresh = false)
    {
        var info = new ProcessStartInfo(Python ?? project.Python)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = kit,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in new[] { "-I", "-u", Path.Combine(kit, "player_engine.py") }.Concat(args)) info.ArgumentList.Add(a);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        if (fresh) info.Environment["BIFROST_FRESH"] = "1";
        lastFailure = null;
        using var p = Process.Start(info) ?? throw new InvalidOperationException("Couldn't start Bifrost's builder.");
        p.StandardInput.Close();
        void Read(string? text)
        {
            if (text == null) return;
            if (text.StartsWith("failed: ", StringComparison.Ordinal)) lastFailure = text["failed: ".Length..];
            line(text);
        }
        p.OutputDataReceived += (_, e) => Read(e.Data);
        p.ErrorDataReceived += (_, e) => Read(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(cancel); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (Exception) { }
            await p.WaitForExitAsync();
            throw;
        }
        return p.ExitCode;
    }
}

public static class AppUpdate
{
    static string? Exe => Environment.ProcessPath is { } p && Path.GetFileName(p).Equals("BifrostStudio.exe", StringComparison.OrdinalIgnoreCase)
                          && !File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "BifrostStudio.dll")) ? p : null;

    public static void CleanUp()
    {
        if (Exe is { } exe)
        {
            try { File.Delete(Path.Combine(Path.GetDirectoryName(exe)!, "BifrostStudio.old.exe")); } catch (Exception) { }
        }
    }

    public static async Task<bool> AvailableAsync(AppRelease? release)
    {
        if (release == null || Exe is not { } exe) return false;
        if (Releases.AppVersion is { } mine) return string.CompareOrdinal(release.Name, mine) > 0;
        return await Releases.HashAsync(exe) != release.Exe.Sha256;
    }

    public static async Task ApplyAsync(AppRelease release, IProgress<double> progress)
    {
        var exe = Exe ?? throw new InvalidOperationException("Not the published app.");
        var dir = Path.GetDirectoryName(exe)!;
        var fresh = Path.Combine(EngineKit.Home, "downloads", "BifrostStudio.new.exe");
        await Releases.DownloadAsync(release.Exe, fresh, progress, CancellationToken.None);
        var old = Path.Combine(dir, "BifrostStudio.old.exe");
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        File.Move(fresh, exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
    }
}

public static class Steps
{
    static readonly Dictionary<string, string> Short = new()
    {
        ["checking"] = "Checking your game", ["preparing"] = "Preparing", ["reading"] = "Reading your game",
        ["turning"] = "Converting", ["putting"] = "Putting it together", ["building"] = "Building", ["installing"] = "Installing",
    };

    public static (int N, int Total, string Text)? TryParse(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, @"^step (\d+)/(\d+): (\w+)");
        if (!m.Success) return null;
        var text = Short.GetValueOrDefault(m.Groups[3].Value, m.Groups[3].Value);
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), text);
    }

    public static string Failure(string? failure)
    {
        if (failure == null) return "It stopped without saying why. See Logs.";
        if (failure.Contains("isn't the Brawlhalla")) return "This Brawlhalla version isn't supported yet, or its files changed. Verify them in Steam.";
        if (failure.Contains("running from") || failure.Contains("is running")) return "Close Brawlhalla first.";
        var first = failure.Split('\n')[0].Trim();
        return first.Length > 160 ? first[..160] + "..." : first;
    }
}
