using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace BifrostStudio.Net;

public sealed class Packet(int type, byte[]? payload = null)
{
    public const int HasExtra = 0x8000;

    public int Type { get; } = type;
    public byte[] Payload { get; } = payload ?? [];
    public uint Field960 { get; set; }
    public uint Field924 { get; set; }

    public static bool Carries960(int t) => t is not (16 or 2463 or 2467) && (t is > 2400 and < 3000 || t is > 1700 and < 2000 || t is > 2000 and < 2300);

    public static bool Carries924(int t) => t is not (16 or 2463 or 178) && (t < 500 || t is 10400 or 10405 or 10202);

    public uint? Extra => Carries960(Type) ? Field960 : Carries924(Type) ? Field924 : null;

    public byte[] Encode()
    {
        if (Payload.Length > 0xFFFF) throw new InvalidOperationException("payload longer than 65535 bytes");
        var extra = Extra;
        var head = extra != null ? 8 : 4;
        var out_ = new byte[head + Payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(out_, (ushort)(Type | (extra != null ? HasExtra : 0)));
        if (extra is { } e) BinaryPrimitives.WriteUInt32BigEndian(out_.AsSpan(2), e);
        BinaryPrimitives.WriteUInt16BigEndian(out_.AsSpan(head - 2), (ushort)Payload.Length);
        Payload.CopyTo(out_, head);
        return out_;
    }
}

public sealed class PacketDecoder
{
    readonly List<byte> buf = [];

    public List<Packet> Feed(ReadOnlySpan<byte> data)
    {
        buf.AddRange(data.ToArray());
        var out_ = new List<Packet>();
        while (One() is { } p) out_.Add(p);
        return out_;
    }

    Packet? One()
    {
        if (buf.Count < 2) return null;
        var raw = buf[0] << 8 | buf[1];
        var t = raw & ~Packet.HasExtra;
        var at = 2;
        uint? extra = null;
        if ((raw & Packet.HasExtra) != 0)
        {
            if (buf.Count < at + 4) return null;
            extra = (uint)(buf[2] << 24 | buf[3] << 16 | buf[4] << 8 | buf[5]);
            at += 4;
        }
        if (buf.Count < at + 2) return null;
        var n = buf[at] << 8 | buf[at + 1];
        at += 2;
        if (buf.Count < at + n) return null;
        var p = new Packet(t, buf.GetRange(at, n).ToArray());
        if (extra is { } e)
        {
            if (Packet.Carries960(t)) p.Field960 = e;
            else p.Field924 = e;
        }
        buf.RemoveRange(0, at + n);
        return p;
    }
}

public sealed class BitWriter
{
    readonly List<byte> out_ = [];
    ulong cur;
    int curN;

    static int Width(ulong v)
    {
        var n = Math.Max(1, 64 - BitOperations.LeadingZeroCount(v));
        return n + (n & 1);
    }

    public BitWriter Bits(int n, ulong v)
    {
        for (var i = n - 1; i >= 0; i--)
        {
            cur = cur << 1 | (v >> i & 1);
            if (++curN == 8)
            {
                out_.Add((byte)cur);
                cur = 0;
                curN = 0;
            }
        }
        return this;
    }

    public BitWriter Bool(bool b) => Bits(1, b ? 1u : 0u);

    public BitWriter UInt(uint v)
    {
        var n = Width(v);
        return Bits(4, (ulong)(n / 2 - 1)).Bits(n, v);
    }

    public BitWriter Small(int v)
    {
        var n = Width((ulong)v);
        if (v < 0 || n > 16) throw new ArgumentOutOfRangeException(nameof(v), "Small holds 16 bits at most");
        return Bits(3, (ulong)(n / 2 - 1)).Bits(n, (ulong)v);
    }

    public BitWriter U64(ulong v)
    {
        var n = Width(v);
        return Bits(5, (ulong)(n / 2 - 1)).Bits(n, v);
    }

    public BitWriter Byte(byte v) => Bits(8, v);

    public BitWriter F32(float v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(b, v);
        return Raw(b);
    }

    public BitWriter String(string s)
    {
        var data = Encoding.UTF8.GetBytes(s);
        if (data.Length > 65535) data = data[..65535];
        Bits(16, (ulong)data.Length);
        return Raw(data);
    }

    public BitWriter Raw(ReadOnlySpan<byte> data)
    {
        if (curN == 0)
        {
            out_.AddRange(data.ToArray());
            return this;
        }
        foreach (var b in data) Bits(8, b);
        return this;
    }

    public byte[] ToArray()
    {
        if (curN == 0) return [.. out_];
        return [.. out_, (byte)(cur << (8 - curN))];
    }
}

public sealed class BitReader(byte[] data)
{
    public const int MaxOverread = 512;
    int pos;

    public int Left => data.Length * 8 - pos;

    public ulong Bits(int n)
    {
        if (n <= 0) return 0;
        if (n > 64) throw new ArgumentOutOfRangeException(nameof(n));
        if (pos + n > data.Length * 8 + MaxOverread) throw new IndexOutOfRangeException($"read of {n} bits past the end ({Left} left)");
        ulong v = 0;
        for (var i = 0; i < n; i++, pos++)
        {
            var bit = pos >> 3 < data.Length ? data[pos >> 3] >> (7 - (pos & 7)) & 1 : 0;
            v = v << 1 | (uint)bit;
        }
        return v;
    }

    public bool Bool() => Bits(1) != 0;
    public uint UInt() => (uint)Bits(((int)Bits(4) + 1) * 2);
    public int Small() => (int)Bits(((int)Bits(3) + 1) * 2);
    public ulong U64() => Bits(((int)Bits(5) + 1) * 2);
    public byte Byte() => (byte)Bits(8);

    public byte[] Raw(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)Bits(8);
        return b;
    }

    public float F32() => BinaryPrimitives.ReadSingleBigEndian(Raw(4));

    public string String() => Encoding.UTF8.GetString(Raw((int)Bits(16)));
}
