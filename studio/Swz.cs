using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BifrostStudio;

public sealed class SwzException(string message) : Exception(message);

public sealed class SwzRandom
{
    readonly uint[] state = new uint[16];
    int index;

    public SwzRandom(uint seed)
    {
        state[0] = seed;
        for (var i = 1; i < 16; i++)
        {
            var prev = state[i - 1];
            state[i] = unchecked(0x6C078965u * (prev ^ (prev >> 30)) + (uint)i);
        }
    }

    public uint Next()
    {
        unchecked
        {
            var a = state[index];
            var b = state[(index + 13) & 15];
            var c = a ^ (a << 16) ^ b ^ (b << 15);
            b = state[(index + 9) & 15];
            b ^= b >> 11;
            state[index] = b ^ c;
            a = state[index];
            var d = a ^ ((a << 5) & 0xDA442D24u);
            index = (index + 15) & 15;
            a = state[index];
            state[index] = a ^ (a << 2) ^ (b << 28) ^ c ^ (c << 18) ^ d;
            return state[index];
        }
    }
}

public static class Swz
{
    public static readonly string[] Archives = ["Dynamic", "Engine", "Init", "Game"];

    static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data[at..]);

    static uint KeyChecksum(uint key, SwzRandom rand)
    {
        var sum = 0x2DF4A1CDu;
        var rounds = key % 31 + 5;
        for (var i = 0u; i < rounds; i++) sum ^= rand.Next();
        return sum;
    }

    public static bool KeyFits(ReadOnlySpan<byte> archive, uint key)
        => archive.Length >= 8 && KeyChecksum(key, new SwzRandom(U32(archive, 4) ^ key)) == U32(archive, 0);

    public static List<byte[]> Read(byte[] archive, uint key)
    {
        if (archive.Length < 8) throw new SwzException("it is too short to be an archive");
        var rand = new SwzRandom(U32(archive, 4) ^ key);
        if (KeyChecksum(key, rand) != U32(archive, 0)) throw new SwzException("the key doesn't fit it");
        var files = new List<byte[]>();
        var at = 8;
        while (at < archive.Length)
        {
            if (archive.Length - at < 12) throw new SwzException("entry " + files.Count + " is cut off");
            var packed = U32(archive, at) ^ rand.Next();
            var size = U32(archive, at + 4) ^ rand.Next();
            var expected = U32(archive, at + 8);
            at += 12;
            if (packed > (uint)(archive.Length - at)) throw new SwzException("entry " + files.Count + " is cut off");
            var data = archive.AsSpan(at, (int)packed).ToArray();
            at += (int)packed;
            var sum = rand.Next();
            for (var i = 0; i < data.Length; i++)
            {
                data[i] ^= (byte)(rand.Next() >> (i % 16));
                sum = data[i] ^ uint.RotateRight(sum, i % 7 + 1);
            }
            if (sum != expected) throw new SwzException("entry " + files.Count + " fails its checksum");
            byte[] plain;
            try
            {
                using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                using var output = new MemoryStream((int)Math.Min(size, 64u << 20));
                input.CopyTo(output);
                plain = output.ToArray();
            }
            catch (InvalidDataException) { throw new SwzException("entry " + files.Count + " doesn't unpack"); }
            if ((uint)plain.Length != size) throw new SwzException("entry " + files.Count + " unpacks to the wrong size");
            files.Add(plain);
        }
        return files;
    }

    internal static byte[] SwfBody(byte[] swf)
    {
        if (swf.Length < 8) throw new SwzException("BrawlhallaAir.swf isn't a SWF file");
        var signature = Encoding.ASCII.GetString(swf, 0, 3);
        if (signature == "FWS") return swf[8..];
        if (signature != "CWS") throw new SwzException("BrawlhallaAir.swf can't be read here");
        try
        {
            using var input = new ZLibStream(new MemoryStream(swf, 8, swf.Length - 8), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException) { throw new SwzException("BrawlhallaAir.swf doesn't unpack"); }
    }

    internal static List<byte[]> AbcBlocks(byte[] body)
    {
        var blocks = new List<byte[]>();
        if (body.Length < 1) return blocks;
        var at = (5 + 4 * (body[0] >> 3) + 7) / 8 + 4;
        while (at + 2 <= body.Length)
        {
            var head = body[at] | body[at + 1] << 8;
            at += 2;
            var code = head >> 6;
            var length = head & 0x3F;
            if (length == 0x3F)
            {
                if (at + 4 > body.Length) break;
                length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at));
                at += 4;
            }
            if (length < 0 || length > body.Length - at) break;
            if (code == 82)
            {
                var p = at + 4;
                while (p < at + length && body[p] != 0) p++;
                if (p + 1 < at + length) blocks.Add(body[(p + 1)..(at + length)]);
            }
            else if (code == 72) blocks.Add(body[at..(at + length)]);
            if (code == 0) break;
            at += length;
        }
        return blocks;
    }

    static uint VarU32(byte[] data, ref int at)
    {
        var value = 0u;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (at >= data.Length) throw new SwzException("the client's code is cut off");
            var b = data[at++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
        }
        return value;
    }

    static IEnumerable<uint> Constants(byte[] abc)
    {
        var at = 4;
        var found = new List<uint>();
        var count = VarU32(abc, ref at);
        for (var i = 1u; i < count; i++) found.Add(VarU32(abc, ref at));
        count = VarU32(abc, ref at);
        for (var i = 1u; i < count; i++) found.Add(VarU32(abc, ref at));
        return found;
    }

    public static uint FindKey(byte[] swf, IReadOnlyList<byte[]> archives)
    {
        var tried = new HashSet<uint>();
        foreach (var abc in AbcBlocks(SwfBody(swf)))
        {
            foreach (var candidate in Constants(abc))
            {
                if (!tried.Add(candidate)) continue;
                if (archives.All(a => KeyFits(a, candidate))) return candidate;
            }
        }
        throw new SwzException(tried.Count == 0 ? "BrawlhallaAir.swf has no code" : "no key in BrawlhallaAir.swf fits the game's archives");
    }
}
