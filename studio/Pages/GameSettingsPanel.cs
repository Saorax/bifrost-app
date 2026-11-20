using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace BifrostStudio;

public sealed record SettingsActions(Action Close, Action Repair, Func<string, Task<string?>> MoveEngine, Func<bool> Busy,
    Func<Task<string?>>? SwitchPlace = null, Action? CleanRebuild = null, Func<bool>? CanCleanRebuild = null,
    Action<bool>? UseOriginal = null, Action? ViewLogs = null);

public sealed class GameSettingsPanel : UserControl
{
    static readonly (string Key, string Title, string Icon)[] Sections =
    [
        ("graphics", "Graphics", Look.Monitor), ("launch", "Launch options", Look.Tune), ("visual", "Visual", Look.Eye),
        ("replays", "Replays", Look.Play), ("server", "Server", Look.Server), ("folders", "Folders", Look.Folder),
        ("advanced", "Advanced", Look.Wrench),
    ];

    readonly GameSettings settings;
    readonly SettingsActions actions;
    readonly Dictionary<string, Button> tabs = [];
    readonly StackPanel body = new() { Spacing = 2, Margin = new Thickness(0, 0, 14, 0) };
    readonly ScrollViewer scroll;
    readonly TextBlock heading = new() { FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text) };
    readonly TextBlock banner = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Look.Brush(Look.Text) };
    readonly Border bannerBox;
    readonly TextBlock note = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Look.Brush(Look.Dim) };
    string section = "graphics";

    public GameSettingsPanel(GameSettings settings, SettingsActions actions)
    {
        this.settings = settings;
        this.actions = actions;
        Focusable = true;
        var list = new StackPanel { Spacing = 4 };
        foreach (var (key, title, icon) in Sections)
        {
            tabs[key] = Look.Nav(title, icon, () => Show(key));
            list.Children.Add(tabs[key]);
        }
        var side = new Border
        {
            Background = Look.Brush(Look.Side), Padding = new Thickness(14, 22, 14, 14), CornerRadius = new CornerRadius(18, 0, 0, 18),
            Child = new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = "GAME SETTINGS", FontSize = 11, LetterSpacing = 1.5, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Dim), Margin = new Thickness(6, 0) },
                    list,
                },
            },
        };
        var close = new Button
        {
            Classes = { "link" }, Background = Brushes.Transparent, Padding = new Thickness(8), CornerRadius = new CornerRadius(8),
            Content = Look.Icon(Look.Close, 18, Look.Dim), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        };
        close.Click += (_, _) => actions.Close();
        ToolTip.SetTip(close, "Close (Esc)");
        bannerBox = new Border
        {
            Background = Look.Brush(Look.Raised), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = banner,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { heading, close } };
        Grid.SetColumn(close, 1);
        scroll = new ScrollViewer { Content = body, Margin = new Thickness(0, 12, 0, 0) };
        var main = new DockPanel { Margin = new Thickness(26, 20, 12, 18) };
        var top = new StackPanel { Children = { head, bannerBox } };
        note.Margin = new Thickness(0, 10, 14, 0);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Bottom);
        main.Children.Add(top);
        main.Children.Add(note);
        main.Children.Add(scroll);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("210,*"), Children = { side, main } };
        Grid.SetColumn(main, 1);
        var card = new Border
        {
            Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(18), BorderBrush = Look.Brush(Look.Line),
            BorderThickness = new Thickness(1), MaxWidth = 900, MaxHeight = 600, Margin = new Thickness(28),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 40, Color = Color.FromArgb(150, 0, 0, 0), OffsetY = 10 }),
            Child = grid,
        };
        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(170, 5, 6, 14)), CornerRadius = new CornerRadius(20) };
        backdrop.PointerPressed += (_, e) =>
        {
            actions.Close();
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            actions.Close();
            e.Handled = true;
        };
        Content = new Panel { Children = { backdrop, card } };
    }

    public void Open()
    {
        note.Text = "";
        Show(section);
        Focus();
    }

    public void Refresh()
    {
        if (!IsVisible) return;
        Banner();
    }

    void Banner()
    {
        var waiting = settings.ModFile is { } f && settings.State.Writes.Waiting(f);
        banner.Text = settings.State.Paths == null ?"Brawlhalla wasn't found. Show the app where it is first."
            : !settings.HasEngine ? "These settings need Bifrost. Install it first."
            : !settings.HasDocs ? "Start Brawlhalla with Bifrost once to make its settings file. You can set Graphics now."
            : settings.State.GameRunning ? "Brawlhalla is running. Changes here are saved when it closes and apply the next time you start it."
            : waiting ? "Some changes are still waiting to be saved."
            : "";
        bannerBox.IsVisible = banner.Text.Length > 0;
    }

    void Show(string key)
    {
        section = key;
        foreach (var (k, b) in tabs) b.Classes.Set("selected", k == key);
        heading.Text = Sections.First(s => s.Key == key).Title;
        body.Children.Clear();
        switch (key)
        {
            case "graphics": Graphics(); break;
            case "launch": Launch(); break;
            case "visual": Visual(); break;
            case "replays": Replays(); break;
            case "server": Server(); break;
            case "folders": Folders(); break;
            default: Advanced(); break;
        }
        Banner();
        scroll.Offset = default;
    }

    void Graphics()
    {
        var engine = settings.Engine();
        var screens = Math.Max(TopLevel.GetTopLevel(this)?.Screens?.ScreenCount ?? 1, engine.Monitor + 1);
        var monitors = Enumerable.Range(-1, screens + 1).ToArray();
        Group("Engine", "Read when the game starts.");
        Row("Renderer", "DirectX 11 works everywhere. DirectX 12 and Vulkan start DirectX 11 instead if this PC can't run them.",
            Choice(EngineOptions.Renderers, engine.Renderer, RendererName, v => SaveEngine(settings.Engine() with { Renderer = v })));
        Row("Display mode", "The game's own setting uses what you chose in the game's Options window.",
            Choice(EngineOptions.DisplayModes, engine.DisplayMode, ModeName, v => SaveEngine(settings.Engine() with { DisplayMode = v })));
        Row("VSync", "Waits for the screen's refresh. Off can tear but shows frames sooner.",
            Toggle(engine.Vsync, v => SaveEngine(settings.Engine() with { Vsync = v })));
        Row("Monitor", "The screen the game opens on.",
            Choice(monitors, engine.Monitor, m => m < 0 ? "Automatic" : $"Monitor {m + 1}", v => SaveEngine(settings.Engine() with { Monitor = v })));
        Group("Frame rate", null);
        var rate = settings.FrameRate();
        var rates = GameSettings.FrameRates;
        Row("Frame rate", "Unlocked draws as fast as your PC can, but the game still runs at 60 steps a second. Drag the slider or click the number to type one from 25 to 1000.",
            SliderBox(rates.Length - 1, 1, rate, "fps",
                v => rates[Math.Clamp((int)Math.Round(v), 0, rates.Length - 1)],
                t => int.TryParse(t.Trim().TrimEnd('s', 'p', 'f', ' '), out var n) ? GameSettings.ClampRate(n) : null,
                RateName, v => RateIndex(v), v => Saved(settings.SetFrameRate(v))));
        Row("Frame pacing", "Smooths movement between the game's 16 ms steps at any refresh rate, but can add up to 16 ms of delay. F11 turns it on or off.",
            Toggle(settings.FramePacing(), v => Saved(settings.SetFramePacing(v))));
    }

    void Launch()
    {
        foreach (var t in GameSettings.Toggles)
            Row(t.Name, t.Help, Toggle(settings.LaunchFlag(t.Flag), v => Saved(settings.SetLaunchFlag(t.Flag, v))));
        Row("Spectate delay", "Delay when spectating. Off uses the game's own setting.",
            Choice(GameSettings.SpectateDelays, settings.SpectateDelay(), DelayName, v => Saved(settings.SetSpectateDelay(v))));
    }

    void Visual()
    {
        var theme = settings.MenuTheme();
        var reset = Look.Secondary("Use the game's default", Look.Refresh, () =>
        {
            Saved(settings.ResetMenuTheme());
            Show("visual");
        });
        reset.IsVisible = theme != null;
        Row("Menu theme", (theme == null ? "The game's default." : $"Chosen in the game: {theme}.") + " Pick themes in the game's Options window.", reset);
        Row("Screen shake", "How much the camera shakes on big hits and some powers. 100% is normal, 0% turns it off, and only your screen changes.",
            PercentSlider(settings.ScreenShake(), v => Saved(settings.SetScreenShake(v))));
        Row("KO effects", "How strong the KO burst at the screen edge and the effect on the legend look. 100% is normal and 0% hides them.",
            PercentSlider(settings.KoEffects(), v => Saved(settings.SetKoEffects(v))));
        Row("Parent skin effects", "On gives Crossovers, Ascended skins and some weapon skins the base legend's and weapon's attack effects and sounds. The skins still look the same, and you pick which to keep in the game's Options window.",
            Toggle(settings.ClassicEffects(), v => Saved(settings.SetClassicEffects(v))));
        Row("Steady blinks", "Legends blink white after respawning or while they can't be hit. On shows an even tint instead, without the flicker.",
            Toggle(settings.SteadyBlinks(), v => Saved(settings.SetSteadyBlinks(v))));
    }

    void Replays()
    {
        Row("Keep recent", "How many of your newest replays stay in the Replays list. Older ones move to the Older folder and aren't deleted, and Favorites stay put.",
            Choice(GameSettings.KeepChoices, settings.KeepRecent(), n => n <= 0 ? "All" : n + " newest", v => Saved(settings.SetKeepRecent(v))));
        Row("Video frame rate", "Frames per second when a replay is exported as a video.",
            Choice(GameSettings.ExportRates, settings.ExportFps(), n => n + " fps", v => Saved(settings.SetExportFps(v))));
        var quality = settings.ExportQuality();
        var qualities = GameSettings.ExportQualities.Contains(quality) ? GameSettings.ExportQualities : GameSettings.ExportQualities.Append(quality).Order().ToArray();
        Row("Video quality", "Higher looks better and makes bigger files.",
            Choice(qualities, quality, n => n + "%", v => Saved(settings.SetExportQuality(v))));
    }

    void Server()
    {
        var current = settings.Current();
        var mainServer = settings.Main();
        Group("Playing on", current == null ? "No server is set, so the game uses its release's main server." : current.Address);
        var entries = new List<ServerEntry>();
        if (mainServer != null) entries.Add(mainServer);
        entries.AddRange(settings.Servers().Where(s => mainServer == null || !s.Same(mainServer.Host, mainServer.Port)));
        if (entries.Count == 0) body.Children.Add(Dim("No saved servers yet. Add one below, or in the game's Options window."));
        foreach (var s in entries)
        {
            var isCurrent = current != null && s.Same(current.Host, current.Port);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (isCurrent) buttons.Children.Add(new TextBlock { Text = "Current", Foreground = Look.Brush(Look.Good), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            else
            {
                var use = Look.Secondary("Use", Look.Check, () =>
                {
                    var why = settings.UseServer(s.Host, s.Port);
                    note.Text = why ?? $"The game plays on {s.Address} from its next start. If you signed in there before, that sign-in is used.";
                    if (why == null) Show("server");
                });
                use.IsEnabled = !settings.State.GameRunning && settings.HasDocs;
                buttons.Children.Add(use);
            }
            if (s != mainServer)
            {
                buttons.Children.Add(Look.Secondary("Remove", Look.Trash, () =>
                {
                    Saved(settings.RemoveServer(s));
                    Show("server");
                }));
            }
            Row(s.Name, s.Address, buttons);
        }
        if (settings.State.GameRunning && entries.Count > 0) body.Children.Add(Dim("Close Brawlhalla to switch servers."));
        Group("Add a server", null);
        var name = new TextBox { PlaceholderText = "Name (optional)", Width = 170 };
        var address = new TextBox { PlaceholderText = "address:port", Width = 210 };
        var add = Look.Secondary("Add", Look.Plus, () =>
        {
            var why = settings.AddServer(name.Text ?? "", address.Text ?? "");
            if (why != null)
            {
                note.Text = why;
                return;
            }
            Show("server");
            note.Text = settings.State.GameRunning ? "Added. It shows in the list after Brawlhalla closes." : "Added.";
        });
        add.IsEnabled = settings.HasDocs;
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Children = { name, address, add } });
        body.Children.Add(Dim("Official Brawlhalla addresses won't work. Bifrost only plays on Bifrost servers."));
    }

    void Advanced()
    {
        var game = settings.State.Game;
        var installed = game != null && EngineKit.Installed(game) != null;
        var free = !settings.State.GameRunning && !actions.Busy();
        if (actions.SwitchPlace is { } switchPlace && game != null)
        {
            var onSteam = InstallPlace.Of(game) == InstallPlace.Steam;
            var other = onSteam ? InstallPlace.Own : InstallPlace.Steam;
            var switchButton = Look.Secondary(onSteam ? "Use its own folder" : "Use Steam", onSteam ? Look.Folder : Look.Play, () => _ = SwitchTo(switchPlace));
            switchButton.IsEnabled = !settings.State.GameRunning && !actions.Busy() && (onSteam || GameFinder.SteamInstall() != null);
            Row("Installed", InstallPlace.Describe(onSteam ? InstallPlace.Steam : InstallPlace.Own)
                + (other == InstallPlace.Steam ? " Use Steam moves it to Steam's folder" : " Use its own folder moves it to a copy of the game")
                + " and builds Bifrost again in a few minutes. Saves and settings come along.", switchButton);
        }
        if (actions.ViewLogs is { } viewLogs)
        {
            var logs = Look.Secondary("View logs", Look.Logs, () =>
            {
                actions.Close();
                viewLogs();
            });
            Row("Logs", "Each run of the game, its errors and the app's install log.", logs);
        }
        var caches = settings.ShaderCaches();
        var size = caches.Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } });
        var clear = Look.Secondary("Clear", Look.Trash, () =>
        {
            var (removed, problem) = settings.ClearShaderCache();
            note.Text = problem ?? (removed == 0 ? "There was nothing to clear." : "Cleared. The next start builds the shaders again, so it takes a little longer.");
            Show("advanced");
        });
        clear.IsEnabled = caches.Count > 0 && !settings.State.GameRunning;
        Row("Shader cache", caches.Count == 0 ? "Empty." : $"{Size(size)} in {caches.Count} file{(caches.Count == 1 ? "" : "s")}. Clear it if the game shows broken graphics after a driver update.", clear);
        var repair = Look.Secondary("Repair", Look.Wrench, () =>
        {
            actions.Close();
            actions.Repair();
        });
        repair.IsEnabled = installed && free;
        Row("Repair", "Builds Bifrost again from the downloaded release. Saves and settings are kept.", repair);
        if (actions.CleanRebuild is { } cleanRebuild)
        {
            var clean = Look.Secondary("Clean rebuild", Look.Refresh, () =>
            {
                actions.Close();
                cleanRebuild();
            });
            clean.IsEnabled = game != null && free && (actions.CanCleanRebuild?.Invoke() ?? true);
            Row("Clean rebuild", "Downloads and builds everything again from scratch. "
                + "Try it if Repair didn't help. Saves and settings are kept.", clean);
        }
        if (actions.UseOriginal is { } useOriginal && game != null && EngineKit.EngineInstalled(game))
        {
            var isOriginal = EngineKit.Original(game);
            var swap = Look.Secondary(isOriginal ? "Use Bifrost" : "Use original game", isOriginal ? Look.Play : Look.Folder, () =>
            {
                actions.Close();
                useOriginal(!isOriginal);
            });
            swap.IsEnabled = free;
            Row("Original game", isOriginal
                ? "Brawlhalla.exe is Steam's own right now, so Play starts the official game. Use Bifrost switches back in seconds."
                : "Puts Steam's own Brawlhalla.exe back without removing Bifrost, so you can play the official game. Switching back takes seconds.", swap);
        }
    }

    void Folders()
    {
        var game = settings.State.Game;
        var free = !settings.State.GameRunning && !actions.Busy();
        var openGame = Look.Secondary("Open", Look.Folder, () => Ui.Open(game, missing: "Brawlhalla wasn't found. Pick its folder in Settings."));
        openGame.IsEnabled = game != null && Directory.Exists(game);
        Row("Game", game ?? "Brawlhalla wasn't found.", openGame);
        var engineDir = settings.State.Paths?.Engine;
        var move = Look.Secondary("Move", Look.Folder, () => _ = MoveEngine());
        move.IsEnabled = game != null && EngineKit.Installed(game) != null && free;
        var openEngine = Look.Secondary("Open", Look.Folder, () => Ui.Open(engineDir, missing: "Bifrost isn't installed yet, so there's no engine folder."));
        openEngine.IsEnabled = engineDir != null && Directory.Exists(engineDir);
        Row("Engine", (engineDir ?? "Not installed") + ". Moving it keeps your saves and settings.", Buttons(openEngine, move));
        var shown = new List<(TextBlock Help, BifrostPlace Place)>();
        foreach (var place in BifrostPlaces.For(settings.State))
        {
            var help = Dim($"{place.Help}\n{place.Path}");
            var open = Look.Secondary("Open", Look.Folder, () =>
            {
                if (BifrostPlaces.Open(place.Path) is { } why) note.Text = why;
            });
            Row(place.Name, help, open);
            shown.Add((help, place));
        }
        var clean = Look.Secondary("Clean up", Look.Trash, () =>
        {
            var (files, bytes) = BifrostPlaces.CleanUp(settings.State.Paths?.Engine, DateTime.Now);
            note.Text = files == 0 ? "There was nothing to clean up." : $"Removed {files} file{(files == 1 ? "" : "s")} ({Size(bytes)}).";
            Show("folders");
        });
        clean.IsEnabled = free;
        Row("Clean up", $"Deletes leftover downloads and logs older than {BifrostPlaces.LogsKept.Days} days. Saves, settings, replays and mods stay.", clean);
        _ = FillSizes(shown);
    }

    static async Task FillSizes(List<(TextBlock Help, BifrostPlace Place)> shown)
    {
        var sizes = await Task.Run(() => shown.Select(s => Directory.Exists(s.Place.Path) ? BifrostPlaces.Size(s.Place.Path) : -1).ToList());
        for (var i = 0; i < shown.Count; i++)
        {
            var (help, place) = shown[i];
            help.Text = $"{place.Help}\n{place.Path} · {(sizes[i] < 0 ? "not made yet" : sizes[i] == 0 ? "empty" : Size(sizes[i]))}";
        }
    }

    async Task SwitchTo(Func<Task<string?>> switchPlace)
    {
        note.Text = "Switching where Bifrost is installed...";
        actions.Close();
        var why = await switchPlace();
        note.Text = why ?? "Switched.";
        Show("advanced");
    }

    async Task MoveEngine()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Pick a new or empty folder for the engine" });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
        var target = Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() ? Path.Combine(folder, "bifrost-engine") : folder;
        note.Text = $"Moving the engine to {target}...";
        var why = await actions.MoveEngine(target);
        note.Text = why ?? $"Moved the engine to {target}.";
        Show("folders");
    }

    void SaveEngine(EngineOptions options)
    {
        var why = settings.SetEngine(options);
        note.Text = why ?? (settings.State.GameRunning ? "Saved. Applies the next time you start the game." : "Saved.");
    }

    void Saved(bool? now)
    {
        note.Text = now switch
        {
            null => "Not saved. Start Brawlhalla with Bifrost once first.",
            true => "Saved.",
            false => "Saved for later. It's written when Brawlhalla closes.",
        };
        Banner();
    }

    void Group(string title, string? sub)
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 18, 0, 4) };
        stack.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, LetterSpacing = 1.5, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Accent) });
        if (sub != null) stack.Children.Add(Dim(sub));
        body.Children.Add(stack);
    }

    void Row(string title, string help, Control input) => Row(title, Dim(help), input);

    void Row(string title, TextBlock help, Control input)
    {
        var words = new StackPanel
        {
            Spacing = 3, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Text) },
                help,
            },
        };
        input.VerticalAlignment = VerticalAlignment.Center;
        input.Margin = new Thickness(18, 0, 0, 0);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { words, input } };
        Grid.SetColumn(input, 1);
        body.Children.Add(new Border
        {
            Padding = new Thickness(0, 12), BorderBrush = Look.Brush(Look.Line), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid,
        });
    }

    static TextBlock Dim(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Look.Brush(Look.Dim) };

    static StackPanel Buttons(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var i in items) row.Children.Add(i);
        return row;
    }

    static ComboBox Choice<T>(IReadOnlyList<T> values, T current, Func<T, string> label, Action<T> pick)
    {
        var box = new ComboBox { MinWidth = 210 };
        foreach (var v in values) box.Items.Add(label(v));
        var at = values.ToList().IndexOf(current);
        box.SelectedIndex = at < 0 ? 0 : at;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0 && box.SelectedIndex < values.Count) pick(values[box.SelectedIndex]);
        };
        return box;
    }

    static Control PercentSlider(int current, Action<int> save) =>
        SliderBox(100, GameSettings.PercentStep, current, "%",
            v => (int)Math.Round(v / GameSettings.PercentStep) * GameSettings.PercentStep,
            t => int.TryParse(t.Trim().TrimEnd('%', ' '), out var n) ? Math.Clamp(n, 0, 100) : null,
            PercentName, v => v, save);

    static string PercentName(int p) => p == 0 ? "Off" : p + "%";

    static int RateIndex(int fps)
    {
        var rates = GameSettings.FrameRates;
        var at = Array.IndexOf(rates, fps);
        if (at >= 0) return at;
        for (var i = 1; i < rates.Length - 1; i++)
            if (rates[i] >= fps) return i;
        return rates.Length - 2;
    }

    public static Control SliderBox(double max, double tick, int current, string unit, Func<double, int> fromSlider,
        Func<string, int?> parse, Func<int, string> name, Func<int, double> toSlider, Action<int> save)
    {
        var slider = new Slider
        {
            Minimum = 0, Maximum = max, TickFrequency = tick, IsSnapToTickEnabled = true, Value = toSlider(current), Width = 170,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var value = new Button
        {
            Classes = { "link" }, Content = name(current), Width = 84, Padding = new Thickness(8, 4), FontWeight = FontWeight.SemiBold,
            Foreground = Look.Brush(Look.Accent), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(value, "Click to type a number");
        var box = new TextBox { Width = 84, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var last = current;
        var moving = false;
        void Show(int v)
        {
            value.Content = name(v);
            moving = true;
            slider.Value = toSlider(v);
            moving = false;
        }
        void Commit(int v)
        {
            Show(v);
            if (v == last) return;
            last = v;
            save(v);
        }
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || moving) return;
            var v = fromSlider(slider.Value);
            value.Content = name(v);
            if (v == last) return;
            last = v;
            save(v);
        };
        void Close(bool keep)
        {
            if (!box.IsVisible) return;
            box.IsVisible = false;
            value.IsVisible = true;
            if (keep && parse(box.Text ?? "") is { } typed) Commit(typed);
        }
        value.Click += (_, _) =>
        {
            box.Text = last > 0 || (last == 0 && unit == "%") ? last.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            value.IsVisible = false;
            box.IsVisible = true;
            box.Focus();
            box.SelectAll();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                Close(e.Key == Key.Enter);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Close(true);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { slider, value, box } };
    }

    static ToggleSwitch Toggle(bool on, Action<bool> set)
    {
        var t = new ToggleSwitch { IsChecked = on, OnContent = "On", OffContent = "Off" };
        t.IsCheckedChanged += (_, _) => set(t.IsChecked == true);
        return t;
    }

    static string RendererName(string r) => r switch { "dx12" => "DirectX 12", "vulkan" => "Vulkan", _ => "DirectX 11" };

    static string ModeName(string m) => m switch
    {
        "windowed" => "Windowed", "borderless" => "Borderless window", "fullscreen" => "Fullscreen", _ => "The game's own setting",
    };

    static string RateName(int fps) => fps < 0 ? "Unlocked" : fps == 0 ? "Default" : fps + " fps";

    static string DelayName(int ms) => ms <= 0 ? "Off" : ms % 1000 == 0 ? ms / 1000 + " s" : (ms / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s";

    static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}