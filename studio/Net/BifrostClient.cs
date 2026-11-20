using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace BifrostStudio.Net;

public sealed record LoginResult(bool Ok, bool Companion, string Message);

public sealed record SignInLink(bool Ok, string Link, string Code, string Message);

public sealed record SignInResult(bool Ok, string Token, string Account, string Message);

public sealed record ModListing(int Type, uint WebPort, string Ticket, JsonNode? Body)
{
    public string? Error => Body?["error"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public string? Message => Body?["message"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}

public sealed record ModUploadRequest(uint WebPort, string Ticket, IReadOnlyList<string> Entries);

public sealed record ModShareAnswer(ModUploadRequest? Upload, ModListing? Mine);

public sealed record ReplayModsOffer(uint WebPort, string Ticket, IReadOnlyList<string> Mods);

public sealed record ReplayDownload(bool Found, byte[] Data, string Name, IReadOnlyList<string> Mods, ReplayModsOffer? Offer, string Message);

public sealed record NewsNotice(string Key, string Title, string Text);

public sealed record Cup(uint Id, string Name, bool Official, int State, DateTime? When, uint Size, bool Double, int Seeding, uint Organizer, uint Art);

public sealed record CupBoard(IReadOnlyList<Cup> Cups, IReadOnlySet<uint> Registered);

public sealed record CupBracketRow(uint Id, uint Rating, string First, string Second, string Third, uint State);

public sealed record CupSide(uint Entrant, IReadOnlyList<uint> Users, string Name, uint Seed);

public sealed record CupSet(uint Id, uint Sub, uint Round, uint Position, uint Result, uint ScoreA, uint ScoreB, CupSide? A, CupSide? B, uint Winner);

public sealed record CupBracketView(uint Id, uint Rating, uint State, IReadOnlyList<CupSet> Sets);

public sealed class BifrostClient : IAsyncDisposable
{
    public const string Protocol = "Brawlhalla client to server protocol 1.0";
    public const int Hello = 178, Version = 30, Challenge = 12000, Login = 20, LoginOk = 2431, KeepAlive = 12100;
    public const int LinkToken = 11001, Notice = 11013, Kicked = 12002, Companion = 11100;
    public const int SignInStart = 11007, SignInLinkPacket = 11008, SignInResultPacket = 11009, SignInNeededPacket = 11010;
    public const int StatsAsk = 11042, StatsAnswer = 11043, TournamentAct = 11047, FriendsCommand = 11052, FriendsState = 11053;
    public const int ReplayGet = 11062, ReplayData = 11063, ReplayMods = 11064, ReplayGetCode = 11067;
    public const int ModUpload = 11015, LibraryAsk = 11070, Library = 11071, ModShare = 11072;
    public const int ModKeyAsk = 11084, ModKey = 11085, MineAsk = 11086, Mine = 11087, ModSet = 11088;
    public const int WorkshopAsk = 11089, WorkshopShelf = 11102;
    public const int Perks = 11090, WebScheme = 11093, News = 11096, ModerationAsk = 11097, ModerationAnswer = 11098, ModeratorState = 11099;
    public const int CupListAsk = 188, CupHistoryAsk = 191, CupBracketAsk = 192, CupRegister = 193, CupUnregister = 194;
    public const int CupList = 2950, CupRegistrations = 2953, CupBrackets = 2954, CupBracket = 2955;
    public const int CupOpen = 0, CupRunning = 1, CupDone = 2, MaxCupRows = 4096;
    public const uint GameVersion = 1100049000;
    public const int MaxReplay = 8 * 1024 * 1024, MaxMods = 60, MaxActionLength = 16, MaxModKey = 40;
    public const string KickedMessage = "The server signed you out.";
    public static readonly TimeSpan KeepAliveEvery = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ReplayTimeout = TimeSpan.FromSeconds(90);
    public static readonly string[] ModActions = ["like", "unlike", "public", "private", "delete", "approve", "reject", "feature", "unfeature"];
    public static readonly string[] WorkshopActions = ["list", "save", "delete", "add", "remove", "follow", "unfollow", "seen", "creator"];

    sealed class Waiter(int[] types, Func<Packet, bool> match)
    {
        public readonly int[] Types = types;
        public readonly Func<Packet, bool> Match = match;
        public readonly TaskCompletionSource<Packet> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly TcpClient tcp = new() { NoDelay = true };
    readonly PacketDecoder decoder = new();
    readonly SemaphoreSlim sending = new(1, 1);
    readonly SemaphoreSlim replays = new(1, 1);
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<string>> stats = new();
    readonly Dictionary<string, (int Count, string?[] Parts)> partial = [];
    readonly ConcurrentQueue<TaskCompletionSource<string>> friends = new();
    readonly List<Waiter> waiters = [];
    readonly TaskCompletionSource<Packet> challenge = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<Packet> companionAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<Packet> loginOk = new(TaskCreationOptions.RunContinuationsAsynchronously);
    NetworkStream? stream;
    Task? reader, pinger;
    DateTime loggedIn = DateTime.Now;
    IReadOnlyList<Cup> cupList = [];
    IReadOnlySet<uint> cupsRegistered = new HashSet<uint>();

    public event Action<string>? Noticed;
    public event Action<string>? Closed;
    public event Action<Packet>? Received;
    public event Action<SignInResult>? SignedIn;
    public event Action<NewsNotice>? NewsArrived;

    public bool Connected => tcp.Connected && !stop.IsCancellationRequested;
    public bool IsCompanion { get; private set; }
    public bool WasKicked { get; private set; }
    public bool SignInNeeded { get; private set; }
    public bool IsAdmin { get; private set; }
    public bool IsDeveloper { get; private set; }
    public string Scheme { get; private set; } = "http";
    public string? Token { get; private set; }
    public string Account { get; private set; } = "";
    public string Host { get; private set; } = "";

    public async Task<LoginResult> ConnectAsync(string host, int port, string app, string installId, string? token = null, CancellationToken cancel = default)
    {
        Host = host;
        try
        {
            await tcp.ConnectAsync(host, port, cancel);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return new LoginResult(false, false, "Can't reach the server. " + e.Message);
        }
        stream = tcp.GetStream();
        reader = Task.Run(ReadLoop);
        await SendAsync(new Packet(Hello, new BitWriter().String(Protocol).ToArray()));
        await SendAsync(new Packet(Version, new BitWriter().UInt(GameVersion).UInt(0).ToArray()));
        if (await Within(challenge, AnswerTimeout, cancel) == null) return await Fail("The server didn't answer.");
        if (!string.IsNullOrEmpty(token))
        {
            Token = token;
            await SendAsync(new Packet(LinkToken, new BitWriter().String(token).ToArray()));
        }
        await SendAsync(new Packet(Companion, new BitWriter().String(app).String(installId).ToArray()));
        await SendAsync(new Packet(Login, new BitWriter().String("").String("").String("").String(app).UInt(0).String("").Byte(0).UInt(GameVersion).ToArray()));
        if (await Within(loginOk, AnswerTimeout, cancel) == null) return await Fail("The server didn't answer the login.");
        pinger = Task.Run(PingLoop);
        if (companionAnswer.Task.IsCompleted)
        {
            var r = new BitReader(companionAnswer.Task.Result.Payload);
            IsCompanion = r.Bool();
            return new LoginResult(true, IsCompanion, IsCompanion ? "" : SafeRest(r));
        }
        return new LoginResult(true, false, "This server is older, so connecting here signs your game out.");
    }

    static string SafeRest(BitReader r)
    {
        try
        {
            return r.String();
        }
        catch (IndexOutOfRangeException)
        {
            return "";
        }
    }

    async Task<T?> Within<T>(TaskCompletionSource<T> t, TimeSpan timeout, CancellationToken cancel) where T : class
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancel, stop.Token);
        var done = await Task.WhenAny(t.Task, Task.Delay(timeout, either.Token).ContinueWith(_ => { }, TaskScheduler.Default));
        return done == t.Task ? await t.Task : null;
    }

    async Task<LoginResult> Fail(string why)
    {
        await DisposeAsync();
        return new LoginResult(false, false, why);
    }

    public async Task SendAsync(Packet p)
    {
        if (stream == null) throw new InvalidOperationException("not connected");
        var bytes = p.Encode();
        await sending.WaitAsync();
        try
        {
            await stream.WriteAsync(bytes);
        }
        finally
        {
            sending.Release();
        }
    }

    async Task<bool> TrySendAsync(Packet p)
    {
        try
        {
            await SendAsync(p);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or SocketException)
        {
            return false;
        }
    }

    async Task<Packet?> AskAsync(Packet? send, int[] answers, Func<Packet, bool>? match, TimeSpan timeout, CancellationToken cancel)
    {
        var w = new Waiter(answers, match ?? (_ => true));
        lock (waiters) waiters.Add(w);
        try
        {
            if (send != null && !await TrySendAsync(send)) return null;
            return await Within(w.Done, timeout, cancel);
        }
        finally
        {
            lock (waiters) waiters.Remove(w);
        }
    }

    async Task<JsonNode?> StatsAfterAsync(string query, Packet send, CancellationToken cancel)
    {
        var t = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        stats[query] = t;
        try
        {
            if (!await TrySendAsync(send)) return null;
            return Parse(await Within(t, AnswerTimeout, cancel));
        }
        finally
        {
            stats.TryRemove(new KeyValuePair<string, TaskCompletionSource<string>>(query, t));
        }
    }

    public Task<JsonNode?> AskStatsAsync(string query, CancellationToken cancel = default) =>
        StatsAfterAsync(query, new Packet(StatsAsk, new BitWriter().String(query).ToArray()), cancel);

    public async Task<JsonNode?> TournamentActAsync(string action, JsonObject? args = null, CancellationToken cancel = default)
    {
        if (action.Length == 0 || action.Length > MaxActionLength) throw new ArgumentException("an action has 1 to 16 characters", nameof(action));
        var body = (args ?? []).ToJsonString();
        return await StatsAfterAsync("tournament-act", new Packet(TournamentAct, new BitWriter().String(action).String(body).ToArray()), cancel);
    }

    public async Task<JsonNode?> FriendsAsync(string action, string who = "", CancellationToken cancel = default)
    {
        var t = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        friends.Enqueue(t);
        if (!await TrySendAsync(new Packet(FriendsCommand, new BitWriter().String(action).String(who).ToArray()))) return null;
        return Parse(await Within(t, AnswerTimeout, cancel));
    }

    public async Task<SignInLink> StartSignInAsync(CancellationToken cancel = default)
    {
        var p = await AskAsync(new Packet(SignInStart), [SignInLinkPacket], null, AnswerTimeout, cancel);
        if (p == null) return new SignInLink(false, "", "", "The server didn't answer the sign-in.");
        try
        {
            var r = new BitReader(p.Payload);
            var ok = r.Bool();
            var text = r.String();
            if (!ok) return new SignInLink(false, "", "", text);
            var code = r.String();
            return IsDiscordLink(text)
                ? new SignInLink(true, text, code, "")
                : new SignInLink(false, "", "", "The server's sign-in link isn't on discord.com, so it wasn't opened.");
        }
        catch (IndexOutOfRangeException)
        {
            return new SignInLink(false, "", "", "The server's sign-in answer couldn't be read.");
        }
    }

    public static bool IsDiscordLink(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase));

