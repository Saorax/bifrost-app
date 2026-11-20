using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BifrostStudio;

public enum SettingKind { Text, Secret, Number, Toggle, Choice, Ids, Names, Json }

public sealed record ServerSetting(string Key, string Group, SettingKind Kind, string Default, string Help, string[]? Choices = null);

public static partial class ServerSettings
{
    public static readonly string[] Groups = ["Server", "Sign-in", "Who can join", "Web and certificates", "Matches", "Features", "Backups and logs"];

    public static readonly ServerSetting[] All =
    [
        new("server_name", "Server", SettingKind.Text, "", "The name players see for this server."),
        new("game_port", "Server", SettingKind.Number, "7910", "The game's TCP port. The P2P relay uses the same number on UDP."),
        new("web_port", "Server", SettingKind.Number, "7911", "The sign-in page's port. Keep it one above the game port, because that's where the app and the game look."),
        new("game_host", "Server", SettingKind.Text, "0.0.0.0", "The address the game port listens on. 0.0.0.0 means every address."),
        new("web_host", "Server", SettingKind.Text, "0.0.0.0", "The address the web port listens on."),
        new("public_url", "Server", SettingKind.Text, "http://localhost:7911", "The address players' browsers use to reach the sign-in page."),
        new("notice", "Server", SettingKind.Text, "", "A message every player sees at login. Leave it empty for none."),
        new("discord_client_id", "Sign-in", SettingKind.Text, "828146347348721694", "Your Discord application's ID."),
        new("discord_client_secret", "Sign-in", SettingKind.Secret, "", "Your Discord application's client secret. Sign-in stays off without it."),
        new("admin_discord_ids", "Sign-in", SettingKind.Ids, "", "Discord accounts that can use the admin tools."),
        new("mod_reviewer_discord_ids", "Sign-in", SettingKind.Ids, "", "Discord accounts that review shared mods."),
        new("developer_discord_ids", "Sign-in", SettingKind.Ids, "", "Discord accounts that can use the Developer title and review mods."),
        new("whitelist", "Who can join", SettingKind.Ids, "", "Only these Discord accounts can play, plus anyone the next two settings let in. If all three are empty, everyone can play."),
        new("whitelist_discord_servers", "Who can join", SettingKind.Ids, "", "Members of these Discord servers can play."),
        new("whitelist_file", "Who can join", SettingKind.Text, "", "A file of Discord IDs, one per line. The server rereads it when it changes, and Who can join edits it for you."),
        new("behind_proxy", "Web and certificates", SettingKind.Toggle, "false", "Turn this on if a proxy serves public_url on the same port number as the web port."),
        new("tls", "Web and certificates", SettingKind.Toggle, "false", "Serves the web port over https with the server's own certificate. Browsers won't trust it."),
        new("tls_cert", "Web and certificates", SettingKind.Text, "", "A real full-chain certificate for https. The server reloads it when it changes."),
        new("tls_key", "Web and certificates", SettingKind.Text, "", "The key for that certificate."),
        new("tls_names", "Web and certificates", SettingKind.Names, "", "Extra names for the server's own certificate."),
        new("relay", "Matches", SettingKind.Choice, "inprocess", "Whether the P2P relay runs inside the server or as a separate program.", ["inprocess", "native"]),
        new("relay_native", "Matches", SettingKind.Text, "", "The relay program to run when relay is native."),
        new("relay_max_rate", "Matches", SettingKind.Number, "400", "The most packets a second the relay passes on for each player."),
        new("relay_max_bytes", "Matches", SettingKind.Number, "160000", "The most bytes a second the relay passes on for each player."),
        new("relay_control_port", "Matches", SettingKind.Number, "", "The native relay's control port. Leave it empty to use the game port + 2."),
        new("p2p_direct", "Matches", SettingKind.Choice, "friends", "Which players can see each other's addresses and connect directly. With none, every match goes through the relay.", ["friends", "all", "none"]),
        new("p2p_ask", "Matches", SettingKind.Toggle, "true", "When p2p_direct is friends, players who aren't friends get asked if they want to connect directly."),
        new("turn", "Matches", SettingKind.Choice, "off", "Sends relayed matches through Cloudflare's TURN servers.", ["off", "cloudflare"]),
        new("turn_key_id", "Matches", SettingKind.Text, "", "Your Cloudflare TURN key's ID."),
        new("turn_api_token", "Matches", SettingKind.Secret, "", "Your Cloudflare TURN API token."),
        new("turn_ttl", "Matches", SettingKind.Number, "7200", "How many seconds each TURN login lasts, up to 172800."),
        new("rate_custom_rooms", "Matches", SettingKind.Toggle, "true", "Custom room matches count toward stats."),
        new("daily_elo_cap", "Matches", SettingKind.Number, "200", "How much rating a player can gain in a day before the integrity check flags them."),
        new("flag_repeat", "Matches", SettingKind.Number, "15", "How many ranked matches the same two players can play in a day before they're flagged."),
        new("public_mechanics", "Matches", SettingKind.Toggle, "false", "Lets everyone see a player's dodges, dashes and combos. When it's off, only that player can."),
        new("cups", "Features", SettingKind.Toggle, "false", "Turns on cups, which are double elimination brackets of 8 players."),
        new("ask_region", "Features", SettingKind.Toggle, "false", "Opens the game's region picker at login for players without a region."),
        new("client_theme", "Features", SettingKind.Text, "auto", "The menu theme every player gets. Use a theme name or id, or auto."),
        new("game_data", "Features", SettingKind.Text, "data/game", "The folder holding the game's Game/ and Init/ tables."),
        new("patch_dir", "Features", SettingKind.Text, "", "The folder of Bifrost update patches the web port serves. Leave it empty for data/patches."),
        new("friends", "Features", SettingKind.Json, "", "Friends options as JSON, with the keys everyone, include_guests, groups, party_follow and requests_in_game. party_follow is pull, invite or off."),
        new("tournaments", "Features", SettingKind.Json, "", "Tournament options as JSON, like {\"creators\": \"everyone\"}."),
        new("achievements", "Features", SettingKind.Json, "", "Achievement options as JSON, like {\"on\": false}."),
        new("feature_flags", "Features", SettingKind.Json, "", "Extra feature flags sent to every game, as JSON. For example, disablehero-1 with a legend name turns that legend off."),
        new("backup_dir", "Backups and logs", SettingKind.Text, "", "Where daily backups go. Leave it empty for data/backups."),
        new("backup_hour", "Backups and logs", SettingKind.Number, "4", "The hour of the day the backup runs."),
        new("backup_keep_days", "Backups and logs", SettingKind.Number, "14", "Backups older than this many days get deleted."),
        new("log_max_mb", "Backups and logs", SettingKind.Number, "10", "How many MB data/server-log.txt can grow to before it rolls over."),
        new("log_keep", "Backups and logs", SettingKind.Number, "5", "How many rolled-over logs to keep."),
    ];

