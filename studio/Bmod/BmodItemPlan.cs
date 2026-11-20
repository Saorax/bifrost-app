using System.Text.Json.Nodes;

namespace BifrostStudio.Bmod;

public sealed class BmodItemPlan
{
    public required BmodItem Item { get; init; }
    public required bool IsCostume { get; init; }
    public required string Core { get; init; }
    public required string Display { get; init; }
    public required string Token { get; init; }
    public required OrderedDictionary<string, string> NewSuffix { get; init; }
    public required OrderedDictionary<string, string> Rename { get; init; }
    public required string? BodySuffix { get; init; }
    public required string? RosterPose { get; init; }
    public required string? RosterGem { get; init; }
    public required List<string> Skipped { get; init; }
    public required List<string> Missing { get; init; }
    public required Dictionary<string, int> ById { get; init; }
    public required Dictionary<string, List<int>?> GameColours { get; init; }
    public required OrderedDictionary<string, OrderedDictionary<int, int>> ModSymbols { get; init; }
    public required OrderedDictionary<string, int> Defines { get; init; }
    public required OrderedDictionary<int, PaletteShade> PaletteTable { get; init; }
    public required List<(string Name, int Colour)> HandColours { get; init; }
    public required List<string> SortedOld { get; init; }
    public required List<string> ClassNames { get; init; }
    public required Dictionary<string, List<int>?> ClassColours { get; init; }
    public required List<string> Report { get; init; }

    public static (List<SwfTag> Art, int Fixed) CopiedArt(BmodFile mod)
    {
        var (art, count) = BmodShapes.FixShapes(mod.Swf.Tags.Where(t => !BmodSwf.NotCopied.Contains(t.Code)).ToList());
        return (art.ToList(), count);
    }

    public List<SwfTag> Art(BmodFile mod)
    {
        var (art, count) = CopiedArt(mod);
        if (count > 0) Report.Add(count + " shapes declare styles mid-shape as DefineShape; made DefineShape2");
        return art;
    }

    public (int Id, string Name, byte[] Sprite, int Parts)? SkinIcon(IReadOnlyList<SwfTag> art, string game, IReadOnlyList<(string Bone, Matrix M)> pose)
    {
        if (!IsCostume || BodySuffix == null) return null;
        var parts = new List<(int Id, Matrix M)>();
        foreach (var (bone, m) in pose)
        {
            var old = bone + "_" + BodySuffix;
            if (Rename.ContainsKey(old) && ById.TryGetValue(old, out var id)) parts.Add((id, m));
        }
        if (parts.Count == 0) return null;
        Box? target = null;
        var originalIcon = BmodTables.Trim(BmodTables.Cell(Item.Row, "CostumeIcon"));
        var icons = Path.Combine(game, "UI_Icons.swf");
        if (originalIcon != "" && File.Exists(icons))
        {
            try
            {
                var iconTags = BmodSwf.Read(File.ReadAllBytes(icons)).Tags;
                foreach (var (cid, name) in BmodSwf.Symbols(iconTags))
                    if (name == originalIcon) target = new BmodBounds(iconTags).BoundsOf(cid);
            }
            catch (Exception e) when (e is BmodException or IOException or IndexOutOfRangeException or ArgumentException)
            {
                Report.Add("UI_Icons.swf not read: " + e.Message);
            }
        }
        var iconId = BmodSwf.MaxCharacterId(art) + 1;
        HashSet<int>? face = ById.TryGetValue("a_Jaw_" + BodySuffix, out var jaw) && parts.Any(p => p.Id == jaw) ? [jaw] : null;
        var sprite = BmodBounds.IconSprite(iconId, parts, new BmodBounds(art), target ?? new Box(-40, -40, 40, 40), face, 0.95, 0, (0, 0));
        if (sprite == null) return null;
        var iconName = "a_SkinIcon_" + Token;
        Report.Add("skin icon " + iconName + ": " + parts.Count + " head parts in the head-shot pose");
        return (iconId, iconName, sprite, parts.Count);
    }

