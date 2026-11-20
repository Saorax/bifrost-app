using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media.Imaging;
using BifrostStudio.Render;

namespace BifrostStudio;

public sealed class ReplayArt
{
    readonly string? game;
    readonly ReplayNames names;
    readonly ConcurrentDictionary<string, Task<Bitmap?>> pictures = new(StringComparer.OrdinalIgnoreCase);
    readonly Lazy<InstallLoader?> loader;

    public ReplayArt(string? game, ReplayNames names)
    {
        this.game = game;
        this.names = names;
        loader = new Lazy<InstallLoader?>(() =>
        {
            if (game == null) return null;
            try { return new InstallLoader(game); }
            catch (Exception e) when (e is SwzException or IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        });
    }

    public string? ThumbnailPath(ReplayEntry e)
    {
        var name = names.Level((uint)e.Level)?.Name ?? e.Map;
        return ThumbnailPath(name.Length == 0 ? MapFromFile(e.File) : name, (uint)e.Level);
    }

    public string? ThumbnailPath(string name, uint levelId)
    {
        if (game == null) return null;
        var level = names.Level(levelId);
        var candidates = new List<ReplayLevel>();
        if (level != null) candidates.Add(level);
        if (level != null) name = level.Name;
        var core = System.Text.RegularExpressions.Regex.Replace(name, "^(Small|Big)", "");
        foreach (var n in new[] { name, core, "Big" + core, "Small" + core })
            candidates.AddRange(names.Levels.Values.Where(l => l.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
        foreach (var l in candidates)
        {
            if (l.Thumbnail.Length == 0 || l.Thumbnail.Contains("testmap", StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(game, "images", "thumbnails", l.Thumbnail);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static string MapFromFile(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"^\[[^\]]*\]\s*", "");
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"\s*\(\d+\)$", "");
        return stem.Replace("'", "").Replace(" ", "");
    }

    public Task<Bitmap?> Thumbnail(ReplayEntry e, int width)
    {
        var path = ThumbnailPath(e);
        return path == null ? Task.FromResult<Bitmap?>(null) : Picture(path, width);
    }

    public Task<Bitmap?> Picture(string path, int width)
    {
        return pictures.GetOrAdd(path + "@" + width, _ => Task.Run(() =>
        {
            try
            {
                using var s = File.OpenRead(path);
                return (Bitmap?)Bitmap.DecodeToWidth(s, width, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return null; }
        }));
    }

    public Task<Bitmap?> Legend(int hero, int size, bool portrait = false)
    {
        var art = portrait ? names.Portrait(hero) : names.Icon(hero);
        if (art is not { } a) return Task.FromResult<Bitmap?>(null);
        return pictures.GetOrAdd($"{a.File}/{a.Symbol}@{size}", _ => Draw(a.File, a.Symbol, size));
    }

    public Task<Bitmap?> Weapon(string weapon, int size) =>
        weapon.Length == 0 ? Task.FromResult<Bitmap?>(null) : pictures.GetOrAdd($"UI_Icons/a_WeaponIcon_{weapon}@{size}", _ => Draw("UI_Icons", "a_WeaponIcon_" + weapon, size));

    async Task<Bitmap?> Draw(string file, string symbol, int size)
    {
        if (await Task.Run(() => loader.Value) is not { } l) return null;
        var frame = await UiArt.Get(l, file, symbol);
        if (frame == null) return null;
        return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                var bounds = ShapeDrawing.Bounds(frame);
                if (bounds.Width <= 0 || bounds.Height <= 0) return null;
                var pixels = size * 2;
                var target = new RenderTargetBitmap(new PixelSize(pixels, pixels));
                using (var dc = target.CreateDrawingContext())
                    PoseView.Paint(dc, frame, new ShapeCache(), bounds, new Size(pixels, pixels), 0);
                return (Bitmap?)target;
            }
            catch (Exception x) when (x is ArgumentException or InvalidOperationException) { return null; }
        });
    }
}