    public static ServerSetting? Find(string key) => All.FirstOrDefault(s => s.Key == key);

    public static string LocalFile(string folder) => Path.Combine(folder, "config.local.json");

    public static JsonObject Shared(string folder) => JsonFiles.Read(Path.Combine(folder, "config.json")) as JsonObject ?? [];

    public static JsonObject? Local(string folder, out string? problem)
    {
        var node = JsonFiles.Read(LocalFile(folder), out problem);
        if (problem == JsonFiles.Missing) { problem = null; return []; }
        if (problem != null) return null;
        if (node is JsonObject o) return o;
        problem = "config.local.json isn't a JSON object";
        return null;
    }

    public static JsonNode? Effective(string folder, string key) =>
        (Local(folder, out _) is { } local && local.ContainsKey(key) ? local[key] : Shared(folder)[key]);

    public static string Show(ServerSetting s, JsonNode? value)
    {
        if (value == null) return "";
        return s.Kind switch
        {
            SettingKind.Ids or SettingKind.Names => value is JsonArray a ? string.Join(", ", a.Select(v => v?.ToString() ?? "")) : value.ToString(),
            SettingKind.Json => value.ToJsonString(),
            SettingKind.Toggle => value is JsonValue v && v.TryGetValue<bool>(out var b) ? (b ? "true" : "false") : value.ToString(),
            SettingKind.Text when s.Key == "notice" && value is JsonObject o => o["text"]?.ToString() ?? "",
            _ => value.ToString(),
        };
    }

    [GeneratedRegex(@"^\d{5,25}$")]
    public static partial Regex DiscordId();

