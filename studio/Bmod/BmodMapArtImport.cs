using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BifrostStudio.Render;

namespace BifrostStudio.Bmod;

public sealed record BmodLevelType(string Name, bool DevOnly, bool TestLevel, string? Display, string? Thumb);

public sealed record BmodLevelData(List<XElement> Descs, List<BmodLevelType> Types, List<(string Name, List<string> Levels)> Sets)
{
    static string? FieldText(XElement row, string tag)
    {
        var f = row.Elements().FirstOrDefault(e => e.Name.LocalName == tag);
        return f == null ? null : string.Concat(f.Nodes().OfType<XText>().Select(t => t.Value));
    }

    public static BmodLevelData Of(string game, GameTables tables)
    {
        var descs = new List<XElement>();
        foreach (var text in LevelDescs.All(game).Values)
        {
            try
            {
                if (XDocument.Parse(text).Root is { } root) descs.Add(root);
            }
            catch (System.Xml.XmlException) { }
        }
        var types = new List<BmodLevelType>();
        if (tables.RawXml("LevelTypes") is { } lt)
            foreach (var row in lt.Elements())
            {
                if (row.Name.LocalName != "LevelType" || row.Attribute("LevelName") is not { } name) continue;
                types.Add(new BmodLevelType(name.Value, (string?)row.Attribute("DevOnly") == "true", (string?)row.Attribute("TestLevel") == "true",
                    FieldText(row, "DisplayName"), FieldText(row, "ThumbnailPNGFile")));
            }
        var sets = new List<(string, List<string>)>();
        if (tables.RawXml("LevelSetTypes") is { } ls)
            foreach (var row in ls.Elements())
            {
                if (row.Name.LocalName != "LevelSetType" || row.Attribute("LevelSetName") is not { } name) continue;
                var levels = FieldText(row, "LevelTypes");
                sets.Add((name.Value, levels == null ? [] : levels.Split(',').ToList()));
            }
        return new BmodLevelData(descs, types, sets);
    }
}

public sealed class BmodMapArtImport : IBmodMade
{
    static readonly (string Kind, string[] Sets)[] MapTypes =
    [
        ("1v1", ["Standard1v1", "Ranked1v1"]), ("2v2", ["Standard2v2", "Ranked2v2", "Unranked2v2"]), ("FFA", ["StandardFFA", "AltFFA"]),
        ("Big", ["StandardBig", "Standard3v3"]),
        ("Game modes", ["GameModeAll", "BrawlballSmall", "BrawlballBig", "SoccerAll", "HordeAll", "CTF3v3", "Showdown", "VolleyBattle", "ClimbAll", "BuddyAll", "RingAll"]),
    ];

    public required string ModId { get; init; }
    public required string Kind { get; init; }
    public required string Message { get; init; }
    public required List<string> Report { get; init; }
    public required int MovedLineAt { get; init; }
    public required OrderedDictionary<string, object?> Manifest { get; init; }
    public required List<(string Rel, byte[] Data)> Files { get; init; }

    static void AddTo(OrderedDictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (!list.Contains(value)) list.Add(value);
    }

    static string Trim(string? s) => BmodTables.Trim(s);

