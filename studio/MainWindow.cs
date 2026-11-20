using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace BifrostStudio;

public sealed class MainWindow : Window
{
    readonly Project project;
    readonly AppState state;
    readonly EngineKit engine;
    readonly ContentControl page = new();
    readonly PageRegistry pages = new();
    readonly Dictionary<string, Button> nav = [];
    string current = "home";
    WindowNotificationManager? notes;

    readonly TextBlock tag = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
    readonly Border tagPill;
    readonly TextBlock title = new() { FontSize = 38, FontWeight = FontWeight.Black, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock detail = new() { FontSize = 15, Foreground = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
    readonly Button main, gear, appUpdate, stop, modeBifrost, modeOfficial;
    readonly GameSettingsPanel gameSettings;
    readonly InstallProgress progress;
    readonly Control idle;
    readonly TextBlock versionValue = new(), versionSub = new(), gameValue = new(), gameSub = new(), runsValue = new(), runsSub = new();
    readonly HomePage home;

    readonly LogView log = new();
    string? game { get => state.Game; set => state.Game = value; }
    Release? latest { get => state.Latest; set => state.Latest = value; }
    OfficialRelease? latestOfficial { get => state.LatestOfficial; set => state.LatestOfficial = value; }
    AppRelease? latestApp { get => state.LatestApp; set => state.LatestApp = value; }
    string? notifiedApp, autoOfficial;
    readonly Dictionary<Product, string?> loggedProblems = [];
    bool checkedOnline, working, everythingArmed, official;
    CancellationTokenSource? cancel;
    Action mainAction = () => { };
    OnlineService? online;
    OnlinePage? onlinePage;

    public MainWindow(Project project)
    {
        this.project = project;
        state = new AppState(project);
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
        gear = RoundButton(Look.Cog);
        ToolTip.SetTip(gear, "Game settings");
        gear.Click += (_, _) => ToggleGameSettings(true);
        stop =Look.Secondary("Stop", Look.Stop, () => cancel?.Cancel());
        appUpdate = Look.Secondary("App update available", Look.Download, () => _ = UpdateApp());
        modeBifrost = ModeButton("Bifrost", false);
        modeOfficial = ModeButton("Official servers", true);
        official = !EngineKit.OfficialPaused && project.Settings.Mode != "bifrost";
        if (EngineKit.OfficialPaused && project.Settings.Mode != "bifrost")
        {
            project.Settings.Mode = "bifrost";
            project.Settings.Save();
        }
        modeOfficial.IsVisible = modeBifrost.IsVisible = !EngineKit.OfficialPaused;
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
        idle = Idle();
        home = new HomePage(appUpdate, idle, progress,
            Look.Card("Version", Look.Tag, versionValue, versionSub,
                Look.Link("What's new", Look.Logs, () => _ = ShowNotes(official ? Product.Official : Product.Bifrost))),
            Look.Card("Brawlhalla", Look.Folder, gameValue, gameSub, Look.Link("Open folder", Look.Folder, () => Ui.Open(game, missing: "Brawlhalla wasn't found. Pick its folder in Settings."))),
            Look.Card("Recent runs", Look.Pulse, runsValue, runsSub, Look.Link("View logs", Look.Logs, () => Show("logs"))));
        gameSettings = new GameSettingsPanel(new GameSettings(state),
            new SettingsActions(() => ToggleGameSettings(false), () => _ = Install(fresh: true), MoveEngine, () => working,
                async () =>
                {
                    var why = await SwitchPlace();
                    if (why != null) Notify("Didn't switch", why, NotificationType.Warning);
                    return why;
                },
                () => _ = CleanRebuild(), () => official ? latestOfficial != null : latest != null,
                original => _ = SwapLauncher(original), () => Show("logs"))) { IsVisible = false };
        home.AddOverlay(gameSettings);
        state.Changed += gameSettings.Refresh;
        pages.Add("home", "Home", Look.Home, () => home)
            .Add("replays", "Replays", Look.Play, () => new ReplaysPage(new ReplayLibrary(state), state))
            .Add("mods", "Mods", Look.Wrench, () => new ModsPage(new ModLibrary(state), state))
            .Add("setups", "Setups", Look.Tag, () => new SetupsPage(new SetupLibrary(state), state))
            .Add("online", "Online", Look.Pulse, () => onlinePage ??= MakeOnline())
            .Add("server", "Server", Look.Server, () => new ServerPage(engine, () => game, () => latest))
            .Add("logs", "Logs", Look.Logs, () => new LogsPage(game, log))
            .Add("settings", "Settings", Look.Tune, () => new SettingsPage(SettingsModel()));
        foreach (var p in pages.All) nav[p.Key] = Look.Nav(p.Title, p.Icon, () => Show(p.Key));

        Grid.SetColumn(page, 1);
        page.Margin = new Thickness(26, 24, 26, 24);
        Content = new Grid { ColumnDefinitions = new ColumnDefinitions("232,*"), Children = { Sidebar(), page, Toast() } };

        game = GameFinder.Find(project.Settings);
        if (game != null && !EngineKit.OfficialPaused && project.Settings.Mode == null && EngineKit.Installed(game) != null)
            official = EngineKit.ReadOfficialMode(game);
        PaintModes();
        LoadArt();
        Show("home");
        Refresh();
        AppUpdate.CleanUp();
        Closed += (_, _) => OldVersions.Job.Stop();
        Opened += async (_, _) =>
        {
            notes = new WindowNotificationManager(this) { Position = NotificationPosition.TopRight, MaxItems = 2 };
            await PauseOfficial();
            await MoveEngineOut();
            await CheckOnline();
            if (OldVersions.KeepAll && await OldVersions.EnsurePackAsync() == null && OldVersions.Missing().Count > 0) OldVersions.Job.GetAll();
        };
        new DispatcherTimer(TimeSpan.FromMinutes(30), DispatcherPriority.Background, (_, _) => { if (!working) _ = CheckOnline(); }).Start();
        new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            if (working) return;
            if (EngineKit.OfficialPaused && pausedFor != (game ?? "")) _ = PauseOfficial();
            else Refresh();
        }).Start();
        new DispatcherTimer(TimeSpan.FromSeconds(14), DispatcherPriority.Background, (_, _) => home.NextArt()).Start();
    }


