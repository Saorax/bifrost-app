namespace BifrostStudio.Bmod;

public sealed class SwfBits(byte[] data, int pos, int limit)
{
    int bit;

    public int P { get; private set; } = pos;

    public static int At(byte[] data, int i, int limit) =>
        i < 0 || i >= limit || i >= data.Length ? throw new BmodException($"past the end of a tag at {i}") : data[i];

    public int Ub(int bits)
    {
        var v = 0;
        for (var i = 0; i < bits; i++)
        {
            v = (v << 1) | ((At(data, P, limit) >> (7 - bit)) & 1);
            if (++bit == 8)
            {
                bit = 0;
                P++;
            }
        }
        return v;
    }

    public double Sb(int bits)
    {
        double v = Ub(bits);
        return bits > 0 && v >= Math.Pow(2, bits - 1) ? v - Math.Pow(2, bits) : v;
    }

    public void Align()
    {
        if (bit == 0) return;
        bit = 0;
        P++;
    }
}

public static class BmodShapes
{
    static int At(byte[] d, int i, int limit) => SwfBits.At(d, i, limit);

    static int SkipRect(byte[] d, int pos, int limit)
    {
        var r = new SwfBits(d, pos, limit);
        var bits = r.Ub(5);
        for (var i = 0; i < 4; i++) r.Sb(bits);
        r.Align();
        return r.P;
    }

    static int SkipMatrix(byte[] d, int pos, int limit)
    {
        var r = new SwfBits(d, pos, limit);
        int bits;
        for (var i = 0; i < 2; i++)
        {
            if (r.Ub(1) == 0) continue;
            bits = r.Ub(5);
            r.Sb(bits);
            r.Sb(bits);
        }
        bits = r.Ub(5);
        r.Sb(bits);
        r.Sb(bits);
        r.Align();
        return r.P;
    }

    static int Rgb(byte[] d, int pos, int limit) => (At(d, pos, limit) << 16) | (At(d, pos + 1, limit) << 8) | At(d, pos + 2, limit);

    static void Add(OrderedDictionary<int, int>? out_, int colour)
    {
        if (out_ == null) return;
        out_[colour] = out_.TryGetValue(colour, out var n) ? n + 1 : 1;
    }

    static int FillColours(byte[] d, int pos, int limit, int ver, OrderedDictionary<int, int>? out_)
    {
        var kind = At(d, pos, limit);
        pos++;
        var size = ver >= 3 ? 4 : 3;
        if (kind == 0)
        {
            Add(out_, Rgb(d, pos, limit));
            return pos + size;
        }
        if (kind is 0x10 or 0x12 or 0x13)
        {
            pos = SkipMatrix(d, pos, limit);
            var count = At(d, pos, limit) & 0x0F;
            pos++;
            for (var i = 0; i < count; i++)
            {
                Add(out_, Rgb(d, pos + 1, limit));
                pos += 1 + size;
            }
            return pos + (kind == 0x13 ? 2 : 0);
        }
        if (kind is >= 0x40 and <= 0x43) return SkipMatrix(d, pos + 2, limit);
        throw new BmodException($"fill style type {kind}");
    }

    static int Count(byte[] d, ref int pos, int limit, int ver)
    {
        var count = At(d, pos, limit);
        pos++;
        if (count == 0xFF && ver >= 2)
        {
            count = At(d, pos, limit) | (At(d, pos + 1, limit) << 8);
            pos += 2;
        }
        return count;
    }

    static int StyleColours(byte[] d, int pos, int limit, int ver, OrderedDictionary<int, int>? out_)
    {
        var count = Count(d, ref pos, limit, ver);
        for (var i = 0; i < count; i++) pos = FillColours(d, pos, limit, ver, out_);
        return SkipLines(d, pos, limit, ver);
    }

    static int SkipLines(byte[] d, int pos, int limit, int ver)
    {
        var count = Count(d, ref pos, limit, ver);
        for (var i = 0; i < count; i++)
        {
            if (ver == 4)
            {
                var flags = At(d, pos + 2, limit);
                var join = (flags >> 4) & 3;
                var hasFill = (flags >> 3) & 1;
                pos += 4 + (join == 2 ? 2 : 0);
                pos = hasFill != 0 ? FillColours(d, pos, limit, ver, null) : pos + 4;
            }
            else pos += 2 + (ver >= 3 ? 4 : 3);
        }
        return pos;
    }

    static void SkipEdge(SwfBits r)
    {
        var straight = r.Ub(1);
        var bits = r.Ub(4) + 2;
        if (straight == 0)
        {
            for (var i = 0; i < 4; i++) r.Sb(bits);
        }
        else if (r.Ub(1) != 0)
        {
            r.Sb(bits);
            r.Sb(bits);
        }
        else
        {
            r.Ub(1);
            r.Sb(bits);
        }
    }