    public static (OrderedDictionary<string, List<string>> Refs, Dictionary<string, string> Names, OrderedDictionary<string, List<string>> Thumbs) LevelArt(BmodLevelData levels)
    {
        var refs = new OrderedDictionary<string, List<string>>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var thumbs = new OrderedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var root in levels.Descs)
        {
            if (root.Attribute("LevelName") is not { } levelAttr) continue;
            var level = levelAttr.Value;
            var dir = (string?)root.Attribute("AssetDir") ?? "";
            var basePath = "mapArt/" + (dir != "" ? dir + "/" : "");
            var stack = new List<XElement> { root };
            while (stack.Count > 0)
            {
                var node = stack[0];
                stack.RemoveAt(0);
                if (node.Attribute("AssetName") is { } assetAttr)
                {
                    var name = assetAttr.Value;
                    if (name != "" && !name.StartsWith(".png", StringComparison.Ordinal))
                    {
                        var path = node.Name.LocalName == "Background" ? "mapArt/Backgrounds/" + name :
                            name.StartsWith("../", StringComparison.Ordinal) ? "mapArt/" + name[3..] : basePath + name;
                        AddTo(refs, path, level);
                    }
                }
                stack.InsertRange(0, node.Elements());
            }
        }
        foreach (var lt in levels.Types)
        {
            if (lt.DevOnly || lt.TestLevel) continue;
            names[lt.Name] = lt.Display ?? lt.Name;
            var thumb = Trim(lt.Thumb);
            if (thumb != "")
            {
                var tp = "images/thumbnails/" + thumb;
                AddTo(thumbs, tp, lt.Name);
                AddTo(refs, tp, lt.Name);
            }
        }
        return (refs, names, thumbs);
    }

    public static Dictionary<string, List<string>> KindsOf(BmodLevelData levels)
    {
        var sets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, list) in levels.Sets)
            foreach (var raw in list)
            {
                var level = Trim(raw);
                if (!sets.TryGetValue(level, out var l)) sets[level] = l = [];
                l.Add(name);
            }
        var output = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, members) in sets)
        {
            var kinds = MapTypes.Where(mt => mt.Sets.Any(members.Contains)).Select(mt => mt.Kind).ToList();
            output[key] = kinds.Count > 0 ? kinds : ["Other"];
        }
        return output;
    }

    static string FolderOf(string reference) => reference[..Math.Max(0, reference.LastIndexOf('/'))];

    static string Text(JsonNode? node) => node == null ? "" : node.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number => BmodJson.Number(node.GetValue<double>()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => node.ToJsonString(),
    };

    static bool Truthy(JsonNode? node) => node != null && node.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>() != "",
        JsonValueKind.Number => node.GetValue<double>() != 0,
        JsonValueKind.False or JsonValueKind.Null => false,
        _ => true,
    };

    public static BmodMapArtImport Make(BmodFile mod, string fileName, BmodLevelData levels)
    {
        var meta = mod.Meta;
        var report = new List<string>();
        if (levels.Descs.Count == 0) throw new InvalidDataException("the game's maps (LevelDesc) weren't read");
        var (refs, names, thumbs) = LevelArt(levels);
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in refs.Keys)
        {
            var key = r[(r.LastIndexOf('/') + 1)..].ToLowerInvariant();
            if (!byName.TryGetValue(key, out var list)) byName[key] = list = [];
            list.Add(r);
        }
        var files = BmodAvatarImport.ModFiles(mod);
        var found = new List<List<string>>();
        var placed = new List<string?>();
        foreach (var f in files)
        {
            var cands = byName.GetValueOrDefault(f.Name.ToLowerInvariant()) ?? [];
            found.Add(cands);
            placed.Add(cands.Count == 1 ? cands[0] : null);
        }
        for (var i = 0; i < found.Count; i++)
        {
            var c = found[i];
            if (c.Count < 2) continue;
            var order = BmodTables.StableSort(Enumerable.Range(0, files.Count).ToList(), Enumerable.Range(0, files.Count).Select(j => new[] { Math.Abs(j - i) }).ToList(), false);
            string? pick = null;
            foreach (var j in order)
            {
                if (j == i || placed[j] == null) continue;
                var d = FolderOf(placed[j]!);
                if (!d.StartsWith("mapArt/", StringComparison.Ordinal) || d.EndsWith("Backgrounds", StringComparison.Ordinal)) continue;
                pick = c.FirstOrDefault(cand => FolderOf(cand) == d);
                if (pick != null) break;
            }
            if (pick == null)
            {
                var sorted = c.ToList();
                sorted.Sort(StringComparer.Ordinal);
                pick = sorted[0];
                report.Add(files[i].Name + " could be " + string.Join(", ", sorted) + ": took " + pick);
            }
            placed[i] = pick;
        }
        var display = Truthy(meta["name"]) ? Text(meta["name"]) : Path.GetFileNameWithoutExtension(fileName);
        var modId = BmodTables.ModIdOf(BmodTables.TokenFor(display));
        var primary = new OrderedDictionary<string, byte[]>(StringComparer.Ordinal);
        var variants = new OrderedDictionary<string, List<(int Id, byte[] Data)>>(StringComparer.Ordinal);
        var unused = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var reference = placed[i];
            if (reference == null)
            {
                unused.Add(f.Name);
                continue;
            }
            if (primary.TryGetValue(reference, out var earlier))
            {
                if (earlier.AsSpan().SequenceEqual(f.Data)) continue;
                if (!variants.TryGetValue(reference, out var vlist)) variants[reference] = vlist = [];
                vlist.Add((f.Id, earlier));
            }
            primary[reference] = f.Data;
        }
        if (primary.Count == 0) throw new InvalidDataException("none of its " + files.Count + " files is art a map of this game reads");
        var movedLineAt = report.Count;
        var output = new List<(string, byte[])>();
        foreach (var (reference, data) in primary) output.Add((reference, data));
        var variantFiles = new OrderedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (reference, list) in variants)
            foreach (var (id, data) in list)
            {
                var local = "variants/" + id + "_" + reference[(reference.LastIndexOf('/') + 1)..];
                output.Add((local, data));
                AddTo(variantFiles, reference, local);
            }
        var own = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (reference, lvls) in refs)
        {
            if (!reference.StartsWith("mapArt/", StringComparison.Ordinal)) continue;
            foreach (var lvl in lvls)
            {
                if (!own.TryGetValue(lvl, out var l)) own[lvl] = l = [];
                l.Add(reference);
            }
        }
        var levelList = new List<string>();
        foreach (var reference in primary.Keys)
            foreach (var lvl in refs[reference])
                if (!levelList.Contains(lvl)) levelList.Add(lvl);
        levelList.Sort(StringComparer.Ordinal);
        var maps = BmodJson.Map();
        foreach (var lvl in levelList)
        {
            var mine = own.GetValueOrDefault(lvl) ?? [];
            var has = mine.Count(primary.ContainsKey);
            var background = mine.Any(r => primary.ContainsKey(r) && r.StartsWith("mapArt/Backgrounds/", StringComparison.Ordinal));
            if (names.TryGetValue(lvl, out var shownName) && (background || (mine.Count > 0 && has * 2 >= mine.Count))) maps[lvl] = shownName;
        }
        var kinds = KindsOf(levels);
        var manifest = BmodJson.Map();
        manifest["id"] = modId;
        manifest["name"] = display;
        manifest["version"] = meta["version"] != null && Text(meta["version"]) != "" ? Text(meta["version"]) : "1.0";
        manifest["author"] = Truthy(meta["author"]) ? Text(meta["author"]) : "";
        manifest["description"] = (Truthy(meta["description"]) ? BmodAvatarImport.CleanText(Text(meta["description"])) + "\n\n" : "") + "Imported from " + fileName +
            " by Bifrost: art for " + maps.Count + " maps, picked per map in the Mods screen; the game's own art is unchanged.";
        var mapArt = BmodJson.Map();
        var fileList = primary.Keys.ToList();
        fileList.Sort(StringComparer.Ordinal);
        mapArt["files"] = fileList.Cast<object?>().ToList();
        mapArt["maps"] = maps;
        var thumbOut = BmodJson.Map();
        foreach (var reference in fileList)
        {
            if (!thumbs.TryGetValue(reference, out var tl)) continue;
            var lv = tl.Where(maps.ContainsKey).ToList();
            lv.Sort(StringComparer.Ordinal);
            thumbOut[reference] = lv.Cast<object?>().ToList();
        }
        mapArt["thumbnails"] = thumbOut;
        var typesOut = BmodJson.Map();
        foreach (var lvl in maps.Keys) typesOut[lvl] = (kinds.GetValueOrDefault(lvl) ?? ["Other"]).Cast<object?>().ToList();
        mapArt["mapTypes"] = typesOut;
        if (variantFiles.Count > 0)
        {
            var vSorted = BmodJson.Map();
            var vKeys = variantFiles.Keys.ToList();
            vKeys.Sort(StringComparer.Ordinal);
            foreach (var reference in vKeys) vSorted[reference] = variantFiles[reference].Cast<object?>().ToList();
            mapArt["variants"] = vSorted;
        }
        manifest["mapArt"] = mapArt;
        if (BmodItemImport.PreviewOf(mod) is { } preview)
        {
            output.Add((preview.Name, preview.Data));
            manifest["preview"] = preview.Name;
        }
        if (unused.Count > 0)
            report.Add("left out (" + unused.Count + ", no map reads them): " + string.Join(", ", unused.Take(12)) + (unused.Count > 12 ? " ..." : ""));
        var shown = new List<string>();
        foreach (var lvl in maps.Keys)
            if (maps[lvl] is string s && !shown.Contains(s)) shown.Add(s);
        shown.Sort(StringComparer.Ordinal);
        report.Add("map art: " + primary.Count + " files for " + maps.Count + " maps (" + string.Join(", ", shown.Take(10)) + ")");
        return new BmodMapArtImport
        {
            ModId = modId, Kind = "map art for " + maps.Count + " maps", Report = report, MovedLineAt = movedLineAt, Manifest = manifest, Files = output,
            Message = display + ": art for " + maps.Count + (maps.Count == 1 ? " map" : " maps") + " (" + string.Join(", ", shown.Take(3)) +
                (shown.Count > 3 ? ", ..." : "") + "), picked per map in Mods, Map art.",
        };
    }

    public void WriteTo(string folder, object? source)
    {
        Directory.CreateDirectory(folder);
        foreach (var (rel, data) in Files)
        {
            var path = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        var manifest = BmodJson.Map();
        foreach (var key in Manifest.Keys) manifest[key] = Manifest[key];
        manifest["source"] = source;
        File.WriteAllText(Path.Combine(folder, "mod.json"), BmodJson.Write(manifest) + "\n", new UTF8Encoding(false));
    }
}
