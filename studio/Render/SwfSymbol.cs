using SwfLib.Data;
using SwfLib.Tags;
using SwfLib.Tags.DisplayListTags;
using SwfLib.Tags.ShapeTags;

namespace BifrostStudio.Render;

public static class SwfSymbol
{
    const int MaxDepth = 32;

    public static List<(ShapeBaseTag Shape, Transform2D Transform)> Shapes(SwfData swf, ushort id) => Shapes(swf, id, Transform2D.Identity, 0);

    static List<(ShapeBaseTag Shape, Transform2D Transform)> Shapes(SwfData swf, ushort id, Transform2D at, int depth)
    {
        var found = new List<(ShapeBaseTag, Transform2D)>();
        if (depth > MaxDepth || !swf.Tags.TryGetValue(id, out var tag)) return found;
        if (tag is ShapeBaseTag shape)
        {
            found.Add((shape, at));
            return found;
        }
        if (tag is not DefineSpriteTag sprite) return found;
        foreach (var (child, matrix) in FirstFrame(sprite).Values)
            found.AddRange(Shapes(swf, child, at * Transform2D.From(matrix), depth + 1));
        return found;
    }

    static SortedDictionary<ushort, (ushort Id, SwfMatrix Matrix)> FirstFrame(DefineSpriteTag sprite)
    {
        var layers = new SortedDictionary<ushort, (ushort Id, SwfMatrix Matrix)>();
        foreach (var t in sprite.Tags)
        {
            switch (t)
            {
                case ShowFrameTag:
                    return layers;
                case RemoveObject2Tag remove:
                    layers.Remove(remove.Depth);
                    break;
                case PlaceObject2Tag p:
                    Place(layers, p.Depth, p.HasCharacter ? p.CharacterID : null, p.HasMatrix ? p.Matrix : null);
                    break;
                case PlaceObject3Tag p:
                    Place(layers, p.Depth, p.HasCharacter ? p.CharacterID : null, p.HasMatrix ? p.Matrix : null);
                    break;
                case PlaceObjectTag p:
                    Place(layers, p.Depth, p.CharacterID, p.Matrix);
                    break;
            }
        }
        return layers;
    }

    static void Place(SortedDictionary<ushort, (ushort Id, SwfMatrix Matrix)> layers, ushort depth, ushort? id, SwfMatrix? matrix)
    {
        var had = layers.TryGetValue(depth, out var old);
        if (id is not { } character && !had) return;
        layers[depth] = (id ?? old.Id, matrix ?? (had ? old.Matrix : Identity));
    }

    static readonly SwfMatrix Identity = new() { ScaleX = 1, ScaleY = 1 };
}
