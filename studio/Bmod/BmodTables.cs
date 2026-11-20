using System.Text;
using System.Text.RegularExpressions;
using BifrostStudio.Render;

namespace BifrostStudio.Bmod;

public sealed class BmodTable(List<string> header, List<Dictionary<string, string>> rows)
{
    public List<string> Header { get; } = header;
    public List<Dictionary<string, string>> Rows { get; } = rows;
}

public sealed record BmodItem(string Table, string Key, Dictionary<string, string> Row, List<string> Header,
    OrderedDictionary<string, List<string>> Hits, OrderedDictionary<string, string> Columns);

public sealed class BmodTableSource
{
    public static readonly string[] CsvTables = ["costumeTypes", "weaponSkinTypes", "powerTypes", "avatarTypes", "spriteData"];
    readonly Dictionary<string, string> texts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, BmodTable> cache = new(StringComparer.OrdinalIgnoreCase);

    public BmodTableSource(Func<string, string?> rawText)
    {
        foreach (var name in CsvTables)
            if (rawText(name) is { } text) texts[name] = WithoutName(text);
    }

    public static BmodTableSource Of(GameTables tables) => new(tables.RawText);

    public static BmodTableSource FromFolder(string tabledata) =>
        new(name => Path.Combine(tabledata, "Game", name + ".csv") is var p && File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null);

    static string WithoutName(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? "" : text[(newline + 1)..];
    }

    public List<string> Lines(string name)
    {
        if (!texts.TryGetValue(name, out var text)) throw new InvalidDataException("the game's " + name + " table wasn't read");
        return CsvTable.Records(text).Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
    }

    public BmodTable Table(string name)
    {
        if (cache.TryGetValue(name, out var cached)) return cached;
        var raw = Lines(name);
        var header = CsvTable.Split(raw[0]);
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in raw.Skip(1))
        {
            if (line.Length == 0) continue;
            var fields = CsvTable.Split(line);
            if (fields[0] == "" || fields[0] == "Template") continue;
            rows.Add(BmodTables.RowOf(header, fields));
        }
        return cache[name] = new BmodTable(header, rows);
    }
}

public static class BmodTables
{
    public static readonly string[] CostumeArt =
    [
        "GfxType.CustomArt", "GfxType.CustomArt2", "GfxType.CustomArt3", "GfxType.CustomArt4", "GfxType.CustomArt5",
        "GfxType.CustomArt6", "GfxType.CustomArt7", "GfxType.CustomArt8", "GfxType.CustomArt9", "GfxType.CustomArt10",
    ];
    public static readonly string[] CostumeSwap = ["SwapCustomArt", "SwapCustomArt2", "SwapCustomArt3", "SwapCustomArt4", "SwapCustomArt5"];
    public static readonly string[] WeaponArt = ["CustomArt", "CustomArt2", "CustomArt3"];

    static readonly Regex NotIdChars = new("[^a-z0-9._-]+", RegexOptions.CultureInvariant);

    public static List<string> SpriteNames(System.Text.Json.Nodes.JsonObject meta)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        if (meta["swfs"] is System.Text.Json.Nodes.JsonObject swfs)
            foreach (var (_, spec) in swfs)
                if (spec?["sprites"] is System.Text.Json.Nodes.JsonArray sprites)
                    foreach (var s in sprites)
                        if (s?.GetValueKind() == System.Text.Json.JsonValueKind.String && seen.Add(s.GetValue<string>())) output.Add(s.GetValue<string>());
        output.Sort(StringComparer.Ordinal);
        return output;
    }

    public static Dictionary<string, string> RowOf(List<string> header, List<string> fields)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = Math.Min(header.Count, fields.Count);
        for (var c = 0; c < count; c++) row[header[c]] = fields[c];
        return row;
    }

    public static string Cell(Dictionary<string, string> row, string column) => row.GetValueOrDefault(column) ?? "";

    public static string Trim(string? s) => s == null ? "" : s.Trim(' ', '\t', '\r', '\n');

    public static OrderedDictionary<string, string> Suffixes(Dictionary<string, string> row, IEnumerable<string> columns)
    {
        var output = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            var value = Trim(Cell(row, column));
            if (!value.Contains('/')) continue;
            var last = value.Split(':')[^1];
            var slash = last.IndexOf('/');
            if (slash < 0) continue;
            output.TryAdd(last[(slash + 1)..], column);
        }
        return output;
    }

    public static string CommonPart(IReadOnlyList<string> words)
    {
        var first = words[0];
        foreach (var w in words)
            if (w.Length < first.Length) first = w;
        for (var size = first.Length; size > 0; size--)
            for (var start = 0; start <= first.Length - size; start++)
            {
                var part = first.Substring(start, size);
                if (words.All(w => w.Contains(part, StringComparison.Ordinal))) return part;
            }
        return first;
    }

    public static string TokenFor(string name)
    {
        var joined = new StringBuilder();
        var word = new StringBuilder();
        for (var i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && char.IsAsciiLetterOrDigit(name[i]))
            {
                word.Append(name[i]);
                continue;
            }
            if (word.Length > 0)
            {
                joined.Append(char.ToUpperInvariant(word[0])).Append(word, 1, word.Length - 1);
                word.Clear();
            }
        }
        var all = joined.ToString();
        return "BH" + (all.Length > 40 ? all[..40] : all);
    }

    public static string ModIdOf(string token) =>
        NotIdChars.Replace(("bmod-" + (token.Length > 2 ? token[2..] : "")).ToLowerInvariant(), "-").Trim('-');

    public static List<T> StableSort<T>(IReadOnlyList<T> list, IReadOnlyList<int[]> keys, bool reverse)
    {
        var order = Enumerable.Range(0, list.Count).ToList();
        order.Sort((a, b) =>
        {
            var c = Compare(keys[a], keys[b]);
            if (reverse) c = -c;
            return c != 0 ? c : a - b;
        });
        return order.Select(i => list[i]).ToList();
    }

    static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
        return 0;
    }

    public static BmodItem FindItem(BmodTableSource tables, IReadOnlyList<string> names)
    {
        var candidates = new List<BmodItem>();
        var keys = new List<int[]>();
        foreach (var (name, art, swap) in new[] { ("costumeTypes", CostumeArt, CostumeSwap), ("weaponSkinTypes", WeaponArt, Array.Empty<string>()) })
        {
            var t = tables.Table(name);
            var columns = art.Concat(swap).ToList();
            foreach (var row in t.Rows)
            {
                var found = Suffixes(row, columns);
                if (found.Count == 0) continue;
                var hits = new OrderedDictionary<string, List<string>>(StringComparer.Ordinal);
                var score = 0;
                foreach (var s in found.Keys)
                {
                    var list = names.Where(n => n.EndsWith("_" + s, StringComparison.Ordinal)).ToList();
                    if (list.Count == 0) continue;
                    hits[s] = list;
                    score += list.Count;
                }
                if (hits.Count == 0) continue;
                var weapon = Cell(row, "BaseWeapon");
                var bonus = name == "weaponSkinTypes" && weapon != "" ? names.Count(n => n.Contains(weapon, StringComparison.Ordinal)) : 0;
                candidates.Add(new BmodItem(name, Cell(row, t.Header[0]), row, t.Header, hits, found));
                keys.Add([score, name == "costumeTypes" ? 1 : 0, bonus]);
            }
        }
        if (candidates.Count == 0)
            throw new InvalidDataException("no skin or weapon skin uses the suffixes of its sprites, so it isn't a skin or weapon skin this game has");
        return StableSort(candidates, keys, true)[0];
    }
}
