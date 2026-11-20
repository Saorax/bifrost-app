using System.Text.Json.Nodes;

namespace BifrostStudio;

public enum ReplayFolder { Recent, Favorites, Older, Bifrost }

public sealed record ReplayPlayer(string Name, IReadOnlyList<int> Legends, int Pid, int Team, int Place)
{
    public IReadOnlyList<int> Skins { get; init; } = [];
    public int Avatar { get; init; }
    public int Colour { get; init; }
    public bool Bot { get; init; }
    public ReplayInputs Inputs { get; init; } = ReplayInputs.None;
}

public sealed record ReplayEntry(
    string Key, string File, string Path, ReplayFolder Folder, DateTime Time, bool Indexed, bool Ok, string Why,
    string Map, string MapName, string Mode, string Playlist, bool Ranked, bool Online, bool Teams, int LengthMs,
    IReadOnlyList<ReplayPlayer> Players, IReadOnlyList<(int Player, int Ms)> Kos)
{
    public int Me { get; init; } = -1;
    public int Level { get; init; }
    public string Scoring { get; init; } = "";
    public int Lives { get; init; }
    public int Duration { get; init; }
    public int Version { get; init; }
    public int Winner { get; init; } = -1;

    public string Result => Me < 0 || Players.Count <= Me ? "" : Players[Me].Place == 0 ? (Winner < 0 ? "" : SameSide(Me, Winner) ? "win" : "loss")
        : Players[Me].Place == 1 ? "win" : "loss";

    bool SameSide(int a, int b) => a == b || Teams && Players[a].Team == Players[b].Team;

    public string Size => Teams ? (Players.Count == 4 ? "2v2" : Players.Count / 2 + "v" + Players.Count / 2) : Players.Count == 2 ? "1v1" : Players.Count + " players";

    public string StatsName => System.Text.RegularExpressions.Regex.Replace(
        File.EndsWith(".replay", StringComparison.OrdinalIgnoreCase) ? File[..^7] : File, "[\\\\/:*?\"<>|]", "_");
}

public sealed record ReplayVideo(string Name, string Path, DateTime Time, long Bytes, string? Replay);

public sealed record SavedMod(string Id, string Name, string Version, bool Live, string Print);

public sealed record ReplayNote(string Note, IReadOnlyList<string> Tags)
{
    public static readonly ReplayNote Empty = new("", []);
    public bool IsEmpty => Note.Length == 0 && Tags.Count == 0;
}

public sealed record ReplayFilter(
    ReplayFolder? Folder = null, string? Result = null, string? Size = null, string? Map = null, int? Legend = null,
    string? Opponent = null, string? Mode = null, string? Tag = null, string? Text = null);

public sealed class ReplayLibrary(AppState state, ReplayCatalog? catalog = null)
{
    public const string NotesFile = "replay-notes.json";
    public const string StatsFolder = "replay-stats";
    public const string FramesFolder = "replay-frames";

    public const string VideosFolder = "Videos";
    public const string FavoritesFolder = "Favorites";
    public const string OlderFolder = "Older";
    public const string BifrostFolder = "Bifrost";

    readonly ReplayCatalog catalog = catalog ?? new ReplayCatalog(ReplayCatalog.DefaultFile);

    public string? Replays => state.Paths?.Replays;
    public string? GameReplays => state.Paths?.GameReplays ?? Replays;
    public string? NotesPath => state.Paths?.DocsFile(NotesFile);

    public const string ModsFile = "replay-mods.json";

    public string? ModsPath => state.Paths?.DocsFile(ModsFile);

    (string Path, DateTime Time, Dictionary<string, IReadOnlyList<SavedMod>> Byname)? modsCache;

