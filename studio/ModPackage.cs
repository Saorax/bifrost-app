using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace BifrostStudio;

public sealed record PackedFile(string Path, byte[] Data);

public static partial class ModPackage
{
    public const int MaxFiles = 4096;
    public const long MaxContent = 256L * 1024 * 1024;
    public const long MaxPackage = 64L * 1024 * 1024;
    static readonly byte[] Magic = "BHMP"u8.ToArray();

    static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    static bool SafePart(string p) => p.Length > 0 && !p.StartsWith('.') && !p.EndsWith('.') && !p.EndsWith(' ') && !Devices.Contains(p.Split('.')[0].TrimEnd());

    public static bool SafePath(string path) => PathRule().IsMatch(path) && path.Split('/').All(SafePart);

    public static bool SafeFolder(string name) => FolderRule().IsMatch(name) && SafePart(name);

    public static IReadOnlyList<PackedFile> Unpack(byte[] package)
    {
        byte[] body;
        try
        {
            using var z = new ZLibStream(new MemoryStream(package), CompressionMode.Decompress);
            using var all = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = z.Read(buf)) > 0)
            {
                all.Write(buf, 0, n);
                if (all.Length > MaxContent + 65536) throw new InvalidDataException("more than the size limit unpacked");
            }
            body = all.ToArray();
        }
        catch (InvalidDataException e) when (e.Message.StartsWith("more than"))
        {
            throw;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            throw new InvalidDataException("not a package");
        }
        if (body.Length < 10 || !body.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("not a package");
        var version = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4));
        if (version != 1) throw new InvalidDataException($"package version {version}");
        var count = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(6));
        if (count > MaxFiles) throw new InvalidDataException($"{count} files (at most {MaxFiles})");
        var at = 10;
        var files = new List<PackedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            if (at + 2 > body.Length) throw new InvalidDataException("cut short");
            int len = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(at));
            if (at + 2 + len + 4 > body.Length) throw new InvalidDataException("cut short");
            string path;
            try
            {
                path = new UTF8Encoding(false, true).GetString(body, at + 2, len);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("cut short");
            }
            at += 2 + len;
            var size = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(at));
            at += 4;
            if (at + (long)size > body.Length) throw new InvalidDataException("cut short");
            if (!SafePath(path)) throw new InvalidDataException($"bad path {path}");
            if (!seen.Add(path)) throw new InvalidDataException($"{path} twice");
            files.Add(new PackedFile(path, body.AsSpan(at, (int)size).ToArray()));
            at += (int)size;
        }
        if (at != body.Length) throw new InvalidDataException("bytes after the last file");
        return files;
    }

    public static byte[] Pack(IEnumerable<PackedFile> files)
    {
        var list = files.ToList();
        using var raw = new MemoryStream();
        Span<byte> head = stackalloc byte[6];
        raw.Write(Magic);
        BinaryPrimitives.WriteUInt16BigEndian(head, 1);
        BinaryPrimitives.WriteUInt32BigEndian(head[2..], (uint)list.Count);
        raw.Write(head);
        Span<byte> n = stackalloc byte[4];
        foreach (var f in list)
        {
            var name = Encoding.UTF8.GetBytes(f.Path);
            BinaryPrimitives.WriteUInt16BigEndian(n, (ushort)name.Length);
            raw.Write(n[..2]);
            raw.Write(name);
            BinaryPrimitives.WriteUInt32BigEndian(n, (uint)f.Data.Length);
            raw.Write(n);
            raw.Write(f.Data);
        }
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, true)) z.Write(raw.ToArray());
        return packed.ToArray();
    }

    public const long MaxPreview = 4L * 1024 * 1024;
    static readonly HashSet<string> TextKinds = new(StringComparer.OrdinalIgnoreCase) { "json", "xml", "txt", "csv", "md", "lang" };
    static readonly HashSet<string> OtherKinds = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "swf", "mp3", "ogg", "wav", "bnk", "bin" };

    public sealed record Folder(IReadOnlyList<PackedFile> Files, IReadOnlyList<string> LeftOut, long Bytes);

    public static Folder ReadFolder(string dir)
    {
        var files = new List<PackedFile>();
        var left = new List<string>();
        long bytes = 0;
        void Walk(string at, string prefix)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(at).OrderBy(System.IO.Path.GetFileName, StringComparer.Ordinal))
            {
                var name = System.IO.Path.GetFileName(entry);
                if (name.StartsWith('.')) continue;
                var path = prefix + name;
                if (Directory.Exists(entry))
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) left.Add(path + "/");
                    else Walk(entry, path + "/");
                    continue;
                }
                if (!SafePath(path)) { left.Add(path); continue; }
                if (files.Count >= MaxFiles) throw new InvalidDataException($"more than {MaxFiles} files");
                var data = File.ReadAllBytes(entry);
                bytes += data.Length;
                if (bytes > MaxContent) throw new InvalidDataException($"more than {MaxContent >> 20} MB of files");
                files.Add(new PackedFile(path, data));
            }
        }
        Walk(dir, "");
        return new Folder(files, left, bytes);
    }

    public static string? KindProblem(string path, byte[] data)
    {
        var last = path[(path.LastIndexOf('/') + 1)..];
        var ext = last.Contains('.') ? last[(last.LastIndexOf('.') + 1)..].ToLowerInvariant() : "";
        if (!TextKinds.Contains(ext) && !OtherKinds.Contains(ext)) return $"{path}: .{(ext.Length > 0 ? ext : "(none)")} files aren't mod files";
        if (TextKinds.Contains(ext))
        {
            if (Utf8Text(data) is not { } text) return $"{path}: not UTF-8 text";
            return text.Contains('\0') ? $"{path}: not text" : null;
        }
        var s = data.AsSpan();
        return ext switch
        {
            "png" when !s.StartsWith(PngMagic) => $"{path}: not a PNG",
            "jpg" or "jpeg" when !s.StartsWith(JpegMagic) => $"{path}: not a JPEG",
            "swf" when s.StartsWith("ZWS"u8) => $"{path}: LZMA-compressed (ZWS), which the game can't read: save it with zlib compression (CWS) or none (FWS)",
            "swf" when s.Length < 8 || !(s.StartsWith("FWS"u8) || s.StartsWith("CWS"u8)) => $"{path}: not a SWF",
            "mp3" when !(s.StartsWith("ID3"u8) || (s.Length > 1 && s[0] == 0xFF && (s[1] & 0xE0) == 0xE0)) => $"{path}: not an MP3",
            "ogg" when !s.StartsWith("OggS"u8) => $"{path}: not an Ogg file",
            "wav" when !(s.StartsWith("RIFF"u8) && s.Length >= 12 && s[8..12].SequenceEqual("WAVE"u8)) => $"{path}: not a WAV",
            "bnk" when !s.StartsWith("BKHD"u8) => $"{path}: not a sound bank",
            _ => null,
        };
    }

    static ReadOnlySpan<byte> PngMagic => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    static ReadOnlySpan<byte> JpegMagic => [0xFF, 0xD8, 0xFF];
    static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    public static bool IsPicture(byte[] data) => data.AsSpan().StartsWith(PngMagic) || data.AsSpan().StartsWith(JpegMagic);

    public static string? Utf8Text(byte[] data)
    {
        try
        {
            var start = data.AsSpan().StartsWith(Bom) ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(data, start, data.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public sealed record Upload(string? Entry, byte[]? Package, string? Problem, IReadOnlyList<string> LeftOut, int Files);

    public static Upload ForUpload(string dir)
    {
        Folder folder;
        try
        {
            folder = ReadFolder(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new Upload(null, null, "its folder can't be read (" + e.Message + ")", [], 0);
        }
        Upload Refuse(string why) => new(null, null, why, folder.LeftOut, folder.Files.Count);
        if (folder.Files.FirstOrDefault(f => f.Path == "mod.json") is not { } manifest) return Refuse("it has no mod.json");
        foreach (var f in folder.Files)
            if (KindProblem(f.Path, f.Data) is { } why) return Refuse(why);
        System.Text.Json.Nodes.JsonObject? m;
        try
        {
            m = System.Text.Json.Nodes.JsonNode.Parse(Utf8Text(manifest.Data)!) as System.Text.Json.Nodes.JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            m = null;
        }
        if (m == null) return Refuse("mod.json is not a JSON object");
        var id = m["id"] is System.Text.Json.Nodes.JsonValue iv && iv.TryGetValue<string>(out var i) ? i : "";
        var version = m["version"] is System.Text.Json.Nodes.JsonValue vv && vv.TryGetValue<string>(out var v) ? v : "";
        if (id.Length == 0 || version.Length == 0) return Refuse("mod.json needs an id and a version");
        var entry = ModLibrary.Identity(id, version, Utf8Text(manifest.Data)!);
        if (!EntryRule().IsMatch(entry)) return Refuse("the server only takes ids and versions without spaces, @ or #");
        var package = Pack(folder.Files);
        if (package.Length > MaxPackage) return Refuse($"it packs to {package.Length >> 20} MB (the server takes at most {MaxPackage >> 20} MB)");
        return new Upload(entry, package, null, folder.LeftOut, folder.Files.Count);
    }

    [GeneratedRegex(@"^[^@#\s]+@[^#\s]+#[0-9a-f]{8}$")]
    private static partial Regex EntryRule();

    [GeneratedRegex(@"^[A-Za-z0-9 _.-]+(/[A-Za-z0-9 _.-]+)*$")]
    private static partial Regex PathRule();

    [GeneratedRegex(@"^[A-Za-z0-9 _.-]+$")]
    private static partial Regex FolderRule();
}
