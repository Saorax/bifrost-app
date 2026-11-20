using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace BifrostStudio;

public sealed class HomePage : UserControl
{
    static readonly string[] HeroArt = ["BG_Valhalla.jpg", "BG_Brawlhaven.jpg", "BG_Grove.jpg", "BG_Fjord.jpg",
        "BG_MoonPalace.jpg", "BG_Elysium2.jpg", "BG_Temple.jpg", "BG_Celestial_ALL.jpg"];

    readonly Image artA = Art(), artB = Art();
    List<string> artFiles = [];
    int art;

    public HomePage(Control appUpdate, Control idle, Control progress, params Control[] cards)
    {
        var shade = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.35, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(40, 9, 11, 22), 0), new GradientStop(Color.FromArgb(150, 9, 11, 22), 0.55),
                    new GradientStop(Color.FromArgb(235, 9, 11, 22), 1),
                },
            },
        };
        var inner = new Panel { Margin = new Thickness(34, 26, 34, 30), Children = { appUpdate, idle, progress } };
        var hero = new Border
        {
            CornerRadius = new CornerRadius(20), ClipToBounds = true, MinHeight = 340,
            Child = new Panel { Children = { new Border { Background = Look.Bridge() }, artA, artB, shade, inner } },
        };
        var row = new UniformGrid { Columns = cards.Length, Margin = new Thickness(-8, 0) };
        foreach (var card in cards)
        {
            card.Margin = new Thickness(8, 0);
            row.Children.Add(card);
        }
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 18 };
        Grid.SetRow(row, 1);
        grid.Children.Add(hero);
        grid.Children.Add(row);
        Content = new Panel { Children = { grid } };
    }

    public void AddOverlay(Control overlay)
    {
        if (Content is Panel panel) panel.Children.Add(overlay);
    }

    static Image Art() => new()
    {
        Stretch = Stretch.UniformToFill, Opacity = 0,
        Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromSeconds(1.4) } },
    };

    public void LoadArt(string? game)
    {
        var dir = game == null ? null : Path.Combine(game, "mapArt", "Backgrounds");
        artFiles = dir == null ? [] : HeroArt.Select(n => Path.Combine(dir, n)).Where(File.Exists).ToList();
        art = 0;
        if (artFiles.Count > 0) SetArt(artA, artFiles[0]);
    }

    public void NextArt()
    {
        if (artFiles.Count < 2) return;
        art = (art + 1) % artFiles.Count;
        var (show, hide) = artA.Opacity > 0.5 ? (artB, artA) : (artA, artB);
        SetArt(show, artFiles[art]);
        hide.Opacity = 0;
    }

    static void SetArt(Image image, string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            image.Source = Bitmap.DecodeToWidth(stream, 1600);
            image.Opacity = 1;
        }
        catch (Exception) { }
    }
}