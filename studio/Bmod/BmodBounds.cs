namespace BifrostStudio.Bmod;

public readonly record struct Box(double X0, double Y0, double X1, double Y1);

public readonly record struct Matrix(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
}

public sealed class SwfBitWriter
{
    readonly MemoryStream out_ = new();
    int cur, n;

    public void Put(double v, int bits)
    {
        for (var i = bits - 1; i >= 0; i--)
        {
            var bit = (int)(Math.Floor(v / Math.Pow(2, i)) % 2);
            cur = (cur << 1) | bit;
            if (++n != 8) continue;
            out_.WriteByte((byte)cur);
            cur = 0;
            n = 0;
        }
    }

    public void PutSigned(double v, int bits) => Put(v < 0 ? v + Math.Pow(2, bits) : v, bits);

    public byte[] Done()
    {
        if (n != 0) Put(0, 8 - n);
        return out_.ToArray();
    }
}

public sealed class BmodBounds
{
    readonly Dictionary<int, SwfTag> shapes = new();
    readonly Dictionary<int, SwfTag> sprites = new();
    readonly Dictionary<int, Box?> memo = new();

    public BmodBounds(IEnumerable<SwfTag> tags)
    {
        foreach (var t in tags)
        {
            if (t.Code is 2 or 22 or 32 or 83) shapes[t.Id] = t;
            else if (t.Code == 39) sprites[t.Id] = t;
        }
    }

    public IEnumerable<int> Characters => shapes.Keys.Concat(sprites.Keys);

    public static Matrix ReadMatrix(byte[] data, int pos, int limit)
    {
        var r = new SwfBits(data, pos, limit);
        double a = 1, d = 1, b = 0, c = 0;
        int bits;
        if (r.Ub(1) != 0)
        {
            bits = r.Ub(5);
            a = r.Sb(bits) / 65536;
            d = r.Sb(bits) / 65536;
        }
        if (r.Ub(1) != 0)
        {
            bits = r.Ub(5);
            b = r.Sb(bits) / 65536;
            c = r.Sb(bits) / 65536;
        }
        bits = r.Ub(5);
        var tx = r.Sb(bits) / 20;
        var ty = r.Sb(bits) / 20;
        return new Matrix(a, b, c, d, tx, ty);
    }

    static Box Rect(byte[] data, int pos, int limit)
    {
        var r = new SwfBits(data, pos, limit);
        var bits = r.Ub(5);
        var x0 = r.Sb(bits) / 20;
        var x1 = r.Sb(bits) / 20;
        var y0 = r.Sb(bits) / 20;
        var y1 = r.Sb(bits) / 20;
        return new Box(x0, y0, x1, y1);
    }

    public static Box AddBox(Box? box, Box cb, Matrix m)
    {
        foreach (var (x, y) in new[] { (cb.X0, cb.Y0), (cb.X1, cb.Y0), (cb.X0, cb.Y1), (cb.X1, cb.Y1) })
        {
            var px = m.A * x + m.C * y + m.Tx;
            var py = m.B * x + m.D * y + m.Ty;
            box = box is not { } had ? new Box(px, py, px, py)
                : new Box(Math.Min(had.X0, px), Math.Min(had.Y0, py), Math.Max(had.X1, px), Math.Max(had.Y1, py));
        }
        return box!.Value;
    }

    static List<(int Id, Matrix M)> Places(SwfTag t)
    {
        var data = t.Src;
        var limit = t.Pos + t.Len;
        var pos = t.Pos + 4;
        var list = new List<(int, Matrix)>();
        while (pos + 2 <= limit)
        {
            var cl = BmodSwf.U16(data, pos);
            pos += 2;
            var kind = cl >> 6;
            var len = cl & 0x3F;
            if (len == 0x3F)
            {
                len = BmodSwf.U32(data, pos);
                pos += 4;
            }
            var s = pos;
            var e = pos + len < limit ? pos + len : limit;
            pos += len;
            if (kind is BmodSwf.ShowFrame or BmodSwf.End) break;
            if (kind != 26 || (SwfBits.At(data, s, e) & 0x02) == 0) continue;
            var cid = SwfBits.At(data, s + 3, e) | (SwfBits.At(data, s + 4, e) << 8);
            list.Add((cid, (data[s] & 0x04) != 0 ? ReadMatrix(data, s + 5, e) : Matrix.Identity));
        }
        return list;
    }

