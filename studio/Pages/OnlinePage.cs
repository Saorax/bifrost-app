using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BifrostStudio.Net;

namespace BifrostStudio;

public sealed record OnlineHooks(
    Func<IReadOnlyList<ServerEntry>> Servers,
    Func<ServerEntry?, Task> Use,
    Func<ServerEntry, string?> GameUse,
    Func<string?> ReplaysDir,
    Action<string> Open,
    Func<ServerEntry, Task<ServerInfo?>> Info,
    Func<DateTime> Now,
    Func<IReadOnlyList<ModInfo>>? LocalMods = null,
    Func<string, byte[], string, string?>? Install = null,
    Func<string?>? Game = null);

public sealed partial class OnlinePage : UserControl
{
    static readonly (string Key, string Name)[] Ladders =
        [("1v1", "1V1"), ("2v2", "2V2"), ("3v3", "3V3"), ("2v2team", "2V2 TEAMS"), ("legends", "BY LEGEND"), ("weapons", "BY WEAPON"), ("all", "ALL MATCHES")];
    string legendName = "";
    static readonly (string Key, string Title)[] Tabs =
    [
        ("leaderboard", "LEADERBOARD"), ("profile", "PROFILE"), ("matches", "MATCHES"), ("tournaments", "TOURNAMENTS"), ("mods", "MODS"),
        ("friends", "FRIENDS"), ("server", "SERVER"), ("admin", "ADMIN"),
    ];