    public async Task<SignInResult?> WaitSignInAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        var p = await AskAsync(null, [SignInResultPacket], null, timeout, cancel);
        return p == null ? null : ReadSignIn(p);
    }

    static SignInResult ReadSignIn(Packet p)
    {
        try
        {
            var r = new BitReader(p.Payload);
            var ok = r.Bool();
            var text = r.String();
            var name = r.String();
            return ok ? new SignInResult(true, text, name, "") : new SignInResult(false, "", "", text);
        }
        catch (IndexOutOfRangeException)
        {
            return new SignInResult(false, "", "", "the server's sign-in result was unreadable");
        }
    }

    static ModListing? ReadListing(Packet? p)
    {
        if (p == null) return null;
        try
        {
            var r = new BitReader(p.Payload);
            var web = r.UInt();
            var ticket = r.String();
            return new ModListing(p.Type, web, ticket, Parse(r.String()));
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    public async Task<ModListing?> ModLibraryAsync(CancellationToken cancel = default) =>
        ReadListing(await AskAsync(new Packet(LibraryAsk), [Library], null, AnswerTimeout, cancel));

    public async Task<ModListing?> ModByKeyAsync(string key, CancellationToken cancel = default)
    {
        key = key.Trim();
        if (key.Length == 0 || key.Length > MaxModKey) throw new ArgumentException("a mod key has 1 to 40 characters", nameof(key));
        return ReadListing(await AskAsync(new Packet(ModKeyAsk, new BitWriter().String(key).ToArray()), [ModKey], null, AnswerTimeout, cancel));
    }

    public async Task<ModListing?> MyModsAsync(CancellationToken cancel = default) =>
        ReadListing(await AskAsync(new Packet(MineAsk), [Mine], null, AnswerTimeout, cancel));

    public async Task<ModListing?> SetModAsync(string modId, string action, string note = "", CancellationToken cancel = default)
    {
        if (!ModActions.Contains(action)) throw new ArgumentException("not a mod action: " + action, nameof(action));
        var send = new Packet(ModSet, new BitWriter().String(modId).String(action).String(note).ToArray());
        return ReadListing(await AskAsync(send, [Library, Mine], null, AnswerTimeout, cancel));
    }

    public async Task<ModListing?> WorkshopAsync(string action, JsonObject? args = null, CancellationToken cancel = default)
    {
        if (!WorkshopActions.Contains(action)) throw new ArgumentException("not a workshop action: " + action, nameof(action));
        var send = new Packet(WorkshopAsk, new BitWriter().String(action).String((args ?? new JsonObject()).ToJsonString()).ToArray());
        return ReadListing(await AskAsync(send, [WorkshopShelf], null, AnswerTimeout, cancel));
    }

    public async Task<ModShareAnswer?> ShareModAsync(string entry, bool makePublic, CancellationToken cancel = default)
    {
        var send = new Packet(ModShare, new BitWriter().UInt(1).String(entry).UInt(makePublic ? 1u : 0u).ToArray());
        var p = await AskAsync(send, [ModUpload, Mine], null, AnswerTimeout, cancel);
        if (p == null) return null;
        if (p.Type == Mine) return new ModShareAnswer(null, ReadListing(p));
        try
        {
            var r = new BitReader(p.Payload);
            var web = r.UInt();
            var ticket = r.String();
            var n = r.UInt();
            if (n > MaxMods) return null;
            var entries = new List<string>();
            for (var i = 0; i < n; i++) entries.Add(r.String());
            return new ModShareAnswer(new ModUploadRequest(web, ticket, entries), null);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    public async Task<JsonNode?> ModerateAsync(string action, JsonObject? args = null, CancellationToken cancel = default)
    {
        var send = new Packet(ModerationAsk, new BitWriter().String(action).String((args ?? []).ToJsonString()).ToArray());
        JsonNode? answer = null;
        var p = await AskAsync(send, [ModerationAnswer], q =>
        {
            var node = Parse(SafeString(q.Payload));
            if (node?["action"] is not JsonValue v || !v.TryGetValue<string>(out var a) || a != action) return false;
            answer = node;
            return true;
        }, AnswerTimeout, cancel);
        return p == null ? null : answer;
    }

    public Task<ReplayDownload> DownloadReplayAsync(string code, CancellationToken cancel = default) =>
        DownloadAsync(new Packet(ReplayGetCode, new BitWriter().String(code.Trim()).ToArray()), 0, 0, cancel);

    public Task<ReplayDownload> DownloadReplayAsync(uint room, uint seed, CancellationToken cancel = default) =>
        DownloadAsync(new Packet(ReplayGet, new BitWriter().UInt(room).UInt(seed).ToArray()), room, seed, cancel);

    public CupBoard Cups => new(cupList, cupsRegistered);

    public async Task<CupBoard?> CupsAsync(CancellationToken cancel = default) =>
        await AskAsync(new Packet(CupListAsk), [CupRegistrations], null, AnswerTimeout, cancel) == null ? null : Cups;

    public async Task<CupBoard?> CupSignUpAsync(uint cup, bool on, CancellationToken cancel = default) =>
        await AskAsync(new Packet(on ? CupRegister : CupUnregister, new BitWriter().UInt(cup).ToArray()), [CupRegistrations], null, AnswerTimeout, cancel) == null ? null : Cups;

    public async Task<IReadOnlyList<CupBracketRow>?> CupBracketsAsync(uint cup, CancellationToken cancel = default)
    {
        var p = await AskAsync(new Packet(CupHistoryAsk, new BitWriter().UInt(cup).ToArray()), [CupBrackets], null, AnswerTimeout, cancel);
        return p == null ? null : ReadBrackets(p.Payload);
    }

    public async Task<CupBracketView?> CupBracketAsync(uint cup, uint bracket, CancellationToken cancel = default)
    {
        var p = await AskAsync(new Packet(CupBracketAsk, new BitWriter().UInt(cup).UInt(bracket).ToArray()), [CupBracket], q => FirstUInt(q.Payload) == bracket, AnswerTimeout, cancel);
        return p == null ? null : ReadBracket(p.Payload);
    }

    static uint? FirstUInt(byte[] payload)
    {
        try
        {
            return new BitReader(payload).UInt();
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    public static IReadOnlyList<Cup>? ReadCups(byte[] payload, DateTime loggedIn)
    {
        try
        {
            var r = new BitReader(payload);
            var list = new List<Cup>();
            while (r.Bool() && list.Count < MaxCupRows)
            {
                var id = r.UInt();
                var name = r.String();
                var official = r.Bool();
                var state = r.Byte();
                var seconds = r.UInt();
                r.UInt();
                r.Byte();
                var size = r.UInt();
                var format = r.Byte();
                r.Byte();
                var seeding = r.Byte();
                r.UInt();
                var organizer = r.UInt();
                r.UInt();
                r.UInt();
                var art = r.UInt();
                list.Add(new Cup(id, name, official, state, state != CupDone && seconds > 0 ? loggedIn.AddSeconds(seconds) : null, size, format == 1, seeding, organizer, art));
            }
            return list;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    public static IReadOnlySet<uint>? ReadRegistrations(byte[] payload)
    {
        try
        {
            var r = new BitReader(payload);
            var set = new HashSet<uint>();
            for (var n = 0; r.Bool() && n < MaxCupRows; n++)
            {
                var id = r.UInt();
                if (r.Bool()) set.Add(id);
            }
            return set;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    public static IReadOnlyList<CupBracketRow>? ReadBrackets(byte[] payload)
    {
        try
        {
            var r = new BitReader(payload);
            var list = new List<CupBracketRow>();
            while (r.Bool() && list.Count < MaxCupRows)
                list.Add(new CupBracketRow(r.UInt(), r.UInt(), r.String(), r.String(), r.String(), r.UInt()));
            return list;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    public static CupBracketView? ReadBracket(byte[] payload)
    {
        try
        {
            var r = new BitReader(payload);
            var id = r.UInt();
            var rating = r.UInt();
            var state = r.UInt();
            var sets = new List<CupSet>();
            while (r.Bool() && sets.Count < MaxCupRows)
            {
                var setId = r.UInt();
                var sub = r.UInt();
                var round = r.UInt();
                var pos = r.UInt();
                var result = r.UInt();
                var a = r.UInt();
                var b = r.UInt();
                r.UInt();
                var sideA = ReadSide(r);
                var sideB = ReadSide(r);
                sets.Add(new CupSet(setId, sub, round, pos, result, a, b, sideA, sideB, r.UInt()));
            }
            return new CupBracketView(id, rating, state, sets);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    static CupSide? ReadSide(BitReader r)
    {
        if (!r.Bool()) return null;
        var entrant = r.UInt();
        var users = new List<uint>();
        while (r.Bool() && users.Count < MaxCupRows) users.Add(r.UInt());
        return new CupSide(entrant, users, r.String(), r.UInt());
    }

    static List<string>? ReadMods(BitReader r)
    {
        var n = r.UInt();
        if (n > MaxMods) return null;
        var mods = new List<string>();
        for (var i = 0; i < n; i++) mods.Add(r.String());
        return mods;
    }

    async Task<ReplayDownload> DownloadAsync(Packet send, uint room, uint seed, CancellationToken cancel)
    {
        await replays.WaitAsync(cancel);
        try
        {
            byte[]? data = null;
            var next = 0;
            var name = "";
            IReadOnlyList<string> mods = [];
            ReplayModsOffer? offer = null;
            string? problem = null;
            var p = await AskAsync(send, [ReplayData, ReplayMods], q =>
            {
                try
                {
                    var r = new BitReader(q.Payload);
                    if (r.UInt() != room || r.UInt() != seed) return false;
                    if (q.Type == ReplayMods)
                    {
                        var web = r.UInt();
                        var ticket = r.String();
                        offer = new ReplayModsOffer(web, ticket, ReadMods(r) ?? []);
                        return false;
                    }
                    var total = r.UInt();
                    var offset = r.UInt();
                    var length = r.UInt();
                    if (total == 0) return true;
                    if (total > MaxReplay) problem = "The replay is larger than 8 MB.";
                    else if (offset != next || (long)offset + length > total) problem = "The replay arrived out of order. Try again.";
                    if (problem != null) return true;
                    data ??= new byte[total];
                    r.Raw((int)length).CopyTo(data, offset);
                    next = (int)(offset + length);
                    name = r.String();
                    mods = ReadMods(r) ?? [];
                    return next == total;
                }
                catch (IndexOutOfRangeException)
                {
                    problem = "The replay couldn't be read.";
                    return true;
                }
            }, ReplayTimeout, cancel);
            if (p == null) return new ReplayDownload(false, [], "", [], offer, data == null ? "The server didn't answer." : "The replay stopped downloading partway. Try again.");
            if (problem != null) return new ReplayDownload(false, [], "", [], offer, problem);
            if (data == null) return new ReplayDownload(false, [], "", [], offer, "The server has no replay for that.");
            return new ReplayDownload(true, data, SafeFileName(name), mods, offer, "");
        }
        finally
        {
            replays.Release();
        }
    }

    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(Path.GetFileName(name.Replace('/', '\\')).Where(c => !bad.Contains(c)).ToArray()).Trim().TrimStart('.');
        if (clean.Length == 0) clean = "Downloaded.replay";
        if (!clean.EndsWith(".replay", StringComparison.OrdinalIgnoreCase)) clean += ".replay";
        return clean;
    }

    static JsonNode? Parse(string? text)
    {
        if (text == null) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static (int Part, int Count, string Body)? SplitPart(string body)
    {
        if (body.Length < 5 || body[0] != '\x01') return null;
        var end = body.IndexOf('\x01', 1);
        if (end < 0) return null;
        var head = body[1..end].Split(' ');
        if (head.Length != 2 || !int.TryParse(head[0], out var k) || !int.TryParse(head[1], out var n) || n < 1 || k < 1 || k > n) return null;
        return (k, n, body[(end + 1)..]);
    }

    static bool FirstBool(byte[] payload)
    {
        try
        {
            return new BitReader(payload).Bool();
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    void OnPacket(Packet p)
    {
        Received?.Invoke(p);
        try
        {
            Track(p);
        }
        finally
        {
            Waiter[] now;
            lock (waiters) now = waiters.Where(w => w.Types.Contains(p.Type)).ToArray();
            foreach (var w in now)
                if (!w.Done.Task.IsCompleted && w.Match(p)) w.Done.TrySetResult(p);
        }
    }

    void Track(Packet p)
    {
        switch (p.Type)
        {
            case Challenge:
                challenge.TrySetResult(p);
                return;
            case Companion:
                companionAnswer.TrySetResult(p);
                return;
            case LoginOk:
                loggedIn = DateTime.Now;
                loginOk.TrySetResult(p);
                return;
            case CupList:
                if (ReadCups(p.Payload, loggedIn) is { } list) cupList = list;
                return;
            case CupRegistrations:
                if (ReadRegistrations(p.Payload) is { } regs) cupsRegistered = regs;
                return;
            case KeepAlive:
                return;
            case Notice:
                Noticed?.Invoke(SafeString(p.Payload));
                return;
            case Kicked:
                WasKicked = true;
                Closed?.Invoke(KickedMessage);
                stop.Cancel();
                return;
            case SignInNeededPacket:
                SignInNeeded = true;
                return;
            case SignInResultPacket:
            {
                var s = ReadSignIn(p);
                if (s.Ok)
                {
                    Token = s.Token;
                    Account = s.Account;
                    SignInNeeded = false;
                }
                SignedIn?.Invoke(s);
                return;
            }
            case ModeratorState:
                IsAdmin = FirstBool(p.Payload);
                return;
            case Perks:
                IsDeveloper = FirstBool(p.Payload);
                return;
            case WebScheme:
                Scheme = SafeString(p.Payload) == "https" ? "https" : "http";
                return;
            case News:
                try
                {
                    var r = new BitReader(p.Payload);
                    NewsArrived?.Invoke(new NewsNotice(r.String(), r.String(), r.String()));
                }
                catch (IndexOutOfRangeException)
                {
                }
                return;
            case StatsAnswer:
            {
                var r = new BitReader(p.Payload);
                var query = r.String();
                var body = r.String();
                string? whole = body;
                if (SplitPart(body) is { } part)
                {
                    lock (partial)
                    {
                        if (!partial.TryGetValue(query, out var got) || got.Count != part.Count) got = (part.Count, new string?[part.Count]);
                        got.Parts[part.Part - 1] = part.Body;
                        partial[query] = got;
                        whole = got.Parts.All(x => x != null) ? string.Concat(got.Parts) : null;
                        if (whole != null) partial.Remove(query);
                    }
                }
                if (whole != null && stats.TryGetValue(query, out var t)) t.TrySetResult(whole);
                return;
            }
            case FriendsState:
                if (friends.TryDequeue(out var f)) f.TrySetResult(SafeString(p.Payload));
                return;
        }
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public Uri? ModLink(uint webPort, string path, string ticket, string entry) =>
        WebLink(webPort, path + "?ticket=" + Uri.EscapeDataString(ticket) + "&entry=" + Uri.EscapeDataString(entry));

    public async Task<(byte[]? Data, string Problem)> DownloadModAsync(uint webPort, string ticket, string entry, CancellationToken cancel = default)
    {
        if (ModLink(webPort, "/mods/get", ticket, entry) is not { } url) return (null, "the server gave no web address for mods");
        try
        {
            using var answer = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!answer.IsSuccessStatusCode) return (null, answer.StatusCode == System.Net.HttpStatusCode.NotFound ? "the server doesn't have it any more" : $"the server said {(int)answer.StatusCode}");
            if (answer.Content.Headers.ContentLength > ModPackageLimit) return (null, "it's larger than 64 MB");
            await using var body = await answer.Content.ReadAsStreamAsync(cancel);
            using var all = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buf, cancel)) > 0)
            {
                all.Write(buf, 0, n);
                if (all.Length > ModPackageLimit) return (null, "it's larger than 64 MB");
            }
            return (all.ToArray(), "");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            return (null, "no answer from the server's web side (" + e.Message + ")");
        }
    }

    public async Task<string?> UploadModAsync(uint webPort, string ticket, string entry, byte[] package, CancellationToken cancel = default)
    {
        if (ModLink(webPort, "/mods/upload", ticket, entry) is not { } url) return "the server gave no web address for mods";
        if (package.Length == 0 || package.Length > ModPackageLimit) return "it's empty or larger than 64 MB";
        try
        {
            using var content = new ByteArrayContent(package);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var answer = await Http.PostAsync(url, content, cancel);
            if (answer.IsSuccessStatusCode) return null;
            var text = await answer.Content.ReadAsStringAsync(cancel);
            return Parse(text)?["error"] is JsonValue v && v.TryGetValue<string>(out var why) && why.Length > 0
                ? "the server refused it: " + why
                : $"the server said {(int)answer.StatusCode}";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            return "no answer from the server's web side (" + e.Message + ")";
        }
    }

    public async Task<byte[]?> ModPreviewAsync(uint webPort, string ticket, string entry, CancellationToken cancel = default)
    {
        if (ModLink(webPort, "/mods/preview", ticket, entry) is not { } url) return null;
        try
        {
            using var answer = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!answer.IsSuccessStatusCode || answer.Content.Headers.ContentLength > ModPreviewLimit) return null;
            await using var body = await answer.Content.ReadAsStreamAsync(cancel);
            using var all = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buf, cancel)) > 0)
            {
                all.Write(buf, 0, n);
                if (all.Length > ModPreviewLimit) return null;
            }
            var data = all.ToArray();
            return ModPackage.IsPicture(data) ? data : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
    }

    public const long ModPackageLimit = 64L * 1024 * 1024, ModPreviewLimit = 4L * 1024 * 1024;

    public Uri? WebLink(uint webPort, string path)
    {
        if (webPort == 0 || webPort > 65535 || Host.Length == 0) return null;
        return Uri.TryCreate($"{Scheme}://{Host}:{webPort}/{path.TrimStart('/')}", UriKind.Absolute, out var u) ? u : null;
    }

    static string SafeString(byte[] payload)
    {
        try
        {
            return new BitReader(payload).String();
        }
        catch (IndexOutOfRangeException)
        {
            return "";
        }
    }

    async Task ReadLoop()
    {
        var buf = new byte[16384];
        var why = "The server closed the connection.";
        try
        {
            while (!stop.IsCancellationRequested && stream != null)
            {
                var n = await stream.ReadAsync(buf, stop.Token);
                if (n <= 0) break;
                foreach (var p in decoder.Feed(buf.AsSpan(0, n)))
                {
                    try
                    {
                        OnPacket(p);
                    }
                    catch (IndexOutOfRangeException)
                    {
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            why = "closed";
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            why = "The connection broke. " + e.Message;
        }
        if (!stop.IsCancellationRequested) Closed?.Invoke(why);
        stop.Cancel();
    }

    async Task PingLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(KeepAliveEvery, stop.Token);
                await SendAsync(new Packet(KeepAlive));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try
        {
            tcp.Close();
        }
        catch (SocketException)
        {
        }
        foreach (var t in new[] { reader, pinger })
        {
            if (t == null) continue;
            try
            {
                await t;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }
    }
}
