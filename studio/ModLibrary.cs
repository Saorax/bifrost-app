using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BifrostStudio;

public enum ModState { Loaded, Off, Skipped, NotSeen }

public sealed record ModInfo(
    string Folder, string Path, string? Id, string Name, string Version, string Author, string Description,
    IReadOnlyList<string> Requires, string? Preview, bool Gameplay, string? Identity, string? Problem,
    ModState State, string StateText, bool Disabled, bool ChangedSinceReport, IReadOnlyList<string> Conflicts)
{
    public string? InPack { get; init; }
    public string? PackName { get; init; }
    public bool PackOff { get; init; }
    public bool RoomsRequired { get; init; }
    public IReadOnlyList<string>? Members { get; init; }
    public string? LoadOrder { get; init; }
    public bool IsPack => Members != null;
    public bool Off => Disabled || PackOff;
}

public sealed partial class ModLibrary(AppState state)
{
    public const string ManifestFile = "mod.json";
    public const string ReportFile = "bh-mods.txt";

    public static readonly string[] VisualTables =
    [
        "costumeTypes", "ColorSchemeTypes", "weaponSkinTypes", "avatarTypes", "TauntTypes", "EmojiTypes", "SpawnBotTypes",
        "SeasonBorderTypes", "TrailEffectTypes", "PodiumTypes", "PlayerThemeTypes", "CompanionTypes", "EmitterTypes",
        "EmitterGroupTypes", "SplashArtTypes", "ClientThemeTypes", "MonikerTypes", "MusicTypes", "EndMatchVoicelineTypes",
        "SoundVolumes", "LanguageTypes", "MasterFileList", "BoneSources", "BoneTypes", "colorExceptionTypes",
        "manualBoneSpriteData", "manualSpriteData", "spriteData",
    ];

    static readonly JsonSerializerOptions AsGame = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly GameSettings settings = new(state);

    public string? Folder => state.Paths?.Mods;

    public static string AsideFolder(string mods, string kind)
    {
        var full = System.IO.Path.GetFullPath(mods).TrimEnd('\\', '/');
        if (string.Equals(full, System.IO.Path.GetFullPath(BifrostFolders.Mods).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.Combine(BifrostFolders.Old, "Mods", char.ToUpperInvariant(kind[0]) + kind[1..]);
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full) ?? full, "mods-" + kind);
    }

    string? AsideOf(string kind) => Folder is { } f ? AsideFolder(f, kind) : null;
    public string? ReportPath => state.Paths?.DocsFile(ReportFile);