    readonly IServerData data;
    readonly OnlineHooks hooks;
    readonly ComboBox serverBox = new() { MinWidth = 240 };
    readonly TextBlock status = new() { FontSize = 12.5, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock said = new() { FontSize = 12.5, Foreground = Look.Brush(Look.Accent), TextWrapping = TextWrapping.Wrap };
    readonly StackPanel account = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    readonly Dictionary<string, Button> tabButtons = [];
    readonly ContentControl body = new();
    readonly ScrollViewer scroller;
    readonly ContentControl fill = new();
    IReadOnlyList<ServerEntry> known = [];
    string tab = "leaderboard";
    string ladder = "1v1", filter = "all", search = "", matchSearch = "";
    int season;
    const int BoardPage = 50;
    int boardOffset;
    string boardShown = "";
    bool pagedBoards = true;
    string? boardPick;
    string historyLadder = "1v1";
    const int MatchPage = 20;
    bool myMatches;
    bool pagedRecent = true;
    int matchOffset;
    string? profileOf;
    JsonNode? seasons;
    bool signingIn;
    LinkState shownState = (LinkState)(-1);
    int version;

    public OnlinePage(IServerData data, OnlineHooks hooks)
    {
        this.data = data;
        this.hooks = hooks;
        var title = new TextBlock { Text = "ONLINE", FontSize = 34, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Text), VerticalAlignment = VerticalAlignment.Center };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Children = { account, serverBox } };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { title, right } };
        Grid.SetColumn(right, 1);
        serverBox.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(serverBox, "The server shown here. The game picks its own in its settings.");
        serverBox.SelectionChanged += (_, _) =>
        {
            if (serverBox.SelectedIndex >= 0 && serverBox.SelectedIndex < known.Count && !Same(known[serverBox.SelectedIndex], data.Server))
                _ = Switch(known[serverBox.SelectedIndex]);
        };
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (key, text) in Tabs)
        {
            var b = TabButton(text, () => Show(key));
            tabButtons[key] = b;
            bar.Children.Add(b);
        }
        var rule = new Border { Height = 3, Background = Look.Brush(Teal), Margin = new Thickness(0, 10, 0, 0), CornerRadius = new CornerRadius(2) };
        status.Margin = new Thickness(0, 2, 0, 0);
        said.Margin = new Thickness(0, 6, 0, 0);
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10), Children = { head, status, bar, rule, said } };
        scroller = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new Panel { Children = { scroller, fill } });
        Content = dock;
        data.Changed += () => Dispatcher.UIThread.Post(StateChanged);
        AttachedToVisualTree += (_, _) => { FillServers(); StateChanged(); };
        FillServers();
        StateChanged();
    }

    static bool Same(ServerEntry? a, ServerEntry? b) => a != null && b != null && a.Same(b.Host, b.Port);

    void FillServers()
    {
        known = hooks.Servers();
        serverBox.ItemsSource = known.Select(s => s.Name == s.Host ? s.Address : s.Name).ToList();
        serverBox.SelectedIndex = data.Server is { } now ? known.ToList().FindIndex(s => Same(s, now)) : -1;
        if (data.Server == null && known.Count > 0) _ = Switch(known[0]);
    }

    async Task Switch(ServerEntry server)
    {
        seasons = null;
        profileOf = null;
        boardPick = null;
        pagedBoards = true;
        myMatches = false;
        matchOffset = 0;
        pagedRecent = true;
        ResetTournaments();
        ResetAdmin();
        await hooks.Use(server);
        StateChanged();
    }

    void StateChanged()
    {
        status.Text = data.Server == null ? "No server yet. Add one from the gear on Home, or start the game once." : data.State switch
        {
            LinkState.Online => "Connected to " + data.Server.Address + (data.Message.Length > 0 ? ". " + data.Message : ""),
            LinkState.Connecting => "Connecting to " + data.Server.Address + "...",
            LinkState.Waiting => "Not connected. " + (data.Message.Length > 0 ? data.Message.TrimEnd('.') + ". " : "") + "Trying again soon.",
            LinkState.Stopped => "The server signed you out.",
            _ => "Not connected.",
        };
        account.Children.Clear();
        if (data.State == LinkState.Online)
        {
            if (data.SignedIn)
            {
                account.Children.Add(new TextBlock { Text = data.Account.Length > 0 ? data.Account : "Signed in", FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Good), VerticalAlignment = VerticalAlignment.Center });
                account.Children.Add(Look.Link("Sign out", Look.Close, () =>
                {
                    if (Confirmed("signout", "Click Sign out again to sign out here. The game stays signed in.")) _ = Relink(data.SignOut, "You're signed out of this server.");
                }));
            }
            else
            {
                var b = Look.Secondary(signingIn ? "Waiting for Discord..." : "Sign in with Discord", Look.Check, () => _ = SignIn());
                b.IsEnabled = !signingIn;
                account.Children.Add(b);
                if (!signingIn && data.GameSignInAvailable)
                    account.Children.Add(Look.Link("Use the game's sign-in", Look.Check, () => _ = Relink(data.UseGameSignIn, "Using the game's sign-in again.")));
            }
        }
        else if (data.Server != null && data.State != LinkState.Connecting)
        {
            account.Children.Add(Look.Secondary("Reconnect", Look.Refresh, data.Reconnect));
        }
        var admin = data.State == LinkState.Online && data.IsAdmin;
        tabButtons["admin"].IsVisible = admin;
        if (!admin && tab == "admin" && data.State == LinkState.Online) Show("leaderboard");
        var wasOnline = shownState == LinkState.Online;
        if (data.State != shownState)
        {
            shownState = data.State;
            if (data.State == LinkState.Online && !wasOnline) Show(tab);
            else if (data.State != LinkState.Online && tab != "server") Put(Note(data.Server == null ? "" : "Not connected to this server."));
        }
    }

    async Task Relink(Func<Task> change, string done)
    {
        ResetTournaments();
        ResetAdmin();
        await change();
        said.Text = done;
        StateChanged();
    }

    async Task SignIn()
    {
        signingIn = true;
        StateChanged();
        var link = await data.StartSignIn();
        if (!link.Ok)
        {
            said.Text = link.Message;
            signingIn = false;
            StateChanged();
            return;
        }
        hooks.Open(link.Link);
        said.Text = $"Discord's sign-in page is open in your browser. If it asks for a code, type {link.Code}.";
        var result = await data.WaitSignIn(TimeSpan.FromMinutes(5));
        signingIn = false;
        said.Text = result == null ? "Sign-in timed out. Try again." : result.Ok ? "Signed in as " + result.Account + "." : "Sign-in failed: " + result.Message;
        StateChanged();
        if (result?.Ok == true) Show(tab);
    }

    void Put(Control content)
    {
        var whole = tab == "leaderboard" && content is not TextBlock;
        fill.Content = whole ? content : null;
        body.Content = whole ? null : content;
        fill.IsVisible = whole;
        scroller.IsVisible = !whole;
    }

    public void Show(string key)
    {
        if (key == "stats" || key == "servers") key = "server";
        if (key == "code") key = "matches";
        if (key != tab) said.Text = "";
        tab = key;
        DropConfirm();
        LoadNames();
        foreach (var (k, b) in tabButtons) Pick(b, k == key);
        var mine = ++version;
        if (key == "server") { Put(ServerView(mine)); return; }
        if (data.State != LinkState.Online) { Put(Note("Not connected to this server.")); return; }
        Put(Note("Loading..."));
        _ = key switch
        {
            "leaderboard" => Leaderboard(mine),
            "profile" => Profile(mine),
            "matches" => Matches(mine),
            "tournaments" => Tournaments(mine),
            "mods" => CommunityMods(mine),
            "friends" => Friends(mine, null),
            "admin" => Admin(mine),
            _ => Task.CompletedTask,
        };
    }

    public void ShowProfile(string query)
    {
        profileOf = query;
        Show("profile");
    }

    static TextBlock Note(string text) => new() { Text = text, FontSize = 14, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 12) };

    static TextBlock Label(string text) => Caption(text);

    static TextBlock Cell(string text, double size = 13.5, bool dim = false, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Foreground = Look.Brush(dim ? Look.Dim : Look.Text),
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    static Border Panel(Control child) => new() { Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12), Child = child };

    static Grid Row(string columns, params Control[] cells)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 10 };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            g.Children.Add(cells[i]);
        }
        return g;
    }

    static Border Head(string columns, params string[] labels) =>
        new() { Padding = new Thickness(8, 0), Child = Row(columns, labels.Select(l => (Control)Cell(l, 11, true, FontWeight.Bold)).ToArray()) };

    static Button Clickable(Control content, Action click)
    {
        var b = new Button
        {
            Classes = { "link" }, Background = Brushes.Transparent, Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = content,
        };
        b.Click += (_, _) => click();
        return b;
    }

    static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> changed)
    {
        var c = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = selected, MinWidth = 120 };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(c.SelectedIndex); };
        return c;
    }

    static TextBox Field(string text, string hint, double width = 220) => new()
    {
        Text = text, PlaceholderText = hint, FontSize = 13, Width = width, Background = Look.Brush(Look.Raised), Foreground = Look.Brush(Look.Text),
        BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7),
    };

    static string? Error(JsonNode? n) => OnlineService.Text(n?["error"]);


    async Task Leaderboard(int mine)
    {
        seasons ??= await data.Stats("seasons");
        var regions = seasons?["regions"] is JsonArray ra ? ra.Select(r => OnlineService.Text(r) ?? "").ToList() : [];
        var seasonList = seasons?["seasons"] is JsonArray sa ? sa.OfType<JsonObject>().ToList() : [];
        var byLegend = ladder == "legends";
        var byWeapon = ladder == "weapons";
        var boardKey = $"{ladder}:{season}:{filter}:{search}";
        if (boardKey != boardShown) { boardOffset = 0; boardShown = boardKey; }
        var old = $"leaderboard:{ladder}:{season}:{filter}:{Uri.EscapeDataString(search)}";
        var query = byLegend ? (season > 0 ? $"legends:{season}" : "legends") : byWeapon ? (season > 0 ? $"weaponladders:{season}" : "weaponladders") :
            ladder == "all" || !pagedBoards ? old :
            $"board:{ladder}:{season}:{filter}:{boardOffset}:{BoardPage}" + (search.Length > 0 ? ":" + Uri.EscapeDataString(search) : "");
        if (query.Length > 64) query = old;
        var board = await data.Stats(query);
        if (query.StartsWith("board:") && Error(board) == "unknown query")
        {
            pagedBoards = false;
            board = await data.Stats(old);
        }
        if (mine != version) return;

        var chips = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        foreach (var (key, name) in Ladders)
            chips.Children.Add(Chip(name, ladder == key, () => { if (ladder != key) { ladder = key; boardPick = null; Show("leaderboard"); } }));
        if (ladder.StartsWith('L'))
            chips.Children.Add(Chip("1V1 AS " + (legendName.Length > 0 ? legendName : "LEGEND " + ladder[1..]).ToUpperInvariant(), true, () => { }));
        if (ladder.StartsWith('W') && ladder.Length > 1)
            chips.Children.Add(Chip("1V1 WITH " + WeaponShown(ladder[1..]).ToUpperInvariant(), true, () => { }));

        var seasonNames = new List<(int Id, string Name)> { (0, "This season") };
        seasonNames.AddRange(seasonList.Where(s => !OnlineService.Bool(s["current"])).Select(s => ((int)(OnlineService.Num(s["id"]) ?? 0), OnlineService.Text(s["name"]) ?? "Season")));
        var tools = new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Margin = new Thickness(0, 10, 0, 8) };
        tools.Children.Add(Combo(seasonNames.Select(s => s.Name), Math.Max(0, seasonNames.FindIndex(s => s.Id == season)), i => { season = seasonNames[i].Id; Show("leaderboard"); }));
        var listPanel = new DockPanel();
        Control list;
        Control? pageBar = null;
        var detail = new ContentControl();
        if (byLegend) list = LegendLadders(board);
        else if (byWeapon) list = WeaponLadders(board);
        else
        {
            var filters = new List<(string Key, string Name)> { ("all", "Everyone"), ("friends", "Friends") };
            if (regions.Count > 1) filters.AddRange(regions.Select((r, i) => ("r" + i, r)));
            tools.Children.Add(Combo(filters.Select(f => f.Name), Math.Max(0, filters.FindIndex(f => f.Key == filter)), i => { filter = filters[i].Key; Show("leaderboard"); }));
            var searchBox = Field(search, "Find a player", 200);
            searchBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { search = searchBox.Text?.Trim() ?? ""; Show("leaderboard"); } };
            tools.Children.Add(searchBox);
            var paged = board?["total"] != null;
            var team = ladder == "2v2team";
            var you = paged ? OnlineService.Num(board?["you"]?["id"]) : OnlineService.Num(board?["you"]);
            var rows = (board?["rows"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var stack = new StackPanel { Spacing = 3 };
            var buttons = new List<(string Id, Button Button)>();
            void Select(JsonObject row, string id)
            {
                boardPick = id;
                foreach (var (bid, b) in buttons) Selected(b, bid == id);
                detail.Content = Note("Loading...");
                _ = PlayerCard(detail, row, team, version);
            }
            foreach (var row in rows)
            {
                var id = row["id"]?.ToJsonString() ?? "";
                var b = BoardRow(row, you, team);
                b.Click += (_, _) => Select(row, id);
                buttons.Add((id, b));
                stack.Children.Add(b);
            }
            if (rows.Count == 0) stack.Children.Add(Note(Error(board) ?? (board == null ? "The server didn't answer." : search.Length > 0 ? "No player by that name." : "Nobody has played a rated match this season yet.")));
            if (paged && board?["you"] is JsonObject yours && rows.All(r => r["id"]?.ToJsonString() != yours["id"]?.ToJsonString()))
            {
                stack.Children.Add(new Border { Height = 1, Background = Look.Brush(Look.Line), Margin = new Thickness(8, 6) });
                var id = yours["id"]?.ToJsonString() ?? "";
                var b = BoardRow(yours, you, team);
                b.Click += (_, _) => Select(yours, id);
                buttons.Add((id, b));
                stack.Children.Add(b);
            }
            list = stack;
            if (paged) pageBar = PageBar(board!);
            var first = rows.FirstOrDefault(r => r["id"]?.ToJsonString() == boardPick) ?? (board?["you"] as JsonObject is { } y && rows.Any(r => r["id"]?.ToJsonString() == y["id"]?.ToJsonString()) ? rows.First(r => r["id"]?.ToJsonString() == y["id"]?.ToJsonString()) : rows.FirstOrDefault());
            if (first != null) Select(first, first["id"]?.ToJsonString() ?? "");
            else detail.Content = Note("Pick a player.");
        }
        var head = new StackPanel { Children = { chips, tools } };
        if (pageBar != null) head.Children.Add(pageBar);
        DockPanel.SetDock(head, Dock.Top);
        listPanel.Children.Add(head);
        listPanel.Children.Add(new Border
        {
            Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 8, 4, 8),
            Child = new ScrollViewer { Content = new Border { Padding = new Thickness(0, 0, 12, 0), Child = list }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        });
        if (byLegend || byWeapon)
        {
            Put(listPanel);
            return;
        }
        var card = new Border
        {
            Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Child = new ScrollViewer { Content = detail, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340"), ColumnSpacing = 16, Children = { listPanel, card } };
        Grid.SetColumn(card, 1);
        Put(grid);
    }

    static void Selected(Button b, bool on)
    {
        b.Background = on ? Look.Brush(Look.Raised) : Brushes.Transparent;
        b.BorderBrush = on ? Look.Brush(Teal) : Brushes.Transparent;
    }

    static readonly Color[] Podium = [Color.Parse("#F2C94C"), Color.Parse("#D5DCE6"), Color.Parse("#D08A4E")];

    Button BoardRow(JsonObject row, double? you, bool team)
    {
        var id = OnlineService.Num(row["id"]);
        var name = OnlineService.Text(row["name"]) ?? NamesOf(row) ?? "Player";
        var isYou = id != null && id == you || team && you is { } y && row["accounts"] is JsonArray acc && acc.Any(a => OnlineService.Num(a) == y);
        var rank = (int)(OnlineService.Num(row["rank"]) ?? 0);
        var rankText = new TextBlock
        {
            Text = rank > 0 ? rank.ToString() : "-", FontSize = rank is > 0 and <= 3 ? 20 : 16, FontWeight = FontWeight.Black, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = rank is > 0 and <= 3 ? new SolidColorBrush(Podium[rank - 1]) : Look.Brush(Look.Dim),
        };
        var rankBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { rankText } };
        if (Change(row) is { Text.Length: > 0 } change)
        {
            change.HorizontalAlignment = HorizontalAlignment.Center;
            rankBox.Children.Add(change);
        }
        var faces = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        if (team && row["members"] is JsonArray members)
            foreach (var m in members.OfType<JsonObject>()) faces.Children.Add(LegendFace(m["top_legend"], 38));
        else faces.Children.Add(LegendFace(row["top_legend"] ?? row["legend"], 42));
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (isYou) title.Children.Add(Pill("YOU", Teal));
        var wins = OnlineService.Num(row["wins"]) ?? 0;
        var games = OnlineService.Num(row["matches"]) ?? 0;
        var sub = games > 0 ? $"{wins:0}W {games - wins:0}L · {Percent(wins, games)}" + (!team && OnlineService.Num(row["kos"]) is > 0 and var kos ? $" · {kos:0} KOs" : "") : "No matches";
        var middle = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { title, Small(sub), WinBar(wins, games, 120) } };
        var (tier, number) = RatingParts(row["rating"]);
        var rating = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                new TextBlock { Text = number.Length > 0 ? number : "-", FontSize = 20, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Text), HorizontalAlignment = HorizontalAlignment.Right },
                new TextBlock { Text = tier.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.Black, LetterSpacing = 0.5, Foreground = new SolidColorBrush(TierColor(tier)), HorizontalAlignment = HorizontalAlignment.Right },
            },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("46,Auto,*,Auto"), ColumnSpacing = 12, MinHeight = 50, Children = { rankBox, faces, middle, rating } };
        Grid.SetColumn(faces, 1);
        Grid.SetColumn(middle, 2);
        Grid.SetColumn(rating, 3);
        var b = new Button
        {
            Classes = { "link" }, Background = Brushes.Transparent, Padding = new Thickness(6, 5, 10, 5), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = grid,
        };
        return b;
    }

    static TextBlock Change(JsonObject row)
    {
        var now = (int)(OnlineService.Num(row["rating"]?["rank"]) ?? 0);
        var cell = Cell("", 11);
        cell.FontWeight = FontWeight.Bold;
        if (now == 0 || !row.ContainsKey("rank_day")) return cell;
        var was = (int)(OnlineService.Num(row["rank_day"]) ?? 0);
        var week = (int)(OnlineService.Num(row["rank_week"]) ?? 0);
        ToolTip.SetTip(cell, (was > 0 ? $"#{was} this morning" : "New today") + (week > 0 ? $", #{week} a week ago" : ""));
        if (was == 0) { cell.Text = "NEW"; cell.Foreground = Look.Brush(Look.Accent); }
        else if (was > now) { cell.Text = "▲" + (was - now); cell.Foreground = Look.Brush(Look.Good); }
        else if (was < now) { cell.Text = "▼" + (now - was); cell.Foreground = Look.Brush(Loss); }
        return cell;
    }

    Control PageBar(JsonNode board)
    {
        var total = (int)(OnlineService.Num(board["total"]) ?? 0);
        var offset = (int)(OnlineService.Num(board["offset"]) ?? 0);
        var count = Math.Max(1, (int)(OnlineService.Num(board["count"]) ?? BoardPage));
        var pages = Math.Max(1, (total + count - 1) / count);
        var page = Math.Min(pages, offset / count + 1);
        var bar = new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        var prev = Look.Secondary("Previous", Look.Left, () => { boardOffset = Math.Max(0, offset - count); Show("leaderboard"); });
        prev.IsEnabled = offset > 0;
        var next = Look.Secondary("Next", Look.Right, () => { boardOffset = offset + count; Show("leaderboard"); });
        next.IsEnabled = offset + count < total;
        bar.Children.Add(prev);
        bar.Children.Add(next);
        bar.Children.Add(Small($"Page {page} of {pages} · {total} {(ladder == "2v2team" ? "teams" : "players")}", 13));
        if (data.SignedIn && board["you"] is JsonObject you && (int)(OnlineService.Num(you["place"]) ?? 0) is > 0 and var place)
            bar.Children.Add(Look.Link($"Find me (#{OnlineService.Num(you["rank"]) ?? place:0})", Look.Eye, () => { boardOffset = (place - 1) / count * count; boardPick = you["id"]?.ToJsonString(); Show("leaderboard"); }));
        return bar;
    }

    async Task PlayerCard(ContentControl holder, JsonObject row, bool team, int mine)
    {
        var stack = new StackPanel();
        var (tier, number) = RatingParts(row["rating"]);
        var name = OnlineService.Text(row["name"]) ?? NamesOf(row) ?? "Player";
        var faces = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (team && row["members"] is JsonArray members)
            foreach (var m in members.OfType<JsonObject>()) faces.Children.Add(LegendFace(m["top_legend"], 64, portrait: true));
        else faces.Children.Add(LegendFace(row["top_legend"] ?? row["legend"], 84, portrait: true));
        var head = new StackPanel { Spacing = 10, Margin = new Thickness(18, 18, 18, 6), Children = { faces, Big(name, 24) } };
        var ratingLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ratingLine.Children.Add(new TextBlock { Text = number.Length > 0 ? number : tier, FontSize = 22, FontWeight = FontWeight.Black, Foreground = new SolidColorBrush(TierColor(tier)) });
        if (number.Length > 0) ratingLine.Children.Add(Small(tier.ToUpperInvariant(), 12, TierColor(tier), FontWeight.Black));
        if (row["rating"] is JsonObject r && OnlineService.Num(r["peak"]) is { } peak) ratingLine.Children.Add(Small($"peak {peak:0}"));
        head.Children.Add(ratingLine);
        var wins = OnlineService.Num(row["wins"]) ?? 0;
        var games = OnlineService.Num(row["matches"]) ?? 0;
        head.Children.Add(Small(games > 0 ? $"{Count(wins, "win", "wins")} · {Count(games - wins, "loss", "losses")} · {Percent(wins, games)}" : "No matches yet", 13));
        stack.Children.Add(head);
        holder.Content = stack;
        var inner = new StackPanel { Spacing = 4, Margin = new Thickness(18, 4, 18, 18) };
        stack.Children.Add(inner);
        if (team)
        {
            if (TeamOf(row) is { } pair) inner.Children.Add(BigButton("OPEN TEAM", () => ShowProfile(TeamQuery(pair.A, pair.B))));
            return;
        }
        if (OnlineService.Num(row["id"]) is not { } acc) return;
        var p = await data.Stats("profile:a" + (long)acc);
        if (mine != version || p == null || Error(p) != null)
        {
            inner.Children.Add(BigButton("OPEN PROFILE", () => ShowProfile("profile:a" + (long)acc)));
            return;
        }
        if (p["ratings"] is JsonObject ratings && ratings.Count > 1)
        {
            inner.Children.Add(Caption("Ratings"));
            var tiles = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            foreach (var (lad, rr) in ratings) tiles.Children.Add(RatingTile(LadderTitle(lad, p), rr, 152));
            inner.Children.Add(tiles);
        }
        if (p["legends"] is JsonArray legends && legends.Count > 0)
        {
            inner.Children.Add(Caption("Most played"));
            foreach (var g in legends.OfType<JsonObject>().OrderByDescending(g => OnlineService.Num(g["matches"]) ?? 0).Take(4))
            {
                var gw = OnlineService.Num(g["wins"]) ?? 0;
                var gm = OnlineService.Num(g["matches"]) ?? 0;
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(0, 2) };
                var face = LegendFace(g["legend"], 34);
                var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Small(LegendShown(g["legend"], g["legend_name"]), 13.5, Look.Text, FontWeight.Bold), Small(Count(gm, "match", "matches")) } };
                var rate = Small(Percent(gw, gm), 13, gm > 0 && gw / gm >= 0.5 ? Look.Good : Look.Dim, FontWeight.Bold);
                line.Children.Add(face);
                line.Children.Add(words);
                line.Children.Add(rate);
                Grid.SetColumn(words, 1);
                Grid.SetColumn(rate, 2);
                inner.Children.Add(line);
            }
        }
        if (p["recent"] is JsonArray recent && recent.Count > 0)
        {
            inner.Children.Add(Caption("Last matches"));
            foreach (var m in recent.OfType<JsonObject>().Take(5)) inner.Children.Add(MatchRow(m, (long)acc, compact: true));
        }
        var open = BigButton("OPEN PROFILE", () => ShowProfile("profile:a" + (long)acc));
        open.Margin = new Thickness(0, 12, 0, 0);
        inner.Children.Add(open);
    }

    string LadderTitle(string lad, JsonNode p)
    {
        if (lad.Length > 1 && lad[0] == 'L' && long.TryParse(lad[1..], out var id)) return "1V1 AS " + LegendShown(id).ToUpperInvariant();
        return lad switch { "2v2team" => "2V2 TEAMS", _ => lad.ToUpperInvariant() };
    }

    static Control RatingTile(string title, JsonNode? r, double width)
    {
        var (tier, number) = RatingParts(r);
        var sub = r is JsonObject ro ? $"peak {OnlineService.Num(ro["peak"]) ?? 0:0} · {OnlineService.Num(ro["games"]) ?? 0:0} games" + (OnlineService.Num(ro["rank"]) is > 0 and var rk ? $" · #{rk:0}" : "") : "";
        return new Border
        {
            Width = width, Background = Look.Brush(Look.Raised), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9),
            BorderThickness = new Thickness(0, 0, 0, 3), BorderBrush = new SolidColorBrush(TierColor(tier)),
            Child = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    Small(title, 11, Look.Dim, FontWeight.Black),
                    new TextBlock { Text = number.Length > 0 ? number : tier, FontSize = 20, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Text) },
                    Small(number.Length > 0 ? tier : "", 11, TierColor(tier), FontWeight.Bold),
                    Small(sub, 11),
                },
            },
        };
    }

    StackPanel LegendLadders(JsonNode? answer)
    {
        var list = new StackPanel { Spacing = 3 };
        var rows = (answer?["legends"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var row in rows)
        {
            var id = (long)(OnlineService.Num(row["legend"]) ?? 0);
            var name = LegendShown(row["legend"], row["name"]);
            var wins = OnlineService.Num(row["wins"]) ?? 0;
            var games = OnlineService.Num(row["matches"]) ?? 0;
            var middle = new StackPanel
            {
                Spacing = 3, VerticalAlignment = VerticalAlignment.Center,
                Children = { Small(name, 15, Look.Text, FontWeight.Bold), Small($"{OnlineService.Num(row["players"]) ?? 0:0} players · {wins:0}W {OnlineService.Num(row["losses"]) ?? 0:0}L"), WinBar(wins, games, 120) },
            };
            var rate = new TextBlock { Text = Percent(wins, games), FontSize = 18, FontWeight = FontWeight.Black, Foreground = Look.Brush(games > 0 && wins / games >= 0.5 ? Look.Good : Look.Text), VerticalAlignment = VerticalAlignment.Center };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, MinHeight = 48, Children = { LegendFace(row["legend"], 44), middle, rate } };
            Grid.SetColumn(middle, 1);
            Grid.SetColumn(rate, 2);
            var b = Clickable(grid, () => { ladder = "L" + id; legendName = name; boardPick = null; Show("leaderboard"); });
            ToolTip.SetTip(b, $"The 1v1 ladder of {name} players");
            list.Children.Add(b);
        }
        if (rows.Count == 0) list.Children.Add(Note(Error(answer) ?? (answer == null ? "The server didn't answer." : "No rated 1v1 matches this season yet.")));
        return list;
    }

    static string WeaponName(string key) => WeaponShown(key);

    StackPanel WeaponLadders(JsonNode? answer)
    {
        var list = new StackPanel { Spacing = 3 };
        var rows = (answer?["weapons"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var row in rows)
        {
            if (OnlineService.Text(row["weapon"]) is not { Length: > 0 } key) continue;
            var faces = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            foreach (var l in (row["legends"] as JsonArray ?? []).Take(6)) faces.Children.Add(LegendFace(l is JsonObject lo ? lo["legend"] ?? lo["id"] : l, 26));
            var middle = new StackPanel
            {
                Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
                Children = { Small(WeaponShown(key), 15, Look.Text, FontWeight.Bold), Small($"{OnlineService.Num(row["players"]) ?? 0:0} players · {(row["legends"] as JsonArray)?.Count ?? 0} legends") },
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, MinHeight = 48, Children = { WeaponFace(key, 40), middle, faces } };
            Grid.SetColumn(middle, 1);
            Grid.SetColumn(faces, 2);
            list.Children.Add(Clickable(grid, () => { ladder = OnlineService.Text(row["ladder"]) ?? "W" + key; boardPick = null; Show("leaderboard"); }));
        }
        var why = Error(answer);
        if (rows.Count == 0)
            list.Children.Add(Note(why == "unknown query" ? "This server has no weapon ladders yet." : why ?? (answer == null ? "The server didn't answer." : "The server can't read the game's legends, so it has no weapon ladders.")));
        else
        {
            var about = Small("Each player is ranked by their best 1v1 rating on a legend that uses the weapon.", 11.5);
            about.TextWrapping = TextWrapping.Wrap;
            about.Margin = new Thickness(8, 8, 0, 0);
            list.Children.Add(about);
        }
        return list;
    }

    static string? NamesOf(JsonObject row) =>
        row["players"] is JsonArray p ? string.Join(" & ", p.Select(x => OnlineService.Text(x?["name"]) ?? "?")) :
        row["names"] is JsonArray n ? string.Join(" & ", n.Select(x => OnlineService.Text(x) ?? "?")) : null;


    async Task Profile(int mine)
    {
        var query = profileOf ?? "me";
        if (query.StartsWith("team:", StringComparison.Ordinal))
        {
            await TeamProfile(query, mine);
            return;
        }
        var p = await data.Stats(query);
        if (query == "me" && Error(p) == null && OnlineService.Num(p?["id"]) is { } myId && await data.Stats("profile:a" + (long)myId) is { } full && Error(full) == null) p = full;
        if (mine != version) return;
        var problem = p == null ? "The server didn't answer." : Error(p);
        if (problem != null || p == null)
        {
            var box = new StackPanel { Children = { Note(problem ?? "") } };
            if (profileOf is { } q && q != "me") box.Children.Add(Look.Secondary("Your profile", Look.Home, () => ShowProfile("me")));
            Put(box);
            return;
        }
        var stack = new StackPanel { Spacing = 6 };
        var name = OnlineService.Text(p["name"]) ?? "Player";
        var legends = (p["legends"] as JsonArray ?? []).OfType<JsonObject>().OrderByDescending(g => OnlineService.Num(g["matches"]) ?? 0).ToList();
        var mainRating = (p["ratings"] as JsonObject)?["1v1"] ?? (p["ratings"] as JsonObject)?.FirstOrDefault().Value;
        var (tier, number) = RatingParts(mainRating);
        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Big(name, 30) } };
        if (profileOf != null && profileOf != "me") nameLine.Children.Add(Look.Link("Back to yours", Look.Home, () => ShowProfile("me")));
        words.Children.Add(nameLine);
        if (number.Length > 0) words.Children.Add(Small($"{tier.ToUpperInvariant()} {number}", 14, TierColor(tier), FontWeight.Black));
        var totals = p["totals"];
        var tw = OnlineService.Num(totals?["wins"]) ?? 0;
        var tm = OnlineService.Num(totals?["matches"]) ?? 0;
        words.Children.Add(Small(tm > 0 ? $"{tm:0} matches · {tw:0} wins ({Percent(tw, tm)}) · {OnlineService.Num(totals?["kos"]) ?? 0:0} KOs · {OnlineService.Num(totals?["damage_dealt"]) ?? 0:0} damage" : "No matches yet", 13));
        if (OnlineService.Text(p["bio"]) is { } bio) words.Children.Add(new TextBlock { Text = bio, FontSize = 13, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 18, Children = { LegendFace(legends.FirstOrDefault()?["legend"], 110, portrait: true), words } };
        Grid.SetColumn(words, 1);
        stack.Children.Add(Panel(header));
        if (p["ratings"] is JsonObject ratings && ratings.Count > 0)
        {
            var tiles = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (lad, r) in ratings) tiles.Children.Add(RatingTile(LadderTitle(lad, p), r, 190));
            stack.Children.Add(tiles);
        }
        if (HistoryLadders(p) is { Count: > 0 } ladders && OnlineService.Num(p["id"]) is { } who)
        {
            var keys = ladders.Select(l => l.Key).ToList();
            var pick = keys.Contains(historyLadder) ? historyLadder : keys.Contains("1v1") ? "1v1" : keys[0];
            var holder = new ContentControl { Content = Note("Loading...") };
            var picker = Combo(ladders.Select(l => l.Name), keys.IndexOf(pick), i => { historyLadder = keys[i]; holder.Content = Note("Loading..."); _ = LoadHistory(holder, $"history:{(long)who}:{keys[i]}", mine); });
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Label("Rating this season"), picker } });
            stack.Children.Add(Panel(holder));
            _ = LoadHistory(holder, $"history:{(long)who}:{pick}", mine);
        }
        if (legends.Count > 0)
        {
            stack.Children.Add(Label("Legends"));
            stack.Children.Add(LegendTable(p["legends"] as JsonArray ?? []));
        }
        ProfileExtras(stack, p);
        if (OnlineService.Num(p["id"]) is { } whose && OnlineService.Num(seasons?["you"]) is { } you && (long)you == (long)whose)
        {
            var holder = new ContentControl();
            stack.Children.Add(holder);
            _ = LoadMechanics(holder, (long)whose, version);
        }
        if (p["recent"] is JsonArray recent && recent.Count > 0)
        {
            stack.Children.Add(Label("Recent matches"));
            stack.Children.Add(MatchList(recent, (long?)OnlineService.Num(p["id"])));
        }
        Put(stack);
    }

    List<(string Key, string Name)> HistoryLadders(JsonNode p)
    {
        var list = new List<(string Key, string Name)>();
        if (p["ratings"] is JsonObject rated) list.AddRange(rated.Select(kv => (kv.Key, kv.Key == "2v2team" ? "2v2 teams" : kv.Key)));
        foreach (var r in (p["legend_ratings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (OnlineService.Text(r["ladder"]) is not { Length: > 1 } key || OnlineService.Num(r["legend"]) is not { } id) continue;
            list.Add((key, "1v1 as " + LegendShown(r["legend"])));
        }
        return list;
    }

    Dictionary<long, string> LegendNames(JsonNode? legends) => LegendNames((legends as JsonArray ?? []).OfType<JsonObject>());

    Dictionary<long, string> LegendNames(IEnumerable<JsonObject> rows) =>
        rows.Where(g => OnlineService.Num(g["legend"]) != null)
            .GroupBy(g => (long)OnlineService.Num(g["legend"])!).ToDictionary(g => g.Key, g => LegendShown(g.First()["legend"], g.First()["legend_name"]));

    async Task LoadHistory(ContentControl holder, string query, int mine)
    {
        var h = await data.Stats(query);
        if (mine != version) return;
        if (RatingChart.From(h) is not { } chart)
        {
            var why = Error(h);
            holder.Content = Note(why == "unknown query" ? "This server doesn't keep rating history yet." : why ?? (h == null ? "The server didn't answer." : "No rated matches on this ladder this season."));
            return;
        }
        var total = (int)(OnlineService.Num(h!["total"]) ?? chart.Points.Count);
        var caption = $"{total} rated {(total == 1 ? "match" : "matches")} · peak {OnlineService.Num(h["peak"]) ?? 0:0}" +
                      (total > chart.Points.Count ? $" · last {chart.Points.Count} shown" : "") + " · hover the line for a match";
        holder.Content = new StackPanel { Spacing = 6, Children = { chart, Cell(caption, 12, dim: true) } };
    }

    static readonly (string Label, string Key)[] WeaponColumns =
        [("WEAPON", "weapon"), ("GAMES", "games"), ("WIN %", "win_rate"), ("KOS / GAME", "kos"), ("DAMAGE / GAME", "damage"), ("HELD / GAME", "held")];
    string weaponSort = "games";

    static double PerGame(JsonObject w, string key)
    {
        var known = OnlineService.Num(w["known"]) ?? 0;
        return known > 0 ? (OnlineService.Num(w[key]) ?? 0) / known : 0;
    }

    static double WeaponValue(JsonObject w, string key) => key switch
    {
        "games" or "win_rate" => OnlineService.Num(w[key]) ?? 0,
        _ => PerGame(w, key),
    };

    Control WeaponCell(string? key) => key is { Length: > 0 }
        ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { WeaponFace(key, 24), Cell(WeaponShown(key), weight: FontWeight.SemiBold) } }
        : Cell("?");

    async Task LoadWeapons(ContentControl holder, long account, int mine)
    {
        var answer = await data.Stats("weapons:" + account);
        if (mine != version || answer?["weapons"] is not JsonArray { Count: > 0 } list || list[0]?["games"] == null) return;
        void Draw()
        {
            const string cols = "*,70,70,90,110,100";
            var rows = list.OfType<JsonObject>().ToList();
            rows = weaponSort == "weapon" ? rows.OrderBy(w => WeaponShown(OnlineService.Text(w["weapon"]) ?? "")).ToList() : rows.OrderByDescending(w => WeaponValue(w, weaponSort)).ToList();
            var t = new StackPanel { Spacing = 2 };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions(cols), ColumnSpacing = 10, Margin = new Thickness(8, 0) };
            for (var i = 0; i < WeaponColumns.Length; i++)
            {
                var (label, key) = WeaponColumns[i];
                var b = new Button { Classes = { "link" }, Background = Brushes.Transparent, Padding = new Thickness(0), Content = Cell(label + (weaponSort == key ? " ▾" : ""), 11, !(weaponSort == key), FontWeight.Bold) };
                b.Click += (_, _) => { weaponSort = key; Draw(); };
                Grid.SetColumn(b, i);
                head.Children.Add(b);
            }
            t.Children.Add(head);
            foreach (var w in rows)
                t.Children.Add(new Border
                {
                    Padding = new Thickness(8, 4),
                    Child = Row(cols, WeaponCell(OnlineService.Text(w["weapon"])), Cell($"{OnlineService.Num(w["games"]) ?? 0:0}", dim: true),
                        Cell((OnlineService.Num(w["win_rate"]) ?? 0).ToString("P0"), dim: true), Cell(PerGame(w, "kos").ToString("0.0"), dim: true),
                        Cell(PerGame(w, "damage").ToString("0"), dim: true), Cell(Clock(PerGame(w, "held") / 1000), dim: true)),
                });
            t.Children.Add(Cell("A game counts for both of the legend's weapons. Per-game numbers come from games with a full breakdown.", 11.5, dim: true));
            holder.Content = Panel(t);
        }
        Draw();
    }

    async Task LoadHeadToHead(ContentControl holder, long me, long them, int mine)
    {
        var h = await data.Stats($"h2h:{me}:{them}");
        if (mine != version || h?["results"] is not JsonArray results) return;
        var stack = new StackPanel { Spacing = 8 };
        var total = (int)(OnlineService.Num(h["matches"]) ?? 0);
        stack.Children.Add(Label("You against them"));
        var lines = new StackPanel { Spacing = 4 };
        lines.Children.Add(Small(total == 0 ? "You haven't played each other yet." : $"{total} {(total == 1 ? "match" : "matches")}: you won {OnlineService.Num(h["wins"]) ?? 0:0}, lost {OnlineService.Num(h["losses"]) ?? 0:0}", 13.5, Look.Text));
        foreach (var pair in (h["legends"] as JsonArray ?? []).OfType<JsonObject>().Take(5))
            lines.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    LegendFace(pair["mine"], 26), Small(LegendShown(pair["mine"]), 13, Look.Text), Small("vs", 12), LegendFace(pair["theirs"], 26),
                    Small(LegendShown(pair["theirs"]), 13, Look.Text), Small($"· won {OnlineService.Num(pair["wins"]) ?? 0:0} of {OnlineService.Num(pair["matches"]) ?? 0:0}", 13),
                },
            });
        stack.Children.Add(Panel(lines));
        if (results.Count > 0) stack.Children.Add(MatchList(results, me));
        holder.Content = stack;
    }

    void ProfileExtras(StackPanel stack, JsonNode p)
    {
        string LadderName(string l) => l.Length > 1 && l[0] == 'L' && long.TryParse(l[1..], out var id) ? "1v1 as " + LegendShown(id) : l == "2v2team" ? "2v2 teams" : l;
        var badges = new List<string>();
        if (p["badges"]?["tournaments"] is JsonArray { Count: > 0 } won) badges.Add("Won " + string.Join(", ", won.OfType<JsonObject>().Select(t => OnlineService.Text(t["name"]) ?? "?")));
        if (p["badges"]?["achievements"] is JsonArray { Count: > 0 } done)
            badges.Add((done.Count == 1 ? "1 achievement" : $"{done.Count} achievements") + " (latest: " + string.Join(", ", done.OfType<JsonObject>().Take(3).Select(a => OnlineService.Text(a["name"]) ?? "?")) + ")");
        if (badges.Count > 0)
        {
            var line = new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var b in badges) line.Children.Add(Pill(b.ToUpperInvariant(), Teal));
            stack.Children.Add(line);
        }
        if (p["weapons"] is JsonArray { Count: > 0 } weapons)
        {
            var w = new StackPanel { Spacing = 2 };
            w.Children.Add(Head("*,110,130", "WEAPON", "KOS", "DAMAGE"));
            foreach (var x in weapons.OfType<JsonObject>().Take(6))
                w.Children.Add(new Border { Padding = new Thickness(8, 4), Child = Row("*,110,130", WeaponCell(OnlineService.Text(x["weapon"])), Cell($"{OnlineService.Num(x["kos"]) ?? 0:0}", dim: true), Cell($"{OnlineService.Num(x["damage"]) ?? 0:0}", dim: true)) });
            var holder = new ContentControl { Content = Panel(w) };
            stack.Children.Add(Label("Weapons"));
            stack.Children.Add(holder);
            if (OnlineService.Num(p["id"]) is { } acc) _ = LoadWeapons(holder, (long)acc, version);
        }
        if (p["vs_you"] is JsonObject && OnlineService.Num(p["id"]) is { } them && OnlineService.Num(seasons?["you"]) is { } me && (long)me != (long)them)
        {
            var holder = new ContentControl();
            stack.Children.Add(holder);
            _ = LoadHeadToHead(holder, (long)me, (long)them, version);
        }
        if (p["partners"] is JsonArray { Count: > 0 } partners)
        {
            var t = new StackPanel { Spacing = 2 };
            t.Children.Add(Head("*,90,150,170", "2V2 PARTNER", "GAMES", "RECORD", "TEAM RATING"));
            foreach (var x in partners.OfType<JsonObject>())
            {
                var games = OnlineService.Num(x["games"]) ?? 0;
                var wins = OnlineService.Num(x["wins"]) ?? 0;
                var r = Row("*,90,150,170", Cell(OnlineService.Text(x["name"]) ?? "Player", weight: FontWeight.SemiBold), Cell($"{games:0}", dim: true), Cell($"{wins:0}W {games - wins:0}L", dim: true), Cell(OnlineService.Rating(x["team"]), dim: true));
                if (OnlineService.Num(x["account"]) is { } acc && OnlineService.Num(p["id"]) is { } self)
                {
                    var b = Clickable(r, () => ShowProfile(TeamQuery((long)self, (long)acc)));
                    ToolTip.SetTip(b, "Open the team");
                    t.Children.Add(b);
                }
                else t.Children.Add(new Border { Padding = new Thickness(8, 6), Child = r });
            }
            stack.Children.Add(Label("Partners"));
            stack.Children.Add(Panel(t));
        }
        if (p["seasons"] is JsonArray { Count: > 0 } past)
        {
            var s = new StackPanel { Spacing = 2 };
            s.Children.Add(Head("*,150,150,80,130,70", "SEASON", "LADDER", "FINISHED", "PEAK", "RECORD", "RANK"));
            foreach (var x in past.OfType<JsonObject>())
            {
                var games = OnlineService.Num(x["games"]) ?? 0;
                var wins = OnlineService.Num(x["wins"]) ?? 0;
                var tier = OnlineService.Text(x["tier"]);
                var finished = new TextBlock
                {
                    Text = (tier != null && tier != "Unranked" ? tier + " " : "") + $"{OnlineService.Num(x["rating"]) ?? 0:0}", FontSize = 13.5, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(TierColor(tier)), VerticalAlignment = VerticalAlignment.Center,
                };
                s.Children.Add(new Border
                {
                    Padding = new Thickness(8, 4),
                    Child = Row("*,150,150,80,130,70", Cell(OnlineService.Text(x["name"]) ?? "Season"), Cell(LadderName(OnlineService.Text(x["ladder"]) ?? ""), dim: true), finished,
                        Cell($"{OnlineService.Num(x["peak"]) ?? 0:0}", dim: true), Cell($"{wins:0}W {games - wins:0}L", dim: true), Cell(OnlineService.Num(x["rank"]) is > 0 and var rank ? $"#{rank:0}" : "", dim: true)),
                });
            }
            stack.Children.Add(Label("Past seasons"));
            stack.Children.Add(Panel(s));
        }
    }


    StackPanel MatchList(JsonArray matches, long? account)
    {
        var list = new StackPanel { Spacing = 4 };
        foreach (var m in matches.OfType<JsonObject>()) list.Children.Add(MatchRow(m, account));
        return list;
    }

    static string ModeShown(JsonObject m)
    {
        var mode = OnlineService.Text(m["mode"]) ?? "";
        var kind = OnlineService.Text(m["kind"]) ?? "";
        var text = kind.Length > 0 && kind != "custom" && kind != "sample" ? kind : mode;
        if (kind.Length > 0 && mode.Length > 0 && mode.Contains(kind, StringComparison.OrdinalIgnoreCase)) text = mode;
        var scoring = System.Text.RegularExpressions.Regex.Match(text, @"^Scoring\s*Type\s*(\w+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (scoring.Success) text = scoring.Groups[1].Value.ToLowerInvariant();
        text = System.Text.RegularExpressions.Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ').Trim();
        if (text.Length == 0) return "";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }


    static string Length(JsonNode? duration) =>
        OnlineService.Num(duration) is { } d && d > 0 ? TimeSpan.FromSeconds(d > 10000 ? d / 1000 : d).ToString(@"m\:ss") : "";

    Button MatchRow(JsonObject m, long? account, bool compact = false)
    {
        var players = (m["players"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var me = account is { } a ? players.FirstOrDefault(x => (long?)OnlineService.Num(x["account"]) == a) : null;
        var outcome = me != null ? OnlineService.Text(me["outcome"]) ?? "" : "";
        var map = OnlineService.Text(m["map"]);
        var w = compact ? 74.0 : 132.0;
        var h = compact ? 36.0 : 62.0;
        var picture = MapPicture(map, m["level"], w, h);
        var mapName = MapShown(map, m["level"]);
        var title = new TextBlock
        {
            Text = compact ? (mapName.Length > 0 ? mapName : ModeShown(m) is { Length: > 0 } cm ? cm : "Match") : string.Join(" vs ", players.Select(x => OnlineService.Text(x["name"]) ?? "?")),
            FontSize = compact ? 13.5 : 15.5, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom,
        };
        var line = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Inlines = [] };
        if (ModeShown(m) is { Length: > 0 } modeText) line.Inlines!.Add(new Avalonia.Controls.Documents.Run(modeText.ToUpperInvariant() + "  ") { FontWeight = FontWeight.Black, Foreground = Look.Brush(Teal) });
        var sub = string.Join(" · ", new[] { compact ? "" : mapName, Length(m["duration"]), OnlineService.Ago(OnlineService.When(m["time"]), hooks.Now()) }.Where(x => x.Length > 0));
        line.Inlines.Add(new Avalonia.Controls.Documents.Run(sub) { Foreground = Look.Brush(Look.Dim) });
        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        if (outcome.Length > 0) marks.Children.Add(Pill(outcome.ToUpperInvariant(), outcome == "win" ? Look.Good : Loss));
        if (me != null && OnlineService.Num(me["rating_before"]) is { } b && OnlineService.Num(me["rating_after"]) is { } af && Math.Abs(af - b) > 0.5)
            marks.Children.Add(Small($"{af - b:+0;-0}", 12.5, af >= b ? Look.Good : Loss, FontWeight.Bold));
        var faces = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var ordered = me != null ? players.Where(x => x == me).Concat(players.Where(x => x != me)) : players;
        foreach (var x in ordered.Take(4)) faces.Children.Add(LegendFace(x["legend"], compact ? 20 : 24));
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("*,*"), ColumnSpacing = 12, RowSpacing = compact ? 1 : 4,
            Height = h, Children = { picture, title, line, marks, faces },
        };
        Grid.SetRowSpan(picture, 2);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(line, 1);
        Grid.SetRow(line, 1);
        Grid.SetColumn(marks, 2);
        Grid.SetColumn(faces, 2);
        Grid.SetRow(faces, 1);
        var row = new Button
        {
            Classes = { "link" }, Background = Look.Brush(compact ? Look.Raised : Look.Panel), Padding = new Thickness(0, 0, 12, 0), CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = grid, ClipToBounds = true,
        };
        row.Click += (_, _) => { tab = "matches"; foreach (var (k, tb) in tabButtons) Pick(tb, k == "matches"); Put(MatchDetail(m)); };
        return row;
    }

    async Task Matches(int mine)
    {
        seasons ??= await data.Stats("seasons");
        var me = data.SignedIn ? (long?)OnlineService.Num(seasons?["you"]) : null;
        if (me == null) myMatches = false;
        var old = matchSearch.Length > 0 ? "recent:" + Uri.EscapeDataString(matchSearch) : "recent";
        var paged = $"recentpage:{matchOffset}:{MatchPage}" + (matchSearch.Length > 0 ? ":" + Uri.EscapeDataString(matchSearch) : "");
        var m = myMatches ? await data.Stats($"matches:{me}:{matchOffset}:{MatchPage}") : await data.Stats(pagedRecent ? paged : old);
        if (!myMatches && pagedRecent && Error(m) == "unknown query")
        {
            pagedRecent = false;
            m = await data.Stats(old);
        }
        if (mine != version) return;
        var chips = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        chips.Children.Add(Chip("RECENT RATED", !myMatches, () => { if (myMatches) { myMatches = false; matchOffset = 0; Show("matches"); } }));
        if (me != null) chips.Children.Add(Chip("MINE", myMatches, () => { if (!myMatches) { myMatches = true; matchOffset = 0; Show("matches"); } }));
        var tools = new DockPanel { Margin = new Thickness(0, 10, 0, 8) };
        var code = Field("", "Replay code", 130);
        var get = Look.Secondary("Get replay", Look.Download, () =>
        {
            var c = (code.Text ?? "").Trim();
            if (c.Length == 0) { said.Text = "Type the replay's share code first."; return; }
            _ = Download(data.Replay(c));
        });
        ToolTip.SetTip(code, "The six-character code of a replay someone shared from the game");
        var getBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { code, get } };
        DockPanel.SetDock(getBox, Dock.Right);
        tools.Children.Add(getBox);
        if (!myMatches)
        {
            var box = Field(matchSearch, "Find a player's matches", 230);
            void Go() { matchSearch = box.Text?.Trim() ?? ""; matchOffset = 0; Show("matches"); }
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Go(); };
            box.HorizontalAlignment = HorizontalAlignment.Left;
            tools.Children.Add(box);
        }
        else tools.Children.Add(new Panel());
        var matches = m?["matches"] as JsonArray ?? [];
        var why = Error(m);
        Control list = matches.Count > 0 ? MatchList(matches, myMatches ? me : null) : Note(why == "unknown query" ? "This server can't list your matches yet." :
            why ?? (m == null ? "The server didn't answer." : myMatches ? "You haven't played on this server yet." : "No rated matches yet."));
        var content = new StackPanel { Spacing = 0, Children = { chips, tools, list } };
        if (m?["total"] != null)
        {
            var total = (int)(OnlineService.Num(m["total"]) ?? 0);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            var prev = Look.Secondary("Previous", Look.Left, () => { matchOffset = Math.Max(0, matchOffset - MatchPage); Show("matches"); });
            prev.IsEnabled = matchOffset > 0;
            var next = Look.Secondary("Next", Look.Right, () => { matchOffset += MatchPage; Show("matches"); });
            next.IsEnabled = matchOffset + MatchPage < total;
            bar.Children.Add(prev);
            bar.Children.Add(next);
            bar.Children.Add(Small(total == 0 ? "" : $"{matchOffset + 1}-{Math.Min(total, matchOffset + MatchPage)} of {total} matches", 13));
            content.Children.Insert(2, bar);
        }
        Put(content);
    }

    Control MatchDetail(JsonObject m)
    {
        var stack = new StackPanel { Spacing = 8 };
        var when = OnlineService.When(m["time"]);
        var map = OnlineService.Text(m["map"]);
        var mapName = MapShown(map, m["level"]);
        var picture = new Image { Stretch = Stretch.UniformToFill, Height = 170 };
        if (MapThumbnail(map, m["level"]) is { } path) _ = Fill(picture, Art.Picture(path, 1200));
        var sub = string.Join(" · ", new[] { ModeShown(m), Length(m["duration"]), when?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "", OnlineService.Bool(m["rated"]) ? "Rated" : "" }.Where(x => x.Length > 0));
        var bannerSub = Small(sub, 13, Look.Text);
        bannerSub.Opacity = 0.85;
        var banner = Banner(picture, 170, Big(mapName.Length > 0 ? mapName : ModeShown(m) is { Length: > 0 } bm ? bm : "Match", 28), bannerSub);
        var back = Look.Secondary("All matches", Look.Left, () => Show("matches"));
        back.Margin = new Thickness(0, 0, 0, 4);
        stack.Children.Add(back);
        var card = new StackPanel();
        card.Children.Add(banner);
        var players = new StackPanel { Spacing = 8, Margin = new Thickness(16, 14, 16, 16) };
        var list = (m["players"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var x = list[i];
            var colour = ReplaysPage.PlayerColours[i % ReplaysPage.PlayerColours.Length];
            var outcome = (OnlineService.Text(x["outcome"]) ?? "").ToUpperInvariant();
            var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            nameLine.Children.Add(new TextBlock { Text = (OnlineService.Text(x["name"]) ?? "?").ToUpperInvariant(), FontSize = 17, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Text), TextTrimming = TextTrimming.CharacterEllipsis });
            if (outcome.Length > 0) nameLine.Children.Add(Pill(outcome, outcome == "WIN" ? Look.Good : Loss));
            var stats = $"{LegendShown(x["legend"], x["legend_name"])}  ·  KOS {OnlineService.Num(x["kos"]) ?? 0:0}  ·  FALLS {OnlineService.Num(x["falls"]) ?? 0:0}  ·  DAMAGE {OnlineService.Num(x["damage_dealt"]) ?? 0:0}";
            var lines = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { nameLine, Small(stats, 12, Look.Text, FontWeight.SemiBold) } };
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            if (OnlineService.Num(x["rating_before"]) is { } b && OnlineService.Num(x["rating_after"]) is { } a)
            {
                right.Children.Add(new TextBlock { Text = $"{a:0}", FontSize = 18, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Text), HorizontalAlignment = HorizontalAlignment.Right });
                right.Children.Add(Small($"{a - b:+0;-0;0} (was {b:0})", 11.5, a >= b ? Look.Good : Loss, FontWeight.Bold));
            }
            var bar = new Border { Width = 5, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(colour) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 10, MinHeight = 54, Children = { bar, LegendFace(x["legend"], 50), lines, right } };
            Grid.SetColumn(row.Children[1], 1);
            Grid.SetColumn(lines, 2);
            Grid.SetColumn(right, 3);
            var acc = OnlineService.Num(x["account"]);
            players.Children.Add(acc is { } id ? Clickable(row, () => ShowProfile("profile:a" + (long)id)) : row);
        }
        if (OnlineService.Bool(m["replay"]) && OnlineService.Num(m["room"]) is { } room && OnlineService.Num(m["seed"]) is { } seed)
        {
            var dl = BigButton("DOWNLOAD REPLAY", () => _ = Download(data.Replay((uint)room, (uint)seed)));
            dl.HorizontalAlignment = HorizontalAlignment.Left;
            dl.Margin = new Thickness(0, 6, 0, 0);
            players.Children.Add(dl);
        }
        card.Children.Add(players);
        stack.Children.Add(new Border { Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = card });
        if (OnlineService.Num(m["id"]) is { } rid)
        {
            var holder = new ContentControl();
            stack.Children.Add(holder);
            _ = LoadBreakdown(holder, (long)rid, version);
        }
        return stack;
    }

    static readonly (string Label, string Damage, string Kos)[] Sources =
        [("UNARMED", "damage_unarmed", "ko_unarmed"), ("THROWN", "damage_thrown", "ko_thrown"), ("WEAPON 1", "damage_weapon1", "ko_weapon1"),
         ("WEAPON 2", "damage_weapon2", "ko_weapon2"), ("GADGETS", "damage_gadgets", "ko_gadgets")];

    async Task LoadBreakdown(ContentControl holder, long result, int mine)
    {
        var full = await data.Stats("match:" + result);
        if (mine != version || full?["players"] is not JsonArray players) return;
        var stack = new StackPanel { Spacing = 8 };
        var rows = players.OfType<JsonObject>().Where(x => x["detail"] is JsonObject).ToList();
        if (rows.Count > 0)
        {
            const string cols = "*,100,100,100,100,100,120";
            var table = new StackPanel { Spacing = 2 };
            table.Children.Add(Head(cols, ["PLAYER", .. Sources.Select(s => s.Label), "HELD 1 / 2"]));
            foreach (var x in rows)
            {
                var d = x["detail"]!;
                var cells = new List<Control> { Cell(OnlineService.Text(x["name"]) ?? "?", weight: FontWeight.SemiBold) };
                cells.AddRange(Sources.Select(s => (Control)Cell($"{OnlineService.Num(d[s.Damage]) ?? 0:0} ({OnlineService.Num(d[s.Kos]) ?? 0:0})", dim: true)));
                cells.Add(Cell($"{Clock((OnlineService.Num(d["held_weapon1"]) ?? 0) / 1000)} / {Clock((OnlineService.Num(d["held_weapon2"]) ?? 0) / 1000)}", dim: true));
                table.Children.Add(new Border { Padding = new Thickness(8, 4), Child = Row(cols, [.. cells]) });
            }
            stack.Children.Add(Label("Damage (KOs) by source"));
            stack.Children.Add(Panel(table));
        }
        if (MatchMechanics(players) is { } mechanics) stack.Children.Add(mechanics);
        var lines = new List<string>();
        foreach (var x in players.OfType<JsonObject>())
        {
            var parts = new List<string>();
            if (OnlineService.Num(x["legend_before"]) is { } lb && OnlineService.Num(x["legend_after"]) is { } la)
                parts.Add($"1v1 as {LegendShown(x["legend"], x["legend_name"])} {lb:0} → {la:0} ({la - lb:+0;-0;0})");
            if (OnlineService.Num(x["xp"]) is { } xp && xp > 0) parts.Add($"+{xp:0} XP");
            if (parts.Count > 0) lines.Add((OnlineService.Text(x["name"]) ?? "?") + ": " + string.Join(" · ", parts));
        }
        if (lines.Count > 0) stack.Children.Add(new TextBlock { Text = string.Join("\n", lines), FontSize = 13, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap });
        holder.Content = stack.Children.Count > 0 ? stack : null;
    }

    async Task Download(Task<ReplayDownload> getting)
    {
        if (data.State != LinkState.Online) { said.Text = "Not connected to the server."; return; }
        said.Text = "Downloading the replay...";
        var got = await getting;
        var path = OnlineService.SaveReplay(got, hooks.ReplaysDir(), out var problem);
        said.Text = path != null ? $"Saved {Path.GetFileName(path)}. It's on the Replays page." + (got.Mods.Count > 0 ? $" It was played with {got.Mods.Count} mod{(got.Mods.Count == 1 ? "" : "s")} on." : "")
            : problem ?? "The replay couldn't be saved.";
    }


    async Task Friends(int mine, JsonNode? state)
    {
        state ??= await data.Friends("list");
        if (mine != version) return;
        if (state == null) { Put(Note("The server didn't answer.")); return; }
        var stack = new StackPanel { Spacing = 8 };
        if (OnlineService.Text(state["message"]) is { } msg) said.Text = msg;
        if (!OnlineService.Bool(state["signed_in"]))
        {
            stack.Children.Add(Note("Sign in with Discord at the top right to add friends."));
            Put(stack);
            return;
        }
        var add = Field("", "Player name", 220);
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { add, Look.Secondary("Add friend", Look.Plus, () => { if (add.Text?.Trim() is { Length: > 0 } n) _ = Act("add", n); }) },
        });
        void Section(string title, string key, params (string Text, string Action)[] actions)
        {
            if (state[key] is not JsonArray arr || arr.Count == 0) return;
            var list = new StackPanel { Spacing = 2 };
            foreach (var f in arr.OfType<JsonObject>())
            {
                var online = OnlineService.Bool(f["online"]);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var id = OnlineService.Text(f["id"]) ?? "";
                foreach (var (text, action) in actions)
                    if (action != "remove" || OnlineService.Bool(f["removable"]))
                        buttons.Children.Add(Look.Link(text, action == "accept" ? Look.Check : action == "add" ? Look.Plus : Look.Close, () => _ = Act(action, action == "add" ? OnlineService.Text(f["name"]) ?? "" : id)));
                var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = Look.Brush(online ? Look.Good : Look.Line), VerticalAlignment = VerticalAlignment.Center };
                list.Children.Add(new Border
                {
                    Padding = new Thickness(8, 4),
                    Child = Row("14,*,220,Auto", dot, Cell(OnlineService.Text(f["name"]) ?? "?", weight: FontWeight.SemiBold), Cell(OnlineService.Text(f["status"]) ?? "", dim: true), buttons),
                });
            }
            stack.Children.Add(Label(title + $" ({arr.Count})"));
            stack.Children.Add(Panel(list));
        }
        Section("Requests", "incoming", ("Accept", "accept"), ("Decline", "decline"));
        Section("Friends", "friends", ("Remove", "remove"));
        Section("Sent", "outgoing", ("Cancel", "cancel"));
        Section("Played with recently", "recent", ("Add", "add"));
        Section("Online now", "online", ("Add", "add"));
        if (state["friends"] is not JsonArray { Count: > 0 }) stack.Children.Insert(1, Note("No friends on this server yet."));
        Put(stack);

        async Task Act(string action, string who)
        {
            var mine2 = ++version;
            var next = await data.Friends(action, who);
            if (mine2 == version) await Friends(mine2, next);
        }
    }


    Control ServerView(int mine)
    {
        var stack = new StackPanel { Spacing = 8 };
        var list = new StackPanel { Spacing = 8 };
        if (known.Count == 0) list.Children.Add(Note("No servers yet. Add one from the gear on Home, under Server."));
        foreach (var s in known)
        {
            var line = Small("Asking the server...", 12.5);
            line.TextWrapping = TextWrapping.Wrap;
            var current = Same(s, data.Server);
            var connect = Look.Secondary(current ? "Shown here" : "Show here", Look.Pulse, () => { serverBox.SelectedIndex = known.ToList().IndexOf(s); });
            connect.IsEnabled = !current;
            var game = Look.Secondary("Play on it", Look.Play, () => said.Text = hooks.GameUse(s) is { } why ? why : $"The game connects to {s.Address} next time it starts.");
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Cell(s.Name == s.Host ? s.Address : s.Name, 16, weight: FontWeight.Bold) } };
            if (current) title.Children.Add(Pill("SHOWN", Teal));
            list.Children.Add(Panel(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Row("*,Auto", title, Cell(s.Address, dim: true)),
                    line,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { connect, game } },
                },
            }));
            _ = Fill(s, line);
        }
        stack.Children.Add(Label("Servers"));
        stack.Children.Add(list);
        if (data.State == LinkState.Online)
        {
            var holder = new ContentControl { Content = Note("Loading...") };
            stack.Children.Add(Label("This server"));
            stack.Children.Add(holder);
            _ = ServerStats(mine, holder);
        }
        return stack;

        async Task Fill(ServerEntry s, TextBlock line)
        {
            var info = await hooks.Info(s);
            if (mine != version) return;
            line.Text = info == null ? "No answer from its web side. It may be offline or have its web port closed." :
                $"{info.Name} · {info.Players} online" + (info.Activity.Count > 0 ? " (" + string.Join(", ", info.Activity.Select(a => $"{a.Count} {a.Activity}")) + ")" : "") +
                (info.SignIn ? " · Discord sign-in" : " · no sign-in") +
                (info.Whitelist ? " · whitelist" : "") + (info.Https ? " · https" : "");
            line.Foreground = Look.Brush(info == null ? Look.Dim : Look.Text);
        }
    }
}
