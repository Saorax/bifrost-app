using System.Text.Json;

namespace BifrostStudio;

public sealed record FramePlayer(int Index, string Name, string Legend, string Hero, int Team, string? Costume = null, string? ColorScheme = null,
    IReadOnlyList<string?>? WeaponSkins = null);

public sealed record FrameLook(string AnimFile, string AnimClass, float Scale, string? WeaponSkin, bool HideItem);

public readonly record struct PlayerFrame(
    float X, float Y, float Vx, float Vy, bool Left, int State, float Damage, int Lives, bool Stunned, bool Air,
    string? Anim, int Frame, string? Power, float CastFrame, string? Weapon, int HitBy, int Cast, int Look = -1)
{
    public bool KnockedOut => State is 3 or 4 or 7 or 8;
}

public sealed record FrameEvent(string Kind, uint Time, int Player, double Damage, int By);

public readonly record struct FrameItem(float X, float Y, int State, string? Type, int ThrownBy)
{
    public bool Thrown => State is 1 or 6;
    public bool Waiting => State is 2 or 3 or 4;
}

public readonly record struct FrameBox(float X, float Y, float Rx, float Ry, bool GroundCheck, int By);

public readonly record struct FrameHurt(float Dx, float Dy, float Rx, float Ry);

public readonly record struct FrameLine(float X1, float Y1, float X2, float Y2, string Kind, bool Off)
{
    public bool Hard => Kind.Contains("Hard", StringComparison.Ordinal) || Kind is "NoSlideCollision" or "LavaCollision";
    public bool Soft => Kind.Contains("Soft", StringComparison.Ordinal);
}

public sealed record FrameLevel(string Name, (float X, float Y, float W, float H)? Camera, int[] Kill, FrameLine[] Lines, FrameLine[] Moving)
{
    public static readonly FrameLevel None = new("", null, [], [], []);

    public (float X, float Y, float W, float H)? Blast => Camera is { } c && Kill.Length == 4
        ? (c.X - Kill[0], c.Y - Kill[2], c.W + Kill[0] + Kill[1], c.H + Kill[2] + Kill[3])
        : null;
}

public sealed class ReplayFrames
{
    public const string Format = "bifrost-replay-frames";
    public const int Version = 1;

    public string Replay { get; private init; } = "";
    public string? Error { get; private init; }
    public int StepMs { get; private init; } = 16;
    public uint Start { get; private init; }
    public uint End { get; private init; }
    public IReadOnlyList<FramePlayer> Players { get; private init; } = [];
    public uint[] Times { get; private init; } = [];
    public PlayerFrame?[][] Rows { get; private init; } = [];
    public IReadOnlyList<FrameEvent> Events { get; private init; } = [];
    public FrameLevel Level { get; private init; } = FrameLevel.None;
    public int[] MovingSteps { get; private init; } = [];
    public FrameLine[][] MovingLines { get; private init; } = [];
    public FrameItem[]?[] Items { get; private init; } = [];
    public FrameBox[]?[] Hits { get; private init; } = [];
    public int[] HurtSteps { get; private init; } = [];
    public FrameHurt?[][] HurtRows { get; private init; } = [];
    public IReadOnlyList<FrameLook> Looks { get; private init; } = [];
    public string? StatsJson { get; private init; }

    public bool HasLooks => Looks.Count > 0;

    public FrameLook? LookOf(PlayerFrame frame) => frame.Look >= 0 && frame.Look < Looks.Count ? Looks[frame.Look] : null;

    public bool HasBoxes => Hits.Length > 0 || HurtSteps.Length > 0;

    public FrameBox[] HitsAtStep(int step) => step >= 0 && step < Hits.Length && Hits[step] is { } hits ? hits : [];

    public FrameBox[] HitsAt(uint time) => HitsAtStep(IndexAt(time));

