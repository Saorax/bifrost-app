using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace BifrostStudio.Bmod;

public sealed record AnmBone(bool Dup, int Id, string Transform, double[]? M, double[]? Pos);

public static class BmodHeadshot
{
    public static List<string>? BoneNames(XElement? boneTypes) =>
        boneTypes?.Elements().Where(e => e.Name.LocalName == "Bone").Select(e => e.Value).ToList();

    public static List<AnmBone>? Frame(byte[] file)
    {
        using var z = new BinaryReader(new MemoryStream(Inflate(file)), Encoding.UTF8);
        while (z.ReadBoolean())
        {
            Str(z);
            var index = Str(z);
            Str(z);
            var count = (int)z.ReadUInt32();
            for (var a = 0; a < count; a++)
            {
                var name = Str(z);
                var frames = (int)z.ReadUInt32();
                for (var k = 0; k < 5; k++) z.ReadUInt32();
                var dataCount = (int)z.ReadUInt32();
                z.BaseStream.Position += dataCount * 4L;
                var size = (int)z.ReadUInt32();
                var start = z.BaseStream.Position;
                if (index == "a__HeadShotAnimation" && name == "FaceIdle" && frames > 0) return ReadFrame(z);
                z.BaseStream.Position = start + size;
            }
        }
        return null;
    }

    static byte[] Inflate(byte[] file)
    {
        using var zlib = new ZLibStream(new MemoryStream(file, 4, file.Length - 4), CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    static string Str(BinaryReader z)
    {
        var len = z.ReadUInt16();
        return len > 0 ? Encoding.UTF8.GetString(z.ReadBytes(len)) : "";
    }

    static List<AnmBone> ReadFrame(BinaryReader z)
    {
        z.ReadInt16();
        for (var k = 0; k < 2; k++)
            if (z.ReadBoolean())
            {
                z.ReadDouble();
                z.ReadDouble();
            }
        var count = z.ReadInt16();
        var bones = new List<AnmBone>();
        for (var i = 0; i < count; i++)
        {
            if (z.ReadBoolean())
            {
                if (!z.ReadBoolean()) z.ReadByte();
                bones.Add(new AnmBone(true, 0, "", null, null));
                continue;
            }
            var id = (int)z.ReadInt16();
            var opaque = z.ReadBoolean();
            string transform;
            double[]? m = null;
            if (z.ReadBoolean()) transform = "prev";
            else
            {
                transform = z.ReadBoolean() ? (z.ReadBoolean() ? "identity" : "symmetric") : "full";
                if (transform == "symmetric") m = [z.ReadSingle(), z.ReadSingle()];
                else if (transform == "full") m = [z.ReadSingle(), z.ReadSingle(), z.ReadSingle(), z.ReadSingle()];
            }
            double[]? pos = z.ReadBoolean() ? null : [z.ReadSingle(), z.ReadSingle()];
            if (z.ReadBoolean()) z.ReadByte();
            if (!opaque) z.ReadByte();
            bones.Add(new AnmBone(false, id, transform, m, pos));
        }
        return bones;
    }

    public static List<(string Bone, Matrix M)> Pose(string game, IReadOnlyList<string>? boneNames)
    {
        if (boneNames == null) return [];
        var f = Path.Combine(game, "anims", "Animation_Player.anm");
        if (!File.Exists(f)) return [];
        if (Frame(File.ReadAllBytes(f)) is not { } bones) return [];
        var known = boneNames.ToHashSet(StringComparer.Ordinal);
        var pose = new List<(string, Matrix)>();
        double[] m = [1, 0, 0, 1];
        double[] pos = [0, 0];
        foreach (var bone in bones)
        {
            if (bone.Dup) return [];
            if (bone.Transform != "prev" && bone.M != null) m = bone.M;
            if (bone.Pos != null) pos = bone.Pos;
            var index = bone.Id - 1;
            if (index < 0 || index >= boneNames.Count) return [];
            var name = boneNames[index];
            if (name.EndsWith('R') && known.Contains(name[..^1])) continue;
            if (m.Length < 4) return [];
            pose.Add((name, new Matrix(m[0], m[1], m[2], m[3], pos[0], pos[1])));
        }
        return pose;
    }
}
