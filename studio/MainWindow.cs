using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace BifrostStudio;

public sealed class MainWindow : Window
{
    static readonly string[] HeroArt = ["BG_Valhalla.jpg", "BG_Brawlhaven.jpg", "BG_Grove.jpg", "BG_Fjord.jpg",
        "BG_MoonPalace.jpg", "BG_Elysium2.jpg", "BG_Temple.jpg", "BG_Celestial_ALL.jpg"];

    readonly Project project;
    readonly EngineKit engine;
    readonly ContentControl page = new();
    readonly Button navHome, navServer, navLogs, navSettings;
    WindowNotificationManager? notes;

    readonly Image artA = Art(), artB = Art();
    readonly Border heroBack = new() { Background = Look.Bridge() };
    readonly TextBlock tag = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
    readonly Border tagPill;
    readonly TextBlock title = new() { FontSize = 38, FontWeight = FontWeight.Black, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock detail = new() { FontSize = 15, Foreground = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
    readonly Button main, options, appUpdate, stop, modeBifrost, modeOfficial;
    readonly InstallProgress progress;
    readonly Control idle;
    readonly TextBlock versionValue = new(), versionSub = new(), gameValue = new(), gameSub = new(), runsValue = new(), runsSub = new();
    readonly Control home;

    readonly LogView log = new();
    string? game;
    Release? latest;
    OfficialRelease? latestOfficial;
    AppRelease? latestApp;
    string? notifiedApp, autoOfficial;
    bool checkedOnline, working, everythingArmed, official;
    CancellationTokenSource? cancel;
    Action mainAction = () => { };
    int art;
    List<string> artFiles = [];

    public MainWindow(Project project)
    {
        this.project = project;
        engine = new EngineKit(project);
        Title = "Bifrost";
        Width = 1120;
        Height = 700;
        MinWidth = 960;
        MinHeight = 620;
        Background = Look.Brush(Look.Back);
        Look.Apply(Styles);
        detail.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) detail.IsVisible = !string.IsNullOrEmpty(detail.Text); };

        main = Look.Primary("Install", Look.Download, () => mainAction());
        options = OptionsButton();
        stop = Look.Secondary("Stop", Look.Stop, () => cancel?.Cancel());
        appUpdate = Look.Secondary("App update available", Look.Download, () => _ = UpdateApp());
        modeBifrost = ModeButton("Bifrost", false);
        modeOfficial = ModeButton("Official servers", true);
        official = project.Settings.Mode != "bifrost";
        appUpdate.IsVisible = false;
        appUpdate.HorizontalAlignment = HorizontalAlignment.Right;
        appUpdate.VerticalAlignment = VerticalAlignment.Top;
        progress = new InstallProgress(project.Settings, stop) { IsVisible = false, VerticalAlignment = VerticalAlignment.Bottom };
        tagPill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Look.Icon(Look.Tag, 12, Colors.White), tag } },
        };
        navHome = Look.Nav("Home", Look.Home, () => Show("home"));
        navServer = Look.Nav("Server", Look.Server, () => Show("server"));
        navLogs = Look.Nav("Logs", Look.Logs, () => Show("logs"));
        navSettings = Look.Nav("Settings", Look.Tune, () => Show("settings"));
        idle = Idle();
        home = Home();

        Grid.SetColumn(page, 1);
        page.Margin = new Thickness(26, 24, 26, 24);
        Content = new Grid { ColumnDefinitions = new ColumnDefinitions("232,*"), Children = { Sidebar(), page } };

        game = GameFinder.Find(project.Settings);
        if (game != null && project.Settings.Mode == null && EngineKit.Installed(game) != null) official = EngineKit.ReadOfficialMode(game);
        PaintModes();
        LoadArt();
        Show("home");
        Refresh();
        AppUpdate.CleanUp();
        Opened += async (_, _) =>
        {
            notes = new WindowNotificationManager(this) { Position = NotificationPosition.TopRight, MaxItems = 2 };
            await CheckOnline();
        };
        new DispatcherTimer(TimeSpan.FromMinutes(30), DispatcherPriority.Background, (_, _) => { if (!working) _ = CheckOnline(); }).Start();
        new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => { if (!working) Refresh(); }).Start();
        new DispatcherTimer(TimeSpan.FromSeconds(14), DispatcherPriority.Background, (_, _) => NextArt()).Start();
    }


    Control Sidebar()
    {
        var word = new TextBlock { Text = "BIFROST", FontSize = 28, FontWeight = FontWeight.Black, LetterSpacing = 4, Foreground = Look.Brush(Look.Accent) };
        var sub = new TextBlock { Text = "for Brawlhalla", FontSize = 12, Foreground = Look.Brush(Look.Dim), Margin = new Thickness(2, 0, 0, 0) };
        var nav = new StackPanel { Spacing = 4, Margin = new Thickness(0, 34, 0, 0), Children = { navHome, navServer, navLogs, navSettings } };
        return new Border
        {
            Background = Look.Brush(Look.Side), Padding = new Thickness(18, 28, 18, 20),
            BorderBrush = Look.Brush(Look.Line), BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new StackPanel { Children = { word, sub, nav } },
        };
    }

    static Image Art() => new()
    {
        Stretch = Stretch.UniformToFill, Opacity = 0,
        Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromSeconds(1.4) } },
    };

    Button OptionsButton()
    {
        var b = new Button
        {
            Classes = { "secondary" }, Background = Look.Brush(Look.Raised), Width = 54, Height = 54,
            CornerRadius = new CornerRadius(27), HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = Look.Icon("M12,16A2,2 0 0,1 14,18A2,2 0 0,1 12,20A2,2 0 0,1 10,18A2,2 0 0,1 12,16M12,10A2,2 0 0,1 14,12A2,2 0 0,1 12,14A2,2 0 0,1 10,12A2,2 0 0,1 12,10M12,4A2,2 0 0,1 14,6A2,2 0 0,1 12,8A2,2 0 0,1 10,6A2,2 0 0,1 12,4Z", 22, Colors.White),
        };
        var repair = new MenuItem { Header = "Repair", Icon = Look.Icon(Look.Wrench, 14, Look.Text) };
        repair.Click += (_, _) => _ = Install(fresh: true);
        var clean = new MenuItem { Header = "Clean rebuild", Icon = Look.Icon(Look.Refresh, 14, Look.Text) };
        clean.Click += (_, _) => _ = CleanRebuild();
        var original = new MenuItem { Header = "Use original game", Icon = Look.Icon(Look.Folder, 14, Look.Text) };
        original.Click += (_, _) => _ = SwapLauncher(original: true);
        var folder = new MenuItem { Header = "Open game folder", Icon = Look.Icon(Look.Folder, 14, Look.Text) };
        folder.Click += (_, _) => Ui.Open(game);
        var logs = new MenuItem { Header = "View logs", Icon = Look.Icon(Look.Logs, 14, Look.Text) };
        logs.Click += (_, _) => Show("logs");
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight, Items = { repair, clean, original, folder, logs } };
        flyout.Opening += (_, _) =>
        {
            repair.IsEnabled = game != null && EngineKit.Installed(game) != null && !working;
            clean.IsEnabled = game != null && !working && (official ? latestOfficial != null : latest != null);
            original.IsEnabled = game != null && !working && EngineKit.Installed(game) != null && !EngineKit.Original(game);
        };
        b.Flyout = flyout;
        return b;
    }

    Button ModeButton(string text, bool isOfficial)
    {
        var b = new Button
        {
            Padding = new Thickness(16, 7), FontSize = 13, FontWeight = FontWeight.SemiBold, CornerRadius = new CornerRadius(16),
            Foreground = Brushes.White, Content = text,
        };
        b.Click += (_, _) => _ = SetMode(isOfficial);
        return b;
    }

    void PaintModes()
    {
        foreach (var (b, on) in new[] { (modeOfficial, official), (modeBifrost, !official) })
        {
            b.Background = on ? Look.Bridge() : new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
            b.Opacity = on ? 1 : 0.8;
        }
    }

    async Task SetMode(bool wantOfficial)
    {
        if (wantOfficial == official || working) return;
        if (wantOfficial && !project.Settings.OfficialAccepted)
        {
            if (!await ConfirmOfficial()) return;
            project.Settings.OfficialAccepted = true;
        }
        official = wantOfficial;
        project.Settings.Mode = official ? "official" : "bifrost";
        project.Settings.Save();
        if (game != null) EngineKit.WriteMode(game, official);
        PaintModes();
        Refresh();
    }

    async Task<bool> ConfirmOfficial()
    {
        var accepted = false;
        var dialog = new Window
        {
            Title = "Official servers", Width = 500, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Look.Brush(Look.Panel),
        };
        var cancelButton = Look.Secondary("Cancel", Look.Stop, () => dialog.Close());
        var ok = Look.Secondary("I understand", Look.Check, () => { accepted = true; dialog.Close(); });
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(26), Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Official servers", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text) },
                new TextBlock
                {
                    Text = "This is a modification of the original Brawlhalla client. This is against Ubisoft Terms of Service, use at "
                           + "your own risk. I am not responsible for your account getting banned. I do not know the chances of you "
                           + "getting banned, as this is a fresh release.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Look.Brush(Look.Text),
                },
                Row(ok, cancelButton),
            },
        };
        await dialog.ShowDialog(this);
        return accepted;
    }

    async Task ShowNotes(Product product)
    {
        var what = product switch { Product.App => "Bifrost app", Product.Official => "Official servers", _ => "Bifrost" };
        var notes = Releases.Notes(product);
        var text = notes == null
            ? Releases.Reached(product) ? $"There is no {what} release yet." : "The latest release couldn't be checked. Try again when you're online."
            : notes.Body.Length > 0 ? Plain(notes.Body) : "This release has no description.";
        var dialog = new Window
        {
            Title = $"What's new: {what}", Width = 640, Height = 520, MinWidth = 420, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Look.Brush(Look.Panel),
        };
        var buttons = Row(Look.Secondary("Close", Look.Stop, () => dialog.Close()));
        if (notes?.Url is { } url) buttons.Children.Insert(0, Look.Secondary("Open on GitHub", Look.Tag, () => Ui.Browse(url)));
        var head = new StackPanel
        {
            Spacing = 4, Margin = new Thickness(0, 0, 0, 14),
            Children =
            {
                new TextBlock { Text = notes?.Title ?? what, FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = notes?.Published is { } at ? at.ToLocalTime().ToString("d MMMM yyyy, HH:mm") : "", FontSize = 13,
                    Foreground = Look.Brush(Look.Dim), IsVisible = notes?.Published != null,
                },
            },
        };
        var body = new ScrollViewer
        {
            Content = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21, Foreground = Look.Brush(Look.Text) },
        };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Thickness(0, 14, 0, 0);
        dialog.Content = new DockPanel { Margin = new Thickness(26), Children = { head, buttons, body } };
        await dialog.ShowDialog(this);
    }

    static string Plain(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var t = line.TrimEnd();
            var trimmed = t.TrimStart();
            if (trimmed.StartsWith('#')) t = trimmed.TrimStart('#').Trim();
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) t = t[..(t.Length - trimmed.Length)] + "•  " + trimmed[2..];
            return t.Replace("**", "").Replace("__", "").Replace("`", "");
        });
        return string.Join("\n", lines).Trim();
    }

    Control Idle()
    {
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6), Children = { modeOfficial, modeBifrost } };
        var words = new StackPanel
        {
            Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left,
            Children = { modes, tagPill, title, detail },
        };
        title.Effect = new DropShadowEffect { BlurRadius = 18, Opacity = 0.55, OffsetX = 0, OffsetY = 2 };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Right, Children = { main, options },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(actions, 1);
        grid.Children.Add(words);
        grid.Children.Add(actions);
        return grid;
    }

    Control Home()
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
            Child = new Panel { Children = { heroBack, artA, artB, shade, inner } },
        };

        var cards = new UniformGrid { Columns = 3, Margin = new Thickness(-8, 0) };
        cards.Children.Add(Pad(Look.Card("Version", Look.Tag, versionValue, versionSub,
            Look.Link("What's new", Look.Logs, () => _ = ShowNotes(official ? Product.Official : Product.Bifrost)))));
        cards.Children.Add(Pad(Look.Card("Brawlhalla", Look.Folder, gameValue, gameSub,
            Look.Link("Open folder", Look.Folder, () => Ui.Open(game)))));
        cards.Children.Add(Pad(Look.Card("Recent runs", Look.Pulse, runsValue, runsSub,
            Look.Link("View logs", Look.Logs, () => Show("logs")))));

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 18 };
        Grid.SetRow(cards, 1);
        grid.Children.Add(hero);
        grid.Children.Add(cards);
        return grid;
    }

    static Control Pad(Control c)
    {
        c.Margin = new Thickness(8, 0);
        return c;
    }

    Control Settings()
    {
        var installed = game == null ? null : EngineKit.Installed(game);
        var have = Have;
        var bifrost = new TextBlock { Text = InstalledText(installed) };
        var bifrostSub = new TextBlock { Text = have == null ? "" : installed == have ? "Up to date" : $"Latest {have}" };
        var remove = Look.Secondary("Remove", Look.Trash, () => _ = Remove());
        remove.IsVisible = installed != null;
        var officialInstalled = game == null ? null : EngineKit.OfficialInstalled(game);
        var officialText = new TextBlock { Text = InstalledText(officialInstalled) };
        var officialSub = new TextBlock
        {
            Text = latestOfficial == null ? "" : officialInstalled == latestOfficial.Name ? "Up to date" : $"Latest {latestOfficial.Name}",
        };
        var removeOfficial = Look.Secondary("Remove", Look.Trash, () => _ = RemoveOfficial());
        removeOfficial.IsVisible = officialInstalled != null;
        var appText = new TextBlock { Text = Releases.AppVersion ?? "Development build" };
        var appSub = new TextBlock
        {
            Text = latestApp == null ? "" : appUpdate.IsVisible ? $"Update to {latestApp.Name}" : "Up to date",
        };
        var everything = Look.Secondary(everythingArmed ? "Click again: remove everything" : "Remove everything", Look.Trash,
            () => _ = RemoveEverything());
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 16, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    new TextBlock { Text = "Settings", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text) },
                    Look.Card("Brawlhalla", Look.Folder, new TextBlock { Text = game ?? "Not found" }, new TextBlock(), Row(
                        Look.Secondary("Change", Look.Folder, () => _ = Locate()), Look.Secondary("Open", Look.Folder, () => Ui.Open(game)),
                        Look.Secondary("Use original game", Look.Folder, () => _ = SwapLauncher(original: true)),
                        Look.Secondary(GameFinder.IsSteamInstall(game) ? "Separate copy" : "Update from Steam", Look.Download, () => _ = CopyFromSteam()))),
                    Look.Card("Bifrost", Look.Tag, bifrost, bifrostSub, Row(remove)),
                    Look.Card("Official servers", Look.Tag, officialText, officialSub, Row(removeOfficial)),
                    Look.Card("App", Look.Tag, appText, appSub, Row(Look.Secondary("What's new", Look.Logs, () => _ = ShowNotes(Product.App)))),
                    Look.Card("App data", Look.Folder, new TextBlock { Text = EngineKit.Home }, new TextBlock(),
                        Row(Look.Secondary("Open", Look.Folder, () => Ui.Open(EngineKit.Home)))),
                    Look.Card("Remove everything", Look.Trash, new TextBlock { Text = "Bifrost, its saves and its files" },
                        new TextBlock { Text = "The game goes back to Steam's own; your server's data is deleted too" }, Row(everything)),
                },
            },
        };
    }

    static StackPanel Row(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var i in items) row.Children.Add(i);
        return row;
    }

    void Show(string name)
    {
        foreach (var (button, key) in new[] { (navHome, "home"), (navServer, "server"), (navLogs, "logs"), (navSettings, "settings") })
            button.Classes.Set("selected", key == name);
        page.Content = name switch
        {
            "logs" => new LogsPage(game, log),
            "server" => new ServerPage(engine, () => game, () => latest),
            "settings" => Settings(),
            _ => home,
        };
    }


    void LoadArt()
    {
        var dir = game == null ? null : System.IO.Path.Combine(game, "mapArt", "Backgrounds");
        artFiles = dir == null ? [] : HeroArt.Select(n => System.IO.Path.Combine(dir, n)).Where(File.Exists).ToList();
        art = 0;
        if (artFiles.Count > 0) SetArt(artA, artFiles[0]);
        Icon = AppIcon.FromGame(game) ?? Icon;
    }

    void NextArt()
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


    async Task CheckOnline()
    {
        var bifrostTask = Releases.LatestAsync();
        var officialTask = Releases.LatestOfficialAsync();
        var appTask = Releases.LatestAppAsync();
        latest = await bifrostTask ?? latest;
        latestOfficial = await officialTask ?? latestOfficial;
        latestApp = await appTask ?? latestApp;
        checkedOnline = true;
        await CheckApp();
        Refresh();
        await UpdateServer();
    }

    async Task UpdateServer()
    {
        if (working || latest?.ServerPackage is not { } package || !HostedServer.UpdateAvailable(package)) return;
        if (engine.Python is not { } python) return;
        if (HostedServer.Running && (await ServerStatus.FetchAsync(HostedServer.Address))?.Players is not 0) return;
        try
        {
            await HostedServer.UpdateAsync(package, python, new Progress<double>());
            if (game is { } g) EngineKit.AddOwnServer(g);
            Notify("Server updated", $"Your server now runs Bifrost {latest.Name}'s version.", NotificationType.Information);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
        {
            log.Append($"Server update: {e.Message}");
        }
    }

    async Task CheckApp()
    {
        appUpdate.IsVisible = await AppUpdate.AvailableAsync(latestApp);
        if (appUpdate.IsVisible && latestApp != null && notifiedApp != latestApp.Name)
        {
            notifiedApp = latestApp.Name;
            Notify("App update", $"Bifrost app {latestApp.Name} is ready: press App update.", NotificationType.Information);
        }
    }

    void Refresh()
    {
        if (working) return;
        var found = GameFinder.IsGame(game) ? game : GameFinder.Find(project.Settings);
        if (found != game)
        {
            game = found;
            LoadArt();
        }
        var running = GameFinder.GameRunning();
        if (game != null && official)
        {
            RefreshOfficial(running);
            return;
        }
        var installed = game == null ? null : EngineKit.Installed(game);
        var have = Have;
        var supported = game != null && EngineKit.GameMatches(game, latest);
        var filesNote = CheckFiles(latest?.GameFileList);
        main.IsEnabled = !running;
        if (game == null)
        {
            Status("Brawlhalla not found", "Install it from Steam, or show where it is.", "Locate", Look.Folder, () => _ = Locate());
            main.IsEnabled = true;
        }
        else if (installed != null && EngineKit.Original(game))
        {
            Status("Original game", "Brawlhalla.exe is Steam's own. Bifrost is still built: Use Bifrost switches back in seconds.",
                "Use Bifrost", Look.Play, () => _ = SwapLauncher(original: false));
            main.IsEnabled = !running;
        }
        else if (installed == null || !EngineKit.LauncherOk(game))
        {
            Status("Ready to install", installed != null ? "Steam put Brawlhalla's own launcher back. Install Bifrost again."
                : !supported ? filesNote ?? "This Brawlhalla version isn't supported yet. Bifrost will update for it soon."
                : have == null ? Releases.LatestUrl == null ? "This copy of the app has no release address."
                    : !checkedOnline ? "Checking for the latest version..."
                    : Releases.Reached(Product.Bifrost) ? "No Bifrost release yet." : "Can't reach the update server right now."
                : "", "Install", Look.Download, () => _ = Install());
            main.IsEnabled = have != null && supported && !running;
        }
        else if (have != null && installed != have)
        {
            Status("Update available", supported ? $"Bifrost {have} is ready."
                : filesNote ?? "Brawlhalla was updated: Bifrost will update for it soon.", "Update", Look.Refresh, () => _ = Install());
            main.IsEnabled = supported && !running;
        }
        else if (!EngineKit.InstalledGameMatches(game, false))
        {
            Status("Waiting for an update", "Steam updated Brawlhalla. Bifrost will update for it soon; until then Play offers Brawlhalla's own game.",
                "Play", Look.Play, () => GameFinder.Play(game));
        }
        else
        {
            Status(running ? "Playing" : "Ready to play", running ? "" : "Everything is up to date.", "Play", Look.Play, () => GameFinder.Play(game));
        }
        if (running && title.Text is "Ready to install" or "Update available") detail.Text = "Close Brawlhalla first.";
        tag.Text = installed is { Length: > 0 } ? installed : have ?? "Bifrost";
        Cards(installed, have, supported);
    }

    string? lastFilesNote;

    string? CheckFiles(IReadOnlyDictionary<string, string>? list)
    {
        var note = EngineKit.FilesNote(game, list);
        if (note != null && note != lastFilesNote) log.Append(note);
        lastFilesNote = note;
        return note;
    }

    void RefreshOfficial(bool running)
    {
        var installed = EngineKit.OfficialInstalled(game!);
        var have = latestOfficial?.Name;
        var supported = EngineKit.OfficialMatches(game!, latestOfficial);
        var waitingFor = (supported ? null : CheckFiles(latestOfficial?.GameFileList))
                         ?? "Brawlhalla was updated: Official will update for it soon.";
        if (supported) CheckFiles(latestOfficial?.GameFileList);
        var oldKit = installed != null && EngineKit.OfficialOnOldKit(game!);
        main.IsEnabled = !running;
        if (installed != null && EngineKit.Original(game!))
        {
            Status("Original game", "Brawlhalla.exe is Steam's own. Official is still built: Use Official switches back in seconds.",
                "Use Official", Look.Play, () => _ = SwapLauncher(original: false));
            main.IsEnabled = !running;
        }
        else if (installed == null || !EngineKit.LauncherOk(game!))
        {
            Status("Official servers", latestOfficial == null
                    ? Releases.Repo(Product.Official) == null ? "This copy of the app has no Official release address."
                    : !checkedOnline ? "Checking for the latest version..."
                    : Releases.Reached(Product.Official) ? "No Official release yet." : "Can't reach the update server right now."
                : !supported ? waitingFor
                : "", "Install", Look.Download, () => _ = Install());
            main.IsEnabled = latestOfficial != null && supported && !running;
        }
        else if (have != null && installed != have && supported && !running && autoOfficial != have
                 && !EngineKit.InstalledGameMatches(game!, true))
        {
            autoOfficial = have;
            Status("Updating", $"Official {have} is ready for this Brawlhalla version.", "Update", Look.Refresh, () => _ = Install());
            Dispatcher.UIThread.Post(() => _ = Install());
        }
        else if (have != null && (installed != have || oldKit))
        {
            Status("Update available", !supported ? waitingFor : installed != have ? $"Official {have} is ready."
                : "Bifrost was updated: Update rebuilds Official on its new engine.", "Update", Look.Refresh, () => _ = Install());
            main.IsEnabled = supported && !running;
        }
        else if (!EngineKit.InstalledGameMatches(game!, true))
        {
            Status("Waiting for an update", "Steam updated Brawlhalla. Until Official updates for it, Play starts Brawlhalla's own game.",
                "Play", Look.Play, () => GameFinder.Play(game));
        }
        else
        {
            Status(running ? "Playing" : "Ready to play", running ? "" : "Everything is up to date.", "Play", Look.Play, () => GameFinder.Play(game));
        }
        if (running && title.Text is "Official servers" or "Update available") detail.Text = "Close Brawlhalla first.";
        tag.Text = installed is { Length: > 0 } ? $"Official {installed}" : have != null ? $"Official {have}" : "Official";
        Cards(installed, have, supported);
    }

    void Cards(string? installed, string? have, bool supported)
    {
        versionValue.Text = InstalledText(installed);
        versionSub.Text = (installed == null ? (have != null ? $"Latest {have}" : "") : have == null ? "" : installed == have ? "Up to date" : $"Update to {have}")
                          + $"{(installed == null && have == null ? "" : " · ")}App {Releases.AppVersion ?? "dev"}";
        gameValue.Text = game == null ? "Not found" : supported ? "Found" : "Not supported yet";
        gameSub.Text = game ?? "";
        var (runs, errors) = RecentRuns();
        runsValue.Text = runs == 0 ? "No runs yet" : $"{runs} run{(runs == 1 ? "" : "s")}";
        runsSub.Text = runs == 0 ? "" : errors == 0 ? "No errors" : $"{errors} with errors";
    }

    string? Have => latest?.Name;

    static string InstalledText(string? installed) =>
        installed is { Length: > 0 } ? installed : installed != null ? "Installed" : "Not installed";

    (int Runs, int Errors) RecentRuns()
    {
        if (LogsPage.Docs(game) is not { } docs) return (0, 0);
        var runs = LogsPage.Sessions(docs);
        return (runs.Count, runs.Count(r => r.Name.EndsWith("(errors)", StringComparison.Ordinal)));
    }

    void Status(string head, string sub, string button, string icon, Action action)
    {
        title.Text = head;
        detail.Text = sub;
        Look.SetContent(main, button, icon);
        mainAction = action;
    }


    async Task CleanRebuild()
    {
        if (game == null || working) return;
        if (GameFinder.GameRunning()) { detail.Text = "Close Brawlhalla first."; return; }
        var serverWasRunning = HostedServer.Running;
        try
        {
            if (serverWasRunning) await HostedServer.StopAsync();
            await Task.Run(() => EngineKit.Clean(kit: latest != null, officialLayer: official && latestOfficial != null));
            log.Append("Clean rebuild: removed the work folder and the downloaded files; downloading and building again.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            detail.Text = $"Couldn't clear Bifrost's files: {e.Message} Close anything started from the app and try again.";
            return;
        }
        await Install(fresh: true);
        if (serverWasRunning && engine.Python is { } python && !HostedServer.Running) HostedServer.Start(python);
    }

    async Task Install(bool fresh = false)
    {
        if (game == null || working) return;
        if (GameFinder.GameRunning()) { detail.Text = "Close Brawlhalla first."; return; }
        var isOfficial = official;
        if (isOfficial && latestOfficial == null) return;
        if (isOfficial && !project.Settings.OfficialAccepted)
        {
            if (!await ConfirmOfficial()) return;
            project.Settings.OfficialAccepted = true;
            project.Settings.Mode = "official";
            project.Settings.Save();
        }
        var updating = isOfficial ? EngineKit.OfficialInstalled(game) != null : EngineKit.Installed(game) != null;
        working = true;
        cancel = new CancellationTokenSource();
        log.Clear();
        idle.IsVisible = false;
        appUpdate.IsVisible = false;
        progress.IsVisible = true;
        progress.Begin(isOfficial ? EngineKit.OfficialDownloadSize(latestOfficial, latest) : EngineKit.DownloadSize(latest));
        bool ok = false;
        string? failure = null;
        try
        {
            Action<double, double, long> download = (p, speed, size) => Ui.OnUi(() => progress.Download(p, speed, size));
            Action unpacking = () => Ui.OnUi(progress.Unpacking);
            Action<int> step = n => Ui.OnUi(() => progress.DriverStep(n));
            Action<string> line = text =>
            {
                log.Append(text);
                Ui.OnUi(() => progress.Line(text));
            };
            if (isOfficial) await engine.InstallOfficialAsync(game, latestOfficial!, latest, download, unpacking, step, line, cancel.Token, fresh);
            else await engine.InstallAsync(game, latest, download, unpacking, step, line, cancel.Token, fresh);
            EngineKit.WriteMode(game, official);
            ok = true;
        }
        catch (OperationCanceledException) { failure = "Stopped."; }
        catch (Exception e) when (e is InvalidOperationException or IOException or HttpRequestException
                                      or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            failure = e.Message;
        }
        progress.Finish(ok);
        working = false;
        progress.IsVisible = false;
        idle.IsVisible = true;
        cancel.Dispose();
        cancel = null;
        Refresh();
        await CheckApp();
        if (ok)
        {
            var what = isOfficial ? "Official" : "Bifrost";
            var version = isOfficial ? EngineKit.OfficialInstalled(game) : EngineKit.Installed(game);
            Notify(updating ? $"{what} updated" : $"{what} installed",
                $"{version} is ready in {InstallProgress.Clock(progress.Elapsed)}. Press Play, or start Brawlhalla from Steam.", NotificationType.Success);
        }
        else if (failure != null)
        {
            title.Text = failure == "Stopped." ? "Stopped" : "Didn't finish";
            detail.Text = failure;
            if (failure != "Stopped.") Notify(isOfficial ? "Official didn't install" : "Bifrost didn't install", failure, NotificationType.Error);
        }
    }

    void Notify(string head, string text, NotificationType type)
    {
        notes?.Show(new Notification(head, text, type, TimeSpan.FromSeconds(12)));
        if (!IsActive && OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is { } hwnd)
        {
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = hwnd, Flags = 3 | 12, Count = 0 };
            FlashWindowEx(ref info);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FlashInfo info);

    async Task UpdateApp()
    {
        if (latestApp == null || working) return;
        appUpdate.IsEnabled = false;
        try
        {
            await AppUpdate.ApplyAsync(latestApp, new Progress<double>(p => SetButtonText(appUpdate, $"Updating the app {p:P0}")));
            Close();
        }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetButtonText(appUpdate, "App update failed: try again");
            appUpdate.IsEnabled = true;
        }
    }

    static void SetButtonText(Button b, string text)
    {
        if (b.Content is StackPanel { Children: [_, TextBlock t] }) t.Text = text;
    }

    async Task Remove()
    {
        if (game == null || working || GameFinder.GameRunning()) return;
        working = true;
        try { await engine.RemoveAsync(game, log.Append); }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { log.Append(e.Message); }
        finally
        {
            working = false;
            Refresh();
            Show("home");
            Notify("Bifrost removed", "Brawlhalla is back to Steam's own game. Your saves are kept.", NotificationType.Information);
        }
    }

    async Task SwapLauncher(bool original)
    {
        if (game == null || working || EngineKit.Installed(game) == null) return;
        if (GameFinder.GameRunning()) { detail.Text = "Close Brawlhalla first."; return; }
        working = true;
        try
        {
            await engine.SwapLauncherAsync(game, original, log.Append);
            Notify(original ? "Original game" : official ? "Official" : "Bifrost",
                original ? "Brawlhalla.exe is Steam's own again. Bifrost and Official stay built for when you switch back."
                    : "Brawlhalla.exe starts " + (official ? "Official" : "Bifrost") + " again.", NotificationType.Information);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            log.Append(e.Message);
            detail.Text = e.Message;
        }
        finally
        {
            working = false;
            Refresh();
            Show("home");
        }
    }

    async Task RemoveOfficial()
    {
        if (game == null || working || GameFinder.GameRunning()) return;
        working = true;
        try { await engine.RemoveOfficialAsync(game, log.Append); }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { log.Append(e.Message); }
        finally
        {
            working = false;
            Refresh();
            Show("settings");
            Notify("Official removed", "Bifrost is the mode again. Official's saves are kept.", NotificationType.Information);
        }
    }

    async Task RemoveEverything()
    {
        if (working) return;
        if (!everythingArmed)
        {
            everythingArmed = true;
            DispatcherTimer.RunOnce(() => { everythingArmed = false; if (page.Content is ScrollViewer) Show("settings"); }, TimeSpan.FromSeconds(4));
            Show("settings");
            return;
        }
        everythingArmed = false;
        if (GameFinder.GameRunning()) return;
        working = true;
        try
        {
            if (HostedServer.Running) await HostedServer.StopAsync();
            await engine.RemoveEverythingAsync(game, log.Append);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { log.Append(e.Message); }
        finally
        {
            working = false;
            Refresh();
            Show("home");
            Notify("Everything removed", "Bifrost, its saves and its files are gone. Delete this app like any file.", NotificationType.Information);
        }
    }

    async Task Locate()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Brawlhalla's folder" });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        if (!GameFinder.IsGame(path))
        {
            Notify("Not Brawlhalla's folder", $"{path} has no BrawlhallaAir.swf. Pick the folder that holds the game's files.", NotificationType.Warning);
            return;
        }
        UseFolder(path);
    }

    void UseFolder(string path)
    {
        project.Settings.GameFolder = path;
        project.Settings.Save();
        game = path;
        LoadArt();
        Refresh();
    }

    async Task CopyFromSteam()
    {
        if (working || GameFinder.SteamInstall() is not { } steam) return;
        if (GameFinder.GameRunning()) { detail.Text = "Close Brawlhalla first."; return; }
        var target = game;
        if (GameFinder.IsSteamInstall(target))
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for the separate copy" });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
            target = !Directory.EnumerateFileSystemEntries(folder).Any() || GameFinder.IsGame(folder) ? folder : Path.Combine(folder, "Brawlhalla");
            if (GameFinder.IsSteamInstall(target))
            {
                Notify("Pick another folder", "That is Steam's own copy of the game.", NotificationType.Warning);
                return;
            }
        }
        var from = game ?? steam;
        working = true;
        Show("home");
        title.Text = "Copying Brawlhalla";
        main.IsEnabled = false;
        var failed = false;
        try
        {
            await GameCopy.MakeAsync(steam, target!, p => Dispatcher.UIThread.Post(() => detail.Text = $"{p:P0} of {steam}"), CancellationToken.None);
            GameCopy.CarrySaves(from, target!);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            failed = true;
            log.Append(e.Message);
            Notify("The copy didn't finish", e.Message, NotificationType.Error);
        }
        finally
        {
            working = false;
        }
        if (failed)
        {
            Refresh();
            return;
        }
        UseFolder(target!);
        Show("home");
        Notify("Separate copy ready", "Steam's updates won't change this copy. Install Bifrost or Official on it; Update from Steam in Settings brings it up to date.",
            NotificationType.Information);
    }
}