    public (float X, float Y, float Rx, float Ry)? HurtboxAt(int player, uint time)
    {
        if (HurtSteps.Length == 0 || At(player, time) is not { KnockedOut: false } p) return null;
        var j = Array.BinarySearch(HurtSteps, IndexAt(time));
        if (j < 0) j = ~j - 1;
        if (j < 0 || player >= HurtRows[j].Length || HurtRows[j][player] is not { } h || h.Rx <= 0 || h.Ry <= 0) return null;
        return (p.X + h.Dx, p.Y + h.Dy, h.Rx, h.Ry);
    }

    public FrameItem[] ItemsAt(uint time)
    {
        var i = IndexAt(time);
        return i >= 0 && i < Items.Length && Items[i] is { } items ? items : [];
    }

    public FrameLine[] MovingAt(uint time)
    {
        if (Level.Moving.Length == 0) return [];
        var i = IndexAt(time);
        var j = Array.BinarySearch(MovingSteps, i);
        if (j < 0) j = ~j - 1;
        return j >= 0 ? MovingLines[j] : Level.Moving;
    }

    public bool Ok => Error == null && Times.Length > 0;
    public uint First => Times.Length > 0 ? Times[0] : 0;
    public uint Last => Times.Length > 0 ? Times[^1] : 0;

