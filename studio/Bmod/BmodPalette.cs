namespace BifrostStudio.Bmod;

public sealed record PaletteShade(int Anchor, double Dh, double Dl, double Ds);

public sealed record BmodPaletteResult(OrderedDictionary<string, int> Defines, OrderedDictionary<int, PaletteShade> Table, List<string> Notes);

public static class BmodPalette
{
    public static readonly (string Family, string[] Levels)[] Families =
    [
        ("Hair", ["HairLt", "Hair", "HairDk"]),
        ("Body1", ["Body1VL", "Body1Lt", "Body1", "Body1Dk", "Body1VD"]),
        ("Body2", ["Body2VL", "Body2Lt", "Body2", "Body2Dk", "Body2VD"]),
        ("Special", ["SpecialVL", "SpecialLt", "Special", "SpecialDk", "SpecialVD"]),
        ("Cloth", ["ClothVL", "ClothLt", "Cloth", "ClothDk"]),
        ("Weapon", ["WeaponVL", "WeaponLt", "Weapon", "WeaponDk"]),
    ];

    public static (double H, double L, double S) Hls(int c)
    {
        double r = ((c >> 16) & 0xFF) / 255.0;
        double g = ((c >> 8) & 0xFF) / 255.0;
        double b = (c & 0xFF) / 255.0;
        var maxc = Math.Max(r, Math.Max(g, b));
        var minc = Math.Min(r, Math.Min(g, b));
        var sumc = maxc + minc;
        var rangec = maxc - minc;
        var l = sumc / 2;
        if (minc == maxc) return (0, l, 0);
        var s = l <= 0.5 ? rangec / sumc : rangec / (2 - maxc - minc);
        var rc = (maxc - r) / rangec;
        var gc = (maxc - g) / rangec;
        var bc = (maxc - b) / rangec;
        double h;
        if (r == maxc) h = bc - gc;
        else if (g == maxc) h = 2 + rc - bc;
        else h = 4 + gc - rc;
        return (Mod1(h / 6), l, s);
    }

    static double Mod1(double x) => x - Math.Floor(x);

    public static double HueGap(double a, double b)
    {
        var d = Mod1(Math.Abs(a - b));
        return Math.Min(d, 1 - d);
    }

    public static bool IsSkin(int c)
    {
        var v = Hls(c);
        return v.H <= 50.0 / 360 && v.S >= 0.2 && v.L >= 0.45 && v.L <= 0.97;
    }

    static string PartOf(string bone) => bone.StartsWith("a_", StringComparison.Ordinal) ? bone[2..] : bone;

    static bool Starts(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal);

    public static string Hex6(int c)
    {
        var chars = new char[6];
        for (var i = 5; i >= 0; i--) chars[5 - i] = "0123456789abcdef"[(int)(((uint)c >> (i * 4)) & 0xF)];
        return new string(chars);
    }

    static void Add<TKey>(OrderedDictionary<TKey, double> map, TKey key, double amount) where TKey : notnull =>
        map[key] = map.TryGetValue(key, out var v) ? v + amount : amount;

    public static (int Light, int Dark)? SkinTones(OrderedDictionary<string, OrderedDictionary<int, int>> spriteColours)
    {
        var use = new OrderedDictionary<int, double>();
        foreach (var (bone, colours) in spriteColours)
        {
            var part = PartOf(bone);
            if (Starts(part, "Eyes") || Starts(part, "Mouth") || Starts(part, "Weapon")) continue;
            foreach (var (c, n) in colours)
                if (IsSkin(c)) Add(use, c, n);
        }
        if (use.Count == 0) return null;
        var light = -1;
        double bestUse = -1, bestTie = 0;
        foreach (var (c, u) in use)
        {
            var tie = -Math.Abs(Hls(c).L - 0.75);
            if (light < 0 || u > bestUse || (u == bestUse && tie > bestTie))
            {
                light = c;
                bestUse = u;
                bestTie = tie;
            }
        }
        var dark = 0;
        foreach (var shift in new[] { 16, 8, 0 })
            dark |= (int)BmodBounds.PyRound(((light >> shift) & 0xFF) * 0.8) << shift;
        return (light, dark);
    }

    public static string[] LevelsFor(string family, int count)
    {
        var names = Families.First(f => f.Family == family).Levels;
        var at = Array.IndexOf(names, family);
        count = Math.Min(count, names.Length);
        var start = (int)Math.Max(0, Math.Min(at - Math.Floor((count - 1) / 2.0), names.Length - count));
        return names[start..(start + count)];
    }

