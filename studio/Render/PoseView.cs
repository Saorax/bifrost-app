using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BifrostStudio.Render;

public sealed class PoseView : Control
{
    PosedFrame? frame;
    Rect? fit;

    public ShapeCache Cache { get; set; } = new();

    public IBrush? Background { get; set; }

    public double Padding { get; set; } = 16;

    public PosedFrame? Frame
    {
        get => frame;
        set
        {
            frame = value;
            InvalidateVisual();
        }
    }

    public Rect? Fit
    {
        get => fit;
        set
        {
            fit = value;
            InvalidateVisual();
        }
    }

    public static Matrix View(Rect content, Size area, double padding)
    {
        if (content.Width <= 0 || content.Height <= 0 || area.Width <= 2 * padding || area.Height <= 2 * padding) return Matrix.Identity;
        var scale = Math.Min((area.Width - 2 * padding) / content.Width, (area.Height - 2 * padding) / content.Height);
        var dx = (area.Width - content.Width * scale) / 2 - content.X * scale;
        var dy = (area.Height - content.Height * scale) / 2 - content.Y * scale;
        return Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy);
    }

    public static void Paint(DrawingContext dc, PosedFrame frame, ShapeCache cache, Rect content, Size area, double padding)
        => ShapeDrawing.Draw(dc, frame, cache, View(content, area, padding));

    public override void Render(DrawingContext context)
    {
        var area = Bounds.Size;
        if (Background != null) context.FillRectangle(Background, new Rect(area));
        if (frame == null || frame.Shapes.Count == 0) return;
        Paint(context, frame, Cache, fit ?? ShapeDrawing.Bounds(frame), area, Padding);
    }
}
