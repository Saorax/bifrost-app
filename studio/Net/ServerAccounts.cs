using System.Text.Json.Nodes;

namespace BifrostStudio.Net;

public sealed record ServerAccount(string Token, string Account, bool FromGame);

public sealed class ServerAccounts(string file, Func<string?>? gameServerFile = null)
{
    public static string DefaultFile => BifrostFolders.StudioFile("server-accounts.json");

    readonly object gate = new();

    public string File { get; } = file;

    public static string Key(string host, int port) => host.Trim().ToLowerInvariant() + ":" + port;

    JsonObject Load() => JsonFiles.Read(File) as JsonObject ?? new JsonObject();

    void Store(JsonObject root)
    {
        try
        {
            JsonFiles.Write(File, root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    public string InstallId
    {
        get
        {
            lock (gate)
            {
                var root = Load();
                if (Text(root["install"]) is { } id) return id;
                id = Guid.NewGuid().ToString("N");
                root["install"] = id;
                Store(root);
                return id;
            }
        }
    }

    public ServerAccount? For(string host, int port)
    {
        lock (gate)
        {
            if (Load()["servers"]?[Key(host, port)] is JsonObject mine)
            {
                if (Text(mine["token"]) is { } t) return new ServerAccount(t, Text(mine["account"]) ?? "", false);
                if (mine["signed_out"] is JsonValue so && so.TryGetValue<bool>(out var off) && off) return null;
            }
        }
        return FromGame(host, port);
    }

    public bool GameHas(string host, int port) => FromGame(host, port) != null;

    public void UseGame(string host, int port) => Remove(host, port);

    ServerAccount? FromGame(string host, int port)
    {
        if (gameServerFile?.Invoke() is not { } path || JsonFiles.Read(path) is not JsonObject cfg) return null;
        var current = Text(cfg["host"]);
        var currentPort = cfg["port"] is JsonValue pv && pv.TryGetValue<int>(out var cp) ? cp : GameSettings.DefaultPort;
        if (current != null && Key(current, currentPort) == Key(host, port) && Text(cfg["token"]) is { } t)
            return new ServerAccount(t, AccountName(cfg["account"]), true);
        if (cfg["tokens"] is JsonObject tokens)
            foreach (var (k, v) in tokens)
                if (k.Trim().ToLowerInvariant() == Key(host, port) && Text(v?["token"]) is { } tt)
                    return new ServerAccount(tt, AccountName(v?["account"]), true);
        return null;
    }

    static string AccountName(JsonNode? n) => Text(n) ?? Text(n?["name"]) ?? "";

    public void Save(string host, int port, string token, string account)
    {
        lock (gate)
        {
            var root = Load();
            if (root["servers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                root["servers"] = servers;
            }
            servers[Key(host, port)] = new JsonObject { ["token"] = token, ["account"] = account, ["saved"] = DateTime.UtcNow.ToString("o") };
            Store(root);
        }
    }

    public void Forget(string host, int port)
    {
        lock (gate)
        {
            var root = Load();
            if (root["servers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                root["servers"] = servers;
            }
            servers[Key(host, port)] = new JsonObject { ["signed_out"] = true, ["saved"] = DateTime.UtcNow.ToString("o") };
            Store(root);
        }
    }

    void Remove(string host, int port)
    {
        lock (gate)
        {
            var root = Load();
            if (root["servers"] is JsonObject servers && servers.Remove(Key(host, port))) Store(root);
        }
    }
}
