using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BifrostStudio.Net;

namespace BifrostStudio;

public sealed partial class OnlinePage
{
    static readonly Color Teal = ReplaysPage.Teal;
    static readonly Color Loss = ReplaysPage.Loss;
    string? artGame;
    ReplayNames names = ReplayNames.Empty;
    ReplayArt? art;

    void LoadNames()
    {
        var game = hooks.Game?.Invoke();
        if (game == artGame && art != null) return;
        artGame = game;
        names = ReplayNames.For(game);
        art = new ReplayArt(game, names);
    }

    ReplayArt Art
    {
        get
        {
            if (art == null) LoadNames();
            return art!;
        }
    }

    string LegendShown(JsonNode? id, JsonNode? serverName = null)
    {
        if (OnlineService.Num(id) is { } n && n > 0 && names.HeroShown((uint)n) is { Length: > 0 } shown && !shown.StartsWith("Legend ", StringComparison.Ordinal)) return shown;
        if (OnlineService.Text(serverName) is { Length: > 0 } s)
        {
            var byName = names.HeroId(s);
            return byName > 0 ? names.HeroShown((uint)byName) : s;
        }
        return OnlineService.Num(id) is { } m && m > 0 ? "Legend " + m : "";
    }

    string LegendShown(long id) => LegendShown(JsonValue.Create(id));

    ReplayLevel? LevelOf(string? map, JsonNode? levelId = null)
    {
        if (OnlineService.Num(levelId) is { } id && id > 0 && names.Level((uint)id) is { } byId) return byId;
        if (string.IsNullOrEmpty(map)) return null;
        return names.Levels.Values.FirstOrDefault(l => string.Equals(l.Name, map, StringComparison.OrdinalIgnoreCase))
            ?? names.Levels.Values.FirstOrDefault(l => string.Equals(l.Shown, map, StringComparison.OrdinalIgnoreCase));
    }

    string MapShown(string? map, JsonNode? levelId = null)
    {
        if (LevelOf(map, levelId) is { } l && names.LevelShown(l.Id) is { Length: > 0 } shown) return shown;
        if (string.IsNullOrEmpty(map)) return "";
        return ReplayNames.Spaced(System.Text.RegularExpressions.Regex.Replace(map, "^(Small|Big)(?=[A-Z])", ""));
    }

    static string WeaponShown(string key) => key switch
    {
        "RocketLance" => "Rocket Lance",
        "Pistol" => "Blasters",
        "Fists" => "Gauntlets",
        "Katar" => "Katars",
        "Boots" => "Battle Boots",
        "GreatSword" => "Greatsword",
        "Orb" => "Orb",
        _ => ReplayNames.Spaced(key),
    };

    static async Task Fill(Image image, Task<Bitmap?> picture)
    {
        try
        {
            if (await picture is { } b) image.Source = b;
        }
        catch (Exception x) when (x is IOException or InvalidOperationException or ArgumentException) { }
    }

