using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace BifrostStudio.Bmod;

public readonly record struct SwfTag(int Code, byte[] Src, int Pos, int Len)
{
    public ReadOnlySpan<byte> Body => Src.AsSpan(Pos, Len);
    public int Id => BmodSwf.U16(Src, Pos);
    public byte[] Copy() => Body.ToArray();
    public static SwfTag Of(int code, byte[] body) => new(code, body, 0, body.Length);
}

public sealed record SwfFile(int Version, byte[] Rect, int Rate, int Count, List<SwfTag> Tags);

public sealed record BmodFile(SwfFile Swf, JsonObject Meta, List<(int Id, string Name)> Symbols);

public sealed class BmodException(string message) : Exception(message);

public static class BmodSwf
{
    public const int End = 0;
    public const int ShowFrame = 1;
    public const int SetBackground = 9;
    public const int FileAttributes = 69;
    public const int SymbolClass = 76;
    public const int Metadata = 77;
    public const int DoAbc = 82;
    public const int DoAbcOld = 72;
    public const int BinaryData = 87;

    public static readonly int[] NotCopied = [0, 1, 69, 76, 77, 82, 87, 9, 56, 72, 41, 64, 65, 86];
    static readonly int[] LongTags = [20, 21, 35, 36, 90, 6];
    static readonly int[] CharacterTags = [2, 22, 32, 83, 39, 6, 20, 21, 35, 36, 90];

    public static int U16(byte[] data, int pos) => data[pos] | (data[pos + 1] << 8);

    public static int U32(byte[] data, int pos) => data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);

    public static SwfFile Read(byte[] data)
    {
        if (data.Length < 8) throw new BmodException("not a SWF (too short)");
        var sig = Encoding.ASCII.GetString(data, 0, 3);
        var body = data;
        var start = 8;
        if (sig == "CWS")
        {
            using var z = new ZLibStream(new MemoryStream(data, 8, data.Length - 8), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            z.CopyTo(plain);
            body = plain.ToArray();
            start = 0;
        }
        else if (sig != "FWS")
        {
            throw new BmodException($"not a SWF (starts with {sig})");
        }
        var nbits = body[start] >> 3;
        var rectLen = (5 + 4 * nbits + 7) >> 3;
        var rect = body.AsSpan(start, rectLen).ToArray();
        var pos = start + rectLen;
        var rate = U16(body, pos);
        var count = U16(body, pos + 2);
        pos += 4;
        var tags = new List<SwfTag>();
        while (pos + 2 <= body.Length)
        {
            var codeLen = U16(body, pos);
            pos += 2;
            var code = codeLen >> 6;
            var length = codeLen & 0x3F;
            if (length == 0x3F)
            {
                length = U32(body, pos);
                pos += 4;
            }
            if (length < 0 || pos + length > body.Length) length = body.Length - pos;
            tags.Add(new SwfTag(code, body, pos, length));
            pos += length;
            if (code == End) break;
        }
        return new SwfFile(data[3], rect, rate, count, tags);
    }

    static void WriteU16(Stream out_, int v)
    {
        out_.WriteByte((byte)(v & 0xFF));
        out_.WriteByte((byte)((v >> 8) & 0xFF));
    }

    static void WriteU32(Stream out_, int v)
    {
        WriteU16(out_, v & 0xFFFF);
        WriteU16(out_, (v >> 16) & 0xFFFF);
    }

    public static void WriteTag(Stream out_, SwfTag t)
    {
        if (t.Len < 0x3F && Array.IndexOf(LongTags, t.Code) < 0)
        {
            WriteU16(out_, (t.Code << 6) | t.Len);
        }
        else
        {
            WriteU16(out_, (t.Code << 6) | 0x3F);
            WriteU32(out_, t.Len);
        }
        if (t.Len > 0) out_.Write(t.Src, t.Pos, t.Len);
    }

    public static byte[] TagBytes(int code, byte[] body)
    {
        using var out_ = new MemoryStream();
        WriteTag(out_, SwfTag.Of(code, body));
        return out_.ToArray();
    }

    public static byte[] Write(SwfFile header, IEnumerable<SwfTag> tags)
    {
        using var body = new MemoryStream();
        body.Write(header.Rect);
        WriteU16(body, header.Rate);
        WriteU16(body, header.Count);
        foreach (var t in tags) WriteTag(body, t);
        using var out_ = new MemoryStream();
        out_.Write("FWS"u8);
        out_.WriteByte((byte)header.Version);
        WriteU32(out_, 8 + (int)body.Length);
        body.Position = 0;
        body.CopyTo(out_);
        return out_.ToArray();
    }

    public static List<(int Id, string Name)> ReadSymbolClass(SwfTag t)
    {
        var data = t.Src;
        var limit = t.Pos + t.Len;
        var count = U16(data, t.Pos);
        var out_ = new List<(int, string)>();
        var i = t.Pos + 2;
        for (var k = 0; k < count && i + 2 <= limit; k++)
        {
            var cid = U16(data, i);
            var j = i + 2;
            while (j < limit && data[j] != 0) j++;
            out_.Add((cid, Encoding.UTF8.GetString(data, i + 2, j - i - 2)));
            i = j + 1;
        }
        return out_;
    }

    public static byte[] SymbolClassTag(IReadOnlyList<(int Id, string Name)> entries)
    {
        using var out_ = new MemoryStream();
        WriteU16(out_, entries.Count);
        foreach (var (id, name) in entries)
        {
            WriteU16(out_, id);
            out_.Write(Encoding.UTF8.GetBytes(name));
            out_.WriteByte(0);
        }
        return out_.ToArray();
    }

    public static List<(int Id, string Name)> Symbols(IEnumerable<SwfTag> tags) =>
        tags.Where(t => t.Code == SymbolClass).SelectMany(ReadSymbolClass).ToList();

    public static string JsonPrefix(string text)
    {
        var depth = 0;
        var inString = false;
        var started = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c is '{' or '[')
            {
                depth++;
                started = true;
            }
            else if (c is '}' or ']')
            {
                depth--;
                if (started && depth == 0) return text[..(i + 1)];
            }
        }
        return text;
    }

    public static BmodFile ReadBmod(byte[] data)
    {
        var swf = Read(data);
        JsonObject? meta = null;
        foreach (var t in swf.Tags)
        {
            if (t.Code != Metadata || meta != null) continue;
            var text = Encoding.UTF8.GetString(t.Body).TrimEnd('\0');
            try { meta = JsonNode.Parse(JsonPrefix(text)) as JsonObject; }
            catch (System.Text.Json.JsonException e) { throw new BmodException($"its Metadata tag isn't JSON ({e.Message})"); }
        }
        if (meta == null) throw new BmodException("no Metadata tag, so not a .bmod");
        return new BmodFile(swf, meta, Symbols(swf.Tags));
    }

    public static Dictionary<int, SwfTag> BinaryDataOf(IEnumerable<SwfTag> tags)
    {
        var out_ = new Dictionary<int, SwfTag>();
        foreach (var t in tags)
            if (t.Code == BinaryData && t.Len >= 6) out_[t.Id] = new SwfTag(0, t.Src, t.Pos + 6, t.Len - 6);
        return out_;
    }

    public static int MaxCharacterId(IEnumerable<SwfTag> tags) =>
        tags.Where(t => Array.IndexOf(CharacterTags, t.Code) >= 0).Select(t => t.Id).DefaultIfEmpty(0).Max();
}