    public byte[] BonesSwf(BmodFile mod, IReadOnlyList<SwfTag> art, (int Id, string Name, byte[] Sprite, int Parts)? icon)
    {
        var classes = new List<string>(ClassNames);
        var colours = new Dictionary<string, List<int>?>(ClassColours, StringComparer.Ordinal);
        var symbols = SortedOld.Select(o => (ById[o], Rename[o])).ToList();
        var all = new List<SwfTag>(art);
        if (icon is { } i)
        {
            all.Add(SwfTag.Of(39, i.Sprite));
            symbols.Add((i.Id, i.Name));
            classes.Add(i.Name);
            colours[i.Name] = null;
        }
        return ModSwf(mod, all, classes, colours, symbols);
    }

    public static byte[] ModSwf(BmodFile mod, IEnumerable<SwfTag> art, List<string> classes, Dictionary<string, List<int>?> colours, List<(int Id, string Name)> symbols)
    {
        var tags = new List<SwfTag> { SwfTag.Of(BmodSwf.FileAttributes, [0x08, 0, 0, 0]), SwfTag.Of(BmodSwf.SetBackground, [255, 255, 255]) };
        tags.AddRange(art);
        tags.Add(SwfTag.Of(BmodSwf.DoAbc, BmodAbc.DoAbcTag(classes, colours)));
        tags.Add(SwfTag.Of(BmodSwf.SymbolClass, BmodSwf.SymbolClassTag(symbols)));
        tags.Add(SwfTag.Of(BmodSwf.ShowFrame, []));
        tags.Add(SwfTag.Of(BmodSwf.End, []));
        return BmodSwf.Write(mod.Swf with { Version = Math.Max(15, mod.Swf.Version), Count = 1 }, tags);
    }

    public static string? GameFile(string game, string name)
    {
        var f = Path.Combine(game, name);
        if (File.Exists(f)) return f;
        f = Path.Combine(game, "bones", name);
        return File.Exists(f) ? f : null;
    }

    public static Dictionary<string, List<int>?> GameColoursOf(string game, IEnumerable<string> swfNames, List<string> report)
    {
        var into = new Dictionary<string, List<int>?>(StringComparer.Ordinal);
        foreach (var name in swfNames)
        {
            if (GameFile(game, name) is not { } f) continue;
            try { BmodAbc.Classes(BmodSwf.Read(File.ReadAllBytes(f)).Tags, into); }
            catch (Exception e) when (e is BmodException or IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                report.Add("game colours from " + name + " not read: " + e.Message);
            }
        }
        return into;
    }