    Control LegendFace(JsonNode? id, double size, bool portrait = false)
    {
        var img = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        var frame = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(Math.Max(3, size / 8)), ClipToBounds = true,
            Background = Look.Brush(Look.Raised), Child = img, VerticalAlignment = VerticalAlignment.Center,
        };
        if (OnlineService.Num(id) is { } n && n > 0)
        {
            ToolTip.SetTip(frame, LegendShown(id));
            _ = Fill(img, Art.Legend((int)n, (int)Math.Ceiling(size), portrait));
        }
        return frame;
    }

    Control LegendFace(long id, double size, bool portrait = false) => LegendFace(JsonValue.Create(id), size, portrait);

    Control WeaponFace(string key, double size)
    {
        var img = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(img, WeaponShown(key));
        _ = Fill(img, Art.Weapon(key, (int)Math.Ceiling(size)));
        return img;
    }

    Control MapPicture(string? map, JsonNode? levelId, double width, double height, double radius = 3)
    {
        var img = new Image { Width = width, Height = height, Stretch = Stretch.UniformToFill };
        var box = new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(radius), ClipToBounds = true, Background = Look.Brush(Look.Raised), Child = img,
        };
        if (MapThumbnail(map, levelId) is { } path) _ = Fill(img, Art.Picture(path, (int)(width * 2)));
        return box;
    }

    string? MapThumbnail(string? map, JsonNode? levelId) =>
        LevelOf(map, levelId) is { } l ? Art.ThumbnailPath(l.Name, l.Id) : string.IsNullOrEmpty(map) ? null : Art.ThumbnailPath(map, 0);

    static Control Banner(Control picture, double height, params Control[] lines)
    {
        var shade = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 13, 15, 28), 0.2), new GradientStop(Color.FromArgb(240, 21, 24, 48), 1) },
            },
        };
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(18, 0, 18, 12), Spacing = 2 };
        foreach (var l in lines) heading.Children.Add(l);
        return new Panel { Height = height, ClipToBounds = true, Background = Look.Brush(Look.Raised), Children = { picture, shade, heading } };
    }

    static TextBlock Big(string text, double size = 26) => new()
    {
        Text = text, FontSize = size, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Text), TextTrimming = TextTrimming.CharacterEllipsis,
    };

    static TextBlock Small(string text, double size = 12.5, Color? color = null, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Foreground = Look.Brush(color ?? Look.Dim), TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Border Pill(string text, Color color) => new()
    {
        Background = new SolidColorBrush(color, 0.2), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.Black, Foreground = new SolidColorBrush(color) },
    };

    static TextBlock Caption(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 12, LetterSpacing = 1, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Dim),
        Margin = new Thickness(0, 10, 0, 4),
    };

    static Button TabButton(string text, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeight.Black, LetterSpacing = 0.5 },
            Padding = new Thickness(14, 7), MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(4),
            Background = Look.Brush(Look.Text), Foreground = Look.Brush(Look.Back), Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Click += (_, _) => click();
        return b;
    }

    static void Pick(Button b, bool on)
    {
        b.Background = Look.Brush(on ? Teal : Look.Text);
        b.Foreground = Look.Brush(Look.Back);
        b.Opacity = on ? 1 : 0.92;
    }

    static Button Chip(string text, bool on, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.Bold },
            Padding = new Thickness(14, 5), CornerRadius = new CornerRadius(14), Cursor = new Cursor(StandardCursorType.Hand),
            Background = Look.Brush(on ? Teal : Look.Raised), Foreground = Look.Brush(on ? Look.Back : Look.Dim),
        };
        b.Click += (_, _) => click();
        return b;
    }

    static Button BigButton(string text, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeight.Black }, MinWidth = 110, Padding = new Thickness(16, 8),
            HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(4), Background = Look.Brush(Look.Text),
            Foreground = Look.Brush(Look.Back), Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Click += (_, _) => click();
        return b;
    }

    static Color TierColor(string? tier) => (tier ?? "").Split(' ')[0] switch
    {
        "Tin" => Color.Parse("#9AA3AD"),
        "Bronze" => Color.Parse("#D08A4E"),
        "Silver" => Color.Parse("#C9D3E0"),
        "Gold" => Color.Parse("#F2C94C"),
        "Platinum" => Color.Parse("#5FE0D0"),
        "Diamond" => Color.Parse("#8FA8FF"),
        "Valhallan" => Color.Parse("#FF7AD9"),
        _ => Look.Dim,
    };

    static (string Tier, string Number) RatingParts(JsonNode? rating)
    {
        if (rating is not JsonObject r || OnlineService.Num(r["rating"]) is not { } value) return ("Unrated", "");
        var games = OnlineService.Num(r["games"]) ?? 0;
        var need = OnlineService.Num(r["placement"]) ?? 10;
        if (games < need) return ($"Placement {games:0}/{need:0}", "");
        var tier = OnlineService.Text(r["tier"]);
        return (tier is { Length: > 0 } && tier != "Unranked" ? tier : "Unranked", value.ToString("0"));
    }

    static string Percent(double wins, double games) => games > 0 ? (wins / games).ToString("P0") : "-";

    static Control WinBar(double wins, double games, double width)
    {
        var share = games > 0 ? wins / games : 0;
        return new Panel
        {
            Width = width, Height = 4, HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Loss, 0.55) },
                new Border { CornerRadius = new CornerRadius(2), Background = Look.Brush(Look.Good), Width = Math.Max(0, width * share), HorizontalAlignment = HorizontalAlignment.Left },
            },
        };
    }
}