    public IReadOnlyList<ModInfo> Load()
    {
        var root = Folder;
        if (root == null || !Directory.Exists(root)) return [];
        var report = ReadReport();
        var reportTime = ReportPath is { } rp && File.Exists(rp) ? File.GetLastWriteTimeUtc(rp) : (DateTime?)null;
        var disabled = settings.Mods()["disabled"] as JsonObject;
        bool IsOff(string? id) => id != null && disabled?[id] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var list = new List<ModInfo>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => System.IO.Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            var folder = System.IO.Path.GetFileName(dir);
            var (mod, m) = Read(dir, folder, report, reportTime, IsOff);
            var (names, rooms, packProblem) = m == null || mod.Problem != null ? (null, false, null) : PackOf(m);
            if (names == null)
            {
                list.Add(packProblem == null ? mod : mod with { Problem = packProblem });
                continue;
            }
            var members = new List<ModInfo>();
            var seen = new HashSet<string> { mod.Id ?? "" };
            string? memberProblem = null;
            foreach (var name in names)
            {
                var (member, mm) = Read(System.IO.Path.Combine(dir, PackFolder, name), folder + "/" + name, report, reportTime, IsOff);
                var why = mm == null ? (member.Problem == "no mod.json" ? "has no mod.json" : "can't be read: " + member.Problem)
                    : mm["pack"] != null ? "is a pack itself"
                    : member.Id == null ? "has no id"
                    : !seen.Add(member.Id) ? "uses the id " + member.Id + " twice" : null;
                memberProblem ??= why == null ? null : "its mod " + name + " " + why;
                members.Add(member with
                {
                    Problem = member.Problem ?? (why == null ? null : why[0].ToString().ToUpperInvariant() + why[1..]),
                    InPack = mod.Id, PackName = mod.Name, PackOff = mod.Disabled, RoomsRequired = rooms, Gameplay = member.Gameplay || rooms,
                    State = mod.State == ModState.Skipped && member.State == ModState.NotSeen ? ModState.Skipped : member.State,
                    StateText = mod.State == ModState.Skipped && member.State == ModState.NotSeen ? "not loaded: its pack wasn't loaded" : member.StateText,
                });
            }
            if (memberProblem != null)
            {
                for (var i = 0; i < members.Count; i++)
                    if (members[i].Problem == null)
                        members[i] = members[i] with { State = ModState.Skipped, StateText = "not loaded: its pack can't be loaded because " + memberProblem };
            }
            list.Add(mod with
            {
                Members = names, RoomsRequired = rooms, Gameplay = rooms || members.Any(x => x.Gameplay), Problem = memberProblem,
                LoadOrder = mod.Id != null && report.Packs.TryGetValue(mod.Id, out var order) ? order : null,
            });
            list.AddRange(members);
        }
        var inPacks = list.Where(x => x.InPack != null && x.Id != null && x.Problem == null).GroupBy(x => x.Id!).ToDictionary(g => g.Key, g => g.First());
        for (var i = 0; i < list.Count; i++)
        {
            var x = list[i];
            if (x.InPack == null && !x.IsPack && x.Id != null && x.State == ModState.NotSeen && inPacks.TryGetValue(x.Id, out var taken))
                list[i] = x with { State = ModState.Skipped, StateText = "not loaded: id " + x.Id + " is already used by " + taken.Folder };
        }
        return list;
    }

    (ModInfo Mod, JsonObject? Manifest) Read(string dir, string folder, Report report, DateTime? reportTime, Func<string?, bool> isOff)
    {
        var manifest = System.IO.Path.Combine(dir, ManifestFile);
        string? text = null;
        JsonObject? m = null;
        string? problem = null;
        if (!File.Exists(manifest)) problem = "no mod.json";
        else
        {
            try
            {
                text = File.ReadAllText(manifest);
                m = JsonNode.Parse(text.TrimStart('﻿')) as JsonObject;
                if (m == null) problem = "mod.json is not a JSON object";
            }
            catch (JsonException e)
            {
                problem = "mod.json is not valid JSON (" + e.Message + ")";
            }
            catch (IOException e)
            {
                problem = "mod.json can't be read (" + e.Message + ")";
            }
        }
        var id = Str(m?["id"]);
        var version = Str(m?["version"]);
        var requires = (m?["requires"] as JsonArray ?? []).Select(Str).Where(s => s.Length > 0).ToList();
        var preview = Str(m?["preview"]);
        var previewPath = preview.Length > 0 && File.Exists(System.IO.Path.Combine(dir, preview)) ? System.IO.Path.Combine(dir, preview) : null;
        var (st, why) = StateOf(report, folder, id);
        var changed = reportTime is { } rt && text != null && File.GetLastWriteTimeUtc(manifest) > rt;
        return (new ModInfo(folder, dir, id.Length > 0 ? id : null, Str(m?["name"]) is { Length: > 0 } n ? n : System.IO.Path.GetFileName(dir), version,
            Str(m?["author"]), Str(m?["description"]), requires, previewPath, m != null && IsGameplay(m),
            m != null && id.Length > 0 && text != null ? Identity(id, version, text) : null, problem, st, why, isOff(id.Length > 0 ? id : null), changed,
            id.Length > 0 && report.Conflicts.TryGetValue(id, out var c) ? c : []), m);
    }

    public const string PackFolder = "mods";

    public static (IReadOnlyList<string>? Members, bool Rooms, string? Problem) PackOf(JsonObject m)
    {
        if (m["pack"] is not { } p) return (null, false, null);
        var names = new List<string>();
        var bad = p is not JsonObject po || po["mods"] is not JsonArray mods || mods.Count == 0 || (po["rooms"] != null && Str(po["rooms"]) != "required");
        if (!bad)
        {
            foreach (var x in (JsonArray)p["mods"]!)
            {
                if (x is not JsonValue v || !v.TryGetValue<string>(out var s) || !MemberName().IsMatch(s) || s is "." or "..")
                {
                    bad = true;
                    break;
                }
                names.Add(s);
            }
        }
        return bad ? (null, false, "\"pack\" needs \"mods\" (folder names under mods/, in load order) and an optional \"rooms\": \"required\"")
            : (names, Str(p["rooms"]) == "required", null);
    }

    public sealed record MapArtPack(string Id, string Name);

    public sealed record MapArtMap(string Level, string Name, IReadOnlyList<MapArtPack> Packs, string Choice);

    public static readonly string[] MapArtModes = ["default", "shuffle", "mass"];

    public static string MapArtChoiceName(string value, IEnumerable<MapArtPack> packs) => value switch
    {
        "default" => "The game's default",
        "shuffle" => "Shuffle",
        "mass" => "Mass random",
        _ => packs.FirstOrDefault(p => p.Id == value)?.Name ?? value,
    };

    public IReadOnlyList<MapArtMap> MapArt(IReadOnlyList<ModInfo>? mods = null)
    {
        mods ??= Load();
        var chosen = settings.Mods()["mapArt"] as JsonObject;
        var byLevel = new Dictionary<string, (string Name, List<MapArtPack> Packs)>();
        foreach (var m in mods.Where(m => m.Id != null && !m.Off && m.Problem == null && !(m.InPack != null && m.State == ModState.Skipped)))
        {
            JsonObject? maps;
            try
            {
                maps = (JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(m.Path, ManifestFile)).TrimStart('﻿')) as JsonObject)?["mapArt"]?["maps"] as JsonObject;
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                continue;
            }
            foreach (var (level, name) in maps ?? [])
            {
                if (!byLevel.TryGetValue(level, out var entry)) byLevel[level] = entry = (Str(name) is { Length: > 0 } n ? n : level, []);
                entry.Packs.Add(new MapArtPack(m.Id!, m.Name));
            }
        }
        return byLevel.Select(kv =>
        {
            var value = Str(chosen?[kv.Key]);
            var choice = MapArtModes.Contains(value) || kv.Value.Packs.Any(p => p.Id == value) ? value : kv.Value.Packs[0].Id;
            return new MapArtMap(kv.Key, kv.Value.Name, kv.Value.Packs, choice);
        }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Level, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool? SetMapArt(string level, string value) =>
        settings.Change(new JsonObject { ["mapArt"] = new JsonObject { [level] = value } });

    public sealed record SetAside(string Kind, string Folder, string Path, DateTime When, string Name, string Version);

    public IReadOnlyList<SetAside> Aside()
    {
        if (Folder == null) return [];
        var list = new List<SetAside>();
        foreach (var kind in new[] { "removed", "replaced" })
        {
            var dir = AsideOf(kind);
            if (dir == null || !Directory.Exists(dir)) continue;
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var name = System.IO.Path.GetFileName(d);
                var m = AsideName().Match(name);
                var folder = m.Success ? m.Groups["folder"].Value : name;
                var when = m.Success && long.TryParse(m.Groups["ms"].Value, out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : Directory.GetLastWriteTime(d);
                JsonObject? j = null;
                try
                {
                    var f = System.IO.Path.Combine(d, ManifestFile);
                    if (File.Exists(f)) j = JsonNode.Parse(File.ReadAllText(f).TrimStart('﻿')) as JsonObject;
                }
                catch (Exception e) when (e is JsonException or IOException)
                {
                }
                list.Add(new SetAside(kind, folder, d, when, Str(j?["name"]) is { Length: > 0 } n ? n : folder, Str(j?["version"])));
            }
        }
        return list.OrderByDescending(a => a.When).ToList();
    }

    public string? Restore(SetAside a)
    {
        if (state.GameRunning) return "Close Brawlhalla first. It keeps mod files open.";
        if (Folder is not { } mods || state.Paths?.Game is null) return "Bifrost isn't installed.";
        var target = System.IO.Path.Combine(mods, a.Folder);
        try
        {
            Directory.CreateDirectory(mods);
            if (Directory.Exists(target))
            {
                var replaced = AsideFolder(mods, "replaced");
                Directory.CreateDirectory(replaced);
                Directory.Move(target, System.IO.Path.Combine(replaced, a.Folder + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }
            Directory.Move(a.Path, target);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "It couldn't be moved back: " + e.Message;
        }
    }

    public IReadOnlyList<string> PendingBmods() =>
        Folder is { } mods && Directory.Exists(mods)
            ? Directory.EnumerateFiles(mods, "*.bmod").Order(StringComparer.OrdinalIgnoreCase).ToList() : [];

    public Bmod.BmodConvertResult ImportBmod(string file)
    {
        var name = System.IO.Path.GetFileName(file);
        if (Folder is not { } mods) return new(Bmod.BmodOutcome.Failed, name, "Bifrost isn't installed.");
        if (!File.Exists(file)) return new(Bmod.BmodOutcome.Failed, name, "The file is gone.");
        if (!file.EndsWith(".bmod", StringComparison.OrdinalIgnoreCase)) return new(Bmod.BmodOutcome.Failed, name, "Only .bmod files can be imported here.");
        if (state.Paths?.Game is { } game && Directory.Exists(game) && AsideOf("replaced") is { } replaced)
        {
            var done = Bmod.BmodConverter.Convert(file, mods, replaced, game);
            if (done.Outcome != Bmod.BmodOutcome.ForTheGame) return done;
        }
        return CopyForTheGame(file, mods) is { } why ? new(Bmod.BmodOutcome.Failed, name, why) :
            new(Bmod.BmodOutcome.ForTheGame, name, "Brawlhalla converts it at its next start");
    }

    public Bmod.BmodConvertResult ConvertWaiting(string file)
    {
        var name = System.IO.Path.GetFileName(file);
        if (Folder is not { } mods || state.Paths?.Game is not { } game || !Directory.Exists(game) || AsideOf("replaced") is not { } replaced)
            return new(Bmod.BmodOutcome.ForTheGame, name, "Brawlhalla converts it at its next start");
        var done = Bmod.BmodConverter.Convert(file, mods, replaced, game);
        if (done.Outcome is Bmod.BmodOutcome.Converted or Bmod.BmodOutcome.Already && done.Folder != null &&
            File.Exists(System.IO.Path.Combine(done.Folder, name)))
        {
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return done;
    }

    public void SwitchOn(string id)
    {
        if ((settings.Mods()["disabled"] as JsonObject)?[id] is JsonValue)
            settings.Change(new JsonObject { ["disabled"] = new JsonObject { [id] = null } });
    }

    static string? CopyForTheGame(string file, string mods)
    {
        try
        {
            Directory.CreateDirectory(mods);
            var stem = System.IO.Path.GetFileNameWithoutExtension(file);
            var target = System.IO.Path.Combine(mods, stem + ".bmod");
            for (var k = 2; File.Exists(target); k++) target = System.IO.Path.Combine(mods, $"{stem} ({k}).bmod");
            File.Copy(file, target);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "It couldn't be copied: " + e.Message;
        }
    }

    public const string SubscribedFile = "mods-subscribed.json";

    public string? InstallDownload(string entry, byte[] package, string uploader = "")
    {
        if (Folder is not { } mods || state.Paths?.Game is not { } game) return "Bifrost isn't installed.";
        IReadOnlyList<PackedFile> files;
        try
        {
            files = ModPackage.Unpack(package);
        }
        catch (InvalidDataException e)
        {
            return e.Message;
        }
        var manifest = files.FirstOrDefault(f => f.Path == ManifestFile);
        if (manifest == null) return "no mod.json";
        string text;
        JsonObject? m;
        try
        {
            text = new UTF8Encoding(false, true).GetString(manifest.Data);
            m = JsonNode.Parse(text.TrimStart('﻿')) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException)
        {
            return "its mod.json can't be read";
        }
        var id = Str(m?["id"]);
        if (m == null || id.Length == 0 || Identity(id, Str(m["version"]), text) != entry) return "its contents aren't the ones offered";
        if (!ModPackage.SafeFolder(id)) return "its id can't be a folder name";
        var old = Load().Where(x => x.Id == id && x.InPack == null).ToList();
        if (old.Count > 0 && state.GameRunning) return "Close Brawlhalla first. It has the installed " + id + "'s files open.";
        string? dir = null;
        try
        {
            Directory.CreateDirectory(mods);
            foreach (var o in old)
            {
                var replaced = AsideFolder(mods, "replaced");
                Directory.CreateDirectory(replaced);
                Directory.Move(o.Path, System.IO.Path.Combine(replaced, o.Folder + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }
            dir = System.IO.Path.Combine(mods, id);
            for (var n = 2; Directory.Exists(dir) || File.Exists(dir); n++) dir = System.IO.Path.Combine(mods, id + "-" + n);
            var root = System.IO.Path.GetFullPath(dir) + System.IO.Path.DirectorySeparatorChar;
            foreach (var f in files)
            {
                var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("a file path leaves the mod's folder");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, f.Data);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException)
            {
            }
            return "it couldn't be written (" + e.Message + ")";
        }
        settings.Change(new JsonObject { ["disabled"] = new JsonObject { [id] = null } });
        if (state.Paths?.DocsFile(SubscribedFile) is { } subscribed)
            state.Writes.Merge(subscribed, new JsonObject { [id] = new JsonObject { ["entry"] = entry, ["uploader"] = uploader } });
        return null;
    }

    public bool? SetDisabled(ModInfo mod, bool off) =>
        mod.Id == null ? null : settings.Change(new JsonObject { ["disabled"] = new JsonObject { [mod.Id] = off ? true : null } });

    public sealed record Report(
        IReadOnlyDictionary<string, string> Loaded, IReadOnlyDictionary<string, string> Off, IReadOnlyDictionary<string, string> Skipped,
        IReadOnlyDictionary<string, List<string>> Conflicts, IReadOnlyList<string> Lines)
    {
        public IReadOnlyDictionary<string, string> Packs { get; init; } = new Dictionary<string, string>();
    }

    public Report ReadReport() => ParseReport(ReportPath is { } p && File.Exists(p) ? SafeLines(p) : []);

    static string[] SafeLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }
    }

    public static Report ParseReport(IEnumerable<string> lines)
    {
        var loaded = new Dictionary<string, string>();
        var off = new Dictionary<string, string>();
        var skipped = new Dictionary<string, string>();
        var conflicts = new Dictionary<string, List<string>>();
        var packs = new Dictionary<string, string>();
        var all = lines.ToList();
        foreach (var raw in all)
        {
            var line = raw.TrimEnd();
            Match m;
            if ((m = LoadedLine().Match(line)).Success) loaded[m.Groups["id"].Value] = m.Groups["folder"].Value;
            else if ((m = OffLine().Match(line)).Success) off[m.Groups["id"].Value] = m.Groups["why"].Value;
            else if ((m = SkippedLine().Match(line)).Success) skipped[m.Groups["folder"].Value] = m.Groups["why"].Value;
            else if ((m = PackLine().Match(line)).Success) packs[m.Groups["id"].Value] = m.Groups["order"].Value;
            else if ((m = ConflictLine().Match(line)).Success)
            {
                var by = m.Groups["by"].Value;
                if (!conflicts.TryGetValue(by, out var l)) conflicts[by] = l = [];
                l.Add($"{m.Groups["kind"].Value} {m.Groups["what"].Value} (over {m.Groups["was"].Value})");
            }
        }
        return new Report(loaded, off, skipped, conflicts, all) { Packs = packs };
    }

    static (ModState, string) StateOf(Report r, string folder, string id)
    {
        if (id.Length > 0 && r.Loaded.TryGetValue(id, out var from) && string.Equals(from, folder, StringComparison.OrdinalIgnoreCase)) return (ModState.Loaded, "loaded");
        if (r.Skipped.TryGetValue(folder, out var why)) return (ModState.Skipped, "not loaded: " + why);
        if (id.Length > 0 && r.Off.TryGetValue(id, out var off)) return (ModState.Off, "off: " + off);
        if (id.Length > 0 && r.Loaded.TryGetValue(id, out var other)) return (ModState.Skipped, "not loaded: id " + id + " is already used by " + other);
        return (ModState.NotSeen, r.Lines.Count == 0 ? "the game hasn't reported its mods yet" : "new since the game last started");
    }

    public static bool IsGameplay(JsonObject m)
    {
        if (m["files"] is JsonArray files && files.Count > 0) return true;
        if (m["animations"] is JsonArray animations && animations.Count > 0) return true;
        if (m["legendMoves"] != null) return true;
        if (m["physics"] != null) return true;
        if (m["weapons"] != null) return true;
        if (m["rules"] != null) return true;
        if (m["tables"] is JsonObject tables)
        {
            foreach (var (table, rows) in tables)
            {
                if (table == "PowerSwapTypes")
                {
                    if (PowerSwapAnim().IsMatch(rows?.ToJsonString(AsGame) ?? "")) return true;
                    continue;
                }
                if (!VisualTables.Contains(table)) return true;
            }
            if (tables["MasterFileList"] is JsonArray mfl && mfl.Any(r => ModAnm().IsMatch(r is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : r?.ToJsonString(AsGame) ?? "")))
                return true;
        }
        if (m["assets"] is JsonObject assets)
        {
            foreach (var (path, local) in assets)
            {
                if (AnimPath().IsMatch(path)) return true;
                if (Str(local).EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    public static string Identity(string id, string version, string manifestText)
    {
        var content = manifestText.StartsWith('﻿') ? manifestText[1..] : manifestText;
        content = content.Replace("\r\n", "\n");
        return id + "@" + version + "#" + Fnv(content).ToString("x8");
    }

    public static uint Fnv(string s)
    {
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h = unchecked(h * 16777619u);
        }
        return h;
    }

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    [GeneratedRegex(@"^loaded (?<id>\S+) (?<version>\S+) \((?<name>.*)\) from (?<folder>.+)$")]
    private static partial Regex LoadedLine();
    [GeneratedRegex(@"^off (?<id>[^:\s]+): (?<why>.+)$")]
    private static partial Regex OffLine();
    [GeneratedRegex(@"^skipped (?<folder>[^:]+): (?<why>.+)$")]
    private static partial Regex SkippedLine();
    [GeneratedRegex(@"^conflict (?<kind>\S+) (?<what>.+?): (?<was>.+) is overridden by (?<by>\S+)$")]
    private static partial Regex ConflictLine();
    [GeneratedRegex(@"(<|"")(CastAnim|GrabAnim|HitReactAnim|AsymRight)(>|""\s*:\s*"")[^<""\s]")]
    private static partial Regex PowerSwapAnim();
    [GeneratedRegex(@"mods\/[^\/""\\\s]+\/([^""\\\s]+\.anm)", RegexOptions.IgnoreCase)]
    private static partial Regex ModAnm();
    [GeneratedRegex(@"(^|\/)anim", RegexOptions.IgnoreCase)]
    private static partial Regex AnimPath();
    [GeneratedRegex(@"^pack (?<id>\S+): \d+ mods? in this order: (?<order>.+)$")]
    private static partial Regex PackLine();
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$")]
    private static partial Regex MemberName();
    [GeneratedRegex(@"^(?<folder>.+)-(?<ms>\d{10,})$")]
    private static partial Regex AsideName();
}
