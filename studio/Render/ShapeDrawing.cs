using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SwfLib.Data;
using SwfLib.Gradients;
using SwfLib.Shapes.FillStyles;
using SwfLib.Shapes.LineStyles;
using SwfLib.Shapes.Records;
using SwfLib.Tags.ShapeTags;

namespace BifrostStudio.Render;

public sealed record ShapePart(Geometry Geometry, IBrush? Fill, IPen? Stroke);

public sealed class ShapeCache
{
    readonly ConcurrentDictionary<(string, ushort, string), IReadOnlyList<ShapePart>> parts = new();

    public int Count => parts.Count;

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyDictionary<uint, uint>, string> swapKeys = new();

    public IReadOnlyList<ShapePart> Get(PosedShape shape)
    {
        var swapKey = shape.ColorSwaps.Count == 0 ? "" : swapKeys.GetValue(shape.ColorSwaps,
            s => string.Join(",", s.OrderBy(p => p.Key).Select(p => p.Key.ToString("x6") + p.Value.ToString("x6"))));
        return parts.GetOrAdd((shape.SwfPath, shape.Shape.ShapeID, swapKey), _ => ShapeDrawing.Convert(shape.Shape, shape.ColorSwaps));
    }
}

public static class ShapeDrawing
{
    const double Twips = 20;
    const double GradientHalf = 16384 / Twips;

    readonly record struct Pt(int X, int Y);

    sealed record Edge(Pt From, Pt? Control, Pt To)
    {
        public Edge Reversed => new(To, Control, From);
    }

    public static Matrix ToMatrix(Transform2D t) => new(t.ScaleX, t.SkewY, t.SkewX, t.ScaleY, t.TranslateX, t.TranslateY);

    public static IReadOnlyList<ShapePart> Convert(ShapeBaseTag shape, IReadOnlyDictionary<uint, uint> swaps)
    {
        var (fillStyles, lineStyles, records) = Styles(shape);
        var output = new List<ShapePart>();
        var fills = new SortedDictionary<int, List<Edge>>();
        var lines = new SortedDictionary<int, List<Edge>>();
        int fill0 = 0, fill1 = 0, line = 0;
        var at = new Pt(0, 0);

        void Flush()
        {
            foreach (var (index, edges) in fills)
                if (index - 1 < fillStyles.Count && Fill(fillStyles[index - 1], swaps) is { } brush)
                    output.Add(new ShapePart(Geometry(edges, true), brush, null));
            foreach (var (index, edges) in lines)
                if (index - 1 < lineStyles.Count && Pen(lineStyles[index - 1], swaps) is { } pen)
                    output.Add(new ShapePart(Geometry(edges, false), null, pen));
            fills.Clear();
            lines.Clear();
        }

        void Add(Edge edge)
        {
            if (fill0 > 0) List(fills, fill0).Add(edge.Reversed);
            if (fill1 > 0) List(fills, fill1).Add(edge);
            if (line > 0) List(lines, line).Add(edge);
        }

        foreach (var record in records)
        {
            switch (record)
            {
                case StyleChangeShapeRecord change:
                    var (newFills, newLines) = NewStyles(change);
                    if (change.StateNewStyles && newFills != null)
                    {
                        Flush();
                        fillStyles = newFills;
                        lineStyles = newLines ?? [];
                        fill0 = fill1 = line = 0;
                    }
                    if (change.StateMoveTo) at = new Pt(change.MoveDeltaX, change.MoveDeltaY);
                    if (change.FillStyle0.HasValue) fill0 = (int)change.FillStyle0.Value;
                    if (change.FillStyle1.HasValue) fill1 = (int)change.FillStyle1.Value;
                    if (change.LineStyle.HasValue) line = (int)change.LineStyle.Value;
                    break;
                case StraightEdgeShapeRecord straight:
                    var to = new Pt(at.X + straight.DeltaX, at.Y + straight.DeltaY);
                    Add(new Edge(at, null, to));
                    at = to;
                    break;
                case CurvedEdgeShapeRecord curved:
                    var control = new Pt(at.X + curved.ControlDeltaX, at.Y + curved.ControlDeltaY);
                    var anchor = new Pt(control.X + curved.AnchorDeltaX, control.Y + curved.AnchorDeltaY);
                    Add(new Edge(at, control, anchor));
                    at = anchor;
                    break;
            }
        }
        Flush();
        return output;
    }

