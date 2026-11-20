using SwfLib.Data;

namespace BifrostStudio.Render;

public readonly record struct Transform2D(double ScaleX, double SkewX, double SkewY, double ScaleY, double TranslateX, double TranslateY)
{
    public static readonly Transform2D Identity = new(1, 0, 0, 1, 0, 0);

    public static Transform2D From(SwfMatrix m) => new(m.ScaleX, m.RotateSkew1, m.RotateSkew0, m.ScaleY, m.TranslateX / 20.0, m.TranslateY / 20.0);

    public static Transform2D operator *(Transform2D a, Transform2D b) => new(
        a.ScaleX * b.ScaleX + a.SkewX * b.SkewY,
        a.ScaleX * b.SkewX + a.SkewX * b.ScaleY,
        a.SkewY * b.ScaleX + a.ScaleY * b.SkewY,
        a.SkewY * b.SkewX + a.ScaleY * b.ScaleY,
        a.ScaleX * b.TranslateX + a.SkewX * b.TranslateY + a.TranslateX,
        a.SkewY * b.TranslateX + a.ScaleY * b.TranslateY + a.TranslateY);

    public static (double X, double Y) operator *(Transform2D t, (double X, double Y) p) => (
        t.ScaleX * p.X + t.SkewX * p.Y + t.TranslateX,
        t.SkewY * p.X + t.ScaleY * p.Y + t.TranslateY);
}
