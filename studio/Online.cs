using System.Text.Json.Nodes;
using BifrostStudio.Net;

namespace BifrostStudio;

public interface IServerData
{
    ServerEntry? Server { get; }
    LinkState State { get; }
    string Message { get; }
    bool SignedIn { get; }
    string Account { get; }
    event Action? Changed;
    Task<JsonNode?> Stats(string query);
    Task<JsonNode?> Friends(string action, string who = "");
    Task<SignInLink> StartSignIn();
    Task<SignInResult?> WaitSignIn(TimeSpan timeout);
    Task<ReplayDownload> Replay(string code);
    Task<ReplayDownload> Replay(uint room, uint seed);
    Task<JsonNode?> Tournament(string action, JsonObject args);
    Task<CupBoard?> Cups();
    Task<CupBoard?> CupSignUp(uint cup, bool on);
    Task<IReadOnlyList<CupBracketRow>?> CupBrackets(uint cup);
    Task<CupBracketView?> CupBracket(uint cup, uint bracket);
    Task<ModListing?> ModLibrary();
    Task<ModListing?> ModByKey(string key);
    Task<ModListing?> MyMods();
    Task<ModListing?> SetMod(string modId, string action);
    Task<(byte[]? Data, string Problem)> DownloadMod(ModListing listing, string entry);
    Task<ModShareAnswer?> ShareMod(string entry, bool makePublic);
    Task<string?> UploadMod(ModUploadRequest request, string entry, byte[] package);
    Task<byte[]?> ModPreview(ModListing listing, string entry);
    bool IsAdmin { get; }
    Task<JsonNode?> Moderate(string action, JsonObject args);
    Task<ModListing?> ReviewMod(string pid, bool approve, string note);
    Task<ModListing?> FeatureMod(string modId, bool on, string note);
    Task<ModListing?> Workshop(string action, JsonObject args);
    bool GameSignInAvailable { get; }
    Task SignOut();
    Task UseGameSignIn();
    void Reconnect();
}

public sealed class OnlineService : IServerData, IAsyncDisposable
{
    public const string AppName = "Bifrost Studio";
    static readonly ReplayDownload NotConnected = new(false, [], "", [], null, "Not connected to the server.");

    readonly ServerAccounts accounts;
    ServerLink? link;

    public OnlineService(ServerAccounts accounts) => this.accounts = accounts;

    public ServerEntry? Server { get; private set; }
    public LinkState State => link?.State ?? LinkState.Off;
    public string Message => link?.Message ?? "";
    public BifrostClient? Client => link?.Client;
    public bool SignedIn => Server is { } s && (Client?.Token is { Length: > 0 } || accounts.For(s.Host, s.Port) != null);
    public string Account => Client?.Account is { Length: > 0 } a ? a : Server is { } s ? accounts.For(s.Host, s.Port)?.Account ?? "" : "";
    public event Action? Changed;

    public async Task UseAsync(ServerEntry? server)
    {
        if (Server is { } now && server != null && now.Same(server.Host, server.Port) && link != null) return;
        if (link is { } old)
        {
            link = null;
            await old.DisposeAsync();
        }
        Server = server;
        if (server != null)
        {
            var l = new ServerLink(server.Host, server.Port, AppName, accounts.InstallId, () => accounts.For(server.Host, server.Port)?.Token);
            l.Changed += () => Changed?.Invoke();
            l.SignedIn += s =>
            {
                if (s.Ok) accounts.Save(server.Host, server.Port, s.Token, s.Account);
                Changed?.Invoke();
            };
            link = l;
            l.Start();
        }
        Changed?.Invoke();
    }

    public void Reconnect() => link?.ReconnectNow();

    public bool IsAdmin => Client?.IsAdmin == true;
    public Task<JsonNode?> Moderate(string action, JsonObject args) => Client is { } c ? c.ModerateAsync(action, args) : Task.FromResult<JsonNode?>(null);
    public Task<ModListing?> ReviewMod(string pid, bool approve, string note) =>
        Client is { } c ? c.SetModAsync(pid, approve ? "approve" : "reject", note) : Task.FromResult<ModListing?>(null);
    public Task<ModListing?> FeatureMod(string modId, bool on, string note) =>
        Client is { } c ? c.SetModAsync(modId, on ? "feature" : "unfeature", note) : Task.FromResult<ModListing?>(null);
    public Task<ModListing?> Workshop(string action, JsonObject args) =>
        Client is { } c ? c.WorkshopAsync(action, args) : Task.FromResult<ModListing?>(null);

    public bool GameSignInAvailable =>Server is { } s && !SignedIn && accounts.GameHas(s.Host, s.Port);

    public Task SignOut() => Relink(s => accounts.Forget(s.Host, s.Port));

    public Task UseGameSignIn() => Relink(s => accounts.UseGame(s.Host, s.Port));

    async Task Relink(Action<ServerEntry> change)
    {
        if (Server is not { } s) return;
        change(s);
        if (link is { } old)
        {
            link = null;
            await old.DisposeAsync();
        }
        await UseAsync(s);
    }

