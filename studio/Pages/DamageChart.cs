using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace BifrostStudio;

public sealed class DamageChart : Control
{
    public static readonly Color[] Series = ReplaysPage.PlayerColours;

    readonly MatchStats stats;
    double? hover;

    public DamageChart(MatchStats stats)
    {
        this.stats = stats;
        Height = 170;
        ClipToBounds = true;
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

    const double Left = 34, Bottom = 20, Top = 6, Right = 6;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width - Left - Right;
        var h = Bounds.Height - Top - Bottom;
        if (w <= 10 || h <= 10) return;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var points = stats.Players.Max(p => p.Damage.Count);
        var top = Math.Max(100, Math.Ceiling(stats.Players.SelectMany(p => p.Damage).DefaultIfEmpty(0).Max() / 50) * 50);
        var grid = new Pen(Look.Brush(Look.Line), 1);
        var ink = Look.Brush(Look.Dim);
        for (var v = 0.0; v <= top; v += top > 200 ? 100 : 50)
        {
            var y = Top + h - v / top * h;
            context.DrawLine(grid, new Point(Left, y), new Point(Left + w, y));
            Text(context, v + "%", new Point(0, y - 7), ink);
        }
        var total = Math.Max(1, (points - 1) * stats.BucketMs);
        for (var m = 0; m <= total; m += 60000)
        {
            var x = Left + m / (double)total * w;
            Text(context, ReplaysPage.Clock(m), new Point(Math.Min(x - 8, Left + w - 26), Top + h + 4), ink);
        }
        for (var i = 0; i < stats.Players.Count && i < Series.Length; i++)
        {
            var d = stats.Players[i].Damage;
            if (d.Count < 2) continue;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                for (var k = 0; k < d.Count; k++)
                {
                    var p = new Point(Left + k * stats.BucketMs / (double)total * w, Top + h - Math.Min(d[k], top) / top * h);
                    if (k == 0) g.BeginFigure(p, false);
                    else g.LineTo(p);
                }
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Series[i]), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
        if (hover is not { } hx || hx < Left || hx > Left + w || points < 2) return;
        var at = (int)Math.Round((hx - Left) / w * (points - 1));
        var cx = Left + at / (double)(points - 1) * w;
        context.DrawLine(new Pen(Look.Brush(Look.Dim), 1), new Point(cx, Top), new Point(cx, Top + h));
        var lines = new List<(string, Color?)> { (ReplaysPage.Clock(at * stats.BucketMs), null) };
        for (var i = 0; i < stats.Players.Count && i < Series.Length; i++)
        {
            var d = stats.Players[i].Damage;
            if (at < d.Count) lines.Add(($"{stats.Players[i].Name}  {d[at]:0}%", Series[i]));
        }
        var boxW = 150.0;
        var boxH = 8 + lines.Count * 16;
        var bx = cx + 8 + boxW > Left + w ? cx - 8 - boxW : cx + 8;
        context.DrawRectangle(Look.Brush(Look.Raised), new Pen(Look.Brush(Look.Line), 1), new Rect(bx, Top, boxW, boxH), 6, 6);
        for (var i = 0; i < lines.Count; i++)
        {
            var (text, color) = lines[i];
            if (color is { } c) context.DrawRectangle(new SolidColorBrush(c), null, new Rect(bx + 8, Top + 9 + i * 16, 8, 8), 2, 2);
            Text(context, text, new Point(bx + (color == null ? 8 : 22), Top + 4 + i * 16), Look.Brush(Look.Text));
        }
    }

    static void Text(DrawingContext context, string text, Point at, IBrush brush) =>
        context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 10.5, brush), at);
}