    static List<T> StableSort<T>(List<T> list, Func<T, double> key, bool reverse) =>
        (reverse ? list.OrderByDescending(key) : list.OrderBy(key)).ToList();

    public static BmodPaletteResult Build(OrderedDictionary<string, OrderedDictionary<int, int>> spriteColours)
    {
        var use = new OrderedDictionary<int, double>();
        var hair = new OrderedDictionary<int, double>();
        var parts = new Dictionary<int, List<string>>();
        foreach (var (bone, colours) in spriteColours)
        {
            var part = PartOf(bone);
            if (Starts(part, "Weapon")) continue;
            foreach (var (c, n) in colours)
            {
                Add(use, c, n);
                if (!parts.TryGetValue(c, out var list)) parts[c] = list = [];
                if (!list.Contains(part)) list.Add(part);
                if (Starts(part, "Hair")) Add(hair, c, n);
            }
        }
        var familyOf = new OrderedDictionary<int, string>();
        var saturated = new List<int>();
        foreach (var (c, n) in use)
        {
            var v = Hls(c);
            if (v.L < 0.1 || (v.L > 0.93 && v.S < 0.25) || IsSkin(c)) continue;
            if (parts[c].All(p => Starts(p, "Eyes") || Starts(p, "Mouth"))) continue;
            if (v.S < 0.15) familyOf[c] = "Cloth";
            else if ((hair.TryGetValue(c, out var h) ? h : 0) * 2 > n) familyOf[c] = "Hair";
            else saturated.Add(c);
        }
        saturated = StableSort(saturated, c => -use[c], false);
        var groups = new List<List<int>>();
        foreach (var c in saturated)
        {
            var hue = Hls(c).H;
            var group = groups.FirstOrDefault(g => HueGap(hue, Hls(g[0]).H) <= 35.0 / 360);
            if (group != null) group.Add(c);
            else groups.Add([c]);
        }
        groups = StableSort(groups, g =>
        {
            double sum = 0;
            foreach (var c in g) sum += use[c];
            return -sum;
        }, false);
        string[] roles = ["Body1", "Body2", "Special"];
        for (var gi = 0; gi < groups.Count && gi < roles.Length; gi++)
            foreach (var c in groups[gi]) familyOf[c] = roles[gi];
        var defines = new OrderedDictionary<string, int>();
        var table = new OrderedDictionary<int, PaletteShade>();
        var notes = new List<string>();
        foreach (var (family, _) in Families)
        {
            var members = familyOf.Where(kv => kv.Value == family).Select(kv => kv.Key).ToList();
            if (members.Count == 0) continue;
            members = StableSort(members, c => Hls(c).L, true);
            var distinct = 0;
            var last = double.NaN;
            foreach (var c in members)
            {
                var l = Hls(c).L;
                if (double.IsNaN(last) || last - l >= 0.08)
                {
                    distinct++;
                    last = l;
                }
            }
            var levels = LevelsFor(family, distinct > 1 ? distinct : 1);
            var anchors = new List<int>();
            for (var i = 0; i < levels.Length; i++)
            {
                var lo = (int)Math.Floor((double)i * members.Count / levels.Length);
                var hi = (int)Math.Floor((double)(i + 1) * members.Count / levels.Length);
                int? anchor = null;
                for (var j = lo; j < hi; j++)
                    if (anchor == null || use[members[j]] > use[anchor.Value]) anchor = members[j];
                anchors.Add(anchor ?? 0);
                defines[levels[i]] = anchor ?? 0;
            }
            foreach (var c in members)
            {
                var cv = Hls(c);
                int? best = null;
                double bestScore = 0;
                foreach (var a in anchors)
                {
                    var av = Hls(a);
                    var score = Math.Abs(av.L - cv.L) + 2 * HueGap(av.H, cv.H);
                    if (best == null || score < bestScore)
                    {
                        best = a;
                        bestScore = score;
                    }
                }
                var bv = Hls(best!.Value);
                var dh = Mod1(cv.H - bv.H + 0.5) - 0.5;
                table[c] = new PaletteShade(best.Value, dh, cv.L - bv.L, cv.S - bv.S);
            }
            notes.Add($"{family}: {members.Count} shades on " + string.Join(", ", levels.Select((r, i) => r + " " + Hex6(anchors[i]))));
        }
        var left = use.Keys.Count(c => !familyOf.ContainsKey(c));
        if (notes.Count > 0 || left > 0) notes.Add($"left as drawn: {left} colours (outlines, whites, skin, eyes and mouths)");
        return new BmodPaletteResult(defines, table, notes);
    }
}
