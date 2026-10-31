using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace BifrostStudio;

public sealed class InstallProgress : UserControl
{
    public static readonly (string Key, string Name, double Seconds)[] DriverSteps =
    [
        ("check", "Check your game", 2), ("prepare", "Prepare", 8), ("decompile", "Read your game", 50),
        ("convert", "Convert to the new engine", 140), ("assemble", "Put it together", 6),
        ("build", "Build DirectX 11 and 12", 40), ("install", "Install", 4),
    ];

    readonly Settings settings;
    readonly List<(string Key, string Name)> steps = [];
    readonly Dictionary<string, double> took = [];
    readonly Dictionary<string, (PathIcon Icon, Control Dots, TextBlock Name, TextBlock Time)> rows = [];
    readonly TextBlock title = new() { FontSize = 26, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
    readonly TextBlock detail = new() { FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock times = new() { FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Right };
    readonly ProgressBar bar = new() { Height = 6, Minimum = 0, Maximum = 1, CornerRadius = new CornerRadius(3) };
    readonly StackPanel list = new() { Spacing = 6 };
    readonly Stopwatch total = new(), current = new();
    readonly DispatcherTimer tick;
    string? active;
    double fraction = -1;
    double downloadSeconds = 20;

    public InstallProgress(Settings settings, Control stop)
    {
        this.settings = settings;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(times, 1);
        head.Children.Add(title);
        head.Children.Add(times);
        times.VerticalAlignment = VerticalAlignment.Bottom;
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetColumn(stop, 1);
        footer.Children.Add(list);
        footer.Children.Add(stop);
        stop.VerticalAlignment = VerticalAlignment.Bottom;
        Content = new StackPanel { Spacing = 10, Children = { head, detail, bar, footer } };
        foreach (var style in DotStyles()) Styles.Add(style);
        tick = new DispatcherTimer(TimeSpan.FromSeconds(0.5), DispatcherPriority.Background, (_, _) => Update());
    }

    public void Begin(long downloadBytes)
    {
        steps.Clear();
        took.Clear();
        rows.Clear();
        list.Children.Clear();
        if (downloadBytes > 0)
        {
            steps.Add(("download", "Download"));
            steps.Add(("unpack", "Unpack"));
            downloadSeconds = downloadBytes / 8e6;
        }
        steps.AddRange(DriverSteps.Select(s => (s.Key, s.Name)));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,*,Auto"), RowSpacing = 5 };
        var half = (steps.Count + 1) / 2;
        for (var i = 0; i < steps.Count; i++)
        {
            var (key, name) = steps[i];
            var icon = new PathIcon { Data = Geometry.Parse(Look.Stop), Width = 9, Height = 9, Foreground = Look.Brush(Look.Dim), HorizontalAlignment = HorizontalAlignment.Left };
            var dots = Dots();
            var mark = new Panel { Width = 22, Margin = new Thickness(0, 0, 6, 0), Children = { icon, dots } };
            var label = new TextBlock { Text = name, FontSize = 12, Foreground = Look.Brush(Look.Dim) };
            var time = new TextBlock { FontSize = 12, Foreground = Look.Brush(Look.Dim), Margin = new Thickness(10, 0, 24, 0) };
            var column = i < half ? 0 : 3;
            var row = i < half ? i : i - half;
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            foreach (var (c, offset) in new (Control, int)[] { (mark, 0), (label, 1), (time, 2) })
            {
                Grid.SetColumn(c, column + offset);
                Grid.SetRow(c, row);
                grid.Children.Add(c);
            }
            rows[key] = (icon, dots, label, time);
        }
        list.Children.Add(grid);
        total.Restart();
        active = null;
        Enter(steps[0].Key);
        tick.Start();
    }

    public void Download(double p, double bytesPerSecond, long size)
    {
        if (active != "download") Enter("download");
        fraction = p;
        if (bytesPerSecond > 0) downloadSeconds = size / bytesPerSecond;
        detail.Text = $"{p * size / 1e6:0} of {size / 1e6:0} MB  ·  {bytesPerSecond / 1e6:0.0} MB/s";
    }

    public void Unpacking()
    {
        Enter("unpack");
        detail.Text = "Unpacking Bifrost's tools";
    }

    public void DriverStep(int n)
    {
        if (n < 1 || n > DriverSteps.Length) return;
        foreach (var (key, _, _) in DriverSteps.Take(n - 1))
        {
            if (key == active || took.ContainsKey(key) || !rows.TryGetValue(key, out var r)) continue;
            r.Icon.Data = Geometry.Parse(Look.Check);
            r.Icon.Foreground = Look.Brush(Look.Dim);
            r.Icon.IsVisible = true;
            r.Dots.IsVisible = false;
            r.Time.Text = "reused";
        }
        Enter(DriverSteps[n - 1].Key);
    }

    static readonly Regex Script = new(@"Exported script (\d+)/(\d+) (\S+)");

    public void Line(string line)
    {
        var text = line.StartsWith("  | ", StringComparison.Ordinal) ? line[4..].Trim() : line.Trim();
        if (text.Length == 0) return;
        if (Script.Match(text) is { Success: true } m)
        {
            var n = int.Parse(m.Groups[1].Value);
            var of = int.Parse(m.Groups[2].Value);
            fraction = (double)n / of;
            detail.Text = $"Reading {m.Groups[3].Value}  ({n} of {of})";
            return;
        }
        detail.Text = Summary(text) ?? detail.Text;
    }

    static string? Summary(string text)
    {
        if (text.StartsWith("building ") || text.StartsWith("built ")) return char.ToUpper(text[0]) + text[1..];
        if (text.Contains("already built for this release")) return "Nothing changed since the last build: reusing it";
        if (text.Contains("FFDec output is current")) return "Your game is unchanged: reusing what was read last time";
        if (text.Contains("renamed")) return "Naming the game's code";
        if (text.Contains("aliases")) return "Applying Bifrost's names";
        if (text.Contains("splices")) return "Fixing what the decompiler couldn't print";
        if (text.Contains("staged") && text.Contains("files for")) return "Preparing the converter";
        if (text.StartsWith("Bifrost tree at")) return "Adding Bifrost to the game's code";
        if (text.Contains("conversion tables from the cache")) return "Using the shipped conversion tables";
        if (text.Contains("conversion cache hit")) return "Nothing changed since the last build: reusing it";
        if (text.Contains("files converted")) return text.Replace("files converted", "files converted to Haxe");
        if (text.StartsWith("native fixes in")) return "Applied the engine's fixes to " + text.Split(':')[1].Trim().Split(',')[0];
        if (text.StartsWith("exported ")) return "Converted code ready";
        if (text.StartsWith("installed the new engine")) return "Bifrost's launcher put in place of Brawlhalla.exe";
        if (text.StartsWith("server ")) return "Server set: " + text[7..];
        return null;
    }

    void Enter(string key)
    {
        if (active != null)
        {
            took[active] = current.Elapsed.TotalSeconds;
            Mark(active, true);
        }
        active = key;
        fraction = -1;
        current.Restart();
        Mark(key, false);
        title.Text = steps.FirstOrDefault(s => s.Key == key).Name ?? key;
        Update();
    }

    static Control Dots()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        for (var i = 0; i < 3; i++)
        {
            panel.Children.Add(new Ellipse { Width = 4.5, Height = 4.5, Fill = Look.Brush(Look.Accent), Classes = { "dot" + i } });
        }
        return panel;
    }

    static IEnumerable<Style> DotStyles() => Enumerable.Range(0, 3).Select(i => new Style(
        x => x.OfType<Ellipse>().Class("dot" + i))
    {
        Animations =
        {
            new Animation
            {
                Duration = TimeSpan.FromSeconds(1.05), IterationCount = IterationCount.Infinite,
                Delay = TimeSpan.FromSeconds(0.35 * i),
                Children =
                {
                    Frame(0, 0.25, 0), Frame(0.3, 1, -2.5), Frame(0.6, 0.25, 0), Frame(1, 0.25, 0),
                },
            },
        },
    });

    static KeyFrame Frame(double cue, double opacity, double rise) => new()
    {
        Cue = new Cue(cue),
        Setters = { new Setter(Visual.OpacityProperty, opacity), new Setter(TranslateTransform.YProperty, rise) },
    };

    void Mark(string key, bool done)
    {
        if (!rows.TryGetValue(key, out var r)) return;
        r.Icon.Data = Geometry.Parse(Look.Check);
        r.Icon.Foreground = Look.Brush(Look.Good);
        r.Icon.IsVisible = done;
        r.Dots.IsVisible = !done;
        r.Name.Foreground = Look.Brush(done ? Look.Dim : Look.Text);
        if (done) r.Time.Text = Clock(took[key]);
    }

    double Expected(string key) => key switch
    {
        "download" => downloadSeconds,
        "unpack" => 6,
        _ => settings.StepSeconds?.GetValueOrDefault(key) is { } s and > 0 ? s : DriverSteps.First(d => d.Key == key).Seconds,
    };

    void Update()
    {
        if (active == null) return;
        var elapsed = current.Elapsed.TotalSeconds;
        var left = fraction > 0.02 ? elapsed * (1 - fraction) / fraction : Math.Max(Expected(active) - elapsed, 3);
        var i = steps.FindIndex(s => s.Key == active);
        left += steps.Skip(i + 1).Sum(s => Expected(s.Key));
        var done = took.Values.Sum() + elapsed;
        bar.Value = Math.Clamp(done / (done + left), 0, 1);
        if (rows.TryGetValue(active, out var r)) r.Time.Text = Clock(elapsed);
        times.Text = $"{Clock(total.Elapsed.TotalSeconds)} elapsed  ·  about {Clock(left)} left";
    }

    public void Finish(bool ok)
    {
        tick.Stop();
        if (active != null) took[active] = current.Elapsed.TotalSeconds;
        if (!ok) return;
        settings.StepSeconds ??= [];
        foreach (var (key, seconds) in took)
        {
            if (DriverSteps.Any(d => d.Key == key)) settings.StepSeconds[key] = Math.Round(seconds, 1);
        }
        settings.Save();
    }

    public double Elapsed => total.Elapsed.TotalSeconds;

    public static string Clock(double seconds) =>
        seconds >= 3600 ? $"{(int)seconds / 3600}:{(int)seconds % 3600 / 60:00}:{(int)seconds % 60:00}" : $"{(int)seconds / 60}:{(int)seconds % 60:00}";
}