    static List<Edge> List(SortedDictionary<int, List<Edge>> map, int key)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        return list;
    }

    static (IList<object> Fills, IList<object> Lines, IEnumerable<IShapeRecord> Records) Styles(ShapeBaseTag shape) => shape switch
    {
        DefineShapeTag s => ([.. s.FillStyles], [.. s.LineStyles], s.ShapeRecords),
        DefineShape2Tag s => ([.. s.FillStyles], [.. s.LineStyles], s.ShapeRecords),
        DefineShape3Tag s => ([.. s.FillStyles], [.. s.LineStyles], s.ShapeRecords),
        DefineShape4Tag s => ([.. s.FillStyles], [.. s.LineStyles], s.ShapeRecords),
        _ => ([], [], []),
    };

    static (IList<object>? Fills, IList<object>? Lines) NewStyles(StyleChangeShapeRecord change) => change switch
    {
        StyleChangeShapeRecordRGB c => ([.. c.FillStyles], [.. c.LineStyles]),
        StyleChangeShapeRecordRGBA c => ([.. c.FillStyles], [.. c.LineStyles]),
        StyleChangeShapeRecordEx c => ([.. c.FillStyles], [.. c.LineStyles]),
        _ => (null, null),
    };

    static Geometry Geometry(List<Edge> edges, bool closed)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.SetFillRule(FillRule.EvenOdd);
        var used = new bool[edges.Count];
        var starts = new Dictionary<Pt, List<int>>();
        for (var i = 0; i < edges.Count; i++)
        {
            if (!starts.TryGetValue(edges[i].From, out var l)) starts[edges[i].From] = l = [];
            l.Add(i);
        }
        for (var first = 0; first < edges.Count; first++)
        {
            if (used[first]) continue;
            var start = edges[first].From;
            g.BeginFigure(P(start), closed);
            var current = first;
            while (true)
            {
                used[current] = true;
                var e = edges[current];
                if (e.Control is { } c) g.QuadraticBezierTo(P(c), P(e.To));
                else g.LineTo(P(e.To));
                if (closed && e.To == start) break;
                var next = -1;
                if (starts.TryGetValue(e.To, out var candidates))
                    foreach (var k in candidates)
                        if (!used[k]) { next = k; break; }
                if (next < 0) break;
                current = next;
            }
            g.EndFigure(closed);
        }
        return geometry;
    }

    static Point P(Pt p) => new(p.X / Twips, p.Y / Twips);

    static Color Swap(byte r, byte g, byte b, byte a, IReadOnlyDictionary<uint, uint> swaps)
    {
        var rgb = (uint)(r << 16 | g << 8 | b);
        if (swaps.TryGetValue(rgb, out var to)) rgb = to;
        return Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    static Color Swap(SwfRGB c, IReadOnlyDictionary<uint, uint> swaps) => Swap(c.Red, c.Green, c.Blue, 255, swaps);

    static Color Swap(SwfRGBA c, IReadOnlyDictionary<uint, uint> swaps) => Swap(c.Red, c.Green, c.Blue, c.Alpha, swaps);

    static IBrush? Fill(object style, IReadOnlyDictionary<uint, uint> swaps) => style switch
    {
        SolidFillStyleRGB s => new ImmutableSolidColorBrush(Swap(s.Color, swaps)),
        SolidFillStyleRGBA s => new ImmutableSolidColorBrush(Swap(s.Color, swaps)),
        LinearGradientFillStyleRGB s => Linear(s.GradientMatrix, s.Gradient.SpreadMode, s.Gradient.GradientRecords.Select(r => (r.Ratio, Swap(r.Color, swaps)))),
        LinearGradientFillStyleRGBA s => Linear(s.GradientMatrix, s.Gradient.SpreadMode, s.Gradient.GradientRecords.Select(r => (r.Ratio, Swap(r.Color, swaps)))),
        RadialGradientFillStyleRGB s => Radial(s.GradientMatrix, s.Gradient.SpreadMode, 0, s.Gradient.GradientRecords.Select(r => (r.Ratio, Swap(r.Color, swaps)))),
        RadialGradientFillStyleRGBA s => Radial(s.GradientMatrix, s.Gradient.SpreadMode, 0, s.Gradient.GradientRecords.Select(r => (r.Ratio, Swap(r.Color, swaps)))),
        FocalGradientFillStyleRGB s => Radial(s.GradientMatrix, s.Gradient.SpreadMode, s.Gradient.FocalPoint, s.Gradient.GradientRecords.Select(r => (r.Ratio, Swap(r.Color, swaps)))),
        FocalGradientFillStyleRGBA s => Radial(s.GradientMatrix, s.Gradient.SpreadMode, s.Gradient.FocalPoint, s.Gradient.GradientRecords.Select(r => (r.Ratio, Swap(r.Color, swaps)))),
        BitmapFillStyleRGB or BitmapFillStyleRGBA => new ImmutableSolidColorBrush(Color.FromArgb(96, 128, 128, 128)),
        _ => null,
    };

    static IPen? Pen(object style, IReadOnlyDictionary<uint, uint> swaps) => style switch
    {
        LineStyleRGB l => new ImmutablePen(new ImmutableSolidColorBrush(Swap(l.Color, swaps)), Math.Max(l.Width / Twips, 0.05), null, PenLineCap.Round, PenLineJoin.Round),
        LineStyleRGBA l => new ImmutablePen(new ImmutableSolidColorBrush(Swap(l.Color, swaps)), Math.Max(l.Width / Twips, 0.05), null, PenLineCap.Round, PenLineJoin.Round),
        LineStyleEx l => new ImmutablePen(l.HasFill && Fill(l.FillStyle, swaps) is IImmutableBrush b ? b : new ImmutableSolidColorBrush(Swap(l.Color, swaps)),
            Math.Max(l.Width / Twips, 0.05), null, Cap(l.StartCapStyle), Join(l.JoinStyle), l.MilterLimitFactor > 0 ? l.MilterLimitFactor : 3),
        _ => null,
    };

    static PenLineCap Cap(CapStyle c) => c switch { CapStyle.NoCap => PenLineCap.Flat, CapStyle.Square => PenLineCap.Square, _ => PenLineCap.Round };

    static PenLineJoin Join(JoinStyle j) => j switch { JoinStyle.Bevel => PenLineJoin.Bevel, JoinStyle.Miter => PenLineJoin.Miter, _ => PenLineJoin.Round };

    static GradientSpreadMethod Spread(SpreadMode mode) => mode switch
    {
        SpreadMode.Reflect => GradientSpreadMethod.Reflect,
        SpreadMode.Repeat => GradientSpreadMethod.Repeat,
        _ => GradientSpreadMethod.Pad,
    };

    static IReadOnlyList<ImmutableGradientStop> Stops(IEnumerable<(byte Ratio, Color Color)> records)
        => [.. records.Select(r => new ImmutableGradientStop(r.Ratio / 255.0, r.Color))];

    static ImmutableTransform GradientTransform(SwfMatrix m)
        => new(new Matrix(m.ScaleX, m.RotateSkew0, m.RotateSkew1, m.ScaleY, m.TranslateX / Twips, m.TranslateY / Twips));

    static IBrush Linear(SwfMatrix m, SpreadMode spread, IEnumerable<(byte, Color)> records)
        => new ImmutableLinearGradientBrush(Stops(records), 1, GradientTransform(m), RelativePoint.TopLeft, Spread(spread),
            new RelativePoint(-GradientHalf, 0, RelativeUnit.Absolute), new RelativePoint(GradientHalf, 0, RelativeUnit.Absolute));

    static IBrush Radial(SwfMatrix m, SpreadMode spread, double focal, IEnumerable<(byte, Color)> records)
        => new ImmutableRadialGradientBrush(Stops(records), 1, GradientTransform(m), RelativePoint.TopLeft, Spread(spread),
            new RelativePoint(0, 0, RelativeUnit.Absolute), new RelativePoint(focal * GradientHalf, 0, RelativeUnit.Absolute),
            new RelativeScalar(GradientHalf, RelativeUnit.Absolute), new RelativeScalar(GradientHalf, RelativeUnit.Absolute));

    public static void Draw(DrawingContext dc, PosedFrame frame, ShapeCache cache, Matrix view)
    {
        foreach (var shape in frame.Shapes)
        {
            var parts = cache.Get(shape);
            if (parts.Count == 0) continue;
            using (dc.PushTransform(ToMatrix(shape.Transform) * view))
            using (dc.PushOpacity(Math.Clamp(shape.Opacity, 0, 1)))
            {
                foreach (var part in parts) dc.DrawGeometry(part.Fill, part.Stroke, part.Geometry);
            }
        }
    }

    public static Rect Bounds(PosedFrame frame)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var s in frame.Shapes)
        {
            var r = s.Shape.ShapeBounds;
            foreach (var (cx, cy) in new[] { (r.XMin, r.YMin), (r.XMax, r.YMin), (r.XMin, r.YMax), (r.XMax, r.YMax) })
            {
                var (px, py) = s.Transform * (cx / Twips, cy / Twips);
                x0 = Math.Min(x0, px); y0 = Math.Min(y0, py); x1 = Math.Max(x1, px); y1 = Math.Max(y1, py);
            }
        }
        return frame.Shapes.Count == 0 ? default : new Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
