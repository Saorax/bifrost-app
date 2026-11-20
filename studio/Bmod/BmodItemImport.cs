using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BifrostStudio.Render;

namespace BifrostStudio.Bmod;

public sealed record BmodSwapRow(string Text, Dictionary<string, string> Attributes)
{
    static readonly Regex Open = new("^<PowerSwapType\\b([^>]*)>", RegexOptions.CultureInvariant);
    static readonly Regex Attribute = new("([\\w.:-]+)\\s*=\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant);
    static readonly Regex Row = new("<PowerSwapType\\b[^>]*?(?:/>|>.*?</PowerSwapType>)", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public string? Attr(string name) => Attributes.GetValueOrDefault(name);

    public string? Field(string tag)
    {
        var m = Regex.Match(Text, "<" + Regex.Escape(tag) + "(?:\\s*/>|>([^<]*)</" + Regex.Escape(tag) + ">)", RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static List<BmodSwapRow> Parse(string? text)
    {
        var rows = new List<BmodSwapRow>();
        if (string.IsNullOrEmpty(text)) return rows;
        foreach (Match m in Row.Matches(text))
        {
            var open = Open.Match(m.Value);
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (open.Success)
                foreach (Match a in Attribute.Matches(open.Groups[1].Value)) attrs.TryAdd(a.Groups[1].Value, a.Groups[2].Value);
            rows.Add(new BmodSwapRow(m.Value, attrs));
        }
        return rows;
    }
}

public sealed class BmodGameData
{
    HashSet<string>? effectNames;

    public required string Game { get; init; }
    public required BmodTableSource Tables { get; init; }
    public required List<BmodSwapRow> CostumeSwaps { get; init; }
    public required List<BmodSwapRow> HeroSwaps { get; init; }
    public Func<string, string?> Strings { get; init; } = _ => null;

    public static BmodGameData Of(string game, GameTables tables, Func<string, string?>? strings = null)
    {
        if (strings == null)
        {
            var english = English(game);
            strings = key => english.GetValueOrDefault(key);
        }
        return Make(game, BmodTableSource.Of(tables), tables.RawText("PowerSwapTypes"), strings);
    }

    public static Dictionary<string, string> English(string game)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = Path.Combine(game, "languages", "language.1.bin");
        if (!File.Exists(file)) return rows;
        try
        {
            var data = File.ReadAllBytes(file);
            using var z = new System.IO.Compression.ZLibStream(new MemoryStream(data, 4, data.Length - 4), System.IO.Compression.CompressionMode.Decompress);
            using var payload = new MemoryStream();
            z.CopyTo(payload);
            var p = payload.GetBuffer();
            var length = (int)payload.Length;
            var count = (p[0] << 24) | (p[1] << 16) | (p[2] << 8) | p[3];
            var offset = 4;
            string Utf()
            {
                var size = (p[offset] << 8) | p[offset + 1];
                offset += 2;
                if (size > length - offset) throw new InvalidDataException("truncated string");
                var value = Encoding.UTF8.GetString(p, offset, size).TrimStart('﻿');
                offset += size;
                return value;
            }
            for (var i = 0; i < count && offset + 2 <= length; i++)
            {
                var key = Utf();
                rows[key] = Utf();
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
        return rows;
    }

    public static BmodGameData Make(string game, BmodTableSource tables, string? powerSwapTypes, Func<string, string?>? strings = null)
    {
        var costume = new List<BmodSwapRow>();
        var hero = new List<BmodSwapRow>();
        foreach (var row in BmodSwapRow.Parse(powerSwapTypes))
        {
            if (row.Attributes.ContainsKey("OwnerCostume")) costume.Add(row);
            else if (row.Attributes.ContainsKey("OwnerHero") && !row.Attributes.ContainsKey("OwnerSpawnBot")) hero.Add(row);
        }
        return new BmodGameData { Game = game, Tables = tables, CostumeSwaps = costume, HeroSwaps = hero, Strings = strings ?? (_ => null) };
    }

    public bool EffectHas(string name)
    {
        if (effectNames == null)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists(Game))
                foreach (var f in Directory.EnumerateFiles(Game))
                {
                    var file = Path.GetFileName(f);
                    if (!file.StartsWith("SFX_", StringComparison.Ordinal) || !file.EndsWith(".swf", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        foreach (var (_, n) in BmodSwf.Symbols(BmodSwf.Read(File.ReadAllBytes(f)).Tags)) names.Add(n);
                    }
                    catch (Exception e) when (e is BmodException or IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
                }
            effectNames = names;
        }
        return effectNames.Contains(name);
    }
}

public interface IBmodMade
{
    string ModId { get; }
    string Kind { get; }
    string Message { get; }
    List<string> Report { get; }
    void WriteTo(string folder, object? source);
}

public sealed class BmodItemImport : IBmodMade
{
    public required string ModId { get; init; }
    public required string SwfFile { get; init; }
    public required byte[] Swf { get; init; }
    public required string Kind { get; init; }
    public required string Message { get; init; }
    public required OrderedDictionary<string, object?> Manifest { get; init; }
    public required (string Name, byte[] Data)? Preview { get; init; }
    public required List<string> Report { get; init; }

    static bool Starts(string? s, string prefix) => s != null && s.StartsWith(prefix, StringComparison.Ordinal);

    static bool Ends(string? s, string suffix) => s != null && s.EndsWith(suffix, StringComparison.Ordinal);

    static string Lower(string? s) => s == null ? "" : s.ToLowerInvariant();

    static string UpperHex(int c) => "0x" + BmodPalette.Hex6(c).ToUpperInvariant();

    static string Cell(Dictionary<string, string> row, string column) => BmodTables.Cell(row, column);

    static string Trim(string? s) => BmodTables.Trim(s);

    static bool ClearCell(OrderedDictionary<string, object?> sets, List<string> header, Dictionary<string, string> row, string column)
    {
        if (!header.Contains(column) || Trim(Cell(row, column)) == "") return false;
        sets[column] = "@none";
        return true;
    }

    static string Text(JsonNode? node)
    {
        if (node == null) return "";
        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.Number => BmodJson.Number(node.GetValue<double>()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => node.ToJsonString(),
        };
    }

    static bool Truthy(JsonNode? node) => node != null && node.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>() != "",
        JsonValueKind.Number => node.GetValue<double>() != 0,
        JsonValueKind.False or JsonValueKind.Null => false,
        _ => true,
    };

    public static (string Name, byte[] Data)? PreviewOf(BmodFile mod)
    {
        if (mod.Meta["previewsIds"] is not JsonObject previews) return null;
        var data = BmodSwf.BinaryDataOf(mod.Swf.Tags);
        var ids = new List<int>();
        foreach (var (k, _) in previews)
            if (int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) ids.Add(id);
        ids.Sort();
        foreach (var id in ids)
        {
            var ext = Text(previews[id.ToString(CultureInfo.InvariantCulture)]);
            if (data.TryGetValue(id, out var tag) && (ext == "png" || ext == "jpg" || ext == "jpeg"))
                return ("preview." + ext, tag.Copy());
        }
        return null;
    }

    public static BmodItemImport Make(BmodFile mod, string fileName, BmodItemPlan plan, IReadOnlyList<string> names, byte[] swf,
        string? iconName, BmodGameData game)
    {
        var report = new List<string>(plan.Report);
        var item = plan.Item;
        var row = item.Row;
        var header = item.Header;
        var isCostume = plan.IsCostume;
        var token = plan.Token;
        var display = plan.Display;
        var rename = plan.Rename;
        var newSuffix = plan.NewSuffix;
        var bodySuffix = plan.BodySuffix;
        var meta = mod.Meta;
        var swfFile = "Bones_" + token + ".swf";
        var modId = BmodTables.ModIdOf(token);
        var keyCol = header[0];
        var artCols = isCostume ? BmodTables.CostumeArt.Concat(BmodTables.CostumeSwap).ToArray() : BmodTables.WeaponArt;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var col in artCols)
            if (Trim(Cell(row, col)) != "") used.Add(col);
        var stringKey = token + "_DisplayName";
        var newKey = isCostume ? token : Cell(row, "BaseWeapon") + token;
        var sets = BmodJson.Map();
        sets[keyCol] = newKey;
        sets["DisplayNameKey"] = stringKey;
        sets[isCostume ? "CostumeID" : "WeaponSkinID"] = "@id";
        if (ClearCell(sets, header, row, "UpgradesTo"))
            report.Add("not an upgrade of anything (UpgradesTo " + Trim(Cell(row, "UpgradesTo")) + " cleared: an item can be the upgrade of only one)");
        var roles = plan.Defines.Keys.ToList();
        roles.Sort(StringComparer.Ordinal);
        foreach (var role in roles)
            if (header.Contains(role + "_Define")) sets[role + "_Define"] = UpperHex(plan.Defines[role]);
        foreach (var (column, colour) in plan.HandColours)
            if (header.Contains(column)) sets[column] = UpperHex(colour);
        var weaponRows = new List<object?>();
        var weaponStrings = BmodJson.Map();
        var weaponSet = isCostume ? Trim(Cell(row, "WeaponSet")) : "";
        var drawsWeapon = names.Any(n => Starts(n, "a_Weapon") && rename.ContainsKey(n));
        if (weaponSet != "" && bodySuffix != null && drawsWeapon)
        {
            var wt = game.Tables.Table("weaponSkinTypes");
            var weaponKey = wt.Header[0];
            var described = new List<string>();
            foreach (var w in wt.Rows)
            {
                var baseWeapon = Trim(Cell(w, "BaseWeapon"));
                if (Cell(w, weaponKey) != baseWeapon + weaponSet) continue;
                var wSets = BmodJson.Map();
                wSets[weaponKey] = baseWeapon + token;
                wSets["WeaponSkinID"] = "@id";
                wSets["DisplayNameKey"] = token + "_" + baseWeapon + "_DisplayName";
                ClearCell(wSets, wt.Header, w, "UpgradesTo");
                foreach (var col in BmodTables.WeaponArt)
                {
                    var value = Trim(Cell(w, col));
                    if (Ends(value, "/" + bodySuffix)) wSets[col] = value[..^bodySuffix.Length] + newSuffix[bodySuffix];
                }
                var wRow = BmodJson.Map();
                wRow["copy"] = Cell(w, weaponKey);
                wRow["set"] = wSets;
                weaponRows.Add(wRow);
                described.Add(baseWeapon + token + " (copy of " + Cell(w, weaponKey) + ")");
                weaponStrings[token + "_" + baseWeapon + "_DisplayName"] = display + " " + baseWeapon;
            }
            if (weaponRows.Count > 0)
            {
                sets["WeaponSet"] = token;
                if (ClearCell(sets, header, row, "DoesNotOwnWeaponSet"))
                    report.Add("owns its new weapon set (DoesNotOwnWeaponSet cleared: " + item.Key + " borrows another legend's)");
                report.Add("weapons: " + string.Join(", ", described));
            }
        }
        var drawsHands = names.Any(n => n.Contains("Hand", StringComparison.Ordinal));
        if (isCostume && !drawsHands)
        {
            foreach (var col in BmodTables.CostumeArt)
            {
                var handArt = Cell(row, col);
                if (Starts(handArt, "Gfx_Hands.swf/") && handArt != "Gfx_Hands.swf/Bare")
                {
                    sets[col] = "Gfx_Hands.swf/Bare";
                    report.Add("bare hands (Gfx_Hands.swf/Bare instead of " + handArt + "): the mod draws none");
                }
            }
        }
        string? crossoverName = null;
        OrderedDictionary<string, object?>? animationAliases = null;
        if (isCostume)
        {
            sets["CostumeIndex"] = "@id/OwnerHero";
            if (iconName != null) sets["CostumeIcon"] = iconName;
            if (plan.RosterPose != null)
            {
                sets["IsCrossover"] = "TRUE";
                sets["RosterOrderID"] = "@id";
                if (plan.RosterGem != null)
                {
                    sets["CrossoverGem"] = plan.RosterGem;
                    sets["CrossoverGemFileName"] = "UI_Roster";
                }
                else
                {
                    foreach (var col in new[] { "CrossoverGem", "CrossoverGemFileName" })
                        if (header.Contains(col) && Trim(Cell(row, col)) != "") sets[col] = "@none";
                }
                sets["ReplacementPortrait"] = plan.RosterPose;
                sets["ReplacementPortraitFileName"] = "UI_Icons";
                if (Trim(Cell(row, "ParentCrossover")) != "") sets["ParentCrossover"] = "@none";
                crossoverName = "Generated|CostumeType_" + token + "_DisplayNameWRTCrossover";
                report.Add("a crossover with the mod's roster portrait " + plan.RosterPose);
            }
            else
            {
                if (header.Contains("ReplacementHeroName")) sets["ReplacementHeroName"] = display;
                if (Lower(Trim(Cell(row, "IsCrossover"))) == "true")
                {
                    sets["IsCrossover"] = "FALSE";
                    foreach (var col in new[] { "CrossoverGem", "CrossoverGemFileName", "ReplacementPortrait", "ReplacementPortraitFileName", "ParentCrossover", "RosterOrderID" })
                        if (header.Contains(col) && Trim(Cell(row, col)) != "") sets[col] = "@none";
                    report.Add("a normal skin named " + display + " (not a crossover like " + item.Key + ")");
                }
            }
            if (Lower(Trim(Cell(row, "OverrideSelectAnims"))) == "true")
            {
                animationAliases = BmodJson.Map();
                animationAliases[token] = item.Key;
                report.Add("character select plays " + item.Key + "'s animations");
            }
        }
        else sets["DbID"] = "@id";
        foreach (var (s, col) in item.Columns)
        {
            if (!newSuffix.ContainsKey(s)) continue;
            var newValue = Cell(row, col).Replace("/" + s, "/" + newSuffix[s], StringComparison.Ordinal);
            var group = BmodTables.CostumeSwap.Contains(col) ? BmodTables.CostumeSwap : BmodTables.CostumeArt.Contains(col) ? BmodTables.CostumeArt : BmodTables.WeaponArt;
            string? free = null;
            for (var gi = group.Length - 1; gi >= 0; gi--)
            {
                if (!used.Contains(group[gi]) && header.Contains(group[gi]))
                {
                    free = group[gi];
                    break;
                }
            }
            if (free != null && Array.IndexOf(group, free) > Array.IndexOf(group, col))
            {
                sets[free] = newValue;
                used.Add(free);
            }
            else
            {
                sets[col] = newValue;
                report.Add("no free art column after " + col + ": parts the mod doesn't redraw won't show the original's");
            }
        }
        var sizeKb = (int)Math.Max(1, Math.Floor((swf.Length + 1023) / 1024.0));
        var powerRows = new List<object?>();
        if (isCostume)
        {
            powerRows = PowerSwaps(plan, newKey, sets, game, report);
            if (powerRows.Count > 0) report.Add("powers: " + powerRows.Count + " PowerSwapTypes rows, their effects drawn with the new skin's art");
        }
        var newNames = rename.Values.ToList();
        newNames.Sort(StringComparer.Ordinal);
        var bones = new StringBuilder();
        foreach (var n in newNames) bones.Append("<Bone>").Append(n).Append("</Bone>");
        var manifest = BmodJson.Map();
        manifest["id"] = modId;
        manifest["name"] = display;
        manifest["version"] = meta["version"] != null && Text(meta["version"]) != "" ? Text(meta["version"]) : "1.0";
        manifest["description"] = "Imported from " + fileName + " (" + (Truthy(meta["author"]) ? Text(meta["author"]) : "unknown author") +
            ") by Bifrost: a new " + (isCostume ? "skin" : "weapon skin") + " made from " + item.Table + "/" + item.Key +
            " with the mod's art; the original is unchanged.";
        var english = BmodJson.Map();
        english[stringKey] = display;
        foreach (var key in weaponStrings.Keys) english[key] = weaponStrings[key];
        if (crossoverName != null) english[crossoverName] = display + " (Crossover)";
        var strings = BmodJson.Map();
        strings["English"] = english;
        manifest["strings"] = strings;
        var assets = BmodJson.Map();
        assets["bones/" + swfFile] = "bones/" + swfFile;
        manifest["assets"] = assets;
        if (plan.PaletteTable.Count > 0)
        {
            var pal = BmodJson.Map();
            pal["costume"] = newKey;
            var shades = BmodJson.Map();
            var shadeKeys = plan.PaletteTable.Keys.ToList();
            shadeKeys.Sort();
            foreach (var c in shadeKeys)
            {
                var e = plan.PaletteTable[c];
                shades[BmodPalette.Hex6(c)] = new List<object?> { BmodPalette.Hex6(e.Anchor), new PyFloat(e.Dh), new PyFloat(e.Dl), new PyFloat(e.Ds) };
            }
            pal["colours"] = shades;
            manifest["palette"] = pal;
        }
        var tables = BmodJson.Map();
        var mainRow = BmodJson.Map();
        mainRow["copy"] = item.Key;
        mainRow["set"] = sets;
        tables[item.Table] = new List<object?> { mainRow };
        if (weaponRows.Count > 0) tables["weaponSkinTypes"] = weaponRows;
        if (powerRows.Count > 0) tables["PowerSwapTypes"] = powerRows;
        tables["MasterFileList"] = new List<object?> { "<File Stage=\"Game\" ExcludedPlatforms=\"\" Size=\"" + sizeKb + "\" Name=\"bones/" + swfFile + "\" Load=\"\" />" };
        tables["BoneSources"] = new List<object?> { "<Original name=\"" + token + "\"><Target name=\"" + swfFile + "\">" + bones + "</Target></Original>" };
        manifest["tables"] = tables;
        if (animationAliases != null) manifest["animationAliases"] = animationAliases;
        manifest["author"] = Truthy(meta["author"]) ? Text(meta["author"]) : "";
        var preview = PreviewOf(mod);
        if (preview is { } p) manifest["preview"] = p.Name;
        var leftOut = plan.Skipped.Concat(plan.Missing).ToList();
        if (leftOut.Count > 0)
            report.Add("left out (" + leftOut.Count + ", not named after the item, so shared by everyone): " + string.Join(", ", leftOut.Take(12)));
        var uncoloured = plan.SortedOld.Where(o => !plan.GameColours.ContainsKey(o)).ToList();
        if (uncoloured.Count > 0)
            report.Add("not in the game's SWFs, so no palette colours (" + uncoloured.Count + "): " + string.Join(", ", uncoloured.Take(8)));
        report.Add(item.Table + "/" + newKey + " (a copy of " + item.Table + "/" + item.Key + ") with " + rename.Count + " sprites");
        var original = game.Strings(Cell(row, "DisplayNameKey"));
        var what = isCostume ? "skin" : "weapon skin";
        return new BmodItemImport
        {
            ModId = modId, SwfFile = swfFile, Swf = swf, Kind = item.Table + "/" + newKey,
            Message = display + ": a new " + what + " next to " + (!string.IsNullOrEmpty(original) ? original : item.Key) + ".",
            Manifest = manifest, Preview = preview, Report = report,
        };
    }

    static bool ListHas(string? list, string? name)
    {
        if (list == null || string.IsNullOrEmpty(name)) return false;
        foreach (var part in list.Split(','))
            if (Trim(part) == name) return true;
        return false;
    }

    static string? OwnedBy(BmodSwapRow x, string attr, string key)
    {
        var from = attr + "=\"" + x.Attr(attr) + "\"";
        var at = x.Text.IndexOf(from, StringComparison.Ordinal);
        if (at < 0) return null;
        return x.Text[..at] + "OwnerCostume=\"" + key + "\"" + x.Text[(at + from.Length)..];
    }

    static List<object?> PowerSwaps(BmodItemPlan plan, string newKey, OrderedDictionary<string, object?> sets, BmodGameData game, List<string> report)
    {
        var item = plan.Item;
        var row = item.Row;
        var key = item.Key;
        var ownByPower = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        var ownCount = 0;
        foreach (var x in game.CostumeSwaps)
        {
            if (!ListHas(x.Attr("OwnerCostume"), key)) continue;
            var target = x.Field("TargetPower");
            var text = OwnedBy(x, "OwnerCostume", key);
            if (target != null && text != null)
            {
                ownCount++;
                ownByPower[target] = text;
            }
        }
        var legend = Trim(Cell(row, "OwnerHero"));
        var wasCrossover = Lower(Trim(Cell(row, "IsCrossover"))) == "true";
        var isCrossover = sets.TryGetValue("IsCrossover", out var cross) ? (cross as string) == "TRUE" : wasCrossover;
        var legendRows = new OrderedDictionary<string, BmodSwapRow>(StringComparer.Ordinal);
        foreach (var x in game.HeroSwaps)
        {
            var target = x.Field("TargetPower");
            if (legend != "" && target != null && ListHas(x.Attr("OwnerHero"), legend)) legendRows[target] = x;
        }
        var legendCopied = new List<string>();
        var legendCovered = new List<string>();
        var legendDropped = new List<string>();
        if (isCrossover && !wasCrossover)
        {
            foreach (var (target, x) in legendRows)
            {
                var text = OwnedBy(x, "OwnerHero", key);
                if (!ownByPower.ContainsKey(target) && text != null)
                {
                    ownByPower[target] = text;
                    legendCopied.Add(target);
                }
            }
        }
        else if (!isCrossover)
        {
            foreach (var target in legendRows.Keys)
            {
                legendCovered.Add(target);
                if (ownByPower.Remove(target)) legendDropped.Add(target);
            }
        }
        var swapSuffixes = plan.Item.Hits.Keys.Where(s => Starts(s, "Swap")).ToList();
        if (swapSuffixes.Count == 0) swapSuffixes = plan.Item.Hits.Keys.ToList();
        var newSuffixes = plan.NewSuffix.Values.ToList();
        var artEntries = new List<string>();
        foreach (var (col, value) in sets)
        {
            var v = value as string ?? "";
            if ((BmodTables.CostumeArt.Contains(col) || BmodTables.CostumeSwap.Contains(col)) && v.Contains('/') &&
                newSuffixes.Contains(v[(v.LastIndexOf('/') + 1)..]))
                artEntries.Add(v);
        }
        var baseGfx = new OrderedDictionary<string, OrderedDictionary<string, OrderedDictionary<string, string>>>(StringComparer.Ordinal);
        if (legend != "")
        {
            var raw = game.Tables.Lines("powerTypes");
            var pHeader = CsvTable.Split(raw[0]);
            var nameCol = pHeader.IndexOf("PowerName");
            for (var i = 1; i < raw.Count; i++)
            {
                var line = raw[i];
                var comma = line.IndexOf(',');
                var first = comma < 0 ? line : line[..comma];
                if (nameCol == 0 && !Ends(first, legend) && !first.Contains('"')) continue;
                var fields = CsvTable.Split(line);
                var pname = nameCol >= 0 && nameCol < fields.Count ? fields[nameCol] : "";
                if (!Ends(pname, legend)) continue;
                var kinds = new OrderedDictionary<string, OrderedDictionary<string, string>>(StringComparer.Ordinal);
                var any = false;
                foreach (var kind in new[] { "Cast", "Fire", "Hit" })
                {
                    var g = new OrderedDictionary<string, string>(StringComparer.Ordinal);
                    for (var c = 0; c < pHeader.Count && c < fields.Count; c++)
                    {
                        var hcol = pHeader[c];
                        if (Starts(hcol, kind + "Gfx.") && Trim(fields[c]) != "") g[hcol[(hcol.IndexOf('.') + 1)..]] = fields[c];
                    }
                    if (g.TryGetValue("AnimClass", out var anim) && anim != "") any = true;
                    kinds[kind] = g;
                }
                if (any) baseGfx[pname] = kinds;
            }
        }
        var powers = ownByPower.Keys.ToList();
        foreach (var bp in baseGfx.Keys)
        {
            if (legendCovered.Contains(bp))
            {
                if (!legendDropped.Contains(bp)) legendDropped.Add(bp);
            }
            else if (!powers.Contains(bp)) powers.Add(bp);
        }
        if (legendCopied.Count > 0)
            report.Add("powers: " + legendCopied.Count + " of " + legend + "'s own swaps copied (a crossover doesn't get them): " + string.Join(", ", legendCopied.Take(8)));
        if (legendDropped.Count > 0)
            report.Add("powers: " + legendDropped.Count + " left to " + legend + "'s own swaps (a normal skin gets them): " + string.Join(", ", legendDropped.Take(8)));
        powers.Sort(StringComparer.Ordinal);
        var output = new List<object?>();
        foreach (var power in powers)
        {
            var own = ownByPower.TryGetValue(power, out var o) ? o :
                "<PowerSwapType OwnerCostume=\"" + key + "\"><TargetPower>" + power + "</TargetPower><CastGfx/><FireGfx/><HitGfx/></PowerSwapType>";
            var from = "OwnerCostume=\"" + key + "\"";
            var at = own.IndexOf(from, StringComparison.Ordinal);
            var r2 = at < 0 ? own : own[..at] + "OwnerCostume=\"" + newKey + "\"" + own[(at + from.Length)..];
            foreach (var kind in new[] { "Cast", "Fire", "Hit" })
            {
                var gfxBase = baseGfx.TryGetValue(power, out var kinds) && kinds.TryGetValue(kind, out var b) ? b : new OrderedDictionary<string, string>(StringComparer.Ordinal);
                var empty = "<" + kind + "Gfx/>";
                var open = "<" + kind + "Gfx>";
                var close = "</" + kind + "Gfx>";
                var e = r2.IndexOf(empty, StringComparison.Ordinal);
                var op = r2.IndexOf(open, StringComparison.Ordinal);
                var closeAt = op >= 0 ? r2.IndexOf(close, op + open.Length, StringComparison.Ordinal) : -1;
                if (op >= 0 && closeAt < 0) op = -1;
                int start = -1, stop = -1;
                string? inner = null;
                if (e >= 0 && (op < 0 || e < op))
                {
                    start = e;
                    stop = e + empty.Length;
                }
                else if (op >= 0)
                {
                    start = op;
                    stop = closeAt + close.Length;
                    inner = r2[(op + open.Length)..closeAt];
                }
                var gfx = GfxXml(kind, inner, gfxBase, plan.Rename, swapSuffixes, artEntries, game);
                r2 = start >= 0 ? r2[..start] + gfx + r2[stop..] : r2.Replace("</PowerSwapType>", gfx + "</PowerSwapType>", StringComparison.Ordinal);
            }
            var add = BmodJson.Map();
            add["add"] = r2;
            output.Add(add);
        }
        if (output.Count > 0) report.Add("powers: " + ownCount + " of them copied from " + key + "'s rows");
        return output;
    }

    static string Effect(string name, OrderedDictionary<string, string> rename, List<string> swapSuffixes, BmodGameData game)
    {
        if (rename.TryGetValue(name, out var renamed)) return renamed;
        foreach (var suffix in swapSuffixes)
        {
            if (!Ends(name, "_" + suffix)) continue;
            var b = name[..(name.Length - suffix.Length - 1)];
            if (game.EffectHas(name) && game.EffectHas(b)) return b;
        }
        return name;
    }

    static string GfxXml(string kind, string? inner, OrderedDictionary<string, string> gfxBase, OrderedDictionary<string, string> rename,
        List<string> swapSuffixes, List<string> artEntries, BmodGameData game)
    {
        if (inner == null || Trim(inner) == "")
        {
            if (!gfxBase.TryGetValue("AnimClass", out var anim) || anim == "") return "<" + kind + "Gfx/>";
            var built = new StringBuilder();
            foreach (var (k, v) in gfxBase) built.Append('<').Append(k).Append('>').Append(v).Append("</").Append(k).Append('>');
            inner = built.ToString();
        }
        const string open = "<AnimClass>";
        const string close = "</AnimClass>";
        var output = new StringBuilder();
        var pos = 0;
        while (true)
        {
            var a = inner.IndexOf(open, pos, StringComparison.Ordinal);
            if (a < 0) break;
            var b = inner.IndexOf('<', a + open.Length);
            if (b < 0 || string.CompareOrdinal(inner, b, close, 0, close.Length) != 0 || b + close.Length > inner.Length)
            {
                output.Append(inner, pos, a + open.Length - pos);
                pos = a + open.Length;
                continue;
            }
            output.Append(inner, pos, a - pos).Append(open).Append(Effect(inner[(a + open.Length)..b], rename, swapSuffixes, game)).Append(close);
            pos = b + close.Length;
        }
        inner = output.Append(inner, pos, inner.Length - pos).ToString();
        if (inner.Contains(open, StringComparison.Ordinal))
        {
            var taken = 0;
            var from = 0;
            while (true)
            {
                var t = inner.IndexOf("<CustomArt", from, StringComparison.Ordinal);
                if (t < 0) break;
                var j = t + 10;
                while (j < inner.Length && inner[j] >= '0' && inner[j] <= '9') j++;
                if (j < inner.Length && inner[j] == '>') taken++;
                from = t + 10;
            }
            var sb = new StringBuilder(inner);
            for (var i = 0; i < artEntries.Count; i++)
                sb.Append("<CustomArt").Append(taken + i + 1).Append('>').Append(artEntries[i]).Append("</CustomArt").Append(taken + i + 1).Append('>');
            inner = sb.ToString();
        }
        return "<" + kind + "Gfx>" + inner + "</" + kind + "Gfx>";
    }

    public string ManifestJson(object? source)
    {
        var manifest = BmodJson.Map();
        foreach (var key in Manifest.Keys) manifest[key] = Manifest[key];
        manifest["source"] = source;
        return BmodJson.Write(manifest) + "\n";
    }

    public static string HashOf(byte[] data)
    {
        unchecked
        {
            int h = 17, g = 0, i = 0;
            for (; i + 4 <= data.Length; i += 4)
            {
                var v = BmodSwf.U32(data, i);
                h = ((h << 5) - h) + v;
                g = (g ^ v) + ((g << 3) | (int)((uint)g >> 29));
            }
            for (; i < data.Length; i++) h = ((h << 5) - h) + data[i];
            return ((uint)h).ToString("x8", CultureInfo.InvariantCulture) + ((uint)g).ToString("x8", CultureInfo.InvariantCulture) + "-" + data.Length;
        }
    }

    public static SortedDictionary<string, object?> Source(string file, string hash, long size, int converter)
    {
        var s = BmodJson.Sorted();
        s["file"] = file;
        s["hash"] = hash;
        s["size"] = size;
        s["converter"] = "Bifrost " + converter;
        return s;
    }

    public void WriteTo(string folder, object? source)
    {
        Directory.CreateDirectory(Path.Combine(folder, "bones"));
        File.WriteAllBytes(Path.Combine(folder, "bones", SwfFile), Swf);
        if (Preview is { } p) File.WriteAllBytes(Path.Combine(folder, p.Name), p.Data);
        File.WriteAllText(Path.Combine(folder, "mod.json"), ManifestJson(source), new UTF8Encoding(false));
    }
}
