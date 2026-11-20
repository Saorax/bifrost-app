using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BifrostStudio;

public sealed record EngineOptions(string Renderer, string DisplayMode, bool Vsync, int Monitor)
{
    public static readonly string[] Renderers = ["dx11", "dx12", "vulkan"];
    public static readonly string[] DisplayModes = ["game", "windowed", "borderless", "fullscreen"];
    public static readonly EngineOptions Default = new("dx11", "game", true, -1);

    public static EngineOptions Parse(JsonNode? node)
    {
        if (node is not JsonObject o) return Default;
        return new EngineOptions(
            Text(o["renderer"]) is { } r && Renderers.Contains(r) ? r : Default.Renderer,
            Text(o["displayMode"]) is { } m && DisplayModes.Contains(m) ? m : Default.DisplayMode,
            GameSettings.Flag(o["vsync"]) ?? Default.Vsync,
            GameSettings.Whole(o["monitor"]) is { } n && n >= -1 ? n : Default.Monitor);
    }

    public JsonObject ToJson() => new() { ["renderer"] = Renderer, ["displayMode"] = DisplayMode, ["vsync"] = Vsync, ["monitor"] = Monitor };

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

public sealed record ServerEntry(string Name, string Host, int Port)
{
    public string Address => Host + ":" + Port;

    public bool Same(string host, int port) => string.Equals(Host, host, StringComparison.OrdinalIgnoreCase) && Port == port;
}

public sealed record LaunchToggle(string Flag, string Name, string Help);

public sealed class GameSettings(AppState state)
{
    public const string ModFileName = "bh-mod-settings.json";
    public const string ServerFileName = "bh-server.json";
    public const int DefaultPort = 7910;

    public static readonly int[] FrameRates = [0, 60, 75, 120, 144, 165, 180, 240, -1];
    public const int PercentStep = 25;
    public static readonly int[] KeepChoices = [0, 50, 100, 200, 500, 1000];
    public static readonly int[] ExportRates = [30, 50, 60];
    public static readonly int[] ExportQualities = [50, 60, 70, 80, 90, 100];
    public static readonly int[] SpectateDelays = [0, 500, 1000, 2000, 3000, 5000, 10000, 30000, 60000];

    public static readonly LaunchToggle[] Toggles =
    [
        new("-noskulls", "No skulls", "Maps without skulls and the bouncy bomb's safe look."),
        new("-broadcaster", "Broadcaster mode", "For casting or streaming team matches. In a custom room, the host can give each team a fixed colour that shows on the legends, scoreboard, name tags, KO effects and stage select. Leave it off for normal play."),
        new("-showversion", "Show version", "The game's version at the bottom left of the menus."),
        new("-steamdeck", "Steam Deck mode", "The Steam Deck layout and settings."),
    ];

    public AppState State => state;

    public string? EngineFile => state.Paths is { } p ? Path.Combine(p.Storage, "engine.json") : null;
    public string? ModFile => state.Paths?.DocsFile(ModFileName);
    public string? ServerFile => state.Paths?.DocsFile(ServerFileName);

    public bool HasEngine => state.Paths is { } p && Directory.Exists(p.Engine);
    public bool HasDocs => ModFile != null;

    public EngineOptions Engine() => EngineFile is { } f ? EngineOptions.Parse(JsonFiles.Read(f)) : EngineOptions.Default;