    public static (JsonNode? Value, string? Problem) Parse(ServerSetting s, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return (null, null);
        switch (s.Kind)
        {
            case SettingKind.Number:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || double.IsNaN(n) || double.IsInfinity(n) || n < 0)
                    return (null, $"{s.Key}: a number, 0 or more");
                if (s.Key.EndsWith("_port") && (n != Math.Floor(n) || n is < 1024 or > 65535)) return (null, $"{s.Key}: a whole number from 1024 to 65535");
                return (n == Math.Floor(n) && Math.Abs(n) < 1e15 ? JsonValue.Create((long)n) : JsonValue.Create(n), null);
            case SettingKind.Toggle:
                return text.ToLowerInvariant() switch
                {
                    "true" or "on" or "yes" => (JsonValue.Create(true), null),
                    "false" or "off" or "no" => (JsonValue.Create(false), null),
                    _ => (null, $"{s.Key}: on or off"),
                };
            case SettingKind.Choice:
                return s.Choices!.Contains(text) ? (JsonValue.Create(text), null) : (null, $"{s.Key}: one of {string.Join(", ", s.Choices!)}");
            case SettingKind.Ids:
            case SettingKind.Names:
                var parts = text.Split([',', ' ', '\n', '\r', '\t', ';'], StringSplitOptions.RemoveEmptyEntries);
                if (s.Kind == SettingKind.Ids && parts.FirstOrDefault(p => !DiscordId().IsMatch(p)) is { } bad)
                    return (null, $"{s.Key}: {bad} isn't a Discord ID (5 to 25 digits)");
                return (new JsonArray([.. parts.Distinct().Select(p => (JsonNode?)JsonValue.Create(p))]), null);
            case SettingKind.Json:
                try { return (JsonNode.Parse(text), null); }
                catch (System.Text.Json.JsonException e) { return (null, $"{s.Key}: not valid JSON ({e.Message})"); }
            default:
                return (JsonValue.Create(text), null);
        }
    }

    public static string? Save(string folder, IReadOnlyDictionary<string, string> edits)
    {
        var local = Local(folder, out var problem);
        if (local == null) return $"config.local.json can't be read ({problem}). Fix or remove it first.";
        var next = (JsonObject)local.DeepClone();
        foreach (var (key, text) in edits)
        {
            if (Find(key) is not { } s) continue;
            var (value, why) = Parse(s, text);
            if (why != null) return why;
            if (value == null) next.Remove(key);
            else if (s.Key == "notice" && local["notice"] is JsonObject before)
            {
                var kept = (JsonObject)before.DeepClone();
                kept["text"] = value;
                next[key] = kept;
            }
            else next[key] = value;
        }
        JsonFiles.Write(LocalFile(folder), next);
        return null;
    }
}

public sealed class ServerConsole(string log)
{
    public string Log => log;
    public string Inbox => log + ".in";

    public void Send(string line)
    {
        line = line.Replace("\r", " ").Replace("\n", " ").Trim();
        if (line.Length == 0) return;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var f = new FileStream(Inbox, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                f.Write(bytes);
                return;
            }
            catch (IOException) when (attempt < 5) { Thread.Sleep(60); }
        }
    }

    public static long Length(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public static string ReadFrom(string path, long offset)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > f.Length) offset = 0;
            f.Seek(offset, SeekOrigin.Begin);
            using var r = new StreamReader(f, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    public async Task<string> AskAsync(string line, TimeSpan wait, TimeSpan? quiet = null)
    {
        var settle = quiet ?? TimeSpan.FromMilliseconds(700);
        var start = Length(log);
        Send(line);
        var began = DateTime.UtcNow;
        var size = start;
        var grew = DateTime.MinValue;
        while (DateTime.UtcNow - began < wait)
        {
            await Task.Delay(120);
            var now = Length(log);
            if (now != size) { size = now; grew = DateTime.UtcNow; }
            else if (grew != DateTime.MinValue && DateTime.UtcNow - grew >= settle) break;
        }
        return ReadFrom(log, start).TrimEnd();
    }
}

public sealed record WhitelistEntry(string Id, string Note);

public static class ServerWhitelist
{
    public static string? FilePath(string folder) =>
        ServerSettings.Effective(folder, "whitelist_file")?.ToString() is { Length: > 0 } name ? Path.GetFullPath(Path.Combine(folder, name)) : null;

    public static List<WhitelistEntry> Read(string path)
    {
        var list = new List<WhitelistEntry>();
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return list; }
        foreach (var line in lines)
        {
            var hash = line.IndexOf('#');
            var id = (hash < 0 ? line : line[..hash]).Trim();
            if (!ServerSettings.DiscordId().IsMatch(id) || list.Any(e => e.Id == id)) continue;
            list.Add(new WhitelistEntry(id, hash < 0 ? "" : line[(hash + 1)..].Trim()));
        }
        return list;
    }

    public static void Write(string path, IEnumerable<WhitelistEntry> entries)
    {
        var header = new List<string>();
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var hash = line.IndexOf('#');
                var id = (hash < 0 ? line : line[..hash]).Trim();
                if (id.Length == 0 && line.Trim().Length > 0) header.Add(line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (header.Count == 0) header.Add("# Discord IDs allowed on this server, one per line. Edit them in Bifrost Studio > Server > Who can join.");
        var body = entries.Where(e => ServerSettings.DiscordId().IsMatch(e.Id)).DistinctBy(e => e.Id)
            .Select(e => e.Note.Length > 0 ? $"{e.Id}  # {e.Note.Replace("\n", " ").Replace("\r", " ")}" : e.Id);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".studio-tmp";
        File.WriteAllLines(temp, [.. header, .. body]);
        File.Move(temp, full, true);
    }
}

public sealed record ServerBackup(string Path, DateOnly Day, long Size);

