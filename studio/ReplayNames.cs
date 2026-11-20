using System.Globalization;
using System.Xml.Linq;
using BifrostStudio.Render;

namespace BifrostStudio;

public sealed record ReplayLevel(uint Id, string Name, string Shown, string File, string Asset)
{
    public string Thumbnail { get; init; } = "";
}

public sealed class ReplayNames
{
    static readonly object gate = new();
    static string? cachedFor;
    static ReplayNames? cached;

    public static readonly ReplayNames Empty = new();

    readonly Dictionary<uint, ReplayLevel> levels = [];
    readonly Dictionary<uint, (string Name, string Shown)> heroes = [];
    readonly Dictionary<uint, (string Name, string Key)> scorings = [];
    readonly Dictionary<uint, string> costumes = [];
    readonly Dictionary<uint, (string File, string Symbol)> portraits = [];

    public (string File, string Symbol)? Icon(int hero) => portraits.TryGetValue((uint)hero, out var p) ? (p.File, p.Symbol + "M") : null;

    public (string File, string Symbol)? Portrait(int hero) => portraits.TryGetValue((uint)hero, out var p) ? p : null;
    readonly Dictionary<string, string> english = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<uint, ReplayLevel> Levels => levels;

    public static ReplayNames For(string? game)
    {
        if (game == null) return Empty;
        var stamp = string.Join("|", Swz.Archives.Select(a => Path.Combine(game, a + ".swz")).Append(Path.Combine(game, "languages", "language.1.bin"))
            .Where(File.Exists).Select(f => f + ":" + File.GetLastWriteTimeUtc(f).Ticks));
        lock (gate)
        {
            if (cachedFor == stamp && cached != null) return cached;
        }
        var names = new ReplayNames();
        try { names.Fill(GameTables.Of(game), Bmod.BmodGameData.English(game)); }
        catch (Exception e) when (e is SwzException or IOException or UnauthorizedAccessException or InvalidDataException) { names = Empty; }
        lock (gate)
        {
            cachedFor = stamp;
            cached = names;
        }
        return names;
    }

    public static ReplayNames From(XElement? levels, XElement? heroes, XElement? scorings, string? costumes, IReadOnlyDictionary<string, string> english)
    {
        var n = new ReplayNames();
        n.Read(levels, heroes, scorings, costumes, english);
        return n;
    }

    void Fill(GameTables tables, IReadOnlyDictionary<string, string> strings) =>
        Read(tables.RawXml("LevelTypes"), tables.RawXml("HeroTypes"), tables.RawXml("ScoringTypes"), tables.RawText("costumeTypes"), strings);

    static uint Id(string? text) => uint.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    void Read(XElement? levelTable, XElement? heroTable, XElement? scoringTable, string? costumeTable, IReadOnlyDictionary<string, string> strings)
    {
        foreach (var (k, v) in strings) english[k] = v;
        foreach (var r in levelTable?.Elements() ?? [])
        {
            var id = Id(r.Element("LevelID")?.Value);
            var name = (string?)r.Attribute("LevelName") ?? "";
            if (id == 0 || name.Length == 0 || levels.ContainsKey(id)) continue;
            levels[id] = new ReplayLevel(id, name, r.Element("DisplayName")?.Value.Trim() ?? "", r.Element("FileName")?.Value.Trim() ?? "", r.Element("AssetName")?.Value.Trim() ?? "")
                { Thumbnail = r.Element("ThumbnailPNGFile")?.Value.Trim() ?? "" };
        }
        foreach (var r in heroTable?.Elements() ?? [])
        {
            var id = Id(r.Element("HeroID")?.Value);
            if (id == 0 || heroes.ContainsKey(id)) continue;
            var name = (string?)r.Attribute("HeroName") ?? "";
            var shown = r.Element("BioName")?.Value.Trim() is { Length: > 0 } bio ? bio : name;
            heroes[id] = (name, shown);
            if (r.Element("Portrait")?.Value.Trim() is { Length: > 0 } portrait && portrait != "--")
                portraits[id] = (r.Element("PortraitFileName")?.Value.Trim() is { Length: > 0 } f ? f : "UI_Icons", portrait);
        }
        foreach (var r in scoringTable?.Elements() ?? [])
        {
            var id = Id(r.Element("ScoringID")?.Value);
            if (id == 0 || scorings.ContainsKey(id)) continue;
            scorings[id] = ((string?)r.Attribute("ScoringName") ?? "", r.Element("DisplayNameKey")?.Value.Trim() ?? "");
        }
        if (costumeTable != null)
        {
            var rows = CsvTable.Records(costumeTable);
            if (rows.Count > 2)
            {
                var head = CsvTable.Split(rows[1]);
                var nameAt = head.IndexOf("CostumeName");
                var idAt = head.IndexOf("CostumeID");
                if (nameAt >= 0 && idAt >= 0)
                    foreach (var line in rows.Skip(2))
                    {
                        var cells = CsvTable.Split(line);
                        if (cells.Count > Math.Max(nameAt, idAt) && Id(cells[idAt]) is var cid && cid != 0) costumes.TryAdd(cid, cells[nameAt]);
                    }
            }
        }
    }

    public string Text(string key) => english.TryGetValue(key, out var v) && v.Length > 0 ? v : key;

    public ReplayLevel? Level(uint id) => levels.GetValueOrDefault(id);

    public string LevelShown(uint id) => levels.TryGetValue(id, out var l) ? (l.Shown.Length > 0 ? l.Shown : Spaced(l.Name)) : "";

    public string HeroName(uint id) => heroes.TryGetValue(id, out var h) ? h.Name : "";

    public int HeroId(string name, string shown = "")
    {
        foreach (var (id, h) in heroes)
            if (name.Length > 0 && h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return (int)id;
        foreach (var (id, h) in heroes)
            if (shown.Length > 0 && h.Shown.Equals(shown, StringComparison.CurrentCultureIgnoreCase)) return (int)id;
        return 0;
    }

    public string HeroShown(uint id) => heroes.TryGetValue(id, out var h) ? h.Shown : id == 0 ? "" : "Legend " + id;

    public IReadOnlyDictionary<int, string> HeroNames => heroes.ToDictionary(h => (int)h.Key, h => h.Value.Shown);

    public string CostumeName(uint id) => costumes.GetValueOrDefault(id) ?? "";

    public string ScoringName(uint id) => scorings.TryGetValue(id, out var s) ? s.Name : "";

    public string ScoringShown(uint id) => scorings.TryGetValue(id, out var s) && s.Key.Length > 0 ? Text(s.Key) : "";

    public static string Spaced(string name) => System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
}
