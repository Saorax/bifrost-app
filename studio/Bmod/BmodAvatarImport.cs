using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BifrostStudio.Render;

namespace BifrostStudio.Bmod;

public sealed class BmodAvatarImport : IBmodMade
{
    static readonly Regex Label = new(@"^\s*(.+?)\s*=\s*(.+?)(?:\s+Avatar)?\s*$", RegexOptions.CultureInvariant);
    static readonly Regex NotAlnum = new("[^a-z0-9]", RegexOptions.CultureInvariant);

    sealed class Touched(Dictionary<string, string> row)
    {
        public Dictionary<string, string> Row { get; } = row;
        public string? IconSprite { get; set; }
        public (int Id, string Name, byte[] Data)? IconPng { get; set; }
        public Dictionary<string, (int Id, string Name, byte[] Data)> Flags { get; } = new(StringComparer.Ordinal);
    }

    public required string ModId { get; init; }
    public required string Kind { get; init; }
    public required string Message { get; init; }
    public required List<string> Report { get; init; }
    public required OrderedDictionary<string, object?> Manifest { get; init; }
    public required List<(string Rel, byte[] Data)> Files { get; init; }

    static string Cell(Dictionary<string, string> row, string column) => BmodTables.Cell(row, column);

    static string Norm(string? text) => NotAlnum.Replace((text ?? "").ToLowerInvariant(), "");

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

    public static string CleanText(string? text)
    {
        text = Regex.Replace(text ?? "", @"<el>\s*", "- ");
        text = text.Replace("</el>", "\n", StringComparison.Ordinal);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        return BmodTables.Trim(text);
    }

    public static List<(int Id, string Name, byte[] Data)> ModFiles(BmodFile mod)
    {
        var data = BmodSwf.BinaryDataOf(mod.Swf.Tags);
        var output = new List<(int, string, byte[])>();
        if (mod.Meta["files"] is not JsonObject files) return output;
        var ids = new List<int>();
        foreach (var (k, _) in files)
            if (int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) ids.Add(id);
        ids.Sort();
        foreach (var id in ids)
            if (data.TryGetValue(id, out var tag)) output.Add((id, Text(files[id.ToString(CultureInfo.InvariantCulture)]), tag.Copy()));
        return output;
    }

    static Touched Entry(OrderedDictionary<string, Touched> touched, Dictionary<string, string> row)
    {
        var name = Cell(row, "AvatarName");
        if (!touched.TryGetValue(name, out var t)) touched[name] = t = new Touched(row);
        return t;
    }

