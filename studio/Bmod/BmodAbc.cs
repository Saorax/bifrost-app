using System.Text;

namespace BifrostStudio.Bmod;

public sealed class BmodAbc
{
    sealed class Undefined
    {
        public static readonly Undefined Value = new();
    }

    readonly byte[] abc;
    int p;
    readonly List<int> ints = [0];
    readonly List<string?> strings = [""];
    readonly List<int> namespaces = [0];
    readonly List<(int Ns, int Name)> multinames = [(0, 0)];
    readonly Dictionary<string, int> frameScripts = [];
    readonly Dictionary<int, int> bodies = [];
    readonly List<string?> classNames = [];

    BmodAbc(byte[] abc, int start)
    {
        this.abc = abc;
        p = start;
    }

    int Byte()
    {
        var b = p >= 0 && p < abc.Length ? abc[p] : 0;
        p++;
        return b;
    }

    int U30()
    {
        var result = 0;
        var shift = 0;
        while (true)
        {
            var b = Byte();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0 || shift >= 28) break;
            shift += 7;
        }
        return result;
    }

    string? StringAt(int index) => index >= 0 && index < strings.Count ? strings[index] : null;

    string? PlainName(int index) => index > 0 && index < multinames.Count ? StringAt(multinames[index].Name) : null;

    string? Qualified(int index)
    {
        if (index <= 0 || index >= multinames.Count) return null;
        var m = multinames[index];
        var name = StringAt(m.Name);
        var ns = m.Ns > 0 && m.Ns < namespaces.Count ? StringAt(namespaces[m.Ns]) : "";
        return string.IsNullOrEmpty(ns) ? name : ns + "." + (name ?? "undefined");
    }

    void Traits(string? owner)
    {
        var count = U30();
        for (var i = 0; i < count; i++)
        {
            var name = U30();
            var kind = Byte();
            var low = kind & 0x0F;
            if (low is 0 or 6)
            {
                U30();
                U30();
                if (U30() != 0) p++;
            }
            else if (low is 1 or 2 or 3)
            {
                U30();
                var method = U30();
                if (owner != null && low == 1 && PlainName(name) == "frame1") frameScripts[owner] = method;
            }
            else if (low is 4 or 5)
            {
                U30();
                U30();
            }
            else throw new BmodException("unknown ABC trait kind " + kind);
            if ((kind & 0x40) != 0)
            {
                var meta = U30();
                for (var j = 0; j < meta; j++) U30();
            }
        }
    }

    void Parse()
    {
        p += 4;
        var count = U30();
        for (var i = 1; i < count; i++) ints.Add(U30());
        count = U30();
        for (var i = 1; i < count; i++) U30();
        count = U30();
        for (var i = 1; i < count; i++) p += 8;
        count = U30();
        for (var i = 1; i < count; i++)
        {
            var len = U30();
            if (p < 0 || len < 0 || p + len > abc.Length) throw new BmodException("ABC string past the end");
            strings.Add(Encoding.UTF8.GetString(abc, p, len));
            p += len;
        }
        count = U30();
        for (var i = 1; i < count; i++)
        {
            p++;
            namespaces.Add(U30());
        }
        count = U30();
        for (var i = 1; i < count; i++)
        {
            var sets = U30();
            for (var j = 0; j < sets; j++) U30();
        }
        count = U30();
        for (var i = 1; i < count; i++)
        {
            var kind = Byte();
            if (kind is 0x07 or 0x0D)
            {
                var ns = U30();
                multinames.Add((ns, U30()));
            }
            else if (kind is 0x0F or 0x10) multinames.Add((0, U30()));
            else if (kind is 0x11 or 0x12) multinames.Add((0, 0));
            else if (kind is 0x09 or 0x0E)
            {
                var mn = U30();
                U30();
                multinames.Add((0, mn));
            }
            else if (kind is 0x1B or 0x1C)
            {
                U30();
                multinames.Add((0, 0));
            }
            else if (kind == 0x1D)
            {
                var b = U30();
                var parameters = U30();
                for (var j = 0; j < parameters; j++) U30();
                multinames.Add((0, b >= 0 && b < multinames.Count ? multinames[b].Name : 0));
            }
            else throw new BmodException("unknown ABC multiname kind " + kind);
        }
        count = U30();
        for (var i = 0; i < count; i++)
        {
            var paramCount = U30();
            U30();
            for (var j = 0; j < paramCount; j++) U30();
            U30();
            var flags = Byte();
            if ((flags & 0x08) != 0)
            {
                var options = U30();
                for (var j = 0; j < options; j++)
                {
                    U30();
                    p++;
                }
            }
            if ((flags & 0x80) != 0)
                for (var j = 0; j < paramCount; j++) U30();
        }
        count = U30();
        for (var i = 0; i < count; i++)
        {
            U30();
            var items = U30();
            for (var j = 0; j < items * 2; j++) U30();
        }
        var classCount = U30();
        for (var i = 0; i < classCount; i++)
        {
            var name = Qualified(U30());
            classNames.Add(name);
            U30();
            var iflags = Byte();
            if ((iflags & 0x08) != 0) U30();
            var interfaces = U30();
            for (var j = 0; j < interfaces; j++) U30();
            U30();
            Traits(name);
        }
        for (var i = 0; i < classCount; i++)
        {
            U30();
            Traits(null);
        }
        count = U30();
        for (var i = 0; i < count; i++)
        {
            U30();
            Traits(null);
        }
        count = U30();
        for (var i = 0; i < count; i++)
        {
            var method = U30();
            for (var j = 0; j < 4; j++) U30();
            var length = U30();
            bodies[method] = p;
            p += length;
            var exceptions = U30();
            for (var j = 0; j < exceptions * 5; j++) U30();
            Traits(null);
        }
    }

    static object? Pop(List<object?> stack)
    {
        if (stack.Count == 0) return Undefined.Value;
        var v = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return v;
    }

    List<object?>? Palette(int start)
    {
        p = start;
        var length = U30();
        var stop = p + length;
        var stack = new List<object?>();
        while (p < stop)
        {
            var op = Byte();
            if (op is 0xD0 or 0xD1 or 0xD2 or 0xD3) stack.Add(null);
            else if (op is 0x30 or 0x29) Pop(stack);
            else if (op == 0x24)
            {
                var b = Byte();
                stack.Add(b >= 0x80 ? b - 0x100 : b);
            }
            else if (op == 0x25)
            {
                var v = U30();
                stack.Add(v is >= 0x8000 and < 0x10000 ? v - 0x10000 : v);
            }
            else if (op == 0x2D)
            {
                var index = U30();
                stack.Add(index >= 0 && index < ints.Count ? ints[index] : Undefined.Value);
            }
            else if (op is 0x2E or 0x2F)
            {
                U30();
                stack.Add(0);
            }
            else if (op == 0x56)
            {
                var count = U30();
                if (count > stack.Count) return null;
                var items = stack.GetRange(stack.Count - count, count);
                stack.RemoveRange(stack.Count - count, count);
                stack.Add(items);
            }
            else if (op is 0x68 or 0x61)
            {
                var name = PlainName(U30());
                var value = Pop(stack);
                Pop(stack);
                if (name == "a" && value is List<object?> list) return list;
            }
            else if (op is 0x5D or 0x5E or 0x60)
            {
                U30();
                stack.Add(null);
            }
            else if (op == 0x66)
            {
                U30();
                Pop(stack);
                stack.Add(null);
            }
            else if (op is 0x46 or 0x4F)
            {
                U30();
                var args = U30();
                if (args + 1 > stack.Count) return null;
                stack.RemoveRange(stack.Count - args - 1, args + 1);
                if (op == 0x46) stack.Add(null);
            }
            else if (op is 0x20 or 0x21 or 0x26 or 0x27 or 0x28) stack.Add(null);
            else if (op == 0x2C)
            {
                var index = U30();
                stack.Add(index >= 0 && index < strings.Count ? strings[index] : Undefined.Value);
            }
            else if (op is 0xF0 or 0xF1 or 0x80) U30();
            else if (op == 0xEF)
            {
                p++;
                U30();
                p++;
                U30();
            }
            else if (op == 0x2A) stack.Add(stack.Count > 0 ? stack[^1] : Undefined.Value);
            else if (op is not (0x82 or 0x85 or 0x73 or 0x74 or 0x75 or 0x88 or 0x89)) return null;
        }
        return null;
    }

    static List<int> Ints(List<object?> list) => list.Select(v => v is int i ? i : 0).ToList();

    public static void Classes(IEnumerable<SwfTag> tags, Dictionary<string, List<int>?> into)
    {
        foreach (var t in tags)
        {
            var src = t.Src;
            var start = -1;
            var limit = t.Pos + t.Len;
            if (t.Code == BmodSwf.DoAbc)
            {
                start = t.Pos + 4;
                while (start < limit && src[start] != 0) start++;
                start++;
            }
            else if (t.Code == BmodSwf.DoAbcOld) start = t.Pos;
            if (start < 0) continue;
            var r = new BmodAbc(src, start);
            try { r.Parse(); }
            catch (BmodException) { continue; }
            foreach (var name in r.classNames)
            {
                if (name == null) continue;
                List<int>? list = null;
                if (r.frameScripts.TryGetValue(name, out var method) && r.bodies.TryGetValue(method, out var body))
                {
                    try { list = r.Palette(body) is { } found ? Ints(found) : null; }
                    catch (BmodException) { list = null; }
                }
                if (!into.ContainsKey(name) || list != null) into[name] = list;
            }
        }
    }

    public static Dictionary<string, List<int>?> Classes(IEnumerable<SwfTag> tags)
    {
        var into = new Dictionary<string, List<int>?>();
        Classes(tags, into);
        return into;
    }

    sealed class Pools
    {
        public readonly List<string> Strings = [""];
        public readonly List<(int Kind, int Name)> Namespaces = [(0, 0)];
        public readonly List<(int Ns, int Name)> Multinames = [(0, 0)];
        public readonly List<int> Ints = [0];
        readonly Dictionary<string, int> index = [];

        public int Str(string s)
        {
            if (!index.TryGetValue("s" + s, out var i))
            {
                i = Strings.Count;
                index["s" + s] = i;
                Strings.Add(s);
            }
            return i;
        }

        public int Ns(int kind, string name)
        {
            var k = "n" + kind + ":" + name;
            if (!index.TryGetValue(k, out var i))
            {
                i = Namespaces.Count;
                index[k] = i;
                Namespaces.Add((kind, Str(name)));
            }
            return i;
        }

        public int QName(int nsKind, string nsName, string name)
        {
            var ns = Ns(nsKind, nsName);
            var k = "q" + ns + ":" + name;
            if (!index.TryGetValue(k, out var i))
            {
                i = Multinames.Count;
                index[k] = i;
                Multinames.Add((ns, Str(name)));
            }
            return i;
        }

        public int Integer(int v)
        {
            var k = "i" + v;
            if (!index.TryGetValue(k, out var i))
            {
                i = Ints.Count;
                index[k] = i;
                Ints.Add(v);
            }
            return i;
        }
    }

    public static void WriteU30(Stream out_, int v)
    {
        var u = (uint)v;
        while (true)
        {
            var b = (byte)(u & 0x7F);
            u >>= 7;
            if (u != 0) out_.WriteByte((byte)(b | 0x80));
            else
            {
                out_.WriteByte(b);
                return;
            }
        }
    }

    static void Ops(Stream out_, params byte[] list) => out_.Write(list);

    static readonly (string Ns, string Name)[] Bases =
    [
        ("", "Object"), ("flash.events", "EventDispatcher"), ("flash.display", "DisplayObject"),
        ("flash.display", "InteractiveObject"), ("flash.display", "DisplayObjectContainer"), ("flash.display", "Sprite"),
        ("flash.display", "MovieClip"),
    ];

    public static byte[] Block(IReadOnlyList<string> classes, IReadOnlyDictionary<string, List<int>?> colours)
    {
        var w = new Pools();
        const int pkg = 0x16, internalNs = 0x17, protectedNs = 0x18;
        var methods = 0;
        var bodyList = new List<(int Method, int MaxStack, int Locals, int InitScope, int MaxScope, byte[] Code)>();
        var instances = new MemoryStream();
        var classInfos = new MemoryStream();
        var scripts = new MemoryStream();
        var movieClip = w.QName(pkg, "flash.display", "MovieClip");
        var slotA = w.QName(pkg, "", "a");
        var arrayType = w.QName(pkg, "", "Array");
        var frame1 = w.QName(internalNs, "", "frame1");
        var addFrameScript = w.QName(pkg, "", "addFrameScript");
        for (var ci = 0; ci < classes.Count; ci++)
        {
            var name = classes[ci];
            var list = colours.TryGetValue(name, out var l) ? l : null;
            var self = w.QName(pkg, "", name);
            var iinit = methods++;
            var code = new MemoryStream();
            Ops(code, 0xD0, 0x30, 0xD0, 0x49);
            WriteU30(code, 0);
            if (list != null)
            {
                code.WriteByte(0x5D);
                WriteU30(code, addFrameScript);
                Ops(code, 0x24, 0, 0xD0, 0x66);
                WriteU30(code, frame1);
                code.WriteByte(0x4F);
                WriteU30(code, addFrameScript);
                WriteU30(code, 2);
            }
            code.WriteByte(0x47);
            bodyList.Add((iinit, 3, 1, 10, 11, code.ToArray()));
            var frameMethod = -1;
            if (list != null)
            {
                frameMethod = methods++;
                code = new MemoryStream();
                Ops(code, 0xD0, 0x30, 0xD0);
                foreach (var colour in list)
                {
                    code.WriteByte(0x2D);
                    WriteU30(code, w.Integer(colour));
                }
                code.WriteByte(0x56);
                WriteU30(code, list.Count);
                code.WriteByte(0x68);
                WriteU30(code, slotA);
                code.WriteByte(0x47);
                bodyList.Add((frameMethod, list.Count + 1 > 2 ? list.Count + 1 : 2, 1, 10, 11, code.ToArray()));
            }
            var cinit = methods++;
            bodyList.Add((cinit, 1, 1, 9, 10, [0xD0, 0x30, 0x47]));
            var sinit = methods++;
            code = new MemoryStream();
            Ops(code, 0xD0, 0x30, 0x65, 0);
            foreach (var (ns, baseName) in Bases)
            {
                code.WriteByte(0x60);
                WriteU30(code, w.QName(pkg, ns, baseName));
                code.WriteByte(0x30);
            }
            code.WriteByte(0x60);
            WriteU30(code, movieClip);
            code.WriteByte(0x58);
            WriteU30(code, ci);
            for (var k = 0; k < Bases.Length; k++) code.WriteByte(0x1D);
            code.WriteByte(0x68);
            WriteU30(code, self);
            code.WriteByte(0x47);
            bodyList.Add((sinit, 2, 1, 1, 9, code.ToArray()));
            WriteU30(instances, self);
            WriteU30(instances, movieClip);
            instances.WriteByte(0x08);
            WriteU30(instances, w.Ns(protectedNs, name));
            WriteU30(instances, 0);
            WriteU30(instances, iinit);
            WriteU30(instances, list != null ? 2 : 1);
            WriteU30(instances, slotA);
            instances.WriteByte(0);
            WriteU30(instances, 0);
            WriteU30(instances, arrayType);
            WriteU30(instances, 0);
            if (list != null)
            {
                WriteU30(instances, frame1);
                instances.WriteByte(1);
                WriteU30(instances, 0);
                WriteU30(instances, frameMethod);
            }
            WriteU30(classInfos, cinit);
            WriteU30(classInfos, 0);
            WriteU30(scripts, sinit);
            WriteU30(scripts, 1);
            WriteU30(scripts, self);
            scripts.WriteByte(4);
            WriteU30(scripts, 1);
            WriteU30(scripts, ci);
        }
        var out_ = new MemoryStream();
        Ops(out_, 16, 0, 46, 0);
        WriteU30(out_, w.Ints.Count > 1 ? w.Ints.Count : 0);
        for (var i = 1; i < w.Ints.Count; i++) WriteU30(out_, w.Ints[i]);
        WriteU30(out_, 0);
        WriteU30(out_, 0);
        WriteU30(out_, w.Strings.Count);
        for (var i = 1; i < w.Strings.Count; i++)
        {
            var utf = Encoding.UTF8.GetBytes(w.Strings[i]);
            WriteU30(out_, utf.Length);
            out_.Write(utf);
        }
        WriteU30(out_, w.Namespaces.Count);
        for (var i = 1; i < w.Namespaces.Count; i++)
        {
            out_.WriteByte((byte)w.Namespaces[i].Kind);
            WriteU30(out_, w.Namespaces[i].Name);
        }
        WriteU30(out_, 0);
        WriteU30(out_, w.Multinames.Count);
        for (var i = 1; i < w.Multinames.Count; i++)
        {
            out_.WriteByte(0x07);
            WriteU30(out_, w.Multinames[i].Ns);
            WriteU30(out_, w.Multinames[i].Name);
        }
        WriteU30(out_, methods);
        for (var i = 0; i < methods; i++) Ops(out_, 0, 0, 0, 0);
        WriteU30(out_, 0);
        WriteU30(out_, classes.Count);
        instances.WriteTo(out_);
        classInfos.WriteTo(out_);
        WriteU30(out_, classes.Count);
        scripts.WriteTo(out_);
        WriteU30(out_, bodyList.Count);
        foreach (var mb in bodyList)
        {
            WriteU30(out_, mb.Method);
            WriteU30(out_, mb.MaxStack);
            WriteU30(out_, mb.Locals);
            WriteU30(out_, mb.InitScope);
            WriteU30(out_, mb.MaxScope);
            WriteU30(out_, mb.Code.Length);
            out_.Write(mb.Code);
            WriteU30(out_, 0);
            WriteU30(out_, 0);
        }
        return out_.ToArray();
    }

    public static byte[] DoAbcTag(IReadOnlyList<string> classes, IReadOnlyDictionary<string, List<int>?> colours)
    {
        var body = new MemoryStream();
        Ops(body, 1, 0, 0, 0, 0);
        body.Write(Block(classes, colours));
        return body.ToArray();
    }
}
