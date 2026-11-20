using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BifrostStudio.Net;

namespace BifrostStudio;

public sealed record RatingPoint(long Result, DateTime When, int Before, int After, char Outcome, string Opponent);

public sealed class RatingChart : Control
{
    static readonly Color Line = Color.Parse("#3987E5");
    static readonly Color Win = Color.Parse("#3BE3A0");
    static readonly Color Loss = Color.Parse("#FF6B81");

    public IReadOnlyList<RatingPoint> Points { get; }
    public IReadOnlyList<(string Name, int Start)> Tiers { get; }
    public int Total { get; }
    double? hover;

    public RatingChart(IReadOnlyList<RatingPoint> points, IReadOnlyList<(string, int)> tiers, int total)
    {
        Points = points;
        Tiers = tiers;
        Total = total;
        Height = 190;
        ClipToBounds = true;
    }

    public static RatingChart? From(JsonNode? history)
    {
        if (history?["points"] is not JsonArray raw) return null;
        var points = new List<RatingPoint>();
        foreach (var p in raw.OfType<JsonArray>())
        {
            if (p.Count < 5 || OnlineService.Num(p[2]) is not { } before || OnlineService.Num(p[3]) is not { } after) continue;
            var outcome = OnlineService.Text(p[4]) is { Length: > 0 } o ? o[0] : 'D';
            points.Add(new RatingPoint((long)(OnlineService.Num(p[0]) ?? 0), OnlineService.When(p[1]) ?? DateTime.MinValue, (int)before, (int)after,
                outcome, p.Count > 5 ? OnlineService.Text(p[5]) ?? "" : ""));
        }
        if (points.Count == 0) return null;
        var tiers = (history["tiers"] as JsonArray ?? []).OfType<JsonArray>()
            .Where(t => t.Count >= 2 && OnlineService.Text(t[0]) != null && OnlineService.Num(t[1]) != null)
            .Select(t => (OnlineService.Text(t[0])!, (int)OnlineService.Num(t[1])!)).ToList();
        return new RatingChart(points, tiers, (int)(OnlineService.Num(history["total"]) ?? points.Count));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        hover = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hover = null;
        InvalidateVisual();
    }

    const double Left = 70, Bottom = 20, Top = 8, Right = 10;

    public (double Low, double High) Range()
    {
        var values = Points.SelectMany(p => new[] { p.Before, p.After }).ToList();
        double low = values.Min(), high = values.Max();
        var pad = Math.Max(25, (high - low) * 0.12);
        return (Math.Max(0, Math.Floor((low - pad) / 25) * 25), Math.Ceiling((high + pad) / 25) * 25);
    }

    public int At(double x, double width)
    {
        var w = width - Left - Right;
        return Math.Clamp((int)Math.Round((x - Left) / Math.Max(1, w) * Points.Count - 0.5), 0, Points.Count - 1);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width - Left - Right;
        var h = Bounds.Height - Top - Bottom;
        if (w <= 10 || h <= 10 || Points.Count == 0) return;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var (low, high) = Range();
        double Y(double v) => Top + h - (v - low) / (high - low) * h;
        double X(int i) => Left + (i + 0.5) / Points.Count * w;
        var grid = new Pen(Look.Brush(Look.Line), 1);
        var ink = Look.Brush(Look.Dim);
        var labelled = new List<double>();
        foreach (var (name, start) in Tiers)
        {
            if (start <= low || start >= high) continue;
            context.DrawLine(new Pen(Look.Brush(Look.Line), 1, new DashStyle([4, 4], 0)), new Point(Left, Y(start)), new Point(Left + w, Y(start)));
            Text(context, $"{name} {start}", new Point(0, Y(start) - 7), ink);
            labelled.Add(Y(start));
        }
        context.DrawLine(grid, new Point(Left, Y(low)), new Point(Left + w, Y(low)));
        if (labelled.All(y => Math.Abs(y - Y(low)) > 14)) Text(context, ((int)low).ToString(), new Point(0, Y(low) - 7), ink);
        if (labelled.All(y => Math.Abs(y - Y(high)) > 14)) Text(context, ((int)high).ToString(), new Point(0, Y(high) - 2), ink);
        var first = Total - Points.Count + 1;
        Text(context, "match " + first, new Point(Left, Top + h + 4), ink);
        Text(context, "match " + Total, new Point(Left + w - 52, Top + h + 4), ink);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(Left, Y(Points[0].Before)), false);
            for (var i = 0; i < Points.Count; i++) g.LineTo(new Point(X(i), Y(Points[i].After)));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Line), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        if (Points.Count <= 120)
            for (var i = 0; i < Points.Count; i++)
            {
                var c = Points[i].Outcome == 'W' ? Win : Points[i].Outcome == 'L' ? Loss : Look.Dim;
                context.DrawEllipse(new SolidColorBrush(c), null, new Point(X(i), Y(Points[i].After)), 2.5, 2.5);
            }
        var peak = Points.Select((p, i) => (p.After, i)).MaxBy(t => t.After);
        context.DrawEllipse(null, new Pen(Look.Brush(Look.Text), 1.5), new Point(X(peak.i), Y(peak.After)), 5, 5);
        if (hover is not { } hx || hx < Left || hx > Left + w) return;
        var at = At(hx, Bounds.Width);
        var cx = X(at);
        var pt = Points[at];
        context.DrawLine(new Pen(Look.Brush(Look.Dim), 1), new Point(cx, Top), new Point(cx, Top + h));
        var change = pt.After - pt.Before;
        var lines = new List<string>
        {
            $"Match {first + at}" + (pt.When > DateTime.MinValue ? " · " + pt.When.ToLocalTime().ToString("d MMM HH:mm") : ""),
            (pt.Outcome == 'W' ? "Win" : pt.Outcome == 'L' ? "Loss" : "Draw") + (pt.Opponent.Length > 0 ? " vs " + pt.Opponent : ""),
            $"{pt.Before} → {pt.After} ({(change >= 0 ? "+" : "")}{change})",
        };
        const double boxW = 190;
        var boxH = 8 + lines.Count * 16;
        var bx = cx + 8 + boxW > Left + w ? cx - 8 - boxW : cx + 8;
        context.DrawRectangle(Look.Brush(Look.Raised), new Pen(Look.Brush(Look.Line), 1), new Rect(bx, Top, boxW, boxH), 6, 6);
        for (var i = 0; i < lines.Count; i++) Text(context, lines[i], new Point(bx + 8, Top + 4 + i * 16), Look.Brush(Look.Text));
    }

    static void Text(DrawingContext context, string text, Point at, IBrush brush) =>
        context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 10.5, brush), at);
}
