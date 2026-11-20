using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace BifrostStudio;

public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<double> Values);

public sealed class LineChart : Control
{
    public IReadOnlyList<string> Labels { get; }
    public IReadOnlyList<ChartSeries> Series { get; }
    readonly bool bars;
    double? hover;

    public LineChart(IReadOnlyList<string> labels, IReadOnlyList<ChartSeries> series, bool bars = false, double height = 160)
    {
        Labels = labels;
        Series = series;
        this.bars = bars;
        Height = height;
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

    const double Left = 34, Bottom = 20, Top = 6, Right = 8;

    public double Max => Math.Max(1, Series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max());

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width - Left - Right;
        var h = Bounds.Height - Top - Bottom;
        var n = Labels.Count;
        if (w <= 10 || h <= 10 || n == 0) return;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var step = Nice(Max / 3);
        var top = Math.Ceiling(Max / step) * step;
        var grid = new Pen(Look.Brush(Look.Line), 1);
        var ink = Look.Brush(Look.Dim);
        for (var v = 0.0; v <= top + 0.001; v += step)
        {
            var y = Top + h - v / top * h;
            context.DrawLine(grid, new Point(Left, y), new Point(Left + w, y));
            Text(context, v.ToString("0"), new Point(0, y - 7), ink);
        }
        double X(int i) => Left + (i + 0.5) / n * w;
        double Y(double v) => Top + h - v / top * h;
        Text(context, Labels[0], new Point(Left, Top + h + 4), ink);
        if (n > 1) Text(context, Labels[^1], new Point(Math.Max(Left + 60, Left + w - 70), Top + h + 4), ink);
        foreach (var s in Series)
        {
            var brush = new SolidColorBrush(s.Color);
            if (bars)
            {
                var bw = Math.Max(1, w / n * 0.7);
                for (var i = 0; i < s.Values.Count && i < n; i++)
                    context.DrawRectangle(brush, null, new Rect(X(i) - bw / 2, Y(s.Values[i]), bw, Top + h - Y(s.Values[i])), 2, 2);
                continue;
            }
            if (s.Values.Count < 2) continue;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(X(0), Y(s.Values[0])), false);
                for (var i = 1; i < s.Values.Count && i < n; i++) g.LineTo(new Point(X(i), Y(s.Values[i])));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
        if (hover is not { } hx || hx < Left || hx > Left + w) return;
        var at = Math.Clamp((int)((hx - Left) / w * n), 0, n - 1);
        context.DrawLine(new Pen(Look.Brush(Look.Dim), 1), new Point(X(at), Top), new Point(X(at), Top + h));
        var lines = new List<(string, Color?)> { (Labels[at], null) };
        foreach (var s in Series)
            if (at < s.Values.Count) lines.Add(($"{s.Name}  {s.Values[at]:0}", s.Color));
        const double boxW = 150;
        var boxH = 8 + lines.Count * 16;
        var bx = X(at) + 8 + boxW > Left + w ? X(at) - 8 - boxW : X(at) + 8;
        context.DrawRectangle(Look.Brush(Look.Raised), new Pen(Look.Brush(Look.Line), 1), new Rect(bx, Top, boxW, boxH), 6, 6);
        for (var i = 0; i < lines.Count; i++)
        {
            var (text, color) = lines[i];
            if (color is { } c) context.DrawRectangle(new SolidColorBrush(c), null, new Rect(bx + 8, Top + 9 + i * 16, 8, 8), 2, 2);
            Text(context, text, new Point(bx + (color == null ? 8 : 22), Top + 4 + i * 16), Look.Brush(Look.Text));
        }
    }

    public static double Nice(double raw)
    {
        if (raw <= 1) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / mag;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * mag;
    }

    static void Text(DrawingContext context, string text, Point at, IBrush brush) =>
        context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 10.5, brush), at);
}
