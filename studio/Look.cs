using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace BifrostStudio;

public static class Look
{
    public static readonly Color Back = Color.Parse("#0D0F1C");
    public static readonly Color Panel = Color.Parse("#151830");
    public static readonly Color Line = Color.Parse("#242845");
    public static readonly Color Text = Color.Parse("#EEF0FF");
    public static readonly Color Dim = Color.Parse("#8C92BD");
    public static readonly Color Good = Color.Parse("#3BE3A0");
    public static readonly Color Side = Color.Parse("#090B16");
    public static readonly Color Raised = Color.Parse("#1D2144");
    public static readonly Color Accent = Color.Parse("#3D95FF");

    public static IBrush Brush(Color c) => new SolidColorBrush(c);

    public static LinearGradientBrush Bridge(double alpha = 1) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#7B3FF2"), 0), new GradientStop(Color.Parse("#2F8CFF"), 0.55),
            new GradientStop(Color.Parse("#20D8B0"), 1),
        },
        Opacity = alpha,
    };

    public const string Play = "M8,5.14V19.14L19,12.14L8,5.14Z";
    public const string Download = "M5,20H19V18H5M19,9H15V3H9V9H5L12,16L19,9Z";
    public const string Check = "M21,7L9,19L3.5,13.5L4.91,12.09L9,16.17L19.59,5.59L21,7Z";
    public const string Refresh = "M17.65,6.35C16.2,4.9 14.21,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20C15.73,20 18.84,17.45 19.73,14H17.65C16.83,16.33 14.61,18 12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6C13.66,6 15.14,6.69 16.22,7.78L13,11H20V4L17.65,6.35Z";
    public const string Folder = "M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z";
    public const string Logs = "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20M8,12V14H16V12H8M8,16V18H13V16H8Z";
    public const string Trash = "M19,4H15.5L14.5,3H9.5L8.5,4H5V6H19M6,19A2,2 0 0,0 8,21H16A2,2 0 0,0 18,19V7H6V19Z";
    public const string Stop = "M18,18H6V6H18V18Z";
    public const string Copy = "M19,21H8V7H19M19,5H8A2,2 0 0,0 6,7V21A2,2 0 0,0 8,23H19A2,2 0 0,0 21,21V7A2,2 0 0,0 19,5M16,1H4A2,2 0 0,0 2,3V17H4V3H16V1Z";
    public const string Server = "M4,1H20A1,1 0 0,1 21,2V6A1,1 0 0,1 20,7H4A1,1 0 0,1 3,6V2A1,1 0 0,1 4,1M4,9H20A1,1 0 0,1 21,10V14A1,1 0 0,1 20,15H4A1,1 0 0,1 3,14V10A1,1 0 0,1 4,9M4,17H20A1,1 0 0,1 21,18V22A1,1 0 0,1 20,23H4A1,1 0 0,1 3,22V18A1,1 0 0,1 4,17M9,5H10V3H9V5M9,13H10V11H9V13M9,21H10V19H9V21M5,3V5H7V3H5M5,11V13H7V11H5M5,19V21H7V19H5Z";
    public const string Home = "M10,20V14H14V20H19V12H22L12,3L2,12H5V20H10Z";
    public const string Tune = "M3,17V19H9V17H3M3,5V7H13V5H3M13,21V19H21V17H13V15H11V21H13M7,9V11H3V13H7V15H9V9H7M21,13V11H11V13H21M15,9H17V7H21V5H17V3H15V9Z";
    public const string Tag = "M5.5,7A1.5,1.5 0 0,1 4,5.5A1.5,1.5 0 0,1 5.5,4A1.5,1.5 0 0,1 7,5.5A1.5,1.5 0 0,1 5.5,7M21.41,11.58L12.41,2.58C12.05,2.22 11.55,2 11,2H4C2.89,2 2,2.89 2,4V11C2,11.55 2.22,12.05 2.59,12.41L11.58,21.41C11.95,21.77 12.45,22 13,22C13.55,22 14.05,21.77 14.41,21.41L21.41,14.41C21.78,14.05 22,13.55 22,13C22,12.44 21.77,11.94 21.41,11.58Z";
    public const string Pulse = "M3,13H5.79L10.1,4.79L11.28,13.75L14.5,9.66L17.83,13H21V15H17L14.67,12.67L9.92,18.73L8.94,11.31L7,15H3V13Z";
    public const string Wrench = "M22.7,19L13.6,9.9C14.5,7.6 14,4.9 12.1,3C10.1,1 7.1,0.6 4.7,1.7L9,6L6,9L1.6,4.7C0.4,7.1 0.9,10.1 2.9,12.1C4.8,14 7.5,14.5 9.8,13.6L18.9,22.7C19.3,23.1 19.9,23.1 20.3,22.7L22.6,20.4C23.1,20 23.1,19.3 22.7,19Z";

    public static Border Card(string label, string icon, TextBlock value, TextBlock sub, Control? extra = null)
    {
        value.FontSize = 19;
        value.FontWeight = FontWeight.SemiBold;
        value.Foreground = Brush(Text);
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        sub.FontSize = 12;
        sub.Foreground = Brush(Dim);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        sub.IsVisible = !string.IsNullOrEmpty(sub.Text);
        sub.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) sub.IsVisible = !string.IsNullOrEmpty(sub.Text); };
        var stack = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        Icon(icon, 15, Dim),
                        new TextBlock { Text = label.ToUpperInvariant(), FontSize = 11, LetterSpacing = 1.5, Foreground = Brush(Dim), FontWeight = FontWeight.SemiBold },
                    },
                },
                value, sub,
            },
        };
        if (extra != null) stack.Children.Add(extra);
        return new Border { Background = Brush(Panel), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 16), Child = stack };
    }

    public static Button Secondary(string text, string icon, Action click)
    {
        var b = new Button
        {
            Classes = { "secondary" }, Background = Brush(Raised), Foreground = Brush(Text), Padding = new Thickness(14, 8),
            FontSize = 13, CornerRadius = new CornerRadius(10),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { Icon(icon, 15, Text), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        b.Click += (_, _) => click();
        return b;
    }

    public static Button Nav(string text, string icon, Action click)
    {
        var b = new Button
        {
            Classes = { "nav" }, Padding = new Thickness(16, 11),
            FontSize = 14, FontWeight = FontWeight.Medium, CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new PathIcon { Data = Geometry.Parse(icon), Width = 18, Height = 18 },
                    new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        b.Click += (_, _) => click();
        return b;
    }

    public static PathIcon Icon(string data, double size, Color color) =>
        new() { Data = Geometry.Parse(data), Width = size, Height = size, Foreground = Brush(color) };

    public static Button Primary(string text, string icon, Action click)
    {
        var b = new Button
        {
            Classes = { "primary" }, Height = 54, MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(27), Padding = new Thickness(34, 0), Background = Bridge(), Foreground = Brushes.White,
            FontSize = 18, FontWeight = FontWeight.SemiBold,
        };
        SetContent(b, text, icon);
        b.Click += (_, _) => click();
        return b;
    }

    public static void SetContent(Button b, string text, string icon) =>
        b.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { Icon(icon, 20, Colors.White), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
        };

    public static Button Link(string text, string icon, Action click)
    {
        var b = new Button
        {
            Classes = { "link" }, Background = Brushes.Transparent, Foreground = Brush(Dim), Padding = new Thickness(10, 6),
            FontSize = 13, CornerRadius = new CornerRadius(8),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { Icon(icon, 14, Dim), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        b.Click += (_, _) => click();
        return b;
    }

    public static void Apply(Styles styles)
    {
        var presenter = (Selector s) => s.Template().OfType<ContentPresenter>();
        foreach (var state in new[] { "", ":pointerover", ":focus" })
        {
            styles.Add(new Style(x =>
            {
                var box = x.OfType<TextBox>().Class("log");
                return (state == "" ? box : box.Class(state)).Template().OfType<Border>().Name("PART_BorderElement");
            })
            {
                Setters =
                {
                    new Setter(Border.BackgroundProperty, Brush(Panel)), new Setter(Border.BorderThicknessProperty, new Thickness(0)),
                    new Setter(Border.CornerRadiusProperty, new CornerRadius(10)),
                },
            });
        }
        styles.Add(new Style(x => x.OfType<TextBox>().Class("log"))
        {
            Setters = { new Setter(TextBox.ForegroundProperty, Brush(Text)), new Setter(TextBox.PaddingProperty, new Thickness(12, 10)) },
        });
        styles.Add(new Style(x => presenter(x.OfType<Button>().Class("primary").Class(":pointerover")))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Bridge(0.85)), new Setter(ContentPresenter.ForegroundProperty, Brushes.White) },
        });
        styles.Add(new Style(x => presenter(x.OfType<Button>().Class("primary").Class(":pressed")))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Bridge(0.7)) },
        });
        styles.Add(new Style(x => presenter(x.OfType<Button>().Class("primary").Class(":disabled")))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Brush(Line)),
                new Setter(ContentPresenter.ForegroundProperty, Brush(Dim)),
            },
        });
        styles.Add(new Style(x => presenter(x.OfType<Button>().Class("link").Class(":pointerover")))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Brush(Panel)),
                new Setter(ContentPresenter.ForegroundProperty, Brush(Text)),
            },
        });
        styles.Add(new Style(x => presenter(x.OfType<Button>().Class("secondary").Class(":pointerover")))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Brush(Line)), new Setter(ContentPresenter.ForegroundProperty, Brush(Text)) },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class("nav"))
        {
            Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent), new Setter(Button.ForegroundProperty, Brush(Dim)) },
        });
        styles.Add(new Style(x => presenter(x.OfType<Button>().Class("nav").Class(":pointerover")))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Brush(Panel)), new Setter(ContentPresenter.ForegroundProperty, Brush(Text)) },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class("nav").Class("selected"))
        {
            Setters = { new Setter(Button.BackgroundProperty, Brush(Raised)), new Setter(Button.ForegroundProperty, Brush(Text)) },
        });
        styles.Add(new Style(x => presenter(x.OfType<Button>().Class("nav").Class("selected").Class(":pointerover")))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Brush(Raised)) },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class("nav").Class("selected").Descendant().OfType<PathIcon>())
        {
            Setters = { new Setter(PathIcon.ForegroundProperty, Bridge()) },
        });
        styles.Add(new Style(x => x.OfType<ProgressBar>())
        {
            Setters = { new Setter(ProgressBar.ForegroundProperty, Bridge()), new Setter(ProgressBar.BackgroundProperty, Brush(Line)) },
        });
    }
}
