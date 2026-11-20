using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace BifrostStudio;

public sealed class GameReplayView : DockPanel
{
    const string PauseIcon = "M14,19H18V5H14M6,19H10V5H6V19Z";
    const string SlowerIcon = "M11.5,12L20,18V6M11,18V6L2.5,12L11,18Z";
    const string FasterIcon = "M13,6V18L21.5,12M4,18L12.5,12L4,6V18Z";
    const string BoxesIcon = "M2,2H8V4H16V2H22V8H20V16H22V22H16V20H8V22H2V16H4V8H2V2M16,8V6H8V8H6V16H8V18H16V16H18V8H16M4,4V6H6V4H4M18,4V6H20V4H18M4,18V20H6V18H4M18,18V20H20V18H18Z";
    const string CameraIcon = "M17,10.5V7A1,1 0 0,0 16,6H4A1,1 0 0,0 3,7V17A1,1 0 0,0 4,18H16A1,1 0 0,0 17,17V13.5L21,17.5V6.5L17,10.5Z";
    public const int LeadMs = 3000;

    readonly ReplayView view;
    readonly Image picture = new() { Stretch = Stretch.Uniform };
    readonly Border screen;
    readonly TextBlock status = new()
    {
        FontSize = 15, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 560,
    };
    readonly Border cover;
    readonly Button again;
    readonly Button playButton, boxesButton;
    readonly TextBlock clock = new() { FontSize = 13, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center };
    readonly ViewTimeline timeline;
    readonly Stopwatch since = Stopwatch.StartNew();
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    WriteableBitmap? bitmap;
    int seekAtStart;
    bool closed;
    (int W, int H) asked;

    public ReplayViewFrame? Last { get; private set; }
    public int Shown { get; private set; }
    public string State => view.State;
    public string? Error => view.Error;
    public bool Boxes { get; private set; }
    public ReplayView Player => view;

