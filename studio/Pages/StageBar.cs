using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BifrostStudio;

public sealed class StageBar : StackPanel
{
    readonly TextBlock label = new() { FontSize = 13, Foreground = Look.Brush(ReplaysPage.Teal), TextWrapping = TextWrapping.Wrap };
    readonly Border fill = new() { Background = Look.Brush(ReplaysPage.Teal), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    readonly Border track;

    public StageBar(double width = 320)
    {
        Spacing = 6;
        track = new Border { Height = 6, Width = width, CornerRadius = new CornerRadius(3), Background = Look.Brush(Look.Raised), Child = fill, HorizontalAlignment = HorizontalAlignment.Left };
        Children.Add(label);
        Children.Add(track);
    }

    public string Text => label.Text ?? "";

    public double Fraction { get; private set; }

    public void Set(string text, double fraction)
    {
        label.Text = text;
        Fraction = Math.Clamp(fraction, 0, 1);
        fill.Width = track.Width * Fraction;
    }

    public static double Starting(TimeSpan elapsed, string kind = "view") => Math.Min(0.9, elapsed.TotalMilliseconds / ReplayBoot.Expected(kind) * 0.9);
}

public static class ReplayBoot
{
    static readonly Dictionary<string, double> Defaults = new() { ["view"] = 20000, ["stats"] = 11000 };
    static JsonObject? known;

    public static string File => BifrostFolders.StudioFile("replay-boot.json");

    public static double Expected(string kind)
    {
        known ??= JsonFiles.Read(File) as JsonObject ?? new JsonObject();
        try { if (known[kind]?.GetValue<double>() is { } ms and > 0) return ms; }
        catch (Exception x) when (x is InvalidOperationException or FormatException) { }
        return Defaults.GetValueOrDefault(kind, 20000);
    }

    public static void Remember(string kind, TimeSpan took)
    {
        var ms = Math.Clamp(took.TotalMilliseconds, 3000, 180000);
        var next = Math.Round(Expected(kind) * 0.5 + ms * 0.5);
        known![kind] = next;
        try { JsonFiles.Write(File, known.DeepClone()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public static class QrPicture
{
    public static Control From(bool[,] code, double size = 220)
    {
        const int quiet = 4;
        var rows = code.GetLength(0);
        var cols = code.GetLength(1);
        var w = cols + quiet * 2;
        var h = rows + quiet * 2;
        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using (var fb = bitmap.Lock())
        {
            var line = new byte[fb.RowBytes];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var dark = y >= quiet && y < quiet + rows && x >= quiet && x < quiet + cols && code[y - quiet, x - quiet];
                    var v = dark ? (byte)0 : (byte)255;
                    line[x * 4] = v;
                    line[x * 4 + 1] = v;
                    line[x * 4 + 2] = v;
                    line[x * 4 + 3] = 255;
                }
                System.Runtime.InteropServices.Marshal.Copy(line, 0, fb.Address + y * fb.RowBytes, fb.RowBytes);
            }
        }
        var image = new Image { Source = bitmap, Width = size, Height = size * h / w, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        return image;
    }
}