    public string? SetEngine(EngineOptions options)
    {
        if (EngineFile is not { } file) return "Bifrost isn't installed for this game yet.";
        var current = JsonFiles.Read(file, out var problem);
        if (problem != null && problem != JsonFiles.Missing) return "engine.json is " + problem;
        try
        {
            JsonFiles.Write(file, JsonFiles.Merge(current, options.ToJson()));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }

    public JsonObject Mods()
    {
        if (ModFile is not { } file) return new JsonObject();
        var current = JsonFiles.Read(file) as JsonObject ?? new JsonObject();
        return state.Writes.PendingFor(file) is { } pending ? JsonFiles.Merge(current, pending) as JsonObject ?? current : current;
    }

    public bool? Change(JsonObject patch)
    {
        if (ModFile is not { } file) return null;
        return state.Writes.Merge(file, patch);
    }

    JsonObject Launch() => Mods()["launchOptions"] as JsonObject ?? new JsonObject();

    public int FrameRate()
    {
        var all = Launch();
        if (Flag(all["-unlockfps"]) == true) return -1;
        return Whole(all["-setfps"]) is { } n && n > 0 ? n : 0;
    }

    public bool? SetFrameRate(int fps) => Change(new JsonObject
    {
        ["launchOptions"] = new JsonObject
        {
            ["-unlockfps"] = fps < 0 ? true : null,
            ["-setfps"] = fps > 0 ? fps : null,
        },
    });

    public bool LaunchFlag(string flag) => Launch()[flag] is { } v && Flag(v) != false && Whole(v) != 0;

    public bool? SetLaunchFlag(string flag, bool on) =>
        Change(new JsonObject { ["launchOptions"] = new JsonObject { [flag] = on ? true : null } });

    public int SpectateDelay() => Whole(Launch()["-spectatedelay"]) is { } n && n > 0 ? n : 0;

    public bool? SetSpectateDelay(int ms) =>
        Change(new JsonObject { ["launchOptions"] = new JsonObject { ["-spectatedelay"] = ms > 0 ? ms : null } });

    public bool FramePacing() => Flag(Mods()["framePacing"]) ?? true;
    public bool? SetFramePacing(bool on) => Change(new JsonObject { ["framePacing"] = on });

    public int ScreenShake() => Percent(Mods()["screenShake"]);
    public bool? SetScreenShake(int percent) => Change(new JsonObject { ["screenShake"] = Math.Clamp(percent, 0, 100) });

    public int KoEffects() => Percent(Mods()["koEffects"]);
    public bool? SetKoEffects(int percent) => Change(new JsonObject { ["koEffects"] = Math.Clamp(percent, 0, 100) });

    public static int ClampRate(int fps) => fps < 0 ? -1 : fps == 0 ? 0 : Math.Clamp(fps, 25, 1000);

    public bool ClassicEffects() => Flag(Mods()["classicEffects"]) ?? false;
    public bool? SetClassicEffects(bool on) => Change(new JsonObject { ["classicEffects"] = on });

    public bool SteadyBlinks() => Flag(Mods()["steadyBlinks"]) ?? false;
    public bool? SetSteadyBlinks(bool on) => Change(new JsonObject { ["steadyBlinks"] = on });

    public string? MenuTheme() => Mods()["menuTheme"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    public bool? ResetMenuTheme() => Change(new JsonObject { ["menuTheme"] = null });

    public int KeepRecent() => Whole(Mods()["replayKeepRecent"]) is { } n && n > 0 ? n : 0;
    public bool? SetKeepRecent(int n) => Change(new JsonObject { ["replayKeepRecent"] = n });

    public int ExportFps() => Whole(Mods()["replayExportFps"]) is { } n && ExportRates.Contains(n) ? n : 60;
    public bool? SetExportFps(int fps) => Change(new JsonObject { ["replayExportFps"] = fps });

    public int ExportQuality() => Whole(Mods()["replayExportQuality"]) is { } n && n >= 50 && n <= 100 ? n : 90;
    public bool? SetExportQuality(int q) => Change(new JsonObject { ["replayExportQuality"] = q });

    public IReadOnlyList<ServerEntry> Servers()
    {
        if (Mods()["servers"] is not JsonArray list) return [];
        var result = new List<ServerEntry>();
        foreach (var item in list)
        {
            if (item is not JsonObject o || o["host"] is not JsonValue h || !h.TryGetValue<string>(out var host) || host.Length == 0) continue;
            var port = Whole(o["port"]) ?? DefaultPort;
            var name = o["name"] is JsonValue n && n.TryGetValue<string>(out var s) && s.Length > 0 ? s : host;
            result.Add(new ServerEntry(name, host, port));
        }
        return result;
    }

    public ServerEntry? Current()
    {
        if (ServerFile is not { } file || JsonFiles.Read(file) is not JsonObject o) return null;
        if (o["host"] is not JsonValue h || !h.TryGetValue<string>(out var host) || host.Length == 0) return null;
        return new ServerEntry(host, host, Whole(o["port"]) ?? DefaultPort);
    }

    public ServerEntry? Main()
    {
        if (ServerFile is not { } file || JsonFiles.Read(file)?["main"] is not JsonObject o) return null;
        if (o["host"] is not JsonValue h || !h.TryGetValue<string>(out var host) || host.Length == 0) return null;
        return new ServerEntry("Main server", host, Whole(o["port"]) ?? DefaultPort);
    }

    public static (string Host, int Port)? ParseAddress(string text)
    {
        var m = Regex.Match(text.Trim(), @"^([^\s:]+|\[[^\]]+\])(?::(\d{1,5}))?$");
        if (!m.Success) return null;
        return (m.Groups[1].Value, m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : DefaultPort);
    }

    public static string? Refuse(string host, int port)
    {
        if (string.IsNullOrEmpty(host)) return "no host";
        if (port <= 0 || port > 65535) return "port must be 1-65535";
        if (!Regex.IsMatch(host, @"^[A-Za-z0-9.\-:\[\]]+$")) return "host has characters a host name can't have";
        if (Regex.IsMatch(host, @"brawlhalla|bmg|ubi(soft)?\b|ubi\.", RegexOptions.IgnoreCase)) return "that is an official host name";
        if (host.StartsWith("199.229.253.", StringComparison.Ordinal) || host == "18.188.245.3") return "that is one of the game's official server addresses";
        return null;
    }

    public string? AddServer(string name, string address)
    {
        if (ParseAddress(address) is not { } parsed) return "type an address like example.com or example.com:7910";
        var (host, port) = parsed;
        if (Refuse(host, port) is { } why) return "Refused: " + why;
        if (Servers().Any(s => s.Same(host, port))) return "That server is already in the list.";
        var list = Mods()["servers"] is JsonArray a ? (JsonArray)a.DeepClone() : new JsonArray();
        list.Add(new JsonObject { ["name"] = name.Trim().Length > 0 ? name.Trim() : host, ["host"] = host, ["port"] = port });
        return Change(new JsonObject { ["servers"] = list }) == null ? "Start the game once first." : null;
    }

    public bool? RemoveServer(ServerEntry server)
    {
        if (Mods()["servers"] is not JsonArray a) return true;
        var list = new JsonArray();
        foreach (var item in a)
        {
            var host = item?["host"] is JsonValue h && h.TryGetValue<string>(out var s) ? s : null;
            var port = Whole(item?["port"]) ?? DefaultPort;
            if (host != null && server.Same(host, port)) continue;
            list.Add(item?.DeepClone());
        }
        return Change(new JsonObject { ["servers"] = list });
    }

    public string? UseServer(string host, int port)
    {
        if (state.GameRunning) return "Close Brawlhalla first. It keeps its own copy of the server setting.";
        if (Refuse(host, port) is { } why) return "Refused: " + why;
        if (ServerFile is not { } file) return "Start the game once first.";
        var read = JsonFiles.Read(file, out var problem);
        if (problem != null && problem != JsonFiles.Missing) return ServerFileName + " is " + problem;
        var cfg = read as JsonObject ?? new JsonObject();
        if (cfg["tokens"] is not JsonObject tokens)
        {
            tokens = new JsonObject();
            cfg["tokens"] = tokens;
        }
        var oldHost = cfg["host"] is JsonValue h && h.TryGetValue<string>(out var oh) ? oh : null;
        var oldToken = cfg["token"] is JsonValue t && t.TryGetValue<string>(out var ot) ? ot : null;
        if (!string.IsNullOrEmpty(oldHost) && !string.IsNullOrEmpty(oldToken))
            tokens[oldHost + ":" + (Whole(cfg["port"]) ?? 0)] = new JsonObject { ["token"] = oldToken, ["account"] = cfg["account"]?.DeepClone() };
        var mine = tokens[host + ":" + port] as JsonObject;
        cfg["host"] = host;
        cfg["port"] = port;
        if (mine?["token"] is JsonValue mt && mt.TryGetValue<string>(out var token) && token.Length > 0)
        {
            cfg["token"] = token;
            cfg["account"] = mine["account"]?.DeepClone();
        }
        else
        {
            cfg.Remove("token");
            cfg.Remove("account");
        }
        try
        {
            JsonFiles.Write(file, cfg);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }

    public IReadOnlyList<string> ShaderCaches()
    {
        if (state.Paths is not { } p) return [];
        try
        {
            var dirs = Directory.Exists(p.Storage) ? Directory.GetDirectories(p.Storage, "BhPort*").Select(d => Path.Combine(d, "Cache")) : [];
            if (EngineKit.CacheDir(p.Game) is { } cache) dirs = dirs.Append(cache);
            return dirs.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "shaders-*.cache")).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    public (int Removed, string? Problem) ClearShaderCache()
    {
        if (state.GameRunning) return (0, "Close Brawlhalla first.");
        var removed = 0;
        foreach (var file in ShaderCaches())
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (removed, e.Message); }
        }
        return (removed, null);
    }

    public static bool? Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static int? Whole(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
        if (v.TryGetValue<System.Text.Json.JsonElement>(out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Number && e.TryGetDouble(out var x)
            && x >= int.MinValue && x <= int.MaxValue) return (int)x;
        return null;
    }

    static int Percent(JsonNode? node) => Whole(node) is { } n ? Math.Clamp(n, 0, 100) : 100;
}