    public GameReplayView(ReplayView view, IReadOnlyList<(int Player, int Ms)> kos, int atMs)
    {
        this.view = view;
        seekAtStart = atMs;
        Focusable = true;
        again = Look.Secondary("Watch again", Look.Refresh, () =>
        {
            view.Restart();
            ShowCover("Starting the replay again…");
        });
        again.HorizontalAlignment = HorizontalAlignment.Center;
        again.IsVisible = false;
        progress.HorizontalAlignment = HorizontalAlignment.Center;
        cover = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 10, 12, 20)),
            Child = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center, Children = { status, progress, again } },
        };
        screen = new Border
        {
            Background = Brushes.Black, CornerRadius = new CornerRadius(12), ClipToBounds = true, Focusable = true,
            Child = new Grid { Children = { picture, cover } },
        };
        timeline = new ViewTimeline(kos) { Margin = new Thickness(0, 8, 0, 0) };
        timeline.Seek += ms => view.Seek(ms);
        playButton = Look.Secondary("Pause", PauseIcon, TogglePause);
        boxesButton = Look.Secondary("Show", BoxesIcon, () => { });
        boxesButton.Flyout = new Flyout { Content = ShowMenu(), Placement = Avalonia.Controls.PlacementMode.Top };
        ToolTip.SetTip(boxesButton, "What the game draws over the match. H turns hitboxes on or off.");
        var camera = Look.Secondary("Camera", CameraIcon, () => PressKey(86));
        ToolTip.SetTip(camera, "Switch the game's camera. Key: V");
        this.kos = kos;
        var previousKo = Look.Secondary("", Look.Left, () => JumpKo(-1));
        ToolTip.SetTip(previousKo, "Previous KO");
        var nextKo = Look.Secondary("", Look.Right, () => JumpKo(1));
        ToolTip.SetTip(nextKo, "Next KO");
        var slower = Look.Secondary("", SlowerIcon, () => StepSpeed(-1));
        ToolTip.SetTip(slower, "Slow down. Past pause it rewinds, down to -5x.");
        var faster = Look.Secondary("", FasterIcon, () => StepSpeed(1));
        ToolTip.SetTip(faster, "Speed up, to 5x at most.");
        var hint = new TextBlock
        {
            Text = "Click the picture to use the game's keys. C pauses, [ and ] skip back and forward, 1 changes speed and V changes the camera.",
            FontSize = 12, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        };
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0),
            Children = { playButton, slower, faster, previousKo, nextKo, boxesButton, camera, Look.Secondary("Restart", Look.Refresh, () => view.Seek(0)), clock },
        };
        var bottom = new StackPanel { Children = { timeline, bar, hint } };
        SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);
        Children.Add(screen);
        ShowCover(StartingText());
        progress.Set("Starting the game", 0);
        foreach (var (name, _) in Options) view.SetOption(name, Chosen(name));
        view.Frame += f =>
        {
            if (Interlocked.Exchange(ref pending, f) is { } dropped) view.Recycle(dropped.Pixels);
            else Dispatcher.UIThread.Post(ShowPending, DispatcherPriority.Render);
        };
        view.Note += n => Dispatcher.UIThread.Post(() => OnNote(n));
        view.Stopped += why => Dispatcher.UIThread.Post(() =>
        {
            if (closed) return;
            ShowCover("The replay player stopped: " + why);
        });
        screen.PointerMoved += (_, e) => Pointer("move", e, 0);
        screen.PointerPressed += (_, e) =>
        {
            screen.Focus();
            Pointer("down", e, e.GetCurrentPoint(screen).Properties.IsRightButtonPressed ? 1 : e.GetCurrentPoint(screen).Properties.IsMiddleButtonPressed ? 2 : 0);
            e.Handled = true;
        };
        screen.PointerReleased += (_, e) =>
        {
            Pointer("up", e, e.InitialPressMouseButton == MouseButton.Right ? 1 : e.InitialPressMouseButton == MouseButton.Middle ? 2 : 0);
            e.Handled = true;
        };
        screen.PointerWheelChanged += (_, e) =>
        {
            if (StagePoint(e.GetPosition(screen)) is { } p) view.Wheel(e.Delta.Y, p.X, p.Y);
            e.Handled = true;
        };
        screen.KeyDown += (_, e) => ForwardKey(e, true);
        screen.KeyUp += (_, e) => ForwardKey(e, false);
        screen.SizeChanged += (_, _) => AskSize();
        ticker.Tick += (_, _) => Tick();
        ticker.Start();
        DetachedFromVisualTree += (_, _) => Close();
    }

    void Tick()
    {
        if (Last == null && view.State == "starting")
            progress.Set(menuAt == null ? "Starting the game" : "Loading the replay",
                menuAt == null ? StageBar.Starting(since.Elapsed) : 0.92 + Math.Min(0.07, (since.Elapsed - menuAt.Value).TotalSeconds * 0.02));
    }

    string StartingText() => view.Game is { } old
        ? $"Starting Brawlhalla {old}, the version that made this replay."
        : "Starting the replay player.";

    public void Close()
    {
        if (closed) return;
        closed = true;
        ticker.Stop();
        view.Dispose();
    }

    void ShowCover(string text)
    {
        status.Text = text;
        cover.IsVisible = true;
        again.IsVisible = false;
        progress.IsVisible = Last == null && view.State == "starting";
    }

    readonly StageBar progress = new(280);
    TimeSpan? menuAt;
    readonly IReadOnlyList<(int Player, int Ms)> kos;

    static readonly (string Key, string Label)[] Options =
    [
        ("names", "Full names"), ("damage", "Damage numbers"), ("hitboxes", "Hitboxes"), ("hurtboxes", "Hurtboxes"),
        ("stun", "Stun time"), ("impulse", "Impulse"), ("spawns", "Spawn points"),
    ];

    static string OptionsFile => BifrostFolders.StudioFile("replay-view.json");

    static bool Chosen(string key)
    {
        try { return JsonFiles.Read(OptionsFile)?[key]?.GetValue<bool>() == true; }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return false; }
    }

    static void Choose(string key, bool on)
    {
        var all = JsonFiles.Read(OptionsFile) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        all[key] = on;
        try { JsonFiles.Write(OptionsFile, all); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    readonly Dictionary<string, CheckBox> optionBoxes = [];

    Control ShowMenu()
    {
        var stack = new StackPanel { Spacing = 2, Width = 200 };
        foreach (var (key, label) in Options)
        {
            var box = new CheckBox { Content = label, IsChecked = Chosen(key) };
            box.IsCheckedChanged += (_, _) =>
            {
                var on = box.IsChecked == true;
                Choose(key, on);
                view.SetOption(key, on);
            };
            optionBoxes[key] = box;
            stack.Children.Add(box);
        }
        return stack;
    }

    void PressKey(int code)
    {
        view.Key(true, code);
        _ = Task.Delay(60).ContinueWith(_ => view.Key(false, code));
    }

    void JumpKo(int direction)
    {
        if (Last is not { } f || kos.Count == 0) return;
        var now = f.NowMs;
        var times = kos.Select(k => k.Ms).Distinct().Order().ToList();
        int? target = direction < 0 ? times.LastOrDefault(t => t < now - LeadMs - 1500, -1) : times.FirstOrDefault(t => t > now + 500, -1);
        if (target is { } t && t >= 0) view.Seek(Math.Max(0, t - LeadMs));
    }

    void OnNote(JsonElement n)
    {
        if (closed) return;
        var state = n.TryGetProperty("state", out var s) ? s.GetString() : null;
        if (state == "starting" && menuAt == null && n.TryGetProperty("screen", out var screenName) && screenName.GetString() == "ScreenMainMenu")
        {
            menuAt = since.Elapsed;
            ReplayBoot.Remember("view", since.Elapsed);
        }
        if (state == "playing")
        {
            progress.Set("Playing", 1);
            if (seekAtStart > 0) view.Seek(Math.Max(0, seekAtStart - LeadMs));
            seekAtStart = 0;
            if (Boxes) view.SetBoxes(true);
        }
        else if (state == "ended")
        {
            status.Text = "The replay is over.";
            cover.IsVisible = true;
            again.IsVisible = true;
        }
        else if (state == "failed")
        {
            ShowCover("The replay couldn't be played: " + (n.TryGetProperty("error", out var e) ? e.GetString() : "no reason given"));
        }
    }

    ReplayViewFrame? pending;

    void ShowPending()
    {
        if (Interlocked.Exchange(ref pending, null) is { } f) Show(f);
    }

    void Show(ReplayViewFrame f)
    {
        if (closed) return;
        try
        {
            if (bitmap == null || bitmap.PixelSize.Width != f.Width || bitmap.PixelSize.Height != f.Height)
            {
                bitmap?.Dispose();
                bitmap = new WriteableBitmap(new PixelSize(f.Width, f.Height), new Vector(96, 96), f.Bgra ? PixelFormat.Bgra8888 : PixelFormat.Rgba8888, AlphaFormat.Opaque);
            }
            using (var fb = bitmap.Lock())
            {
                var row = f.Width * 4;
                if (fb.RowBytes == row) Marshal.Copy(f.Pixels, 0, fb.Address, row * f.Height);
                else
                    for (var y = 0; y < f.Height; y++) Marshal.Copy(f.Pixels, y * row, fb.Address + y * fb.RowBytes, row);
            }
            if (picture.Source != bitmap) picture.Source = bitmap;
            else picture.InvalidateVisual();
            Last = f;
            Shown++;
            if (!f.Ended && cover.IsVisible && view.State == "playing") cover.IsVisible = false;
            timeline.Update(f.NowMs, f.LengthMs);
            clock.Text = ReplaysPage.Clock(Seconds(Math.Min(f.NowMs, f.LengthMs))) + " / " + ReplaysPage.Clock(Seconds(f.LengthMs)) + "   " +
                         (f.Speed >= 0 && f.Speed < ReplayView.SpeedNames.Length ? ReplayView.SpeedNames[f.Speed] : "");
            Look.SetContent(playButton, f.Speed == ReplayView.Paused ? "Play" : "Pause", f.Speed == ReplayView.Paused ? Look.Play : PauseIcon);
            if (Shown == 1) AskSize();
        }
        finally
        {
            view.Recycle(f.Pixels);
        }
    }

    static int Seconds(int ms) => (int)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero) * 1000;

    void AskSize()
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var w = (int)Math.Round(screen.Bounds.Width * scale);
        var h = (int)Math.Round(screen.Bounds.Height * scale);
        if (w < 160 || h < 90) return;
        var fit = Math.Min(w / 16.0, h / 9.0);
        var size = ((int)(fit * 16), (int)(fit * 9));
        if (size == asked) return;
        asked = size;
        view.Size(size.Item1, size.Item2);
    }

    public Point? StagePoint(Point at)
    {
        if (Last is not { } f || f.StageWidth <= 0 || f.StageHeight <= 0) return null;
        var box = screen.Bounds.Size;
        var k = Math.Min(box.Width / f.Width, box.Height / f.Height);
        var w = f.Width * k;
        var h = f.Height * k;
        var x = (at.X - (box.Width - w) / 2) / w;
        var y = (at.Y - (box.Height - h) / 2) / h;
        if (x < 0 || y < 0 || x > 1 || y > 1) return null;
        return new Point(x * f.StageWidth, y * f.StageHeight);
    }

    void Pointer(string kind, PointerEventArgs e, int button)
    {
        if (StagePoint(e.GetPosition(screen)) is { } p) view.Mouse(kind, button, p.X, p.Y);
    }

    void ForwardKey(KeyEventArgs e, bool down)
    {
        if (e.Key == Key.H && !down)
        {
            ToggleBoxes();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.H) return;
        if (KeyCode(e.Key) is { } code)
        {
            view.Key(down, code);
            e.Handled = true;
        }
    }

    public static int? KeyCode(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 65 + (key - Key.A),
        >= Key.D0 and <= Key.D9 => 48 + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 96 + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F12 => 112 + (key - Key.F1),
        Key.Space => 32, Key.Enter => 13, Key.Escape => 27, Key.Tab => 9, Key.Back => 8, Key.Delete => 46,
        Key.Left => 37, Key.Up => 38, Key.Right => 39, Key.Down => 40,
        Key.LeftShift or Key.RightShift => 16, Key.LeftCtrl or Key.RightCtrl => 17, Key.LeftAlt or Key.RightAlt => 18,
        Key.OemOpenBrackets => 219, Key.OemCloseBrackets => 221, Key.OemComma => 188, Key.OemPeriod => 190, Key.OemMinus => 189,
        Key.OemPlus => 187, Key.OemQuestion => 191, Key.OemSemicolon => 186, Key.OemQuotes => 222, Key.OemPipe => 220, Key.OemTilde => 192,
        _ => null,
    };

    public void TogglePause() => view.SetSpeed(Last?.Speed == ReplayView.Paused ? ReplayView.Normal : ReplayView.Paused);

    public void StepSpeed(int by)
    {
        var now = Last is { Speed: >= 0 } f ? f.Speed : ReplayView.Normal;
        var next = Math.Clamp(now + by, 0, ReplayView.SpeedNames.Length - 1);
        if (next == ReplayView.Paused) next += by;
        view.SetSpeed(Math.Clamp(next, 0, ReplayView.SpeedNames.Length - 1));
    }

    public void ToggleBoxes()
    {
        Boxes = !Boxes;
        view.SetBoxes(Boxes);
        foreach (var key in new[] { "hitboxes", "hurtboxes" })
            if (optionBoxes.TryGetValue(key, out var box)) box.IsChecked = Boxes;
    }
}

