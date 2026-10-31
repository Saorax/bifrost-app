using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace BifrostStudio;

public static class AppIcon
{
    const double Hue = 215 / 360.0;

    public static WindowIcon? FromGame(string? game)
    {
        if (game == null || !OperatingSystem.IsWindows()) return null;
        var original = Path.Combine(game, "Brawlhalla.exe.bifrost-original");
        var dib = LargestImage(File.Exists(original) ? original : Path.Combine(game, "Brawlhalla.exe"));
        return dib == null ? null : Decode(dib) is { } bitmap ? new WindowIcon(bitmap) : null;
    }

    static Bitmap? Decode(byte[] dib)
    {
        if (dib.Length < 40 || BitConverter.ToInt16(dib, 14) != 32) return null;
        var header = BitConverter.ToInt32(dib, 0);
        var width = BitConverter.ToInt32(dib, 4);
        var height = BitConverter.ToInt32(dib, 8) / 2;
        if (width <= 0 || height <= 0 || header + 4 * width * height > dib.Length) return null;
        var pixels = new byte[4 * width * height];
        for (var y = 0; y < height; y++)
            Array.Copy(dib, header + 4 * width * (height - 1 - y), pixels, 4 * width * y, 4 * width);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var (b, g, r) = (pixels[i], pixels[i + 1], pixels[i + 2]);
            if (r == g && g == b) continue;
            var max = Math.Max(r, Math.Max(g, b)) / 255.0;
            var min = Math.Min(r, Math.Min(g, b)) / 255.0;
            (pixels[i + 2], pixels[i + 1], pixels[i]) = Hsv(Hue, (max - min) / max, max);
        }
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var locked = bitmap.Lock())
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(pixels, 4 * width * y, locked.Address + locked.RowBytes * y, 4 * width);
        }
        return bitmap;
    }

    static (byte, byte, byte) Hsv(double h, double s, double v)
    {
        var i = (int)(h * 6) % 6;
        var f = h * 6 - Math.Floor(h * 6);
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    static byte[]? LargestImage(string exe)
    {
        if (!File.Exists(exe)) return null;
        var module = LoadLibraryExW(exe, IntPtr.Zero, 0x2 | 0x20);
        if (module == IntPtr.Zero) return null;
        try
        {
            IntPtr first = IntPtr.Zero;
            EnumResourceNamesW(module, 14, (_, _, name, _) => { if (name >> 16 == 0) first = name; return first == IntPtr.Zero; }, IntPtr.Zero);
            if (first == IntPtr.Zero || Resource(module, first, 14) is not { Length: >= 6 } group) return null;
            int count = BitConverter.ToUInt16(group, 4), best = -1, bestSize = 0;
            for (var i = 0; i < count && 6 + 14 * (i + 1) <= group.Length; i++)
            {
                var size = group[6 + 14 * i] == 0 ? 256 : group[6 + 14 * i];
                if (size > bestSize) (best, bestSize) = (BitConverter.ToUInt16(group, 6 + 14 * i + 12), size);
            }
            return best < 0 ? null : Resource(module, best, 3);
        }
        finally { FreeLibrary(module); }
    }

    static byte[]? Resource(IntPtr module, IntPtr name, int type)
    {
        var found = FindResourceW(module, name, type);
        if (found == IntPtr.Zero) return null;
        var data = LockResource(LoadResource(module, found));
        var size = SizeofResource(module, found);
        if (data == IntPtr.Zero || size == 0) return null;
        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, (int)size);
        return bytes;
    }

    delegate bool EnumNames(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll")] static extern bool EnumResourceNamesW(IntPtr module, nint type, EnumNames callback, IntPtr param);
    [DllImport("kernel32.dll")] static extern IntPtr FindResourceW(IntPtr module, IntPtr name, nint type);
    [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr info);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr data);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr info);
}