    static bool WalkShape(byte[] d, int start, int limit, int ver, OrderedDictionary<int, int>? out_, bool stopAtNew)
    {
        var r = new SwfBits(d, start, limit);
        var nf = r.Ub(4);
        var nl = r.Ub(4);
        while (true)
        {
            if (r.Ub(1) != 0)
            {
                SkipEdge(r);
                continue;
            }
            var flags = r.Ub(5);
            if (flags == 0) return false;
            if ((flags & 1) != 0)
            {
                var bits = r.Ub(5);
                r.Sb(bits);
                r.Sb(bits);
            }
            if ((flags & 2) != 0) r.Ub(nf);
            if ((flags & 4) != 0) r.Ub(nf);
            if ((flags & 8) != 0) r.Ub(nl);
            if ((flags & 16) == 0) continue;
            if (stopAtNew) return true;
            r.Align();
            r = new SwfBits(d, StyleColours(d, r.P, limit, ver > 2 ? ver : 2, out_), limit);
            nf = r.Ub(4);
            nl = r.Ub(4);
        }
    }

    public static bool UsesNewStyles(SwfTag t)
    {
        var limit = t.Pos + t.Len;
        var start = StyleColours(t.Src, SkipRect(t.Src, t.Pos + 2, limit), limit, 1, null);
        return WalkShape(t.Src, start, limit, 1, null, true);
    }

    public static (List<SwfTag> Tags, int Fixed) FixShapes(IEnumerable<SwfTag> tags)
    {
        var out_ = new List<SwfTag>();
        var fixedCount = 0;
        foreach (var tag in tags)
        {
            var t = tag;
            if (t.Code == 2)
            {
                try
                {
                    if (UsesNewStyles(t))
                    {
                        t = t with { Code = 22 };
                        fixedCount++;
                    }
                }
                catch (Exception e) when (e is BmodException or IndexOutOfRangeException) { }
            }
            out_.Add(t);
        }
        return (out_, fixedCount);
    }

    public static OrderedDictionary<int, int> ShapeColours(SwfTag t, int ver)
    {
        var out_ = new OrderedDictionary<int, int>();
        var limit = t.Pos + t.Len;
        var pos = SkipRect(t.Src, t.Pos + 2, limit);
        if (ver == 4) pos = SkipRect(t.Src, pos, limit) + 1;
        WalkShape(t.Src, StyleColours(t.Src, pos, limit, ver, out_), limit, ver, out_, false);
        return out_;
    }

    public static List<int> SpriteChildren(SwfTag t)
    {
        var d = t.Src;
        var limit = t.Pos + t.Len;
        var pos = t.Pos + 4;
        var out_ = new List<int>();
        while (pos + 2 <= limit)
        {
            var cl = BmodSwf.U16(d, pos);
            pos += 2;
            var code = cl >> 6;
            var len = cl & 0x3F;
            if (len == 0x3F)
            {
                len = BmodSwf.U32(d, pos);
                pos += 4;
            }
            var s = pos;
            var e = pos + len < limit ? pos + len : limit;
            pos += len;
            if (code == 26 && e > s && (d[s] & 0x02) != 0)
            {
                out_.Add(At(d, s + 3, e) | (At(d, s + 4, e) << 8));
            }
            else if (code == 70 && e - s > 2 && (d[s] & 0x02) != 0)
            {
                var i = s + 4;
                if ((d[s + 1] & 0x18) != 0)
                {
                    while (i < e && d[i] != 0) i++;
                    if (i >= e) throw new BmodException("no class name end");
                    i++;
                }
                out_.Add(At(d, i, e) | (At(d, i + 1, e) << 8));
            }
            if (code == BmodSwf.End) break;
        }
        return out_;
    }

    static int ShapeVersion(int code) => code switch { 2 => 1, 22 => 2, 32 => 3, 83 => 4, _ => 0 };

    public static OrderedDictionary<string, OrderedDictionary<int, int>> SymbolColours(IEnumerable<SwfTag> tags)
    {
        var shapes = new Dictionary<int, OrderedDictionary<int, int>>();
        var sprites = new Dictionary<int, List<int>>();
        var names = new OrderedDictionary<int, string>();
        foreach (var t in tags)
        {
            var ver = ShapeVersion(t.Code);
            if (ver != 0)
            {
                try { shapes[t.Id] = ShapeColours(t, ver); }
                catch (Exception e) when (e is BmodException or IndexOutOfRangeException) { }
            }
            else if (t.Code == 39) sprites[t.Id] = SpriteChildren(t);
            else if (t.Code == BmodSwf.SymbolClass)
                foreach (var (id, name) in BmodSwf.ReadSymbolClass(t)) names[id] = name;
        }
        OrderedDictionary<int, int> Collect(int cid, HashSet<int> seen)
        {
            if (shapes.TryGetValue(cid, out var shape)) return shape;
            var total = new OrderedDictionary<int, int>();
            if (!sprites.TryGetValue(cid, out var children)) return total;
            foreach (var child in children)
            {
                if (!seen.Add(child)) continue;
                foreach (var (c, n) in Collect(child, seen)) total[c] = total.TryGetValue(c, out var had) ? had + n : n;
            }
            return total;
        }
        var out_ = new OrderedDictionary<string, OrderedDictionary<int, int>>();
        foreach (var (cid, name) in names) out_[name] = Collect(cid, [cid]);
        return out_;
    }
}
