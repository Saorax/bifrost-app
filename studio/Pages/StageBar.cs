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
