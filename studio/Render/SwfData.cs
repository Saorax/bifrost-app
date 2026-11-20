// Parts ported from WallyAnmRenderer (https://github.com/eyalzus12/WallyAnmRenderer), MIT License,
// Copyright (c) 2025 AllHailCheese.

using SwfLib;
using SwfLib.Tags;
using SwfLib.Tags.ActionsTags;
using SwfLib.Tags.BitmapTags;
using SwfLib.Tags.ControlTags;
using SwfLib.Tags.ShapeTags;
using SwfLib.Tags.TextTags;

namespace BifrostStudio.Render;

public sealed class SwfData
{
    public Dictionary<string, ushort> Symbols { get; } = new(StringComparer.Ordinal);
    public Dictionary<ushort, string> SymbolNames { get; } = [];
    public Dictionary<ushort, SwfTagBase> Tags { get; } = [];
    public int TagCount { get; private set; }

    public static SwfData Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length > 17 && bytes[0] == 'Z' && bytes[1] == 'W' && bytes[2] == 'S') bytes = Unpack(bytes);
        using var input = new MemoryStream(bytes, false);
        var swf = SwfFile.ReadFrom(input);
        var data = new SwfData { TagCount = swf.Tags.Count };
        foreach (var tag in swf.Tags)
        {
            switch (tag)
            {
                case SymbolClassTag symbols:
                    foreach (var r in symbols.References)
                    {
                        data.Symbols[r.SymbolName] = r.SymbolID;
                        data.SymbolNames[r.SymbolID] = r.SymbolName;
                    }
                    break;
                case DefineSpriteTag sprite: data.Tags[sprite.SpriteID] = sprite; break;
                case ShapeBaseTag shape: data.Tags[shape.ShapeID] = shape; break;
                case DefineTextBaseTag text: data.Tags[text.CharacterID] = text; break;
                case DefineEditTextTag edit: data.Tags[edit.CharacterID] = edit; break;
                case BitmapBaseTag bitmap: data.Tags[bitmap.CharacterID] = bitmap; break;
            }
        }
        return data;
    }

    public static byte[] Unpack(byte[] zws)
    {
        var length = BitConverter.ToUInt32(zws, 4);
        var packed = BitConverter.ToUInt32(zws, 8);
        if (length < 8 || length > 512 * 1024 * 1024 || packed > zws.Length - 17) throw new InvalidDataException("bad LZMA SWF header");
        var output = new byte[length];
        output[0] = (byte)'F';
        output[1] = (byte)'W';
        output[2] = (byte)'S';
        output[3] = zws[3];
        BitConverter.GetBytes(length).CopyTo(output, 4);
        using var source = new MemoryStream(zws, 17, (int)packed, false);
        using var lzma = SharpCompress.Compressors.LZMA.LzmaStream.Create(zws[12..17], source, packed, length - 8, false);
        var read = 8;
        while (read < output.Length)
        {
            var n = lzma.Read(output, read, output.Length - read);
            if (n <= 0) break;
            read += n;
        }
        if (read != output.Length) throw new InvalidDataException("LZMA SWF ends early");
        return output;
    }
}
