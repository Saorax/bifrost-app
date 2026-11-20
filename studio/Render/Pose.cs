using SwfLib.Tags.ShapeTags;

namespace BifrostStudio.Render;

public sealed record PosedShape(string SwfPath, ShapeBaseTag Shape, Transform2D Transform, double Opacity, IReadOnlyDictionary<uint, uint> ColorSwaps, string Symbol);

public sealed class PosedFrame
{
    public List<PosedShape> Shapes { get; } = [];

    public void Add(PosedShape shape) => Shapes.Add(shape);
}