    public static ReplayFrames Read(string path)
    {
        try
        {
            return Parse(File.ReadLines(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed("", "the dump couldn't be read: " + e.Message);
        }
    }

    public static ReplayFrames Parse(IEnumerable<string> lines)
    {
        using var it = lines.Where(l => !string.IsNullOrWhiteSpace(l)).GetEnumerator();
        if (!it.MoveNext()) return Failed("", "the dump is empty");
        JsonElement head;
        try
        {
            head = JsonDocument.Parse(it.Current).RootElement.Clone();
        }
        catch (JsonException)
        {
            return Failed("", "the dump's first line isn't JSON");
        }
        var replay = Str(head, "replay");
        if (head.ValueKind != JsonValueKind.Object || Str(head, "format") != Format) return Failed(replay, "not a replay frame dump");
        if (Int(head, "version") != Version) return Failed(replay, "made by another version of Bifrost, dump version " + Int(head, "version"));
        if (head.TryGetProperty("error", out var err)) return Failed(replay, err.ToString());

        var fields = head.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(x => x.ToString()).ToList() : [];
        int Col(string name) => fields.IndexOf(name);
        var cols = new[] { "x", "y", "vx", "vy", "left", "state", "damage", "lives", "stunned", "air", "anim", "frame", "power", "castFrame", "weapon", "hitBy", "cast", "look" }
            .Select(Col).ToArray();
        if (cols[0] < 0 || cols[1] < 0) return Failed(replay, "the dump has no positions");

        var players = new List<FramePlayer>();
        if (head.TryGetProperty("players", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ps.EnumerateArray())
            {
                IReadOnlyList<string?>? skins = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("weaponSkins", out var ws) && ws.ValueKind == JsonValueKind.Array
                    ? [.. ws.EnumerateArray().Select(s => s.ValueKind == JsonValueKind.String ? s.GetString() : null)]
                    : null;
                players.Add(new FramePlayer(Int(p, "index", players.Count), Str(p, "name"), Str(p, "legend"), Str(p, "hero"), Int(p, "team"),
                    OptStr(p, "costume"), OptStr(p, "colorScheme"), skins));
            }
        }
        var looks = ReadLooks(head);

        var names = new Dictionary<string, string>();
        string? Intern(JsonElement row, int col)
        {
            if (col < 0 || col >= row.GetArrayLength()) return null;
            var v = row[col];
            if (v.ValueKind != JsonValueKind.String) return null;
            var s = v.GetString()!;
            if (!names.TryGetValue(s, out var known)) names[s] = known = s;
            return known;
        }
        double Num(JsonElement row, int col) =>
            col >= 0 && col < row.GetArrayLength() && row[col].ValueKind == JsonValueKind.Number ? row[col].GetDouble() : 0;

        var level = head.TryGetProperty("level", out var lv) ? ReadLevel(lv) : FrameLevel.None;
        var movingSteps = new List<int>();
        var movingLines = new List<FrameLine[]>();
        var itemFields = head.TryGetProperty("itemFields", out var itf) && itf.ValueKind == JsonValueKind.Array
            ? itf.EnumerateArray().Select(x => x.ToString()).ToList() : [];
        var itemCols = new[] { "x", "y", "state", "type", "thrownBy" }.Select(n => itemFields.IndexOf(n)).ToArray();
        var items = new List<FrameItem[]?>();
        var anyItems = false;
        List<string> FieldList(string name) => head.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.ToString()).ToList() : [];
        var hitFields = FieldList("hitFields");
        var hitCols = new[] { "x", "y", "rx", "ry", "kind", "by" }.Select(n => hitFields.IndexOf(n)).ToArray();
        var hurtFields = FieldList("hurtFields");
        var hurtCols = new[] { "dx", "dy", "rx", "ry" }.Select(n => hurtFields.IndexOf(n)).ToArray();
        var hits = new List<FrameBox[]?>();
        var anyHits = false;
        var hurtSteps = new List<int>();
        var hurtRows = new List<FrameHurt?[]>();

        var times = new List<uint>();
        var rows = new List<PlayerFrame?[]>();
        var events = new List<FrameEvent>();
        string? stats = null;
        var footSteps = -1;
        while (it.MoveNext())
        {
            JsonElement line;
            try
            {
                line = JsonDocument.Parse(it.Current).RootElement.Clone();
            }
            catch (JsonException)
            {
                return Failed(replay, "line " + (times.Count + 2) + " isn't JSON");
            }
            if (line.ValueKind != JsonValueKind.Object) continue;
            if (line.TryGetProperty("t", out var t) && line.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.Array)
            {
                var time = (uint)Math.Max(0, t.GetDouble());
                if (times.Count > 0 && time <= times[^1]) continue;
                var row = new PlayerFrame?[Math.Max(players.Count, p.GetArrayLength())];
                var i = 0;
                foreach (var v in p.EnumerateArray())
                {
                    if (v.ValueKind == JsonValueKind.Array)
                    {
                        row[i] = new PlayerFrame(
                            (float)Num(v, cols[0]), (float)Num(v, cols[1]), (float)Num(v, cols[2]), (float)Num(v, cols[3]), Num(v, cols[4]) != 0,
                            (int)Num(v, cols[5]), (float)Num(v, cols[6]), (int)Num(v, cols[7]), Num(v, cols[8]) != 0, Num(v, cols[9]) != 0,
                            Intern(v, cols[10]), (int)Num(v, cols[11]), Intern(v, cols[12]), (float)Num(v, cols[13]), Intern(v, cols[14]),
                            cols[15] < 0 ? -1 : (int)Num(v, cols[15]), cols[16] < 0 ? -1 : (int)Num(v, cols[16]),
                            cols[17] >= 0 && cols[17] < v.GetArrayLength() && v[cols[17]].ValueKind == JsonValueKind.Number ? (int)Num(v, cols[17]) : -1);
                    }
                    i++;
                }
                if (level.Moving.Length > 0 && line.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Array)
                {
                    var moved = new FrameLine[level.Moving.Length];
                    var k = 0;
                    foreach (var c in d.EnumerateArray())
                    {
                        if (k >= moved.Length) break;
                        moved[k] = c.ValueKind == JsonValueKind.Array
                            ? Line(c, level.Moving[k].Kind)
                            : level.Moving[k];
                        k++;
                    }
                    for (; k < moved.Length; k++) moved[k] = level.Moving[k];
                    movingSteps.Add(times.Count);
                    movingLines.Add(moved);
                }
                FrameItem[]? stepItems = null;
                if (itemCols[0] >= 0 && itemCols[1] >= 0 && line.TryGetProperty("i", out var its) && its.ValueKind == JsonValueKind.Array)
                {
                    stepItems = its.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Array)
                        .Select(v => new FrameItem((float)Num(v, itemCols[0]), (float)Num(v, itemCols[1]), itemCols[2] < 0 ? 0 : (int)Num(v, itemCols[2]),
                            Intern(v, itemCols[3]), itemCols[4] < 0 ? -1 : (int)Num(v, itemCols[4])))
                        .ToArray();
                    anyItems |= stepItems.Length > 0;
                }
                items.Add(stepItems);
                FrameBox[]? stepHits = null;
                if (hitCols.Take(4).All(c => c >= 0) && line.TryGetProperty("h", out var hs) && hs.ValueKind == JsonValueKind.Array)
                {
                    stepHits = hs.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Array)
                        .Select(v => new FrameBox((float)Num(v, hitCols[0]), (float)Num(v, hitCols[1]), (float)Num(v, hitCols[2]), (float)Num(v, hitCols[3]),
                            hitCols[4] >= 0 && Num(v, hitCols[4]) == 1,
                            hitCols[5] >= 0 && hitCols[5] < v.GetArrayLength() && v[hitCols[5]].ValueKind == JsonValueKind.Number ? (int)Num(v, hitCols[5]) : -1))
                        .Where(b => b.Rx > 0 && b.Ry > 0)
                        .ToArray();
                    anyHits |= stepHits.Length > 0;
                }
                hits.Add(stepHits);
                if (hurtCols.All(c => c >= 0) && line.TryGetProperty("u", out var us) && us.ValueKind == JsonValueKind.Array)
                {
                    var hurt = new FrameHurt?[us.GetArrayLength()];
                    var k = 0;
                    foreach (var v in us.EnumerateArray())
                    {
                        if (v.ValueKind == JsonValueKind.Array)
                            hurt[k] = new FrameHurt((float)Num(v, hurtCols[0]), (float)Num(v, hurtCols[1]), (float)Num(v, hurtCols[2]), (float)Num(v, hurtCols[3]));
                        k++;
                    }
                    hurtSteps.Add(times.Count);
                    hurtRows.Add(hurt);
                }
                times.Add(time);
                rows.Add(row);
            }
            else if (line.TryGetProperty("steps", out var n))
            {
                footSteps = n.ValueKind == JsonValueKind.Number ? n.GetInt32() : -1;
                if (line.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object && st.TryGetProperty("players", out _))
                    stats = st.GetRawText();
                if (line.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in ev.EnumerateArray())
                        events.Add(new FrameEvent(Str(e, "e"), (uint)Math.Max(0, Int(e, "t")), Int(e, "p"),
                            e.TryGetProperty("damage", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0, Int(e, "by", -1)));
                }
            }
        }
        if (footSteps < 0) return Failed(replay, "the dump ends early, so the game may have closed while writing it");
        if (times.Count == 0) return Failed(replay, "the dump has no steps");
        while (players.Count < rows.Max(r => r.Length)) players.Add(new FramePlayer(players.Count, "Player " + (players.Count + 1), "", "", 0));
        return new ReplayFrames
        {
            Replay = replay, StepMs = Math.Max(1, Int(head, "step", 16)), Start = (uint)Math.Max(0, Int(head, "start")),
            End = (uint)Math.Max(0, Int(head, "end")), Players = players, Times = [.. times], Rows = [.. rows], Events = events,
            Level = level, MovingSteps = [.. movingSteps], MovingLines = [.. movingLines], Items = anyItems ? [.. items] : [],
            Hits = anyHits ? [.. hits] : [], HurtSteps = [.. hurtSteps], HurtRows = [.. hurtRows], Looks = looks, StatsJson = stats
        };
    }

    static List<FrameLook> ReadLooks(JsonElement head)
    {
        var result = new List<FrameLook>();
        if (!head.TryGetProperty("lookFields", out var lf) || lf.ValueKind != JsonValueKind.Array ||
            !head.TryGetProperty("looks", out var ls) || ls.ValueKind != JsonValueKind.Array) return result;
        var fields = lf.EnumerateArray().Select(x => x.ToString()).ToList();
        var c = new[] { "animFile", "animClass", "scale", "weaponSkin", "hideItem" }.Select(n => fields.IndexOf(n)).ToArray();
        if (c[0] < 0 || c[1] < 0) return result;
        foreach (var row in ls.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) return [];
            string? S(int i) => i >= 0 && i < row.GetArrayLength() && row[i].ValueKind == JsonValueKind.String ? row[i].GetString() : null;
            double N(int i, double fallback) => i >= 0 && i < row.GetArrayLength() && row[i].ValueKind == JsonValueKind.Number ? row[i].GetDouble() : fallback;
            result.Add(new FrameLook(S(c[0]) ?? "", S(c[1]) ?? "", (float)N(c[2], 1), S(c[3]), N(c[4], 0) != 0));
        }
        return result;
    }

    private static string? OptStr(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static FrameLevel ReadLevel(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return FrameLevel.None;
        (float, float, float, float)? camera = null;
        if (o.TryGetProperty("camera", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 4)
        {
            var v = c.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetSingle() : float.NaN).ToArray();
            if (v.All(float.IsFinite) && v[2] > 0 && v[3] > 0) camera = (v[0], v[1], v[2], v[3]);
        }
        var kill = o.TryGetProperty("kill", out var k) && k.ValueKind == JsonValueKind.Array && k.GetArrayLength() == 4 &&
                   k.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number)
            ? k.EnumerateArray().Select(x => (int)Math.Clamp(x.GetDouble(), 0, 100000)).ToArray()
            : [];
        FrameLine[] Lines(string name) =>
            o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() >= 4)
                    .Select(x => Line(x, x.GetArrayLength() > 5 && x[5].ValueKind == JsonValueKind.String ? x[5].GetString()! : "")).ToArray()
                : [];
        return new FrameLevel(Str(o, "name"), camera, kill, Lines("lines"), Lines("moving"));
    }

    static FrameLine Line(JsonElement a, string kind)
    {
        float N(int i) => i < a.GetArrayLength() && a[i].ValueKind == JsonValueKind.Number ? a[i].GetSingle() : 0;
        return new FrameLine(N(0), N(1), N(2), N(3), kind, N(4) != 0);
    }

    public int IndexAt(uint time)
    {
        if (Times.Length == 0 || time < Times[0]) return -1;
        var i = Array.BinarySearch(Times, time);
        return i >= 0 ? i : ~i - 1;
    }

    public PlayerFrame? At(int player, uint time)
    {
        var i = IndexAt(time);
        if (i < 0) return null;
        var a = Get(i, player);
        if (a is not { } now || i + 1 >= Times.Length || Get(i + 1, player) is not { } next) return a;
        if (now.KnockedOut != next.KnockedOut) return now;
        var k = (float)(time - Times[i]) / Math.Max(1, Times[i + 1] - Times[i]);
        return now with { X = now.X + (next.X - now.X) * k, Y = now.Y + (next.Y - now.Y) * k };
    }

    public PlayerFrame? Get(int step, int player) =>
        step >= 0 && step < Rows.Length && player >= 0 && player < Rows[step].Length ? Rows[step][player] : null;

    public (float MinX, float MinY, float MaxX, float MaxY) Bounds()
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var row in Rows)
        {
            foreach (var p in row)
            {
                if (p is not { } f || f.KnockedOut) continue;
                minX = Math.Min(minX, f.X); maxX = Math.Max(maxX, f.X);
                minY = Math.Min(minY, f.Y); maxY = Math.Max(maxY, f.Y);
            }
        }
        return minX > maxX ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);
    }

    public static ReplayFrames Failed(string replay, string why) => new() { Replay = replay, Error = why };

    private static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static int Int(JsonElement o, string name, int fallback = 0) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : fallback;
}
