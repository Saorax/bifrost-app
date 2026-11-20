using System.IO.Compression;
using System.Text;

namespace BifrostStudio;

public sealed record ReplayHero(uint Hero, uint Costume, uint Stance, uint WeaponSkins);

public sealed record ReplayFilePlayer(uint Entity, uint Pid, string Name, uint Colour, uint Avatar, uint Team, uint BotLevel,
    IReadOnlyList<ReplayHero> Heroes, IReadOnlyList<uint> InputTimes, IReadOnlyList<uint> Inputs, uint Place);

public sealed record ReplaySettings(uint Flags, uint MaxPlayers, uint Duration, uint RoundDuration, uint Lives, uint Scoring,
    uint ScoreToWin, uint GameSpeed, uint DamageRatio, uint LevelSet)
{
    public bool Teams => (Flags & 1) != 0;
}

public sealed record ReplayHeader(uint Version, uint Seed, uint Playlist, string PlaylistName, bool Online, ReplaySettings Settings,
    uint Level, uint HeroesPerPlayer, IReadOnlyList<ReplayFilePlayer> Players, IReadOnlyList<(uint Entity, uint Ms)> Kos, uint LengthMs, bool Complete);

public sealed class ReplayFileException(string message) : Exception(message);

public static class ReplayFile
{
    public static byte[] Unpack(byte[] file, string name, IReadOnlyList<byte> key)
    {
        if (name.Contains("[sdr]", StringComparison.Ordinal)) return file;
        byte[] plain;
        try
        {
            using var z = new ZLibStream(new MemoryStream(file), CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            plain = output.ToArray();
        }
        catch (InvalidDataException) { throw new ReplayFileException("it isn't a packed replay"); }
        if (Version(plain) is >= 1 and < XorSince) return plain;
        if (key.Count == 0) throw new ReplayFileException(NoKey);
        for (var i = 0; i < plain.Length; i++) plain[i] ^= key[i % key.Count];
        return plain;
    }

    public static uint Version(byte[] plain)
    {
        try { return Start(new Bits(plain)).Version; }
        catch (ReplayFileException) { return 0; }
    }

    public const uint NewestKnown = 271;
    public const uint XorSince = 30;
    public const string NoKey = "no replay key";

    static (uint Version, int TagBits, bool First, uint Seed) Start(Bits s)
    {
        var first = s.U32();
        if (first >= 236 && first <= 4096) return (first, 4, true, 0);
        foreach (var width in new[] { 3, 4 })
        {
            var t = new Bits(s.Data);
            if (t.Read(width) != 3) continue;
            var seed = t.U32();
            var version = t.U32();
            if (version >= 1 && version < 236 && (width == 4) == (version >= 235)) return (version, width, false, seed);
        }
        throw new ReplayFileException("not a replay Studio knows");
    }

    public static ReplayHeader Read(byte[] plain)
    {
        var (version, tagBits, versionFirst, _) = Start(new Bits(plain));
        var v = version;
        var s = new Bits(plain);
        if (versionFirst) s.U32();
        uint seed = 0, playlist = 0, level = 0, heroes = 1, end = 0, maxInput = 0;
        var playlistName = "";
        var online = false;
        var complete = false;
        ReplaySettings? settings = null;
        var order = new List<uint>();
        var made = new Dictionary<uint, (uint Pid, string Name, uint Colour, uint Avatar, uint Team, List<ReplayHero> Heroes)>();
        var times = new Dictionary<uint, List<uint>>();
        var bits = new Dictionary<uint, List<uint>>();
        var places = new Dictionary<uint, uint>();
        var koWho = new List<uint>();
        var koWhen = new List<uint>();
        void Input(uint ent, uint when, uint input)
        {
            if (!times.TryGetValue(ent, out var t)) { t = times[ent] = []; bits[ent] = []; }
            t.Add(when);
            bits[ent].Add(input);
            if (when > maxInput) maxInput = when;
        }
        string Name()
        {
            var name = s.Text();
            if (name.Length > 64 || name.Any(ch => char.IsControl(ch) || ch == '�')) throw new ReplayFileException("a player name that doesn't read: a damaged file");
            return name;
        }
        var done = false;
        while (!done && s.Left >= tagBits)
        {
            var chunk = s.Read(tagBits);
            switch (chunk)
            {
                case 1 when v >= 82:
                    while (s.Read(1) == 1)
                    {
                        var ent = s.Read(5);
                        var count = s.U32();
                        if (count > s.Left) throw new ReplayFileException("an input list runs past the end");
                        for (var i = 0u; i < count; i++)
                        {
                            var when = s.U32();
                            Input(ent, when, s.Read(1) != 0 ? s.Read(14) : 0);
                        }
                    }
                    break;
                case 1:
                {
                    var ent = s.Read(5);
                    var when = s.U32();
                    Input(ent, when, s.Read(1) != 0 ? s.Read(14) : 0);
                    break;
                }
                case 2:
                    if (v < 129)
                    {
                        end = s.U32();
                        s.U32();
                    }
                    done = true;
                    complete = true;
                    break;
                case 3:
                    seed = s.U32();
                    if (!versionFirst) s.U32();
                    var third = s.U32();
                    if (v < 97) level = third;
                    else
                    {
                        playlist = third;
                        playlistName = playlist != 0 ? s.Text() : "";
                    }
                    online = s.Read(1) != 0;
                    break;
                case 4:
                {
                    var ints = new List<uint>();
                    var teams = false;
                    if (v < 198)
                    {
                        teams = s.Read(1) != 0;
                        for (var i = 0; i < 7; i++) ints.Add(s.U32());
                        s.Read(2);
                        s.U32();
                        s.Read(v < 28 ? 4 : v < 38 ? 5 : 6);
                        if (v < 13)
                        {
                            var count = s.U32();
                            if (count > s.Left / 32) throw new ReplayFileException("a list runs past the end");
                            for (var i = 0u; i < count; i++) s.U32();
                        }
                        else
                        {
                            s.U32();
                            if (v >= 101) s.Read(1);
                            if (v >= 191) s.Read(1);
                        }
                        level = s.U32();
                        settings = new ReplaySettings(teams ? 1u : 0, ints[0], ints[1], ints[2], ints[3], ints[4], ints[5], ints[6], 0, 0);
                    }
                    else
                    {
                        var count = v < 228 ? 11 : v < 235 ? 13 : v < 236 ? 17 : 16;
                        for (var i = 0; i < count; i++) ints.Add(s.U32());
                        level = ints[^1];
                        settings = new ReplaySettings(ints[0], ints[1], ints[2], ints[3], ints[4], ints[5], ints[6], ints[7], ints[8], ints[9]);
                    }
                    heroes = v >= 18 ? s.U16() : 1;
                    if (heroes > 5) throw new ReplayFileException("too many legends per player: a damaged file");
                    while (s.Read(1) != 0)
                    {
                        var ent = s.U32();
                        uint pid = v >= 268 ? s.U32() : 0;
                        var name = Name();
                        var list = new List<ReplayHero>();
                        uint colour, avatar = 0, team;
                        if (v < 18)
                        {
                            var fixedInts = new uint[12];
                            for (var i = 0; i < 12; i++) fixedInts[i] = s.U32();
                            s.U16();
                            s.U16();
                            team = s.U32();
                            colour = fixedInts[2];
                            list.Add(new ReplayHero(fixedInts[0], fixedInts[1], 0, 0));
                        }
                        else
                        {
                            colour = s.U32();
                            var cosmetics = v < 90 ? 7 : v < 246 ? 3 : v < 256 ? 4 : 5;
                            for (var i = 0; i < cosmetics; i++) s.U32();
                            if (v >= 90) for (var i = 0; i < 8; i++) s.U32();
                            s.U16();
                            s.U16();
                            if (v >= 90) while (s.Read(1) != 0) s.U32();
                            if (v >= 32) avatar = s.U16();
                            team = s.U32();
                            if (v >= 198) s.U32();
                            for (var i = 0; i < heroes; i++) list.Add(new ReplayHero(s.U32() & 0xFFFF, s.U32(), s.U32(), s.U32()));
                            if (v >= 158) s.Read(1);
                            if (v >= 209 && s.Read(1) != 0)
                            {
                                s.U32();
                                s.U32();
                                s.U32();
                            }
                        }
                        order.Add(ent);
                        made[ent] = (pid, name, colour, avatar, team, list);
                    }
                    if (v >= 3 && v < 236) s.U32();
                    s.U32();
                    break;
                }
                case 5 when v >= 91:
                case 7 when v >= 152:
                    while (s.Read(1) != 0)
                    {
                        var ent = s.Read(5);
                        var when = s.U32();
                        if (chunk != 5) continue;
                        var at = koWhen.FindIndex(w => w > when);
                        if (at < 0) { koWho.Add(ent); koWhen.Add(when); }
                        else { koWho.Insert(at, ent); koWhen.Insert(at, when); }
                    }
                    break;
                case 6 when v >= 129:
                    end = s.U32();
                    if (!versionFirst) s.U32();
                    if (s.Read(1) != 0)
                        while (s.Read(1) != 0)
                        {
                            var ent = s.Read(5);
                            places[ent] = s.U16();
                        }
                    if (v >= 219) s.U32();
                    break;
                case 8 when v >= 129:
                    throw new ReplayFileException("the replay was cut off while it was written");
                default:
                    end = maxInput;
                    done = true;
                    break;
            }
        }
        if (settings == null || order.Count == 0) throw new ReplayFileException("no players in it");
        var players = order.Select(e =>
        {
            var p = made[e];
            return new ReplayFilePlayer(e, p.Pid, p.Name, p.Colour, p.Avatar, p.Team, 0, p.Heroes,
                times.GetValueOrDefault(e) ?? [], bits.GetValueOrDefault(e) ?? [], places.GetValueOrDefault(e));
        }).ToList();
        var kos = koWho.Zip(koWhen, (w, t) => (w, t)).ToList();
        return new ReplayHeader(version, seed, playlist, playlistName, online, settings, level, heroes, players, kos, end != 0 ? end : maxInput, complete);
    }

    sealed class Bits(byte[] data)
    {
        long at;

        public byte[] Data => data;

        public long Left => data.Length * 8L - at;

        public uint Read(int count)
        {
            if (count > Left) throw new ReplayFileException("the replay ends early");
            uint value = 0;
            while (count > 0)
            {
                var index = (int)(at >> 3);
                var offset = (int)(at & 7);
                var room = 8 - offset;
                var take = Math.Min(count, room);
                var part = (uint)((data[index] & ((1 << room) - 1)) >> (room - take));
                value |= part << (count - take);
                count -= take;
                at += take;
            }
            return value;
        }

        public uint U32() => Read(32);

        public uint U16() => Read(16);

        public string Text()
        {
            var length = (int)U16();
            if (length * 8L > Left) throw new ReplayFileException("a name runs past the end");
            var bytes = new byte[length];
            for (var i = 0; i < length; i++) bytes[i] = (byte)Read(8);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
