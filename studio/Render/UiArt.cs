using System.Collections.Concurrent;

namespace BifrostStudio.Render;

public static class UiArt
{
    static readonly ConcurrentDictionary<(InstallLoader, string), Task<PosedFrame?>> cache = new();

    public static string SwfPath(string file) => file.EndsWith(".swf", StringComparison.OrdinalIgnoreCase) ? file : file + ".swf";

    public static Task<PosedFrame?> Get(InstallLoader loader, string file, string symbol) =>
        cache.GetOrAdd((loader, SwfPath(file) + "/" + symbol), _ => Task.Run(() => Build(loader, file, symbol)));

    public static PosedFrame? Build(InstallLoader loader, string file, string symbol)
    {
        var path = SwfPath(file);
        try
        {
            if (loader.Swf(path) is not { } swf || !swf.Symbols.TryGetValue(symbol, out var id)) return null;
            var posed = new PosedFrame();
            foreach (var (shape, transform) in SwfSymbol.Shapes(swf, id)) posed.Add(new PosedShape(path, shape, transform, 1, NoSwaps, symbol));
            return posed.Shapes.Count > 0 ? posed : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException) { return null; }
    }

    static readonly IReadOnlyDictionary<uint, uint> NoSwaps = new Dictionary<uint, uint>();
}
