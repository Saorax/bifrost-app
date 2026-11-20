using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Avalonia;

namespace BifrostStudio;

public sealed record ArtPiece(string File, Matrix Inner, double? W, double? H, double Sx, double Sy, string? Mover, Matrix Outer);

public sealed record ArtMover(string Id, double PathX, double PathY, (double X1, double Y1, double X2, double Y2, string Kind)[] Lines);

public sealed class LevelArt
{
    public string Name { get; init; } = "";
    public string AssetDir { get; init; } = "";
    public string? Background { get; init; }
    public double BackgroundW { get; init; }
    public double BackgroundH { get; init; }
    public List<ArtPiece> Pieces { get; } = [];
    public Dictionary<string, ArtMover> Movers { get; } = [];
    public int Missing { get; private set; }
    public Dictionary<string, Avalonia.Media.Imaging.Bitmap?> Pictures { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Avalonia.Media.Imaging.Bitmap? LoadPicture(string file)
    {
        try
        {
            return new Avalonia.Media.Imaging.Bitmap(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    public LevelArt LoadPictures()
    {
        foreach (var file in Pieces.Select(p => p.File).Append(Background).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            Pictures[file] = LoadPicture(file);
        return this;
    }

    static readonly System.Text.RegularExpressions.Regex GluedAttribute = new("\"(?=[A-Za-z_][\\w.-]*\\s*=)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static double Num(XElement el, string name, double fallback = 0) =>
        el.Attribute(name) is { } a && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) ? v : fallback;

    static bool Picture(string name) =>
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    public static string AssetPath(string game, string dir, string name) =>
        name.StartsWith("../", StringComparison.Ordinal)
            ? Path.Combine(game, "mapArt", name[3..].Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(game, "mapArt", dir, name.Replace('/', Path.DirectorySeparatorChar));

    public static LevelArt Parse(string xml, string game)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            doc = XDocument.Parse(GluedAttribute.Replace(xml, "\" "));
        }
        var root = doc.Root ?? throw new FormatException("empty level");
        var dir = (string?)root.Attribute("AssetDir") ?? "";
        var backgrounds = root.Elements("Background").ToList();
        var bg = backgrounds.FirstOrDefault(b => b.Attribute("Theme") == null) ?? backgrounds.FirstOrDefault();
        string? bgFile = null;
        if (bg?.Attribute("AssetName")?.Value is { Length: > 0 } bgName && Picture(bgName))
        {
            var f = Path.Combine(game, "mapArt", "Backgrounds", bgName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(f)) bgFile = f;
        }
        var art = new LevelArt
        {
            Name = (string?)root.Attribute("LevelName") ?? "",
            AssetDir = dir,
            Background = bgFile,
            BackgroundW = bg != null ? Num(bg, "W") : 0,
            BackgroundH = bg != null ? Num(bg, "H") : 0,
        };
        foreach (var el in root.Elements())
            if (el.Name.LocalName is "Platform" or "MovingPlatform") art.Walk(el, Matrix.Identity, null, Matrix.Identity, game, dir);
        foreach (var dc in root.Elements("DynamicCollision"))
        {
            var id = (string?)dc.Attribute("PlatID");
            if (id == null || !art.Movers.TryGetValue(id, out var mover)) continue;
            double ox = Num(dc, "X"), oy = Num(dc, "Y");
            var lines = dc.Elements()
                .Where(l => l.Attribute("X1") != null || l.Attribute("X") != null)
                .Select(l => (Num(l, "X1", Num(l, "X")) + ox, Num(l, "Y1", Num(l, "Y")) + oy,
                    Num(l, "X2", Num(l, "X")) + ox, Num(l, "Y2", Num(l, "Y")) + oy, l.Name.LocalName));
            art.Movers[id] = mover with { Lines = [.. mover.Lines, .. lines] };
        }
        return art;
    }

    static (double X, double Y) Scale(XElement el) =>
        el.Attribute("Scale") != null ? (Num(el, "Scale", 1), Num(el, "Scale", 1)) : (Num(el, "ScaleX", 1), Num(el, "ScaleY", 1));

    static Matrix Place(XElement el, bool scaled, bool moved)
    {
        var (sx, sy) = scaled ? Scale(el) : (1, 1);
        return Matrix.CreateScale(sx, sy) * Matrix.CreateRotation(Num(el, "Rotation") * Math.PI / 180)
            * (moved ? Matrix.CreateTranslation(Num(el, "X"), Num(el, "Y")) : Matrix.Identity);
    }

    void Walk(XElement el, Matrix inner, string? mover, Matrix outer, string game, string dir)
    {
        if (el.Attribute("Theme") != null || el.Attribute("ScoringType") != null) return;
        if (string.Equals((string?)el.Attribute("PlatformAssetSwap"), "animated", StringComparison.OrdinalIgnoreCase)) return;
        var kind = el.Name.LocalName;
        if (kind is "Platform" or "Asset" && el.Attribute("AssetName")?.Value is { } name)
        {
            if (name.StartsWith(".png", StringComparison.Ordinal) || !Picture(name)) return;
            var file = AssetPath(game, dir, name);
            if (!File.Exists(file))
            {
                Missing++;
                return;
            }
            double? w = el.Attribute("W") != null ? Num(el, "W") : null;
            double? h = el.Attribute("H") != null ? Num(el, "H") : null;
            var (sx, sy) = Scale(el);
            Pieces.Add(new ArtPiece(file, Place(el, false, true) * inner, w, h, sx, sy, mover, outer));
            return;
        }
        if (kind is not ("Platform" or "MovingPlatform")) return;
        Matrix nextInner;
        Matrix nextOuter = outer;
        var nextMover = mover;
        if (kind == "MovingPlatform")
        {
            var id = (string?)el.Attribute("PlatID") ?? "";
            var key = el.Descendants("KeyFrame").FirstOrDefault();
            Movers[id] = new ArtMover(id, Num(el, "X") + (key != null ? Num(key, "X") : 0), Num(el, "Y") + (key != null ? Num(key, "Y") : 0), []);
            nextOuter = mover == null ? inner : inner * outer;
            nextInner = Place(el, true, false);
            nextMover = id;
        }
        else
        {
            nextInner = Place(el, true, true) * inner;
        }
        foreach (var child in el.Elements())
            if (child.Name.LocalName is "Platform" or "Asset" or "MovingPlatform") Walk(child, nextInner, nextMover, nextOuter, game, dir);
    }

    public Dictionary<string, (int Line, double X, double Y)> Match(FrameLine[] moving)
    {
        var found = new Dictionary<string, (int, double, double)>();
        var used = new HashSet<int>();
        static bool Same(double a, double b) => Math.Abs(a - b) < 1.01;
        foreach (var mover in Movers.Values)
        {
            if (mover.Lines.Length == 0) continue;
            var first = mover.Lines[0];
            var best = (Count: 0, Line: -1, X: 0.0, Y: 0.0);
            for (var i = 0; i < moving.Length; i++)
            {
                var s = moving[i];
                if (used.Contains(i) || s.Kind != first.Kind) continue;
                foreach (var (ax, ay, bx, by) in new[] { (first.X1, first.Y1, first.X2, first.Y2), (first.X2, first.Y2, first.X1, first.Y1) })
                {
                    if (!Same(s.X2 - s.X1, bx - ax) || !Same(s.Y2 - s.Y1, by - ay)) continue;
                    double dx = s.X1 - ax, dy = s.Y1 - ay;
                    var count = mover.Lines.Count(l => moving.Any(m => m.Kind == l.Kind && Fits(m, l.X1 + dx, l.Y1 + dy, l.X2 + dx, l.Y2 + dy)));
                    if (count > best.Count) best = (count, i, ax, ay);
                }
            }
            if (best.Line < 0) continue;
            used.Add(best.Line);
            found[mover.Id] = (best.Line, best.X, best.Y);
        }
        return found;

        static bool Fits(FrameLine m, double x1, double y1, double x2, double y2) =>
            (Same(m.X1, x1) && Same(m.Y1, y1) && Same(m.X2, x2) && Same(m.Y2, y2))
            || (Same(m.X1, x2) && Same(m.Y1, y2) && Same(m.X2, x1) && Same(m.Y2, y1));
    }
}

public static class LevelDescs
{
    static readonly object gate = new();
    static string? cachedFor;
    static Dictionary<string, string> levels = new(StringComparer.Ordinal);

    static string Stamp(string game) =>
        string.Join("|", Swz.Archives.Select(a => Path.Combine(game, a + ".swz")).Where(File.Exists)
            .Select(f => f + ":" + File.GetLastWriteTimeUtc(f).Ticks + ":" + new FileInfo(f).Length))
        + "|" + Path.Combine(game, "BrawlhallaAir.swf");

    public static Dictionary<string, string> All(string game)
    {
        var stamp = Stamp(game);
        lock (gate)
        {
            if (cachedFor == stamp) return levels;
        }
        var swf = Path.Combine(game, "BrawlhallaAir.swf");
        if (!File.Exists(swf)) throw new SwzException("BrawlhallaAir.swf isn't in the game folder");
        var archives = Swz.Archives.Select(a => Path.Combine(game, a + ".swz")).Where(File.Exists).Select(File.ReadAllBytes).ToList();
        if (archives.Count == 0) throw new SwzException("the game's .swz archives aren't in the game folder");
        var key = Swz.FindKey(File.ReadAllBytes(swf), archives);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var archive in archives)
        {
            foreach (var entry in Swz.Read(archive, key))
            {
                var text = Encoding.UTF8.GetString(entry).TrimStart('\uFEFF');
                var start = text.IndexOf("<LevelDesc", StringComparison.Ordinal);
                if (start < 0 || start > 200) continue;
                var head = text.IndexOf('>', start);
                if (head < 0) continue;
                var m = System.Text.RegularExpressions.Regex.Match(text[start..head], "\\sLevelName=\"([^\"]+)\"");
                if (m.Success) found.TryAdd(m.Groups[1].Value, text);
            }
        }
        lock (gate)
        {
            cachedFor = stamp;
            levels = found;
        }
        return found;
    }

    public static Task<LevelArt?> LoadAsync(string game, string levelName) => Task.Run(() => Load(game, levelName)?.LoadPictures());

    public static LevelArt? Load(string game, string levelName)
    {
        if (levelName.Length == 0 || !Directory.Exists(game)) return null;
        try
        {
            return All(game).TryGetValue(levelName, out var xml) ? LevelArt.Parse(xml, game) : null;
        }
        catch (Exception e) when (e is SwzException or IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
        {
            return null;
        }
    }
}