    public Box? BoundsOf(int cid) => BoundsAt(cid, 0);

    Box? BoundsAt(int cid, int depth)
    {
        if (memo.TryGetValue(cid, out var known)) return known;
        if (shapes.TryGetValue(cid, out var st)) return memo[cid] = Rect(st.Src, st.Pos + 2, st.Pos + st.Len);
        Box? box = null;
        if (sprites.TryGetValue(cid, out var sprite) && depth < 20)
        {
            foreach (var (child, m) in Places(sprite))
                if (BoundsAt(child, depth + 1) is { } cb) box = AddBox(box, cb, m);
        }
        return memo[cid] = box;
    }

    public static double PyRound(double v)
    {
        var f = Math.Floor(v);
        var diff = v - f;
        if (diff > 0.5) return f + 1;
        if (diff < 0.5) return f;
        return f % 2 == 0 ? f : f + 1;
    }

    public static int BitLength(double v)
    {
        var bits = 0;
        while (v >= 1)
        {
            v = Math.Floor(v / 2);
            bits++;
        }
        return bits;
    }

    public static int NBits(params double[] values) =>
        Math.Max(1, values.Select(v => (v >= 0 ? BitLength(v) : BitLength(-v - 1)) + 1).DefaultIfEmpty(1).Max());

    public static byte[] WriteMatrix(Matrix m)
    {
        var w = new SwfBitWriter();
        var sx = PyRound(m.A * 65536);
        var sy = PyRound(m.D * 65536);
        w.Put(1, 1);
        var bits = NBits(sx, sy);
        w.Put(bits, 5);
        w.PutSigned(sx, bits);
        w.PutSigned(sy, bits);
        var r0 = PyRound(m.B * 65536);
        var r1 = PyRound(m.C * 65536);
        if (r0 != 0 || r1 != 0)
        {
            w.Put(1, 1);
            bits = NBits(r0, r1);
            w.Put(bits, 5);
            w.PutSigned(r0, bits);
            w.PutSigned(r1, bits);
        }
        else w.Put(0, 1);
        var x = PyRound(m.Tx * 20);
        var y = PyRound(m.Ty * 20);
        bits = NBits(x, y);
        w.Put(bits, 5);
        w.PutSigned(x, bits);
        w.PutSigned(y, bits);
        return w.Done();
    }

    public static byte[]? IconSprite(int iconId, IReadOnlyList<(int Id, Matrix M)> parts, BmodBounds bounds, Box target,
        ISet<int>? fit, double fill, double drop, (double X, double Y)? centre)
    {
        Box? box = null;
        foreach (var (cid, m) in parts)
            if (bounds.BoundsOf(cid) is { } cb && !(fit != null && !fit.Contains(cid))) box = AddBox(box, cb, m);
        if (box is not { } b) return null;
        var scale = fill * Math.Min((target.X1 - target.X0) / Math.Max(0.000001, b.X1 - b.X0),
            (target.Y1 - target.Y0) / Math.Max(0.000001, b.Y1 - b.Y0));
        var cx = centre?.X ?? (target.X0 + target.X1) / 2;
        var cy = centre?.Y ?? (target.Y0 + target.Y1) / 2;
        var ox = cx - scale * (b.X0 + b.X1) / 2;
        var oy = cy - scale * (b.Y0 + b.Y1) / 2 + drop * (target.Y1 - target.Y0);
        using var inner = new MemoryStream();
        var depth = 1;
        foreach (var (cid, m) in parts)
        {
            using var place = new MemoryStream();
            place.WriteByte(0x06);
            place.Write([(byte)(depth & 0xFF), (byte)(depth >> 8), (byte)(cid & 0xFF), (byte)(cid >> 8)]);
            place.Write(WriteMatrix(new Matrix(m.A * scale, m.B * scale, m.C * scale, m.D * scale, m.Tx * scale + ox, m.Ty * scale + oy)));
            inner.Write(BmodSwf.TagBytes(26, place.ToArray()));
            depth++;
        }
        inner.Write(BmodSwf.TagBytes(BmodSwf.ShowFrame, []));
        inner.Write(BmodSwf.TagBytes(BmodSwf.End, []));
        using var out_ = new MemoryStream();
        out_.Write([(byte)(iconId & 0xFF), (byte)(iconId >> 8), 1, 0]);
        inner.Position = 0;
        inner.CopyTo(out_);
        return out_.ToArray();
    }
}