public static class ServerBackups
{
    static readonly Regex Name = new(@"^accounts-(\d{4}-\d{2}-\d{2})\.sqlite$");

    public static string Database(string folder) => Path.Combine(folder, "data", "accounts.sqlite");

    public static string Dir(string folder) =>
        ServerSettings.Effective(folder, "backup_dir")?.ToString() is { Length: > 0 } d ? Path.GetFullPath(Path.Combine(folder, d)) : Path.Combine(folder, "data", "backups");

    public static List<ServerBackup> List(string folder)
    {
        var list = new List<ServerBackup>();
        try
        {
            foreach (var f in new DirectoryInfo(Dir(folder)).EnumerateFiles("accounts-*.sqlite"))
            {
                var m = Name.Match(f.Name);
                if (m.Success && DateOnly.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", out var day)) list.Add(new ServerBackup(f.FullName, day, f.Length));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return [.. list.OrderByDescending(b => b.Day)];
    }

    const string Checkpoint = "import sqlite3, sys\nc = sqlite3.connect(sys.argv[1])\nc.execute('PRAGMA wal_checkpoint(TRUNCATE)')\nc.close()";

    public static string? Settle(string folder, string? python)
    {
        var db = Database(folder);
        if (!File.Exists(db)) return "The server has no accounts database yet.";
        if (ServerConsole.Length(db + "-wal") == 0) return null;
        if (python == null) return "The database still has changes in its journal. Install Bifrost's tools so they can be saved into it.";
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-I", "-c", Checkpoint, db }) info.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(info)!;
            var error = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000)) { p.Kill(); return "Saving the journal into the database took too long."; }
            if (p.ExitCode != 0) return "Couldn't save the journal into the database: " + error.Trim().Split('\n').Last();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return "Couldn't run Python: " + e.Message; }
        return ServerConsole.Length(db + "-wal") == 0 ? null : "The database's journal is still in use. Is the server really stopped?";
    }

    public static (string? Made, string? Problem) CopyNow(string folder, DateTime now, string? python)
    {
        if (Settle(folder, python) is { } why) return (null, why);
        var dest = Path.Combine(Dir(folder), $"accounts-{now:yyyy-MM-dd}.sqlite");
        try
        {
            Directory.CreateDirectory(Dir(folder));
            File.Copy(Database(folder), dest + ".part", true);
            File.Move(dest + ".part", dest, true);
            return (dest, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, e.Message); }
    }

    public static (string? KeptAs, string? Problem) Restore(string folder, ServerBackup backup, DateTime now, string? python)
    {
        if (Settle(folder, python) is { } why) return (null, why);
        var db = Database(folder);
        var kept = Path.Combine(folder, "data", $"accounts-before-restore-{now:yyyyMMdd-HHmmss}.sqlite");
        try
        {
            File.Copy(db, kept, false);
            File.Copy(backup.Path, db + ".restoring", true);
            File.Move(db + ".restoring", db, true);
            foreach (var side in new[] { db + "-wal", db + "-shm" }) File.Delete(side);
            return (kept, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, e.Message); }
    }
}

public sealed record BugReportRow(int Id, string When, string Who, string Note, string Size);

public static partial class ServerBugReports
{
    public static string Folder(string folder) => Path.Combine(folder, "data", "bug-reports");

    [GeneratedRegex(@"^\s*#(\d+)\s+(\d{4}-\d{2}-\d{2} \d{2}:\d{2})\s+(.*?):\s(.*)\s\((\d+ KB)\)\s*$")]
    private static partial Regex Line();

    [GeneratedRegex(@"^\s*#(\d+)\s+(\d{4}-\d{2}-\d{2} \d{2}:\d{2})\s+(.*):\s(\(no note\))\s\((\d+ KB)\)\s*$")]
    private static partial Regex NoNote();

    public static List<BugReportRow> ParseListing(string text)
    {
        var rows = new List<BugReportRow>();
        foreach (var line in text.Split('\n'))
        {
            var m = NoNote().Match(line.TrimEnd('\r')) is { Success: true } plain ? plain : Line().Match(line.TrimEnd('\r'));
            if (m.Success && int.TryParse(m.Groups[1].Value, out var id) && rows.All(r => r.Id != id))
                rows.Add(new BugReportRow(id, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value));
        }
        return rows;
    }

    public static List<(int Id, string Path, DateTime Written)> Written(string folder)
    {
        var list = new List<(int, string, DateTime)>();
        try
        {
            foreach (var f in new DirectoryInfo(Folder(folder)).EnumerateFiles("*.txt"))
            {
                if (int.TryParse(Path.GetFileNameWithoutExtension(f.Name), out var id)) list.Add((id, f.FullName, f.LastWriteTime));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return [.. list.OrderByDescending(r => r.Item1)];
    }

    public static string FileFor(string folder, int id) => Path.Combine(Folder(folder), $"{id}.txt");
}
