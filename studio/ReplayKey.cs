namespace BifrostStudio;

public static class ReplayKey
{
    public const int Length = 64;

    static readonly object gate = new();
    static string? cachedFor;
    static byte[]? cached;

    public static byte[]? For(string? game, IEnumerable<string> samples)
    {
        if (game == null) return null;
        var swf = Path.Combine(game, "BrawlhallaAir.swf");
        var original = swf + ".bifrost-original";
        if (File.Exists(original)) swf = original;
        if (!File.Exists(swf)) return null;
        var stamp = swf + ":" + File.GetLastWriteTimeUtc(swf).Ticks + ":" + new FileInfo(swf).Length;
        lock (gate)
        {
            if (cachedFor == stamp) return cached;
        }
        byte[]? key = null;
        try
        {
            var proof = samples.Where(f => !Path.GetFileName(f).Contains("[sdr]", StringComparison.Ordinal)).Take(3)
                .Select(f => { try { return File.ReadAllBytes(f); } catch (IOException) { return null; } }).OfType<byte[]>().ToList();
            key = Find(File.ReadAllBytes(swf), proof);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SwzException) { key = null; }
        if (key == null) return null;
        lock (gate)
        {
            cachedFor = stamp;
            cached = key;
        }
        return key;
    }

    public static void Forget()
    {
        lock (gate)
        {
            cachedFor = null;
            cached = null;
        }
    }

    public static List<byte[]> Candidates(byte[] swf)
    {
        var candidates = new List<byte[]>();
        foreach (var abc in Swz.AbcBlocks(Swz.SwfBody(swf)))
            foreach (var c in Lists(abc))
                if (!candidates.Any(k => k.AsSpan().SequenceEqual(c))) candidates.Add(c);
        return candidates;
    }

    public static byte[]? Find(byte[] swf, IReadOnlyList<byte[]> replays)
    {
        var candidates = Candidates(swf);
        if (replays.Count == 0) return candidates.Count == 1 ? candidates[0] : null;
        return candidates.Select(c => (Key: c, Fits: replays.Count(r => Fits(r, c)))).Where(c => c.Fits > 0).OrderByDescending(c => c.Fits)
            .Select(c => c.Key).FirstOrDefault();
    }

    public static bool Fits(byte[] replay, byte[] key)
    {
        try
        {
            var plain = ReplayFile.Unpack(replay, "", key);
            var h = ReplayFile.Read(plain);
            return h.Players.Count > 0 && h.Players.Count <= 32 && h.Players.All(p => p.Name.All(ch => !char.IsControl(ch)));
        }
        catch (ReplayFileException) { return false; }
    }

    static List<int> IntPool(byte[] abc)
    {
        var pool = new List<int> { 0 };
        var at = 4;
        if (!U30(abc, ref at, out var count)) return pool;
        for (var i = 1u; i < count; i++)
        {
            if (!U30(abc, ref at, out var v)) break;
            pool.Add((int)v);
        }
        return pool;
    }

    static IEnumerable<byte[]> Lists(byte[] abc)
    {
        var ints = IntPool(abc);
        for (var i = 0; i < abc.Length - Length * 2; i++)
        {
            if (abc[i] != 0x24 && abc[i] != 0x25 && abc[i] != 0x2D) continue;
            var at = i;
            var values = new byte[Length];
            var ok = true;
            for (var n = 0; n < Length && ok; n++)
            {
                if (at >= abc.Length) { ok = false; break; }
                var op = abc[at++];
                int value;
                if (op == 0x24)
                {
                    if (at >= abc.Length) { ok = false; break; }
                    value = (sbyte)abc[at++];
                }
                else if (op == 0x25)
                {
                    if (!U30(abc, ref at, out var u)) { ok = false; break; }
                    value = (short)u;
                }
                else if (op == 0x2D)
                {
                    if (!U30(abc, ref at, out var index) || index >= ints.Count) { ok = false; break; }
                    value = ints[(int)index];
                }
                else { ok = false; break; }
                if (value < 0 || value > 255) { ok = false; break; }
                values[n] = (byte)value;
            }
            if (!ok || at + 1 >= abc.Length || abc[at] != 0x56) continue;
            var after = at + 1;
            if (U30(abc, ref after, out var count) && count == Length) yield return values;
        }
    }

    static bool U30(byte[] data, ref int at, out uint value)
    {
        value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (at >= data.Length) return false;
            var b = data[at++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }
}