    public Task<JsonNode?> Stats(string query) => Client is { } c ? c.AskStatsAsync(query) : Task.FromResult<JsonNode?>(null);
    public Task<JsonNode?> Friends(string action, string who = "") => Client is { } c ? c.FriendsAsync(action, who) : Task.FromResult<JsonNode?>(null);
    public Task<SignInLink> StartSignIn() => Client is { } c ? c.StartSignInAsync() : Task.FromResult(new SignInLink(false, "", "", "Not connected to the server."));
    public Task<SignInResult?> WaitSignIn(TimeSpan timeout) => Client is { } c ? c.WaitSignInAsync(timeout) : Task.FromResult<SignInResult?>(null);
    public Task<ReplayDownload> Replay(string code) => Client is { } c ? c.DownloadReplayAsync(code) : Task.FromResult(NotConnected);
    public Task<ReplayDownload> Replay(uint room, uint seed) => Client is { } c ? c.DownloadReplayAsync(room, seed) : Task.FromResult(NotConnected);
    public Task<JsonNode?> Tournament(string action, JsonObject args) => Client is { } c ? c.TournamentActAsync(action, args) : Task.FromResult<JsonNode?>(null);
    public Task<CupBoard?> Cups() => Client is { } c ? c.CupsAsync() : Task.FromResult<CupBoard?>(null);
    public Task<CupBoard?> CupSignUp(uint cup, bool on) => Client is { } c ? c.CupSignUpAsync(cup, on) : Task.FromResult<CupBoard?>(null);
    public Task<IReadOnlyList<CupBracketRow>?> CupBrackets(uint cup) => Client is { } c ? c.CupBracketsAsync(cup) : Task.FromResult<IReadOnlyList<CupBracketRow>?>(null);
    public Task<CupBracketView?> CupBracket(uint cup, uint bracket) => Client is { } c ? c.CupBracketAsync(cup, bracket) : Task.FromResult<CupBracketView?>(null);
    public Task<ModListing?> ModLibrary() => Client is { } c ? c.ModLibraryAsync() : Task.FromResult<ModListing?>(null);
    public Task<ModListing?> ModByKey(string key) => Client is { } c ? c.ModByKeyAsync(key) : Task.FromResult<ModListing?>(null);
    public Task<ModListing?> MyMods() => Client is { } c ? c.MyModsAsync() : Task.FromResult<ModListing?>(null);
    public Task<ModListing?> SetMod(string modId, string action) => Client is { } c ? c.SetModAsync(modId, action) : Task.FromResult<ModListing?>(null);
    public Task<(byte[]? Data, string Problem)> DownloadMod(ModListing listing, string entry) =>
        Client is { } c ? c.DownloadModAsync(listing.WebPort, listing.Ticket, entry) : Task.FromResult<(byte[]?, string)>((null, "not connected to the server"));
    public Task<ModShareAnswer?> ShareMod(string entry, bool makePublic) => Client is { } c ? c.ShareModAsync(entry, makePublic) : Task.FromResult<ModShareAnswer?>(null);
    public Task<string?> UploadMod(ModUploadRequest request, string entry, byte[] package) =>
        Client is { } c ? c.UploadModAsync(request.WebPort, request.Ticket, entry, package) : Task.FromResult<string?>("not connected to the server");
    public Task<byte[]?> ModPreview(ModListing listing, string entry) =>
        Client is { } c && listing.Ticket.Length > 0 ? c.ModPreviewAsync(listing.WebPort, listing.Ticket, entry) : Task.FromResult<byte[]?>(null);

    public async ValueTask DisposeAsync()
    {
        if (link is { } l) await l.DisposeAsync();
        link = null;
    }

    public static IReadOnlyList<ServerEntry> KnownServers(GameSettings settings)
    {
        var all = new List<ServerEntry>();
        void Add(ServerEntry? e)
        {
            if (e != null && !all.Any(x => x.Same(e.Host, e.Port))) all.Add(e);
        }
        Add(settings.Main());
        foreach (var s in settings.Servers()) Add(s);
        Add(settings.Current());
        return all;
    }

    public static string? SaveReplay(ReplayDownload download, string? replaysDir, out string? problem)
    {
        problem = null;
        if (!download.Found) { problem = download.Message; return null; }
        if (string.IsNullOrEmpty(replaysDir)) { problem = "Couldn't find the game's replay folder. Start the game once, then try again."; return null; }
        try
        {
            Directory.CreateDirectory(replaysDir);
            var name = BifrostClient.SafeFileName(download.Name);
            var path = Path.Combine(replaysDir, name);
            for (var i = 2; File.Exists(path); i++)
            {
                if (new FileInfo(path).Length == download.Data.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(download.Data)) return path;
                path = Path.Combine(replaysDir, Path.GetFileNameWithoutExtension(name) + $" ({i}).replay");
            }
            File.WriteAllBytes(path, download.Data);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = e.Message;
            return null;
        }
    }

    public static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    public static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }

    public static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static DateTime? When(JsonNode? n) => Num(n) is { } t && t > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds((long)(t > 1e12 ? t : t * 1000)).LocalDateTime : null;

    public static string Ago(DateTime? when, DateTime now)
    {
        if (when is not { } w) return "";
        var d = now - w;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} h ago";
        if (d < TimeSpan.FromDays(30)) return $"{(int)d.TotalDays} d ago";
        return w.ToString("yyyy-MM-dd");
    }

    public static string Rating(JsonNode? rating)
    {
        if (rating is not JsonObject r || Num(r["rating"]) is not { } value) return "Unrated";
        var tier = Text(r["tier"]);
        var games = Num(r["games"]) ?? 0;
        var need = Num(r["placement"]) ?? 10;
        if (games < need) return $"Placement {games:0}/{need:0}";
        return (tier != null && tier != "Unranked" ? tier + " " : "") + value.ToString("0");
    }

    public static string Record(JsonNode? row)
    {
        var wins = Num(row?["wins"]) ?? 0;
        var matches = Num(row?["matches"]) ?? 0;
        return matches == 0 ? "no matches" : $"{wins:0}W {matches - wins:0}L ({wins / matches:P0})";
    }
}