    public IReadOnlyList<SavedMod> ModsMissingNow(IReadOnlyList<SavedMod> saved)
    {
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (state.Paths?.DocsFile("bh-mods.txt") is { } report && File.Exists(report))
                foreach (var line in File.ReadAllLines(report))
                    if (line.StartsWith("loaded ", StringComparison.Ordinal) && line.Split(' ') is { Length: >= 3 } parts)
                        loaded[parts[1]] = parts[2];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return saved.Where(m => !m.Live && (!loaded.TryGetValue(m.Id, out var v) || v != m.Version)).ToList();
    }

    public IReadOnlyList<SavedMod>? ModsOf(ReplayEntry e)
    {
        if (ModsPath is not { } path || !File.Exists(path)) return null;
        var time = File.GetLastWriteTimeUtc(path);
        if (modsCache is not { } c || c.Path != path || c.Time != time)
        {
            var byName = new Dictionary<string, IReadOnlyList<SavedMod>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))?["replays"] is System.Text.Json.Nodes.JsonObject replays)
                    foreach (var (name, node) in replays)
                        if (node?["mods"] is System.Text.Json.Nodes.JsonArray list)
                            byName[name] = list.OfType<System.Text.Json.Nodes.JsonObject>().Select(m => new SavedMod(
                                (string?)m["id"] ?? "", (string?)m["name"] ?? "", (string?)m["version"] ?? "",
                                m["live"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var live) && live, (string?)m["print"] ?? "")).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
            modsCache = (path, time, byName);
        }
        return modsCache!.Value.Byname.TryGetValue(e.File, out var mods) ? mods : null;
    }
    public string? Videos => state.Paths?.DocsFile(VideosFolder);
    public Dictionary<int, string> LegendNames { get; } = [];
    public int MyPid { get; private set; }

    public IReadOnlyList<ReplayAccount> Accounts { get; private set; } = [];

    static int Mine(IReadOnlyList<ReplayPlayer> players, IReadOnlyList<int> accounts)
    {
        foreach (var pid in accounts)
            if (IndexOf(players, pid) is var at && at >= 0) return at;
        return -1;
    }
    public ReplayNames Names { get; private set; } = ReplayNames.Empty;
    public bool HasKey { get; private set; }
    public int NewestVersion { get; private set; }

    public int PlayerVersion { get; set; }

    public IReadOnlyList<(ReplayFolder Folder, string Key, string Path)> Files()
    {
        var found = new List<(ReplayFolder, string, string)>();
        if (Replays is not { } dir || !Directory.Exists(dir)) return found;
        foreach (var (folder, sub) in Places)
        {
            var at = sub.Length == 0 ? dir : System.IO.Path.Combine(dir, sub);
            string[] here;
            try { here = Directory.Exists(at) ? Directory.GetFiles(at, "*.replay") : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { here = []; }
            foreach (var path in here)
            {
                var name = System.IO.Path.GetFileName(path);
                found.Add((folder, sub.Length == 0 ? name : sub + "/" + name, path));
            }
        }
        return found;
    }

    public IReadOnlyList<ReplayEntry> Load(Action<int, int>? progress = null)
    {
        MyPid = 0;
        var files = Files();
        if (files.Count == 0) return [];
        var newest = files.Select(f => f.Path).OrderByDescending(p => { try { return System.IO.File.GetLastWriteTimeUtc(p); } catch (IOException) { return DateTime.MinValue; } }).Take(6).ToList();
        var key = ReplayKey.For(state.Game, newest);
        HasKey = key != null;
        Names = ReplayNames.For(state.Game);
        LegendNames.Clear();
        foreach (var (id, name) in Names.HeroNames) LegendNames[id] = name;
        var summaries = new ReplaySummary[files.Count];
        var done = 0;
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
        {
            summaries[i] = catalog.Get(files[i].Path, key);
            var n = Interlocked.Increment(ref done);
            if (progress != null && (n % 50 == 0 || n == files.Count)) progress(n, files.Count);
        });
        catalog.Forget(files.Select(f => f.Path));
        catalog.Save();
        NewestVersion = summaries.Where(s => s.Ok).Select(s => (int)s.Version).DefaultIfEmpty(0).Max();
        var list = files.Select((f, i) => Entry(f.Key, System.IO.Path.GetFileName(f.Path), f.Path, f.Folder, summaries[i])).ToList();
        var accounts = ReplayAccounts.Mine(list);
        var me = MostSeen(list.Where(e => !e.Online));
        MyPid = accounts.FirstOrDefault(me);
        var marked = list.Select(e =>
        {
            var at = e.Online ? Mine(e.Players, accounts) : me != 0 ? IndexOf(e.Players, me) : -1;
            return e with { Me = at >= 0 ? at : Human(e) };
        }).OrderByDescending(e => e.Time).ToList();
        Accounts = ReplayAccounts.Summary(marked, accounts);
        return marked;
    }

    static int Human(ReplayEntry e)
    {
        var people = e.Players.Select((p, i) => (p, i)).Where(x => !x.p.Bot).ToList();
        return people.Count == 1 ? people[0].i : -1;
    }

    ReplayEntry Entry(string key, string name, string path, ReplayFolder folder, ReplaySummary s)
    {
        DateTime time;
        try { time = System.IO.File.GetLastWriteTime(path); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { time = DateTime.MinValue; }
        if (!s.Ok)
            return new ReplayEntry(key, name, path, folder, time, false, false,
                s.Why == "nokey" ? "nokey" : s.Why.Contains("no players", StringComparison.Ordinal) ? "empty" : "unreadable",
                "", "", "", "", false, false, false, 0, [], []);
        var players = s.Players.Select(p =>
        {
            var legends = p.Heroes.Select(h => (int)h).ToList();
            var bot = p.Pid == 0 && legends.Count > 0 && legends.Any(l => string.Equals(Names.HeroShown((uint)l), p.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            return new ReplayPlayer(p.Name, legends, (int)p.Pid, (int)p.Team, (int)p.Place)
            {
                Skins = p.Costumes.Select(c => (int)c).ToList(), Avatar = (int)p.Avatar, Colour = (int)p.Colour, Bot = bot, Inputs = p.Inputs,
            };
        }).ToList();
        var kos = s.Kos.Where(k => k.Length == 2 && k[0] >= 0 && k[0] < players.Count).Select(k => (k[0], k[1])).ToList();
        var level = Names.Level(s.Level);
        var scoring = Names.ScoringName(s.Scoring);
        var mode = Names.ScoringShown(s.Scoring);
        var playlist = s.PlaylistName.Length > 0 ? Names.Text(s.PlaylistName) : "";
        var current = PlayerVersion != 0 ? PlayerVersion : NewestVersion;
        var old = current != 0 && s.Version != current;
        var e = new ReplayEntry(key, name, path, folder, time, true, !old && level != null, old ? "version" : level == null ? "map" : "",
            level?.Name ?? "", level != null ? Names.LevelShown(s.Level) : "", mode.Length > 0 ? mode : ReplayNames.Spaced(Title(scoring)), playlist,
            playlist.Contains("Ranked", StringComparison.OrdinalIgnoreCase), s.Online, s.Teams, s.LengthMs, players, kos)
        {
            Level = (int)s.Level, Scoring = scoring, Lives = (int)s.Lives, Duration = (int)s.Duration, Version = (int)s.Version,
        };
        return e with { Winner = WinnerOf(e) };
    }

    static string Title(string name) => name.Length == 0 ? "" : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();

    static int WinnerOf(ReplayEntry e)
    {
        var placed = e.Players.Select((p, i) => (p, i)).Where(x => x.p.Place == 1).ToList();
        if (placed.Count > 0) return placed[0].i;
        if (e.Lives <= 0 || e.Scoring.Contains("TIMED", StringComparison.OrdinalIgnoreCase) || e.Players.Count < 2) return -1;
        var lost = e.Players.Select((_, i) => e.Kos.Count(k => k.Player == i)).ToList();
        var standing = Enumerable.Range(0, e.Players.Count).Where(i => lost[i] < e.Lives).ToList();
        if (standing.Count == 0) return -1;
        if (!e.Teams) return standing.Count == 1 ? standing[0] : -1;
        var teams = standing.Select(i => e.Players[i].Team).Distinct().ToList();
        return teams.Count == 1 ? standing[0] : -1;
    }

    static readonly (ReplayFolder Folder, string Sub)[] Places =
    [
        (ReplayFolder.Recent, ""), (ReplayFolder.Favorites, FavoritesFolder), (ReplayFolder.Older, OlderFolder),
        (ReplayFolder.Bifrost, BifrostFolder), (ReplayFolder.Favorites, BifrostFolder + "/" + FavoritesFolder), (ReplayFolder.Older, BifrostFolder + "/" + OlderFolder),
    ];

    public string? HomeOf(ReplayEntry e) =>
        Replays is not { } dir ? null : e.Key.StartsWith(BifrostFolder + "/", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.Combine(dir, BifrostFolder) : dir;

    public IReadOnlyList<ReplayVideo> LoadVideos(IEnumerable<ReplayEntry> entries)
    {
        if (Videos is not { } dir || !Directory.Exists(dir)) return [];
        var stems = entries.Select(e => e.File.EndsWith(".replay", StringComparison.OrdinalIgnoreCase) ? e.File[..^7] : e.File)
            .Distinct().OrderByDescending(s => s.Length).ToList();
        var list = new List<ReplayVideo>();
        try
        {
            foreach (var path in Directory.GetFiles(dir))
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".avi" or ".mp4" or ".webm" or ".mkv")) continue;
                var name = System.IO.Path.GetFileName(path);
                var info = new FileInfo(path);
                var stem = stems.FirstOrDefault(s => name.StartsWith(s + " ", StringComparison.Ordinal));
                list.Add(new ReplayVideo(name, path, info.LastWriteTime, info.Length, stem == null ? null : stem + ".replay"));
            }
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { }
        return list.OrderByDescending(v => v.Time).ToList();
    }

    public HashSet<string> FavoriteNames(IEnumerable<ReplayEntry> entries) =>
        entries.Where(e => e.Folder == ReplayFolder.Favorites).Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string? AddToFavorites(ReplayEntry e)
    {
        if (HomeOf(e) is not { } dir) return "Brawlhalla's replay folder wasn't found.";
        if (e.Folder == ReplayFolder.Favorites) return "It's already in Favorites.";
        var to = System.IO.Path.Combine(dir, FavoritesFolder, e.File);
        try
        {
            if (System.IO.File.Exists(to)) return "It's already in Favorites.";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            System.IO.File.Copy(e.Path, to);
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return x.Message; }
        return null;
    }

    public string? RemoveFromFavorites(ReplayEntry e)
    {
        if (HomeOf(e) is not { } dir) return "Brawlhalla's replay folder wasn't found.";
        if (state.GameRunning) return "Close Brawlhalla first. Its Replays window may be using the file.";
        var copy = System.IO.Path.Combine(dir, FavoritesFolder, e.File);
        try
        {
            if (!System.IO.File.Exists(copy)) return "It isn't in Favorites.";
            var other = e.Folder == ReplayFolder.Favorites
                && !System.IO.File.Exists(System.IO.Path.Combine(dir, e.File)) && !System.IO.File.Exists(System.IO.Path.Combine(dir, OlderFolder, e.File));
            if (other) Directory.CreateDirectory(System.IO.Path.Combine(dir, OlderFolder));
            if (other) System.IO.File.Move(copy, System.IO.Path.Combine(dir, OlderFolder, e.File));
            else System.IO.File.Delete(copy);
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return x.Message; }
        return null;
    }

    static int IndexOf(IReadOnlyList<ReplayPlayer> players, int pid)
    {
        for (var i = 0; i < players.Count; i++) if (players[i].Pid == pid) return i;
        return -1;
    }

    public static int MostSeen(IEnumerable<ReplayEntry> entries) =>
        entries.SelectMany(e => e.Players.Select(p => p.Pid).Where(p => p != 0).Distinct()).GroupBy(p => p)
            .Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public IReadOnlyDictionary<string, ReplayNote> Notes()
    {
        var result = new Dictionary<string, ReplayNote>(StringComparer.Ordinal);
        if (NotesPath is not { } file) return result;
        var current = JsonFiles.Read(file) as JsonObject ?? new JsonObject();
        if (state.Writes.PendingFor(file) is { } pending) current = JsonFiles.Merge(current, pending) as JsonObject ?? current;
        if (current["replays"] is not JsonObject all) return result;
        foreach (var (name, value) in all)
        {
            if (value is not JsonObject o) continue;
            var tags = (o["tags"] as JsonArray ?? []).Select(Str).Where(t => t.Length > 0).ToList();
            var note = new ReplayNote(Str(o["note"]), tags);
            if (!note.IsEmpty) result[name] = note;
        }
        return result;
    }

    public bool? SetNote(string file, ReplayNote note)
    {
        if (NotesPath is not { } path) return null;
        var clean = new ReplayNote(note.Note.Trim(), note.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList());
        JsonNode? value = clean.IsEmpty ? null : new JsonObject { ["note"] = clean.Note, ["tags"] = new JsonArray(clean.Tags.Select(t => (JsonNode?)t).ToArray()) };
        return state.Writes.Merge(path, new JsonObject { ["replays"] = new JsonObject { [file] = value } });
    }

    public IReadOnlyList<string> AllTags() => Notes().Values.SelectMany(n => n.Tags).Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

    public string? StatsPath(ReplayEntry e) => state.Paths?.DocsFile(System.IO.Path.Combine(StatsFolder, e.StatsName + ".json"));

    public bool HasStats(ReplayEntry e) => StatsPath(e) is { } p && System.IO.File.Exists(p);

    public string? FramesPath(ReplayEntry e) => state.Paths?.DocsFile(System.IO.Path.Combine(FramesFolder, e.StatsName + ".jsonl"));

    public bool HasFrames(ReplayEntry e) =>
        FramesPath(e) is { } p && System.IO.File.Exists(p) && System.IO.File.GetLastWriteTimeUtc(p) >= System.IO.File.GetLastWriteTimeUtc(e.Path);

    public static IEnumerable<ReplayEntry> Filter(IEnumerable<ReplayEntry> entries, ReplayFilter f, IReadOnlyDictionary<string, ReplayNote> notes) =>
        entries.Where(e =>
            (f.Folder == null || e.Folder == f.Folder)
            && (f.Result == null || e.Result == f.Result)
            && (f.Size == null || e.Size == f.Size)
            && (f.Map == null || Has(e.MapName, f.Map) || Has(e.Map, f.Map))
            && (f.Mode == null || Has(e.Mode, f.Mode) || Has(e.Playlist, f.Mode))
            && (f.Legend == null || e.Players.Where((p, i) => e.Me < 0 || i == e.Me).Any(p => p.Legends.Contains(f.Legend.Value)))
            && (f.Opponent == null || e.Players.Where((p, i) => i != e.Me).Any(p => Has(p.Name, f.Opponent)))
            && (f.Tag == null || notes.TryGetValue(e.File, out var n) && n.Tags.Contains(f.Tag, StringComparer.OrdinalIgnoreCase))
            && (f.Text == null || Has(e.File, f.Text) || Has(e.MapName, f.Text) || Has(e.Mode, f.Text) || Has(e.Playlist, f.Text)
                || e.Players.Any(p => Has(p.Name, f.Text)) || notes.TryGetValue(e.File, out var m) && Has(m.Note, f.Text)));

    static bool Has(string value, string part) => value.Contains(part, StringComparison.OrdinalIgnoreCase);
}