    public static BmodAvatarImport Make(BmodFile mod, string fileName, BmodGameData game)
    {
        var report = new List<string>();
        var meta = mod.Meta;
        var at = game.Tables.Table("avatarTypes");
        var raw = game.Tables.Lines("spriteData");
        var sHeader = CsvTable.Split(raw[0]);
        var sRows = new List<Dictionary<string, string>>();
        for (var i = 1; i < raw.Count; i++)
        {
            if (raw[i] == "") continue;
            var fields = CsvTable.Split(raw[i]);
            if (fields.Count >= 3) sRows.Add(BmodTables.RowOf(sHeader, fields));
        }
        var byIcon = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var byFlag = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var row in at.Rows)
        {
            if (Cell(row, "IconName") != "") byIcon[Cell(row, "IconName")] = row;
            byFlag.TryAdd(Cell(row, "FlagArt"), row);
        }
        var spriteRows = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
        var fileSet = new Dictionary<string, (string Set, string Bone)>(StringComparer.Ordinal);
        foreach (var row in sRows)
        {
            var setName = Cell(row, "SetName");
            if (!spriteRows.TryGetValue(setName, out var list)) spriteRows[setName] = list = [];
            list.Add(row);
            var file = Cell(row, "File");
            fileSet[file[(file.LastIndexOf('/') + 1)..].ToLowerInvariant()] = (setName, Cell(row, "BoneName"));
        }
        var touched = new OrderedDictionary<string, Touched>(StringComparer.Ordinal);
        var sprites = new List<string>();
        if (meta["swfs"] is JsonObject swfs)
            foreach (var (_, spec) in swfs)
                if (spec?["sprites"] is JsonArray list)
                    foreach (var s in list) sprites.Add(Text(s));
        var unused = new List<string>();
        foreach (var sn in sprites)
        {
            if (byIcon.TryGetValue(sn, out var row)) Entry(touched, row).IconSprite = sn;
            else unused.Add(sn);
        }
        foreach (var mf in ModFiles(mod))
        {
            var name = mf.Name;
            var stem = name.LastIndexOf('.') >= 0 ? name[..name.LastIndexOf('.')] : name;
            var found = fileSet.TryGetValue(name.ToLowerInvariant(), out var fs);
            if (byIcon.TryGetValue(stem, out var row)) Entry(touched, row).IconPng = mf;
            else if (found && byFlag.TryGetValue(fs.Set, out var flagRow)) Entry(touched, flagRow).Flags[fs.Bone] = mf;
            else unused.Add(name);
        }
        if (touched.Count == 0) throw new InvalidDataException("it redraws no avatar of this game");
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in CleanText(Truthy(meta["description"]) ? Text(meta["description"]) : "").Split('\n'))
        {
            var m = Label.Match(line.Replace("\r", "", StringComparison.Ordinal));
            if (!m.Success) continue;
            var wanted = Norm(m.Groups[2].Value);
            foreach (var (tn, t) in touched)
            {
                var shownName = game.Strings(Cell(t.Row, "DisplayNameKey")) ?? "";
                var iconWord = Cell(t.Row, "IconName").Replace("a_AvatarIcon_", "", StringComparison.Ordinal);
                if (wanted == Norm(tn) || wanted == Norm(shownName) || wanted == Norm(iconWord)) labels[tn] = m.Groups[1].Value;
            }
        }
        var modName = Truthy(meta["name"]) && Text(meta["name"]) != "" ? Text(meta["name"]) : Path.GetFileNameWithoutExtension(fileName);
        var prefix = BmodTables.TokenFor(modName);
        var modId = BmodTables.ModIdOf(prefix);
        var rows = new List<object?>();
        var spriteAdds = new List<object?>();
        var assets = BmodJson.Map();
        var english = BmodJson.Map();
        var rename = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        var files = new List<(string, byte[])>();
        var pngIcons = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var row in spriteRows.GetValueOrDefault("") ?? []) pngIcons[Cell(row, "BoneName")] = row;
        var bare = modName.TrimEnd('!');
        var touchedNames = touched.Keys.ToList();
        touchedNames.Sort(StringComparer.Ordinal);
        foreach (var tn in touchedNames)
        {
            var t = touched[tn];
            var row = t.Row;
            var shown = game.Strings(Cell(row, "DisplayNameKey")) ?? tn;
            var labelled = labels.TryGetValue(tn, out var given);
            var label = labelled ? given! : shown + " (" + bare + ")";
            var token = BmodTables.TokenFor(labelled ? label : tn + " " + modName);
            var icon = "a_AvatarIcon_" + token;
            var sets = BmodJson.Map();
            sets["AvatarName"] = token;
            sets["AvatarID"] = "@id";
            sets["DisplayNameKey"] = token + "_DisplayName";
            sets["IconName"] = icon;
            sets["FlagArt"] = token;
            if (BmodTables.Trim(Cell(row, "SpriteFilePath")) != "") sets["SpriteFilePath"] = "@none";
            english[token + "_DisplayName"] = label;
            List<string> dims;
            if (t.IconSprite != null) rename[t.IconSprite] = icon;
            else if (t.IconPng != null || pngIcons.ContainsKey(Cell(row, "IconName")))
            {
                var orig = pngIcons.GetValueOrDefault(Cell(row, "IconName"));
                var gamePath = orig != null ? Cell(orig, "File") : "";
                if (t.IconPng is { } png)
                {
                    gamePath = "sprites/AutoGenerated/Sprites_Avatars/" + icon + ".png";
                    files.Add(("sprites/" + icon + ".png", png.Data));
                    assets[gamePath] = "sprites/" + icon + ".png";
                }
                dims = orig != null ? sHeader.Skip(3).Select(h => Cell(orig, h)).ToList() : ["128", "128", "1", "1"];
                var addIcon = BmodJson.Map();
                addIcon["add"] = string.Join(",", new[] { "", icon, gamePath }.Concat(dims));
                spriteAdds.Add(addIcon);
            }
            else
            {
                sets["IconName"] = Cell(row, "IconName");
                report.Add(token + ": the mod has no icon for it, so it shows " + tn + "'s");
            }
            var flagCount = 0;
            foreach (var fr in spriteRows.GetValueOrDefault(Cell(row, "FlagArt")) ?? [])
            {
                var bone = Cell(fr, "BoneName");
                var filePath = Cell(fr, "File");
                if (t.Flags.TryGetValue(bone, out var flag))
                {
                    filePath = "sprites/AutoGenerated/Sprites_Emote/" + bone + "_" + token + ".png";
                    files.Add(("sprites/" + bone + "_" + token + ".png", flag.Data));
                    assets[filePath] = "sprites/" + bone + "_" + token + ".png";
                    flagCount++;
                }
                dims = sHeader.Skip(3).Select(h => Cell(fr, h)).ToList();
                var addFlag = BmodJson.Map();
                addFlag["add"] = string.Join(",", new[] { token, bone, filePath }.Concat(dims));
                spriteAdds.Add(addFlag);
            }
            var avatarRow = BmodJson.Map();
            avatarRow["copy"] = tn;
            avatarRow["set"] = sets;
            rows.Add(avatarRow);
            report.Add(token + " \"" + label + "\": copy of " + tn + (t.IconSprite != null || t.IconPng != null ? " with the mod's icon" : "") +
                (flagCount > 0 ? ", " + flagCount + " flag pictures" : ""));
        }
        var manifest = BmodJson.Map();
        manifest["id"] = modId;
        manifest["name"] = modName;
        manifest["version"] = meta["version"] != null && Text(meta["version"]) != "" ? Text(meta["version"]) : "1.0";
        manifest["author"] = Truthy(meta["author"]) ? Text(meta["author"]) : "";
        manifest["description"] = "Imported from " + fileName + " by Bifrost: " + rows.Count + " new avatars next to the ones the mod redrew; the originals are unchanged.";
        var strings = BmodJson.Map();
        strings["English"] = english;
        manifest["strings"] = strings;
        manifest["assets"] = assets;
        var tables = BmodJson.Map();
        tables["avatarTypes"] = rows;
        if (spriteAdds.Count > 0) tables["spriteData"] = spriteAdds;
        manifest["tables"] = tables;
        if (rename.Count > 0)
        {
            var byId = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (id, name) in mod.Symbols) byId[name] = id;
            var art = BmodItemPlan.CopiedArt(mod).Art;
            var oldNames = rename.Keys.ToList();
            oldNames.Sort(StringComparer.Ordinal);
            var symbols = oldNames.Where(byId.ContainsKey).Select(o => (byId[o], rename[o])).ToList();
            var classNames = rename.Values.ToList();
            var classColours = classNames.ToDictionary(c => c, _ => (List<int>?)null, StringComparer.Ordinal);
            var swf = BmodItemPlan.ModSwf(mod, art, classNames, classColours, symbols);
            var swfName = "UI_" + prefix + ".swf";
            files.Add((swfName, swf));
            assets[swfName] = swfName;
            tables["MasterFileList"] = new List<object?> { "<File Stage=\"Required\" ExcludedPlatforms=\"\" Size=\"" + (int)Math.Max(1, Math.Floor(swf.Length / 1024.0)) + "\" Name=\"" + swfName + "\" Load=\"\" />" };
        }
        if (BmodItemImport.PreviewOf(mod) is { } preview)
        {
            files.Add((preview.Name, preview.Data));
            manifest["preview"] = preview.Name;
        }
        if (unused.Count > 0) report.Add("left out (" + unused.Count + ", not an avatar's icon or flag): " + string.Join(", ", unused.Take(12)));
        return new BmodAvatarImport
        {
            ModId = modId, Kind = rows.Count + " avatars", Report = report, Manifest = manifest, Files = files,
            Message = modName + ": " + rows.Count + (rows.Count == 1 ? " new avatar." : " new avatars."),
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