    public static BmodItemPlan Make(BmodFile mod, string stem, IReadOnlyList<string> names, BmodTableSource tables, string game)
    {
        var meta = mod.Meta;
        var report = new List<string>();
        var item = BmodTables.FindItem(tables, names);
        var hits = item.Hits;
        var matched = hits.Values.Sum(v => v.Count);
        if (matched * 2 < names.Count)
            throw new InvalidDataException($"only {matched} of its {names.Count} sprites are named after one item ({item.Table}/{item.Key}), so it doesn't look like a skin or weapon skin");
        var isCostume = item.Table == "costumeTypes";
        var core = BmodTables.CommonPart(hits.Keys.ToList());
        var display = (string?)meta["name"] is { Length: > 0 } metaName ? metaName : stem;
        var token = BmodTables.TokenFor(display);
        var newSuffix = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in hits.Keys) newSuffix[s] = s.Replace(core, token, StringComparison.Ordinal);
        var rename = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (s, list) in hits)
            foreach (var n in list)
                rename.TryAdd(n, n[..^s.Length] + newSuffix[s]);
        var bodySuffix = hits.Keys.FirstOrDefault(s => !s.StartsWith("Swap", StringComparison.Ordinal));
        var bodyNew = bodySuffix != null ? newSuffix[bodySuffix] : null;
        string? rosterPose = null, rosterGem = null;
        if (isCostume)
        {
            foreach (var n in names)
            {
                if (n.StartsWith("a_Roster_Pose_", StringComparison.Ordinal))
                {
                    rename[n] = "a_Roster_Pose_" + token + (n.EndsWith('M') ? "M" : "");
                    rosterPose = "a_Roster_Pose_" + token;
                }
                else if (n.StartsWith("a_Roster_Gem_", StringComparison.Ordinal))
                {
                    rosterGem = "a_Roster_Gem_" + token;
                    rename[n] = rosterGem;
                }
            }
        }
        if (isCostume && bodyNew != null)
            foreach (var n in names)
                if (!rename.ContainsKey(n) && !n.Contains("_Swap", StringComparison.Ordinal)) rename[n] = n + "_" + bodyNew;
        var skipped = names.Where(n => !rename.ContainsKey(n)).ToList();
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, name) in mod.Symbols) byId[name] = id;
        var missing = new List<string>();
        foreach (var n in rename.Keys.ToList())
        {
            if (byId.ContainsKey(n)) continue;
            missing.Add(n);
            rename.Remove(n);
        }
        var swfNames = meta["swfs"] is JsonObject swfs ? swfs.Select(kv => kv.Key).ToList() : [];
        var colours = GameColoursOf(game, swfNames, report);
        var modSymbols = BmodShapes.SymbolColours(mod.Swf.Tags);
        var body = new OrderedDictionary<string, OrderedDictionary<int, int>>(StringComparer.Ordinal);
        foreach (var old in rename.Keys)
        {
            if (colours.GetValueOrDefault(old) == null || !modSymbols.TryGetValue(old, out var symbolColours)) continue;
            var suffix = hits.Keys.FirstOrDefault(s => old.EndsWith("_" + s, StringComparison.Ordinal));
            if (suffix != null) body[old[..(old.Length - suffix.Length - 1)]] = symbolColours;
        }
        var defines = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        var paletteTable = new OrderedDictionary<int, PaletteShade>();
        if (body.Count > 0)
        {
            var built = BmodPalette.Build(body);
            defines = built.Defines;
            paletteTable = built.Table;
            report.AddRange(built.Notes.Select(n => "palette " + n));
        }
        var hands = new List<(string, int)>();
        if (BmodPalette.SkinTones(body) is { } skin && isCostume)
        {
            hands = [("HandsLt_Swap", skin.Light), ("HandsDk_Swap", skin.Dark), ("HandsSkinLt_Swap", skin.Light), ("HandsSkinDk_Swap", skin.Dark)];
            report.Add("hands in the skin tone " + BmodPalette.Hex6(skin.Light) + " / " + BmodPalette.Hex6(skin.Dark));
        }
        var sortedOld = rename.Keys.ToList();
        sortedOld.Sort(StringComparer.Ordinal);
        var classNames = new List<string>();
        var classColours = new Dictionary<string, List<int>?>(StringComparer.Ordinal);
        foreach (var old in sortedOld)
        {
            var cls = rename[old];
            classNames.Add(cls);
            if (colours.GetValueOrDefault(old) == null)
            {
                classColours[cls] = null;
                continue;
            }
            var anchors = new List<int>();
            if (modSymbols.TryGetValue(old, out var oldColours))
                foreach (var c in oldColours.Keys)
                    if (paletteTable.TryGetValue(c, out var shade) && !anchors.Contains(shade.Anchor)) anchors.Add(shade.Anchor);
            anchors.Sort();
            classColours[cls] = anchors;
        }
        return new BmodItemPlan
        {
            Item = item, IsCostume = isCostume, Core = core, Display = display, Token = token, NewSuffix = newSuffix, Rename = rename,
            BodySuffix = bodySuffix, RosterPose = rosterPose, RosterGem = rosterGem, Skipped = skipped, Missing = missing, ById = byId,
            GameColours = colours, ModSymbols = modSymbols, Defines = defines, PaletteTable = paletteTable, HandColours = hands,
            SortedOld = sortedOld, ClassNames = classNames, ClassColours = classColours, Report = report,
        };
    }
}