public sealed class ViewTimeline : Control
{
    readonly IReadOnlyList<(int Player, int Ms)> kos;
    int now, length;
    bool dragging;

    public event Action<int>? Seek;

    public ViewTimeline(IReadOnlyList<(int Player, int Ms)> kos)
    {
        this.kos = kos;
        Height = 26;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public void Update(int nowMs, int lengthMs)
    {
        if (nowMs == now && lengthMs == length) return;
        now = nowMs;
        length = lengthMs;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        if (w < 10) return;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var y = 13.0;
        context.DrawRectangle(Look.Brush(Look.Raised), null, new Rect(0, y - 3, w, 6), 3, 3);
        var k = length > 0 ? Math.Clamp(now / (double)length, 0, 1) : 0;
        context.DrawRectangle(Look.Brush(Look.Accent), null, new Rect(0, y - 3, k * w, 6), 3, 3);
        if (length > 0)
            foreach (var ko in kos)
            {
                var x = Math.Clamp(ko.Ms / (double)length, 0, 1) * w;
                var color = ReplaysPage.PlayerColours[Math.Max(0, ko.Player) % ReplaysPage.PlayerColours.Length];
                context.DrawRectangle(new SolidColorBrush(color), null, new Rect(x - 1.5, 2, 3, 22), 1, 1);
            }
        context.DrawEllipse(Look.Brush(Look.Text), null, new Point(k * w, y), 6, 6);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        dragging = true;
        e.Pointer.Capture(this);
        SeekTo(e.GetPosition(this).X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragging) SeekTo(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragging = false;
        e.Pointer.Capture(null);
    }

    void SeekTo(double x)
    {
        if (length <= 0) return;
        Seek?.Invoke((int)(Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * length));
    }
}