    Control Sidebar()
    {
        var word = new TextBlock { Text = "BIFROST", FontSize = 28, FontWeight = FontWeight.Black, LetterSpacing = 4, Foreground = Look.Brush(Look.Accent) };
        var sub = new TextBlock { Text = "for Brawlhalla", FontSize = 12, Foreground = Look.Brush(Look.Dim), Margin = new Thickness(2, 0, 0, 0) };
        var list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 34, 0, 0) };
        foreach (var p in pages.All) list.Children.Add(nav[p.Key]);
        return new Border
        {
            Background = Look.Brush(Look.Side), Padding = new Thickness(18, 28, 18, 20),
            BorderBrush = Look.Brush(Look.Line), BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new StackPanel { Children = { word, sub, list } },
        };
    }

    static Button RoundButton(string icon) => new()
    {
        Classes = { "secondary" }, Background = Look.Brush(Look.Raised), Width = 54, Height = 54,
        CornerRadius = new CornerRadius(27), HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center, Content = Look.Icon(icon, 22, Colors.White),
    };

    void ToggleGameSettings(bool open)
    {
        if (open && current != "home") Show("home");
        gameSettings.IsVisible = open;
        if (open) gameSettings.Open();
    }

    async Task<string?> MoveEngine(string target)
    {
        if (game == null || working) return "Wait for the app to finish what it's doing.";
        if (GameFinder.GameRunning()) return "Close Brawlhalla first.";
        working = true;
        string? failure = null;
        try
        {
            await engine.MoveEngineAsync(game, target, log.Append);
            state.Poll();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            log.Append(e.Message);
            failure = e.Message;
        }
        finally
        {
            working = false;
            Refresh();
        }
        if (failure == null) Notify("Engine moved", $"Bifrost's engine is now in {target}. Saves and settings came with it.", NotificationType.Information);
        return failure;
    }

    async Task MoveEngineOut()
    {
        if (working || game == null || engine.Folder == null || !FolderMigration.EngineInGameFolder(game)) return;
        var target = game;
        working = true;
        string? moved = null;
        try
        {
            moved = await FolderMigration.MoveEngineOutAsync(target, GameFinder.GameRunning(), (g, to) => engine.MoveEngineAsync(g, to, log.Append));
            if (moved != null) state.Poll();
        }
        finally
        {
            working = false;
            Refresh();
        }
        if (moved != null)
            Notify("Bifrost moved out of the game folder", $"Its engine, saves and Steam's original Brawlhalla.exe are now in {moved}. Only Bifrost's Brawlhalla.exe and the mods folder stay in the game folder.",
                NotificationType.Information);
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

    string? pausedFor, pauseFailure;

    async Task PauseOfficial()
    {
        if (!EngineKit.OfficialPaused || working || pausedFor == (game ?? "") || GameFinder.GameRunning()) return;
        var target = game;
        working = true;
        try
        {
            var removed = await Task.Run(() => EngineKit.PauseOfficial(target));
            var onlyOfficial = target != null && EngineKit.EngineInstalled(target) && EngineKit.Installed(target) == null;
            if (onlyOfficial) await engine.RemoveAsync(target!, log.Append);
            pausedFor = target ?? "";
            if (removed)
            {
                log.Append("Official servers are paused, so Official was removed" + (onlyOfficial ? " and Brawlhalla is Steam's own game again" : "")
                           + ". Its saves are kept.");
                Notify("Official servers paused", onlyOfficial
                    ? "Official servers are paused for now, so Official was removed and Brawlhalla is Steam's own game again. You can install Bifrost any time."
                    : "Official servers are paused for now, so Official was removed. Bifrost works as usual.", NotificationType.Information);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (e.Message != pauseFailure) log.Append($"Couldn't remove Official yet: {e.Message}");
            pauseFailure = e.Message;
        }
        finally
        {
            working = false;
            Refresh();
        }
    }

    async Task SetMode(bool wantOfficial)
    {
        if (wantOfficial == official || working || wantOfficial && EngineKit.OfficialPaused) return;
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
            HorizontalAlignment = HorizontalAlignment.Right, Children = { main, gear },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(actions, 1);
        grid.Children.Add(words);
        grid.Children.Add(actions);
        return grid;
    }

    SettingsView SettingsModel() => new(
        game, game == null ? null : EngineKit.Installed(game), Have, game == null ? null : EngineKit.OfficialInstalled(game),
        latestOfficial?.Name, latestApp?.Name, appUpdate.IsVisible, everythingArmed,
        () => _ = Locate(), () => _ = SwapLauncher(original: true), () => _ = CopyFromSteam(), () => _ = Remove(),
        () => _ = RemoveOfficial(), () => _ = ShowNotes(Product.App), () => _ = RemoveEverything());

    internal static StackPanel Row(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var i in items) row.Children.Add(i);
        return row;
    }

    OnlinePage MakeOnline()
    {
        var settings = new GameSettings(state);
        online ??= new OnlineService(new Net.ServerAccounts(Net.ServerAccounts.DefaultFile, () => settings.ServerFile));
        return new OnlinePage(online, new OnlineHooks(
            () => OnlineService.KnownServers(settings),
            online.UseAsync,
            s => settings.UseServer(s.Host, s.Port),
            () => state.Paths?.GameReplays ?? state.Paths?.Replays,
            Ui.Browse,
            s => ServerStatus.FetchAsync(s.Host, s.Port),
            () => DateTime.Now,
            () => new ModLibrary(state).Load(),
            (entry, package, uploader) => new ModLibrary(state).InstallDownload(entry, package, uploader),
            () => state.Game));
    }

    void Show(string name)
    {
        var info = pages.Find(name);
        current = info.Key;
        if (info.Key != "home") gameSettings.IsVisible = false;
        foreach (var (key, button) in nav) button.Classes.Set("selected", key == info.Key);
        page.Content = info.Make();
    }


    void LoadArt()
    {
        home.LoadArt(game);
        Icon = AppIcon.FromGame(game) ?? Icon;
    }


    async Task CheckOnline()
    {
        var bifrostTask = Releases.LatestAsync();
        var officialTask = EngineKit.OfficialPaused ? Task.FromResult<OfficialRelease?>(null) : Releases.LatestOfficialAsync();
        var appTask = Releases.LatestAppAsync();
        latest = await bifrostTask ?? latest;
        latestOfficial = await officialTask ?? latestOfficial;
        latestApp = await appTask ?? latestApp;
        checkedOnline = true;
        foreach (var product in Enum.GetValues<Product>())
        {
            var problem = Releases.Problem(product);
            if (problem != null && problem != loggedProblems.GetValueOrDefault(product)) log.Append($"Update check ({product}): {problem}");
            loggedProblems[product] = problem;
        }
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
            Notify("App update", $"Bifrost app {latestApp.Name} is ready. Press App update to get it.", NotificationType.Information);
        }
    }

    void Refresh()
    {
        if (working) return;
        var before = game;
        state.Poll();
        if (game != before) LoadArt();
        var running = state.GameRunning;
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
            Status("Original game", "Brawlhalla.exe is Steam's own right now. Use Bifrost switches back in seconds.",
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
                : filesNote ?? "Brawlhalla was updated. Bifrost will update for it soon.", "Update", Look.Refresh, () => _ = Install());
            main.IsEnabled = supported && !running;
        }
        else if (!EngineKit.InstalledGameMatches(game, false))
        {
            Status("Waiting for an update", "Steam updated Brawlhalla. Until Bifrost updates for it, Play starts Brawlhalla's own game.",
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
                         ?? "Brawlhalla was updated. Official will update for it soon.";
        if (supported) CheckFiles(latestOfficial?.GameFileList);
        var oldKit = installed != null && EngineKit.OfficialOnOldKit(game!);
        main.IsEnabled = !running;
        if (installed != null && EngineKit.Original(game!))
        {
            Status("Original game", "Brawlhalla.exe is Steam's own right now. Use Official switches back in seconds.",
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
                : "Bifrost was updated. Press Update to rebuild Official on the new engine.", "Update", Look.Refresh, () => _ = Install());
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

    internal static string InstalledText(string? installed) =>
        installed is { Length: > 0 } ? installed : installed != null ? "Installed" : "Not installed";

    (int Runs, int Errors) RecentRuns()
    {
        if (LogsPage.Docs(game) is not { } docs) return (0, 0);
        var runs = LogsPage.Sessions(docs);
        return (runs.Count, runs.Count(r => r.Name.EndsWith("(errors)", StringComparison.Ordinal)));
    }

    public static Border Toast()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Foreground = Look.Brush(Look.Text), MaxWidth = 640 };
        var toast = new Border
        {
            Child = text, Background = Look.Brush(Look.Panel), BorderBrush = Look.Brush(Look.Accent), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 10), Margin = new Thickness(0, 0, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false,
        };
        Grid.SetColumnSpan(toast, 2);
        var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        hide.Tick += (_, _) => { hide.Stop(); toast.IsVisible = false; };
        toast.PointerPressed += (_, _) => { hide.Stop(); toast.IsVisible = false; };
        Ui.Said += said => Ui.OnUi(() =>
        {
            text.Text = said;
            toast.IsVisible = true;
            hide.Stop();
            hide.Start();
        });
        return toast;
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
            log.Append("Clean rebuild: removed the build folder and the downloads. Downloading and building again.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            detail.Text = $"Couldn't clear Bifrost's files. Close anything started from the app and try again. {e.Message}";
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
        if (InstallPlace.NeedsAsking(project.Settings, game, updating || EngineKit.EngineInstalled(game)))
        {
            if (await AskInstallPlace() is not { } place) return;
            project.Settings.InstallPlace = place;
            project.Settings.Save();
            if (place == InstallPlace.Own && !await CopyGame(game, BifrostFolders.Game, game)) return;
            if (game == null) return;
        }
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
            SetButtonText(appUpdate, "App update failed. Try again");
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
            Notify("Official removed", "You're back on Bifrost. Official's saves are kept.", NotificationType.Information);
        }
    }

    async Task RemoveEverything()
    {
        if (working) return;
        if (!everythingArmed)
        {
            everythingArmed = true;
            DispatcherTimer.RunOnce(() => { everythingArmed = false; if (current == "settings") Show("settings"); }, TimeSpan.FromSeconds(4));
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
            Notify("Everything removed", "Bifrost, its saves and its files are gone. You can delete this app now.", NotificationType.Information);
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
        if (!await CopyGame(steam, target!, game ?? steam)) return;
        Notify("Separate copy ready", "Steam's updates won't change this copy. Install Bifrost or Official on it, and use Update from Steam in Settings to bring it up to date.",
            NotificationType.Information);
    }

    async Task<bool> CopyGame(string steam, string target, string from)
    {
        working = true;
        Show("home");
        title.Text = "Copying Brawlhalla";
        main.IsEnabled = false;
        var failed = false;
        try
        {
            await GameCopy.MakeAsync(steam, target, p => Dispatcher.UIThread.Post(() => detail.Text = $"{p:P0} of {steam}"), CancellationToken.None);
            GameCopy.CarrySaves(from, target);
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
            return false;
        }
        UseFolder(target);
        Show("home");
        return true;
    }

    async Task<string?> AskInstallPlace()
    {
        string? chosen = null;
        var dialog = new Window
        {
            Title = "Where to install Bifrost", Width = 560, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Look.Brush(Look.Panel),
        };
        Control Choice(string place, string head, string icon)
        {
            var button = Look.Secondary(head, icon, () => { chosen = place; dialog.Close(); });
            return new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    button,
                    new TextBlock { Text = InstallPlace.Describe(place), TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Look.Brush(Look.Dim) },
                },
            };
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(26), Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Where should Bifrost go?", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text) },
                Choice(InstallPlace.Steam, "Install on Steam", Look.Play),
                Choice(InstallPlace.Own, "Install in its own folder", Look.Folder),
                new TextBlock
                {
                    Text = "Either way, Bifrost's engine, saves and settings stay out of Steam's folder. "
                           + "You can switch later under Advanced in the gear menu.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Look.Brush(Look.Dim),
                },
                Row(Look.Secondary("Cancel", Look.Stop, () => dialog.Close())),
            },
        };
        await dialog.ShowDialog(this);
        return chosen;
    }

    async Task<string?> SwitchPlace()
    {
        if (game == null || working) return "Wait for the app to finish what it's doing.";
        if (GameFinder.GameRunning()) return "Close Brawlhalla first.";
        if (GameFinder.SteamInstall() is not { } steam) return "Steam's Brawlhalla wasn't found.";
        var to = InstallPlace.Of(game) == InstallPlace.Steam ? InstallPlace.Own : InstallPlace.Steam;
        var installed = EngineKit.EngineInstalled(game);
        if (installed && !engine.PlacesEngine) return "Switching needs a newer Bifrost release. Update first, then try again.";
        if (installed)
        {
            working = true;
            try { await engine.RemoveAsync(game, log.Append); }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                log.Append(e.Message);
                return e.Message;
            }
            finally
            {
                working = false;
                Refresh();
            }
        }
        project.Settings.InstallPlace = to;
        project.Settings.Save();
        FolderMigration.Log($"switching the install from {game} to {(to == InstallPlace.Own ? BifrostFolders.Game : steam)}");
        if (to == InstallPlace.Own)
        {
            if (!await CopyGame(steam, BifrostFolders.Game, game)) return "The copy didn't finish. See Logs for why. Bifrost was taken out of Steam's folder, and Install puts it back.";
        }
        else UseFolder(steam);
        if (installed) await Install();
        return null;
    }
}
