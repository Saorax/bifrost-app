using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.Controls.Presenters;

namespace BifrostStudio;

public sealed class ReplaysPage : UserControl
{
    public static readonly Color Teal = Color.Parse("#2EE6C8");
    public static readonly Color Loss = Color.Parse("#FF6B81");
    public static readonly Color[] PlayerColours = [Color.Parse("#E8475D"), Color.Parse("#3D95FF"), Color.Parse("#3BE38C"), Color.Parse("#F2D33C"),
        Color.Parse("#B46CFF"), Color.Parse("#FF9A3C"), Color.Parse("#4FD6FF"), Color.Parse("#FF7FD1")];

    public static readonly (string Label, ReplayFolder Folder)[] Tabs =
        [("RECENT", ReplayFolder.Recent), ("FAVORITES", ReplayFolder.Favorites), ("OLDER", ReplayFolder.Older), ("BIFROST", ReplayFolder.Bifrost)];

    public static readonly string[] Chips = ["ALL", "WINS", "LOSSES", "1V1", "2V2", "RANKED", "NOTED"];

    const int ThumbWidth = 132;
    const int ThumbHeight = 62;
    const int IconSize = 24;

    readonly ReplayLibrary library;
    readonly AppState state;
    readonly ReplayRunner runner;
    readonly ListBox list = new() { Background = Brushes.Transparent, Padding = new Thickness(0) };
    readonly ContentControl detail = new();
    readonly TextBlock status = new() { FontSize = 13, Foreground = Look.Brush(Teal), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock empty = new() { FontSize = 14, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 16) };
    readonly TextBlock filterLine = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox search = new() { PlaceholderText = "Search names, maps, notes", Width = 220, VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox legend = Combo("Any legend");
    readonly ComboBox map = Combo("Any map");
    readonly ComboBox opponent = Combo("Any opponent");
    readonly ComboBox mode = Combo("Any mode");
    readonly ComboBox tag = Combo("Any tag");
    readonly List<(ReplayFolder Folder, Button Button)> tabButtons = [];
    readonly List<(string Chip, Button Button)> chipButtons = [];
    readonly HashSet<string> picked = new(StringComparer.Ordinal);
    readonly ContentControl main = new();
    readonly Grid body;
    IReadOnlyList<ReplayEntry> all = [];
    IReadOnlyDictionary<string, ReplayNote> notes = new Dictionary<string, ReplayNote>();
    HashSet<string> favorites = [];
    List<int> legendIds = [];
    List<string> mapNames = [];
    List<string> opponentNames = [];
    List<string> modeNames = [];
    List<string> tagNames = [];
    ReplayArt art = new(null, ReplayNames.Empty);
    ReplayFolder tab = ReplayFolder.Recent;
    bool loading;
    bool editing;
    readonly Dictionary<string, (DateTime Time, MatchStats? Stats)> framesCache = new(StringComparer.OrdinalIgnoreCase);
    string? busyFile;
    string? busyNote;
    bool busyProgress;
    readonly StageBar busyBar = new(300);
    string? noteFile;
    string? note;
    string? deleteArmed;

    public ReplaysPage(ReplayLibrary library, AppState state, ReplayRunner? runner = null)
    {
        this.library = library;
        this.state = state;
        this.runner = runner ?? new ReplayRunner(ReplayRunner.PlayerBuildDir);
        StartView = StartRealView;
        PickPlayer = (version, game, current) => ReplayPlayers.For(version, game, this.runner, current);
        KeepPlayer = KeepNow;
        var title = new TextBlock { Text = "REPLAYS", FontSize = 34, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Text) };
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Children = { accountsButton, Look.Secondary("Refresh", Look.Refresh, Reload), Look.Secondary("Open folder", Look.Folder, OpenFolder) },
        };
        accountsButton.Flyout = new Flyout { Content = accountsBox, Placement = PlacementMode.BottomEdgeAlignedRight };
        accountsButton.Click += (_, _) => FillAccounts();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { title, tools } };
        Grid.SetColumn(tools, 1);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, f) in Tabs)
        {
            var b = TabButton(label, () => PickTab(f));
            tabButtons.Add((f, b));
            tabs.Children.Add(b);
        }
        var rule = new Border { Height = 3, Background = Look.Brush(Teal), Margin = new Thickness(0, 10, 0, 14), CornerRadius = new CornerRadius(2) };
        var chips = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        foreach (var c in Chips)
        {
            var b = ChipButton(c, () => Toggle(c));
            chipButtons.Add((c, b));
            chips.Children.Add(b);
        }
        var filterButton = Look.Secondary("Filters", Look.Tune, () => { });
        filterButton.Flyout = FiltersFlyout();
        var filters = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        DockPanel.SetDock(search, Dock.Right);
        DockPanel.SetDock(filterButton, Dock.Right);
        filterButton.Margin = new Thickness(8, 0);
        filters.Children.Add(search);
        filters.Children.Add(filterButton);
        filters.Children.Add(filterLine);
        search.TextChanged += (_, _) => Apply();
        foreach (var c in new[] { legend, map, opponent, mode, tag }) c.SelectionChanged += (_, _) => Apply();
        list.ItemTemplate = new FuncDataTemplate<ReplayEntry>((e, _) => e == null ? new TextBlock() : Row(e));
        var rowBrush = new SolidColorBrush(Look.Raised);
        list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Avalonia.Styling.Setter(ListBoxItem.MarginProperty, new Thickness(0, 0, 0, 4)),
                new Avalonia.Styling.Setter(ListBoxItem.BorderThicknessProperty, new Thickness(4, 0, 0, 0)),
                new Avalonia.Styling.Setter(ListBoxItem.BorderBrushProperty, Brushes.Transparent),
            },
        });
        foreach (var pseudo in new[] { ":selected", ":pointerover", ":selected:pointerover", ":selected:focus" })
            list.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>().Class(pseudo).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Avalonia.Styling.Setter(ContentPresenter.BackgroundProperty, pseudo.Contains("selected") ? rowBrush : new SolidColorBrush(Look.Raised, 0.5)),
                    new Avalonia.Styling.Setter(ContentPresenter.BorderBrushProperty, pseudo.Contains("selected") ? new SolidColorBrush(Teal) : Brushes.Transparent),
                },
            });
        list.SelectionChanged += (_, _) =>
        {
            deleteArmed = null;
            editing = false;
            ShowDetail(list.SelectedItem as ReplayEntry);
        };
        var listPanel = new Border
        {
            Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 12, 6, 6),
            Child = new DockPanel { Children = { chips, filters, new Panel { Children = { list, empty } } } },
        };
        DockPanel.SetDock(chips, Dock.Top);
        DockPanel.SetDock(filters, Dock.Top);
        var detailPanel = new Border
        {
            Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Child = new ScrollViewer { Content = detail, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 14, Children = { listPanel, detailPanel } };
        Grid.SetColumn(detailPanel, 1);
        main.Content = body;
        var foot = new Border { Margin = new Thickness(4, 10, 0, 0), Child = status };
        var dock = new DockPanel();
        foreach (var c in new Control[] { head, tabs, rule })
        {
            DockPanel.SetDock(c, Dock.Top);
            dock.Children.Add(c);
        }
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);
        dock.Children.Add(main);
        Content = dock;
        PickTab(ReplayFolder.Recent);
        Reload();
    }

    public IReadOnlyList<ReplayEntry> All => all;
    public IReadOnlyList<ReplayEntry> Shown => list.ItemsSource as IReadOnlyList<ReplayEntry> ?? [];
    public ListBox List => list;
    public ContentControl Detail => detail;
    public TextBlock Status => status;
    public ReplayFolder Tab => tab;
    public bool Loading => loading;
    public Task? Reading { get; private set; }
    public Task? Working { get; private set; }

    readonly Button accountsButton = Look.Secondary("Your accounts", Look.Home, () => { });
    readonly StackPanel accountsBox = new() { Spacing = 8, Width = 380 };

    void FillAccounts()
    {
        accountsBox.Children.Clear();
        accountsBox.Children.Add(Caption("YOUR ACCOUNTS"));
        accountsBox.Children.Add(Dim("An ID that keeps showing up in your online replays against different people is you. Wins and losses on this page count these."));
        var (_, removed) = ReplayAccounts.Choices();
        if (library.Accounts.Count == 0) accountsBox.Children.Add(Dim("None yet. Online matches you've saved as replays show up here."));
        foreach (var a in library.Accounts)
        {
            var games = a.Wins + a.Losses;
            var words = new StackPanel
            {
                Spacing = 1, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = a.Name, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text) },
                    new TextBlock
                    {
                        Text = $"ID {a.Pid} · {a.Replays} replays" + (games > 0 ? $" · {a.Wins}W {a.Losses}L ({(double)a.Wins / games:P0})" : "") + (a.Names.Count > 1 ? " · also " + string.Join(", ", a.Names.Skip(1).Take(2)) : ""),
                        FontSize = 12, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap,
                    },
                },
            };
            var pid = a.Pid;
            var notMe = Look.Link("Not me", Look.Close, () => Choose(pid, false));
            notMe.VerticalAlignment = VerticalAlignment.Center;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { words, notMe } };
            Grid.SetColumn(notMe, 1);
            accountsBox.Children.Add(row);
        }
        if (removed.Count > 0)
        {
            accountsBox.Children.Add(Caption("NOT YOU"));
            foreach (var pid in removed.Order())
            {
                var name = all.SelectMany(e => e.Players).FirstOrDefault(p => p.Pid == pid)?.Name.Trim() ?? "";
                var back = Look.Link("It's me", Look.Check, () => Choose(pid, true));
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { Dim((name.Length > 0 ? name + " · " : "") + "ID " + pid), back } };
                Grid.SetColumn(back, 1);
                accountsBox.Children.Add(row);
            }
        }
        var box = new TextBox { PlaceholderText = "Another ID of yours", Width = 200 };
        var add = Look.Secondary("Add", Look.Plus, () =>
        {
            if (int.TryParse((box.Text ?? "").Trim(), out var id) && id > 0) Choose(id, true);
        });
        accountsBox.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Children = { box, add } });
    }

    void Choose(int pid, bool mine)
    {
        if (ReplayAccounts.Choose(pid, mine) is { } why) status.Text = why;
        accountsButton.Flyout?.Hide();
        Reload();
    }

    static Button TabButton(string text, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeight.Black, LetterSpacing = 0.5 },
            Padding = new Thickness(26, 8), MinWidth = 150, HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(4),
            Background = Look.Brush(Look.Text), Foreground = Look.Brush(Look.Back), Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Click += (_, _) => click();
        return b;
    }

    static Button ChipButton(string text, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.Bold },
            Padding = new Thickness(14, 5), CornerRadius = new CornerRadius(14), Background = Look.Brush(Look.Raised), Foreground = Look.Brush(Look.Dim),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Click += (_, _) => click();
        return b;
    }

    Flyout FiltersFlyout()
    {
        var stack = new StackPanel { Spacing = 8, Width = 260 };
        foreach (var (label, box) in new[] { ("LEGEND", legend), ("MAP", map), ("OPPONENT", opponent), ("MODE", mode), ("TAG", tag) })
        {
            box.HorizontalAlignment = HorizontalAlignment.Stretch;
            stack.Children.Add(new StackPanel { Spacing = 3, Children = { Caption(label), box } });
        }
        stack.Children.Add(Look.Secondary("Clear", Look.Close, () =>
        {
            foreach (var c in new[] { legend, map, opponent, mode, tag }) c.SelectedIndex = 0;
            search.Text = "";
            Apply();
        }));
        return new Flyout { Content = stack, Placement = PlacementMode.BottomEdgeAlignedRight };
    }

    void OpenFolder() =>
        Ui.Open(library.Replays, create: true, missing: "Can't find your replays yet. Pick the game's folder in Settings.");

    void PickTab(ReplayFolder f)
    {
        tab = f;
        foreach (var (folder, b) in tabButtons)
        {
            var on = folder == f;
            b.Background = Look.Brush(on ? Teal : Look.Text);
            b.Foreground = Look.Brush(Look.Back);
            b.Opacity = on ? 1 : 0.92;
        }
        if (main.Content != body)
        {
            CloseViewer();
            return;
        }
        Apply();
    }

    void Toggle(string chip)
    {
        if (chip == "ALL") picked.Clear();
        else if (!picked.Remove(chip)) picked.Add(chip);
        foreach (var (c, b) in chipButtons)
        {
            var on = c == "ALL" ? picked.Count == 0 : picked.Contains(c);
            b.Background = Look.Brush(on ? Teal : Look.Raised);
            b.Foreground = Look.Brush(on ? Look.Back : Look.Dim);
        }
        Apply();
    }

    public void SetChip(string chip)
    {
        if (chip == "ALL" ? picked.Count > 0 : true) Toggle(chip);
    }

    public void Reload()
    {
        if (loading) return;
        loading = true;
        status.Text = "Reading your replays...";
        var keep = (list.SelectedItem as ReplayEntry)?.Path;
        library.PlayerVersion = ReplayPlayers.LiveReplayVersion(state.Game);
        KeepPlayer();
        Reading = Task.Run(() => library.Load((done, total) => Dispatcher.UIThread.Post(() =>
        {
            if (loading) status.Text = $"Reading your replays... {done} of {total}";
        }))).ContinueWith(t => Dispatcher.UIThread.InvokeAsync(() =>
        {
            loading = false;
            all = t.IsCompletedSuccessfully ? t.Result : [];
            art = new ReplayArt(state.Game, library.Names);
            notes = library.Notes();
            favorites = library.FavoriteNames(all);
            legendIds = all.SelectMany(e => e.Players.Where((p, i) => e.Me < 0 || i == e.Me)).SelectMany(p => p.Legends).Where(id => id > 0).Distinct()
                .OrderBy(LegendName, StringComparer.OrdinalIgnoreCase).ToList();
            mapNames = all.Select(e => e.MapName).Where(m => m.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
            opponentNames = all.SelectMany(e => e.Players.Where((p, i) => i != e.Me && !p.Bot)).Select(p => p.Name.Trim()).Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            modeNames = all.Select(ModeText).Where(m => m.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
            tagNames = library.AllTags().ToList();
            Refill(legend, "Any legend", legendIds.Select(LegendName));
            Refill(map, "Any map", mapNames);
            Refill(opponent, "Any opponent", opponentNames);
            Refill(mode, "Any mode", modeNames);
            Refill(tag, "Any tag", tagNames);
            Apply(keep);
        }).GetTask()).Unwrap();
    }

    static void Refill(ComboBox box, string any, IEnumerable<string> items)
    {
        var keep = box.SelectedItem as string;
        box.Items.Clear();
        box.Items.Add(any);
        foreach (var i in items) box.Items.Add(i);
        box.SelectedIndex = keep != null && box.Items.Contains(keep) ? box.Items.IndexOf(keep) : 0;
    }

    static ComboBox Combo(params string[] items)
    {
        var box = new ComboBox { MinWidth = 140 };
        foreach (var i in items) box.Items.Add(i);
        box.SelectedIndex = 0;
        return box;
    }

    string LegendName(int id) => library.LegendNames.TryGetValue(id, out var n) ? n : "Legend " + id;

    public ReplayFilter CurrentFilter() => new(
        Folder: tab, Map: map.SelectedIndex > 0 ? map.SelectedItem as string : null,
        Legend: legend.SelectedIndex > 0 && legend.SelectedIndex - 1 < legendIds.Count ? legendIds[legend.SelectedIndex - 1] : null,
        Opponent: opponent.SelectedIndex > 0 ? opponent.SelectedItem as string : null,
        Tag: tag.SelectedIndex > 0 && tag.SelectedIndex - 1 < tagNames.Count ? tagNames[tag.SelectedIndex - 1] : null,
        Text: string.IsNullOrWhiteSpace(search.Text) ? null : search.Text.Trim());

    bool ChipsMatch(ReplayEntry e)
    {
        var results = new[] { "WINS", "LOSSES" }.Where(picked.Contains).ToList();
        if (results.Count > 0 && !results.Any(r => e.Result == (r == "WINS" ? "win" : "loss"))) return false;
        var sizes = new[] { "1V1", "2V2" }.Where(picked.Contains).ToList();
        if (sizes.Count > 0 && !sizes.Any(s => e.Size.Equals(s, StringComparison.OrdinalIgnoreCase))) return false;
        if (picked.Contains("RANKED") && !e.Ranked) return false;
        if (picked.Contains("NOTED") && !notes.ContainsKey(e.File)) return false;
        return true;
    }

    void Apply(string? keepPath = null)
    {
        var picked = list.SelectedItem as ReplayEntry;
        keepPath ??= picked?.Path;
        var modeWanted = mode.SelectedIndex > 0 ? mode.SelectedItem as string : null;
        var shown = ReplayLibrary.Filter(all, CurrentFilter(), notes).Where(ChipsMatch).Where(e => modeWanted == null || ModeText(e) == modeWanted).ToList();
        list.ItemsSource = shown;
        var parts = new List<string>();
        if (legend.SelectedIndex > 0) parts.Add("LEGEND " + legend.SelectedItem);
        if (map.SelectedIndex > 0) parts.Add("MAP " + map.SelectedItem);
        if (opponent.SelectedIndex > 0) parts.Add("VS " + opponent.SelectedItem);
        if (modeWanted != null) parts.Add(modeWanted);
        if (tag.SelectedIndex > 0) parts.Add("TAG " + tag.SelectedItem);
        filterLine.Text = parts.Count == 0 ? "NO FILTERS" : string.Join(" · ", parts).ToUpperInvariant();
        var inTab = all.Count(e => e.Folder == tab);
        if (!loading) status.Text = all.Count == 0 ? "" : (shown.Count == inTab ? $"{inTab} replays" : $"{shown.Count} of {inTab} replays")
            + (library.Accounts.Count > 0 ? " · you: " + string.Join(", ", library.Accounts.Select(a => a.Name)) : "");
        empty.Text = loading ? "Reading your replays..."
            : library.Replays == null ? "Can't find Brawlhalla yet. Pick the game's folder in Settings."
            : !library.HasKey && all.Count > 0 ? "Couldn't read these replays with your game's files. Check the game folder in Settings."
            : all.Count == 0 ? "No replays yet. Brawlhalla saves one after every match in " + library.Replays + "."
            : inTab == 0 ? (tab == ReplayFolder.Bifrost ? "No replays from Bifrost's client and server yet." : "Nothing in this folder.")
            : shown.Count == 0 ? "No replays match these filters." : "";
        empty.IsVisible = empty.Text.Length > 0;
        list.SelectedItem = shown.FirstOrDefault(e => e.Path == keepPath) ?? shown.FirstOrDefault();
        if (list.SelectedItem == null) ShowDetail(null);
    }

    public static string ModeText(ReplayEntry e)
    {
        if (!e.Indexed || e.Players.Count == 0) return "";
        var rule = "";
        if (e.Scoring.Contains("TIMED", StringComparison.OrdinalIgnoreCase) && e.Duration > 0)
            rule = e.Duration % 60 == 0 ? e.Duration / 60 + " MIN" : Clock(e.Duration * 1000);
        else if (!e.Scoring.Contains("TIMED", StringComparison.OrdinalIgnoreCase) && e.Lives > 0)
            rule = e.Lives + (e.Lives == 1 ? " LIFE" : " LIVES");
        var ranked = e.Ranked && !e.Playlist.Contains("RANKED", StringComparison.OrdinalIgnoreCase) ? "RANKED" : "";
        return Join(" · ", ranked, e.Playlist != e.Mode ? e.Playlist : "", e.Mode, rule).ToUpperInvariant();
    }

    static string Title(ReplayEntry e) => e.File.EndsWith(".replay", StringComparison.OrdinalIgnoreCase) ? e.File[..^7] : e.File;

    static string Ago(DateTime t)
    {
        var s = (DateTime.Now - t).TotalSeconds;
        if (s < 3600) return Math.Max(1, (int)(s / 60)) + " MIN AGO";
        if (s < 86400) return (int)(s / 3600) + " H AGO";
        if (s < 86400 * 30) return (int)(s / 86400) + (s < 86400 * 2 ? " DAY AGO" : " DAYS AGO");
        return t.ToString("d MMM yyyy").ToUpperInvariant();
    }

    static string Why(ReplayEntry e) => !e.Indexed ? (e.Why == "nokey" ? "NOT READ" : e.Why == "empty" ? "NO PLAYERS" : "UNREADABLE") : e.Why == "version" ? "OLD VERSION" : e.Why == "map" ? "MAP NOT FOUND" : "";

    Control Row(ReplayEntry e)
    {
        var thumb = new Image { Width = ThumbWidth, Height = ThumbHeight, Stretch = Stretch.UniformToFill };
        var thumbBox = new Border { Width = ThumbWidth, Height = ThumbHeight, CornerRadius = new CornerRadius(3), ClipToBounds = true, Background = Look.Brush(Look.Raised), Child = thumb };
        _ = Fill(thumb, art.Thumbnail(e, ThumbWidth * 2));
        var name = new TextBlock
        {
            Text = Title(e), FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var modeLine = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Inlines = [] };
        var why = Why(e);
        if (why.Length > 0) modeLine.Inlines!.Add(new Avalonia.Controls.Documents.Run(why + "  ") { FontWeight = FontWeight.Black, Foreground = Look.Brush(Loss) });
        if (ModeText(e) is { Length: > 0 } m) modeLine.Inlines!.Add(new Avalonia.Controls.Documents.Run(m + "  ") { FontWeight = FontWeight.Black, Foreground = Look.Brush(Teal) });
        var sub = Join(" · ", e.MapName, e.LengthMs > 0 ? Clock(e.LengthMs) : "");
        if (sub.Length > 0) modeLine.Inlines!.Add(new Avalonia.Controls.Documents.Run(sub) { FontSize = 12, Foreground = Look.Brush(Look.Dim) });
        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        if (e.Result.Length > 0) marks.Children.Add(Pill(e.Result == "win" ? "WIN" : "LOSS", e.Result == "win" ? Look.Good : Loss));
        if (favorites.Contains(e.File)) marks.Children.Add(Pill("FAV", Teal));
        if (library.HasStats(e)) marks.Children.Add(Pill("STATS", Look.Accent));
        if (notes.ContainsKey(e.File)) marks.Children.Add(Pill("NOTE", Look.Dim));
        marks.Children.Add(new TextBlock { Text = Ago(e.Time), FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text), VerticalAlignment = VerticalAlignment.Center });
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (var p in OrderedPlayers(e).Take(4))
        {
            if (p.Legends.Count == 0) continue;
            var img = new Image { Width = IconSize, Height = IconSize };
            var frame = new Border { Width = IconSize, Height = IconSize, CornerRadius = new CornerRadius(3), ClipToBounds = true, Background = Look.Brush(Look.Raised), Child = img };
            ToolTip.SetTip(frame, LegendName(p.Legends[0]));
            _ = Fill(img, art.Legend(p.Legends[0], IconSize));
            icons.Children.Add(frame);
        }
        name.VerticalAlignment = VerticalAlignment.Bottom;
        modeLine.VerticalAlignment = VerticalAlignment.Center;
        marks.VerticalAlignment = VerticalAlignment.Bottom;
        icons.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("*,*"), ColumnSpacing = 12, RowSpacing = 4,
            Height = ThumbHeight, Children = { thumbBox, name, modeLine, marks, icons },
        };
        Grid.SetRowSpan(thumbBox, 2);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(modeLine, 1);
        Grid.SetRow(modeLine, 1);
        Grid.SetColumn(marks, 2);
        Grid.SetColumn(icons, 2);
        Grid.SetRow(icons, 1);
        return new Border { Padding = new Thickness(0, 0, 22, 0), Child = grid };
    }

    static IEnumerable<ReplayPlayer> OrderedPlayers(ReplayEntry e) =>
        e.Me >= 0 && e.Me < e.Players.Count ? e.Players.Skip(e.Me).Take(1).Concat(e.Players.Where((_, i) => i != e.Me)) : e.Players;

    static async Task Fill(Image image, Task<Bitmap?> picture)
    {
        try
        {
            var b = await picture;
            if (b != null) image.Source = b;
        }
        catch (Exception x) when (x is IOException or InvalidOperationException or ArgumentException) { }
    }

    void Say(ReplayEntry e, string? text)
    {
        noteFile = e.File;
        note = text;
        if ((list.SelectedItem as ReplayEntry)?.Path == e.Path && main.Content == body) ShowDetail(e);
    }

    void ShowDetail(ReplayEntry? e)
    {
        if (e == null)
        {
            detail.Content = new TextBlock { Text = loading ? "" : "Pick a replay.", Foreground = Look.Brush(Look.Dim), Margin = new Thickness(18) };
            return;
        }
        if (noteFile != e.File) note = null;
        var stack = new StackPanel { Spacing = 0 };
        var banner = new Image { Stretch = Stretch.UniformToFill, Height = 190 };
        _ = Fill(banner, art.Thumbnail(e, 720));
        var shade = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 13, 15, 28), 0.25), new GradientStop(Color.FromArgb(235, 21, 24, 48), 1) },
            },
        };
        var heading = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(18, 0, 18, 12), Spacing = 2,
            Children =
            {
                new TextBlock { Text = Title(e), FontSize = 26, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Text), TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock
                {
                    Text = Join(" · ", e.Mode, ModeRule(e), e.Playlist != e.Mode ? e.Playlist : "", e.MapName, e.LengthMs > 0 ? Clock(e.LengthMs) : "", e.Time.ToString("yyyy-MM-dd HH:mm")),
                    FontSize = 13, Foreground = Look.Brush(Look.Text), Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        stack.Children.Add(new Panel { Height = 190, ClipToBounds = true, Background = Look.Brush(Look.Raised), Children = { banner, shade, heading } });
        var inner = new StackPanel { Spacing = 10, Margin = new Thickness(16, 14, 16, 16) };
        stack.Children.Add(inner);
        if (Why(e) is { Length: > 0 } why)
            inner.Children.Add(Dim(why switch
            {
                "OLD VERSION" => KeptPick(e) is { } kept
                    ? $"Made by Brawlhalla {ReplayPlayers.Shown(kept.Version)}. WATCH and GET STATS use the {ReplayPlayers.Shown(kept.Version)} replay player and game files saved before the update."
                    : e.Indexed && ReplayPlayers.LiveReplayVersion(state.Game) is > 0 and var live && e.Version > live
                    ? "Made by a newer Brawlhalla than your Bifrost. It'll play here once Bifrost is updated to that version."
                    : e.Indexed && OldCopy(e) is { } copy
                    ? $"Made by Brawlhalla {copy.Game}, replay version {e.Version}. WATCH and GET STATS play it in a saved copy of {copy.Game}."
                    : e.Indexed
                    ? $"Made by an older Brawlhalla, replay version {e.Version}. You can see its details below, but there's no copy of that version to play it in yet."
                    : "Made by a much older Brawlhalla that saved replays differently. It can't be read or played.",
                "MAP NOT FOUND" => "Its map isn't in your game's files, so it can't be shown.",
                "NOT READ" => "Couldn't read it with your game's files. Check the game folder in Settings.",
                "NO PLAYERS" => "Someone quit while the match was loading, so there are no players or map in it.",
                _ => "This replay can't be read. The file is damaged or didn't finish saving.",
            }));
        for (var i = 0; i < e.Players.Count; i++) inner.Children.Add(PlayerRow(e, i));
        if (e.Indexed && e.LengthMs > 0)
        {
            inner.Children.Add(Caption(e.Kos.Count > 0 ? "KOS · CLICK ONE TO WATCH FROM IT" : "NO KOS"));
            var timeline = new KoTimeline(e, PlayerColours);
            timeline.Picked += ms => _ = Watch(e, Math.Max(0, ms - 3000));
            inner.Children.Add(timeline);
        }
        if (library.ModsOf(e) is { } savedMods)
        {
            inner.Children.Add(Caption(savedMods.Count == 0 ? "SAVED WITH NO MODS" : "SAVED WITH MODS"));
            if (savedMods.Count > 0)
            {
                var off = library.ModsMissingNow(savedMods);
                inner.Children.Add(Dim(string.Join(" · ", savedMods.Select(m => $"{m.Name} {m.Version}" + (m.Live ? " (art only)" : "")))
                    + (off.Count == 0 ? ". The same mods are on now." :
                        ". Off or changed since: " + string.Join(", ", off.Select(m => $"{m.Name} {m.Version}")) + ". Turn them on in Mods or the replay may play differently.")));
            }
        }
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        var watch = Big(busyFile == e.File ? "WORKING..." : "WATCH", () => _ = Watch(e, -1));
        var oldCopy = e.Indexed && e.Why == "version" ? OldCopy(e) : null;
        var playable = e.Ok || oldCopy != null || KeptPick(e) != null;
        watch.IsEnabled = busyFile == null && playable;
        buttons.Children.Add(watch);
        if (StatsOf(e) is not null) buttons.Children.Add(Big("MATCH STATS", () =>
        {
            if (StatsOf(e) is { } now && Current(now)) ShowStats(e, now);
            else _ = GetStats(e);
        }));
        else
        {
            var get = Big(busyFile == e.File ? "WORKING..." : "GET STATS", () => _ = GetStats(e));
            get.IsEnabled = busyFile == null && playable;
            buttons.Children.Add(get);
        }
        buttons.Children.Add(Big(editing ? "DONE" : "TAGS", () => { editing = !editing; ShowDetail(e); }));
        var more = Big("MORE", () => { });
        more.Flyout = MoreMenu(e);
        buttons.Children.Add(more);
        inner.Children.Add(buttons);
        if (busyFile == e.File && busyProgress)
        {
            if (busyBar.Parent is Panel holder) holder.Children.Remove(busyBar);
            inner.Children.Add(busyBar);
        }
        else if (busyFile == e.File && busyNote != null) inner.Children.Add(new TextBlock { Text = busyNote, FontSize = 13, Foreground = Look.Brush(Teal), TextWrapping = TextWrapping.Wrap });
        if (note != null) inner.Children.Add(Dim(note));
        var current = notes.TryGetValue(e.File, out var n) ? n : ReplayNote.Empty;
        if (editing) NoteEditor(inner, e, current);
        else if (!current.IsEmpty)
        {
            inner.Children.Add(Caption("NOTE AND TAGS"));
            if (current.Note.Length > 0) inner.Children.Add(new TextBlock { Text = current.Note, FontSize = 13, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap });
            if (current.Tags.Count > 0)
            {
                var tags = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
                foreach (var t in current.Tags) tags.Children.Add(Pill(t.ToUpperInvariant(), Teal));
                inner.Children.Add(tags);
            }
        }
        detail.Content = stack;
    }

    static string ModeRule(ReplayEntry e) =>
        e.Scoring.Contains("TIMED", StringComparison.OrdinalIgnoreCase) && e.Duration > 0 ? (e.Duration % 60 == 0 ? e.Duration / 60 + " MIN" : Clock(e.Duration * 1000))
        : e.Lives > 0 ? e.Lives + (e.Lives == 1 ? " LIFE" : " LIVES") : "";

    Control PlayerRow(ReplayEntry e, int i)
    {
        var p = e.Players[i];
        var colour = PlayerColours[i % PlayerColours.Length];
        var bar = new Border { Width = 5, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(colour) };
        var portrait = new Image { Width = 50, Height = 50 };
        var face = new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = Look.Brush(Look.Raised), Child = portrait };
        if (p.Legends.Count > 0) _ = Fill(portrait, art.Legend(p.Legends[0], 50));
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(new TextBlock
        {
            Text = (p.Name.Trim().Length > 0 ? p.Name.Trim() : "Player " + (i + 1)).ToUpperInvariant(), FontSize = 18, FontWeight = FontWeight.Black,
            Foreground = Look.Brush(Look.Text), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (i == e.Me) name.Children.Add(Pill("YOU", Teal));
        if (e.Winner >= 0 && (e.Winner == i || e.Teams && e.Players[e.Winner].Team == p.Team) || p.Place == 1) name.Children.Add(Pill("WINNER", Look.Good));
        var lines = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        var who = Join(" · ", string.Join(", ", p.Legends.Where(l => l > 0).Select(LegendName)), e.Teams ? "Team " + p.Team : "", p.Pid != 0 ? "ID " + p.Pid : "");
        if (who.Length > 0) lines.Children.Add(new TextBlock { Text = who, FontSize = 12, Foreground = Look.Brush(Look.Dim) });
        if (p.Inputs.Actions > 0)
            lines.Children.Add(new TextBlock
            {
                Text = $"APM {p.Inputs.PerMinute(e.LengthMs)}  ·  LIGHT {p.Inputs.Light}  ·  HEAVY {p.Inputs.Heavy}  ·  DODGE {p.Inputs.Dodge}  ·  JUMP {p.Inputs.Jump}  ·  THROW {p.Inputs.Throw}",
                FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Text), Opacity = 0.85,
            });
        face.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 10, MinHeight = 56, Children = { bar, face, lines } };
        Grid.SetColumn(face, 1);
        Grid.SetColumn(lines, 2);
        return row;
    }

    Flyout MoreMenu(ReplayEntry e)
    {
        var stack = new StackPanel { Spacing = 4, Width = 230 };
        var starred = favorites.Contains(e.File);
        stack.Children.Add(Look.Link(starred ? "Remove from Favorites" : "Add to Favorites", starred ? Look.Close : Look.Tag, () =>
        {
            var why = starred ? library.RemoveFromFavorites(e) : library.AddToFavorites(e);
            Say(e, why ?? (starred ? "Taken out of Favorites." : "Added to Favorites. There's a copy in the Favorites folder."));
            Reload();
        }));
        stack.Children.Add(Look.Link("Show the file", Look.Folder, () => Ui.Reveal(e.Path)));
        if (library.FramesPath(e) is { } frames && File.Exists(frames))
            stack.Children.Add(Look.Link("Make the match view again", Look.Refresh, () =>
            {
                try { File.Delete(frames); } catch (IOException) { }
                _ = Watch(e, -1);
            }));
        if (OperatingSystem.IsWindows())
            stack.Children.Add(Look.Link(deleteArmed == e.Path ? "Click again to delete it" : "Delete", Look.Trash, () =>
            {
                if (deleteArmed != e.Path)
                {
                    deleteArmed = e.Path;
                    Say(e, "This moves the replay to the Recycle Bin. Open MORE and click again to confirm.");
                    return;
                }
                deleteArmed = null;
                var why = Recycle(e.Path);
                Say(e, why ?? "Moved to the Recycle Bin.");
                Reload();
            }));
        return new Flyout { Content = stack, Placement = PlacementMode.TopEdgeAlignedLeft };
    }

    void NoteEditor(StackPanel inner, ReplayEntry e, ReplayNote current)
    {
        inner.Children.Add(Caption("NOTE AND TAGS"));
        var text = new TextBox { Text = current.Note, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, PlaceholderText = "A note for this replay" };
        var tags = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        var chosen = current.Tags.ToList();
        void Save()
        {
            var now = library.SetNote(e.File, new ReplayNote(text.Text ?? "", chosen));
            notes = library.Notes();
            tagNames = library.AllTags().ToList();
            Refill(tag, "Any tag", tagNames);
            Say(e, now switch
            {
                null => "Not saved. Pick the game folder in Settings first.",
                true => "Saved.",
                false => "Saved for later. It's written when Brawlhalla closes.",
            });
        }
        foreach (var t in chosen.ToList())
            tags.Children.Add(Look.Secondary(t, Look.Close, () => { chosen.Remove(t); Save(); }));
        foreach (var t in tagNames.Where(t => !chosen.Contains(t, StringComparer.OrdinalIgnoreCase)).Take(12))
            tags.Children.Add(Look.Link("+ " + t, Look.Tag, () => { chosen.Add(t); Save(); }));
        var newTag = new TextBox { PlaceholderText = "New tag, then Enter", Width = 160 };
        newTag.KeyDown += (_, k) =>
        {
            if (k.Key != Key.Enter || string.IsNullOrWhiteSpace(newTag.Text)) return;
            chosen.Add(newTag.Text.Trim());
            Save();
        };
        tags.Children.Add(newTag);
        inner.Children.Add(text);
        inner.Children.Add(tags);
        inner.Children.Add(Look.Secondary("Save note", Look.Check, Save));
    }

    public Task Watch(ReplayEntry e, int atMs) => Play(e, atMs, false);

    public Task GetStats(ReplayEntry e) => Play(e, -1, true);

    public Func<int, string, bool, (ReplayPlayerPick? Pick, string? Why)> PickPlayer { get; set; }

    async Task Play(ReplayEntry e, int atMs, bool statsOnly)
    {
        if (busyFile != null) return;
        var game = state.Game;
        var (pick, why) = game != null && e.Indexed && e.Version > 0 ? PickPlayer(e.Version, game, e.Why != "version") : (null, null);
        if (e.Indexed && e.Why == "version" && pick == null)
        {
            if (OldCopy(e) is { } old)
            {
                if (statsOnly) await OldStats(e, old);
                else ShowView(e, atMs, () => StartOldView(e, old));
                return;
            }
            var live = ReplayPlayers.LiveReplayVersion(game);
            Say(e, live > 0 && e.Version > live
                ? "This replay is from a newer Brawlhalla than your Bifrost. It'll play once Bifrost is updated to that version."
                : "This replay is from an older Brawlhalla, and there's no copy of that version to play it in.");
            return;
        }
        if (!e.Indexed || (!e.Ok && e.Why != "version"))
        {
            Say(e, "This replay can't be played.");
            return;
        }
        if (game == null)
        {
            Say(e, "Can't find Brawlhalla yet. Pick the game's folder in Settings.");
            return;
        }
        if (pick == null)
        {
            Say(e, why ?? "There's no replay player for this replay's version.");
            return;
        }
        var player = pick.Runner;
        var files = pick.Game;
        if (!statsOnly)
        {
            if (!player.CanView && !(player.CanPrepare && player.HasViewSource))
            {
                Say(e, "Your Bifrost doesn't have the replay player yet. "
                    + "Update Bifrost in Settings once the next release is out.");
                return;
            }
            if (!player.CanView)
            {
                busyFile = e.File;
                Working = PrepareThenWatch(e, player, files, atMs);
                await Working;
                return;
            }
            ShowView(e, atMs, () => StartView(e, player, files));
            return;
        }
        if (library.HasFrames(e) && StatsOf(e) is { } known && Current(known))
        {
            ShowStats(e, known);
            return;
        }
        if (library.FramesPath(e) is not { } target)
        {
            Say(e, "Can't find Bifrost's files yet. Start Bifrost once or pick the game folder in Settings.");
            return;
        }
        if (!player.Prepared && !player.CanPrepare)
        {
            Say(e, "Match stats need the replay player, and your Bifrost doesn't have it yet. "
                + "Update Bifrost in Settings once the next release is out.");
            return;
        }
        busyFile = e.File;
        Working = Run(e, player, files, target);
        await Working;
    }

    async Task PrepareThenWatch(ReplayEntry e, ReplayRunner player, string game, int atMs)
    {
        try
        {
            busyNote = "Setting up the replay player. The first time takes a minute or two.";
            ShowDetail(list.SelectedItem as ReplayEntry);
            if (await player.PrepareAsync() is { } why)
            {
                busyFile = null;
                Say(e, "The replay player couldn't be built: " + why);
                return;
            }
        }
        finally
        {
            busyFile = null;
            busyNote = null;
        }
        KeepPlayer();
        if (player.CanView) ShowView(e, atMs, () => StartView(e, player, game));
        else Say(e, "The replay player is ready but can't show the game. Update Bifrost in Settings.");
    }

    async Task Run(ReplayEntry e, ReplayRunner player, string game, string target)
    {
        try
        {
            if (!player.Prepared)
            {
                busyNote = "Setting up the replay player. The first time takes a minute or two.";
                ShowDetail(list.SelectedItem as ReplayEntry);
                if (await player.PrepareAsync() is { } why)
                {
                    busyFile = null;
                    Say(e, "The replay player couldn't be built: " + why);
                    return;
                }
                KeepPlayer();
            }
            busyProgress = true;
            busyBar.Set("Starting the game", 0);
            ShowDetail(list.SelectedItem as ReplayEntry);
            var frames = await player.DumpAsync(game, e.Path, target, default, (text, part) => Dispatcher.UIThread.Post(() => busyBar.Set(text, part)));
            busyProgress = false;
            busyFile = null;
            busyNote = null;
            if (!frames.Ok)
            {
                Say(e, "The match couldn't be played: " + frames.Error);
                return;
            }
            framesCache.Remove(target);
            if (StatsOf(e) is { } stats) ShowStats(e, stats);
            else Say(e, "An older replay player made this, so there are no match stats for it. Update Bifrost, then use Make the match view again under MORE.");
        }
        finally
        {
            busyFile = null;
            busyNote = null;
            busyProgress = false;
        }
    }

    public GameReplayView? Viewer { get; private set; }

    public Func<ReplayEntry, ReplayRunner, string, ReplayView> StartView { get; set; }

    ReplayView StartRealView(ReplayEntry e, ReplayRunner player, string game)
    {
        var v = new ReplayView();
        v.Start(player, game, e.Path, 1280, 720, false);
        return v;
    }

    public Action KeepPlayer { get; set; }

    void KeepNow()
    {
        if (state.Game is { } game) _ = ReplayPlayers.KeepAsync(game, runner);
    }

    void CloseViewer()
    {
        Viewer?.Close();
        Viewer = null;
        main.Content = body;
        Apply();
    }

    public Func<ReplayEntry, HarnessCopy, ReplayView> StartOldView { get; set; } = (e, copy) =>
    {
        var v = new ReplayView();
        v.StartHarness(copy, e.Path, 1280, 720);
        return v;
    };

    public Func<int, HarnessCopy?> FindOldCopy { get; set; } = ReplayHarness.For;

    public HarnessCopy? OldCopy(ReplayEntry e) => e.Version > 0 ? FindOldCopy(e.Version) : null;

    ReplayPlayerPick? KeptPick(ReplayEntry e) =>
        e.Indexed && e.Why == "version" && e.Version > 0 && state.Game is { } game ? PickPlayer(e.Version, game, false).Pick : null;

    public Func<HarnessCopy, string, string, Task<ReplayFrames>> OldStatsRunner { get; set; } = (copy, replay, output) => ReplayHarness.StatsAsync(copy, replay, output);

    async Task OldStats(ReplayEntry e, HarnessCopy old)
    {
        if (library.HasFrames(e) && StatsOf(e) is { } known && Current(known))
        {
            ShowStats(e, known);
            return;
        }
        if (library.FramesPath(e) is not { } target)
        {
            Say(e, "Can't find Bifrost's files yet. Start Bifrost once or pick the game folder in Settings.");
            return;
        }
        busyFile = e.File;
        busyNote = $"Playing the match in Brawlhalla {old.Game}, the version that made it. This takes about 30 seconds.";
        ShowDetail(list.SelectedItem as ReplayEntry);
        var work = OldStatsRunner(old, e.Path, target);
        Working = work;
        try
        {
            var frames = await work;
            busyFile = null;
            busyNote = null;
            if (!frames.Ok)
            {
                Say(e, "The match couldn't be played: " + frames.Error);
                return;
            }
            framesCache.Remove(target);
            if (StatsOf(e) is { } stats) ShowStats(e, stats);
            else Say(e, "Brawlhalla " + old.Game + " didn't give match stats for this replay.");
        }
        finally
        {
            busyFile = null;
            busyNote = null;
        }
    }

    public void ShowGameView(ReplayEntry e, string game, int atMs) => ShowView(e, atMs, () => StartView(e, runner, game));

    void ShowView(ReplayEntry e, int atMs, Func<ReplayView> start)
    {
        ReplayView player;
        try
        {
            player = start();
        }
        catch (Exception x) when (x is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            Say(e, "The replay player couldn't start: " + x.Message);
            return;
        }
        Viewer?.Close();
        Viewer = new GameReplayView(player, e.Kos, atMs);
        var title = new TextBlock
        {
            Text = Join(" · ", Title(e), ModeText(e), e.MapName), FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var back = Look.Secondary("Back to the list", Look.Left, () =>
        {
            CloseViewer();
            list.SelectedItem = (list.ItemsSource as IEnumerable<ReplayEntry>)?.FirstOrDefault(x => x.Path == e.Path) ?? list.SelectedItem;
        });
        back.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(back, Dock.Left);
        top.Children.Add(back);
        var stats = Look.Secondary("Match stats", Look.Pulse, () => _ = GetStats(e));
        stats.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(stats, Dock.Right);
        top.Children.Add(stats);
        top.Children.Add(title);
        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(Viewer);
        main.Content = panel;
        Viewer.Focus();
    }

    public static bool Current(MatchStats stats) => stats.Players.Count == 0 || stats.Players.Any(p => p.DodgeGrid != null) || stats.Unknown.Contains("dodges");

    public MatchStats? StatsOf(ReplayEntry e)
    {
        if (library.HasFrames(e) && library.FramesPath(e) is { } path)
        {
            var time = File.GetLastWriteTimeUtc(path);
            if (!framesCache.TryGetValue(path, out var hit) || hit.Time != time)
            {
                var frames = ReplayFrames.Read(path);
                hit = (time, frames.Ok ? MatchStats.FromFrames(frames, Join(" · ", e.Mode, ModeRule(e)), e.Teams) : null);
                framesCache[path] = hit;
            }
            if (hit.Stats != null) return hit.Stats;
        }
        return library.StatsPath(e) is { } saved && File.Exists(saved) ? MatchStats.Read(saved) : null;
    }

    public static readonly string[] StatsTabs = ["SUMMARY", "DAMAGE", "ATTACKS"];

    public static IEnumerable<string> TabsFor(MatchStats stats) => StatsTabs.Where(t => t == "SUMMARY" || !stats.Unknown.Contains(t.ToLowerInvariant()));

    public void ShowStats(ReplayEntry e, MatchStats stats, string tab = "SUMMARY")
    {
        Viewer?.Close();
        Viewer = null;
        var title = new TextBlock
        {
            Text = "MATCH STATS · " + Join(" · ", Title(e), ModeText(e), e.MapName), FontSize = 16, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Text),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var back = Look.Secondary("Back to the list", Look.Left, () =>
        {
            CloseViewer();
            list.SelectedItem = (list.ItemsSource as IEnumerable<ReplayEntry>)?.FirstOrDefault(x => x.Path == e.Path) ?? list.SelectedItem;
        });
        back.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(back, Dock.Left);
        top.Children.Add(back);
        top.Children.Add(title);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var t in TabsFor(stats))
        {
            var b = TabButton(t, () => ShowStats(e, stats, t));
            b.MinWidth = 130;
            b.Background = Look.Brush(t == tab ? Teal : Look.Text);
            tabs.Children.Add(b);
        }
        Control content = tab switch
        {
            "DAMAGE" => DamageTab(stats),
            "ATTACKS" => AttacksTab(stats),
            _ => SummaryTab(stats, MatchStats.Diverges(stats, e.Kos, e.LengthMs)),
        };
        var card = new Border
        {
            Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 16),
            Child = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(tabs, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(tabs);
        panel.Children.Add(card);
        main.Content = panel;
        ShownStats = stats;
    }

    public MatchStats? ShownStats { get; private set; }

    static string Percent(int part, int whole) => whole <= 0 ? "-" : $"{100.0 * part / whole:0}%";

    Control SummaryTab(MatchStats stats, int? diverges = null)
    {
        var players = stats.Players;
        var rows = new (string Label, string Field, Func<StatsPlayer, string> Text, Func<StatsPlayer, double>? Score, int Better)[]
        {
            ("LEGEND", "", p => p.Legend, null, 0),
            ("DAMAGE DEALT", "", p => Math.Floor(p.Dealt).ToString("0"), p => p.Dealt, 1),
            ("DAMAGE TAKEN", "", p => Math.Floor(p.Taken).ToString("0"), p => p.Taken, -1),
            ("KOS", "", p => p.Kos.ToString(), p => p.Kos, 1),
            ("FALLS", "", p => p.Falls.ToString(), p => p.Falls, -1),
            ("SELF-KOS", "sds", p => p.Sds.ToString(), p => p.Sds, -1),
            ("DODGES (IN THE AIR)", "dodges", p => stats.Unknown.Contains("airDodges") ? p.Dodges.ToString() : $"{p.Dodges} ({p.AirDodges})", null, 0),
            ("DASHES", "dashes", p => p.Dashes.ToString(), null, 0),
            ("JUMPS", "jumps", p => p.Jumps.ToString(), null, 0),
            ("TIME IN THE AIR (ON WALLS)", "air", p => $"{Percent(p.AirMs, p.AirMs + p.GroundMs + p.WallMs)} ({Percent(p.WallMs, p.AirMs + p.GroundMs + p.WallMs)})", null, 0),
            ("CLASHES", "clashes", p => p.Clashes.ToString(), null, 0),
            ("COMBOS (2+ HITS)", "combos", p => p.Combos.ToString(), p => p.Combos, 1),
            ("BEST COMBO", "combos", p => p.BestHits == 0 ? "-" : $"{p.BestHits} hits, {p.BestDamage:0}", p => p.BestHits * 1000 + p.BestDamage, 1),
        };
        rows = rows.Where(r => !stats.Unknown.Contains(r.Field) && (stats.CombosKnown || r.Field != "combos")).ToArray();
        var grid = new Grid { ColumnSpacing = 18, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(230)));
        foreach (var _ in players) grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 120 });
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < players.Count; c++)
        {
            var head = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(PlayerColours[c % PlayerColours.Length]) },
                    new TextBlock { Text = players[c].Name.Trim().ToUpperInvariant(), FontSize = 16, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Text), TextTrimming = TextTrimming.CharacterEllipsis },
                },
            };
            Grid.SetColumn(head, c + 1);
            grid.Children.Add(head);
        }
        for (var r = 0; r < rows.Length; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = rows[r].Label, FontSize = 13, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, r + 1);
            grid.Children.Add(label);
            var best = rows[r].Score is { } score && players.Count > 1
                ? (rows[r].Better > 0 ? players.Max(score) : players.Min(score)) : (double?)null;
            var spread = rows[r].Score is { } sc2 && players.Count > 1 && players.Select(sc2).Distinct().Count() > 1;
            for (var c = 0; c < players.Count; c++)
            {
                var top = best != null && spread && rows[r].Score!(players[c]) == best;
                var cell = new TextBlock
                {
                    Text = rows[r].Text(players[c]), FontSize = 15, FontWeight = top ? FontWeight.Black : FontWeight.SemiBold,
                    Foreground = Look.Brush(top ? Teal : Look.Text), TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c + 1);
                grid.Children.Add(cell);
            }
        }
        if (players.Any(p => p.DodgeGrid != null))
        {
            var row = rows.Length + 1;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = "DODGE DIRECTIONS", FontSize = 13, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            Grid.SetRow(label, row);
            grid.Children.Add(label);
            for (var c = 0; c < players.Count; c++)
            {
                var g = DodgeGridView(players[c].DodgeGrid ?? [0, 0, 0, 0, 0, 0, 0, 0, 0], PlayerColours[c % PlayerColours.Length]);
                g.Margin = new Thickness(0, 12, 0, 0);
                Grid.SetRow(g, row);
                Grid.SetColumn(g, c + 1);
                grid.Children.Add(g);
            }
        }
        return new StackPanel
        {
            Spacing = 14,
            Children = { grid, Capped(Dim((stats.CombosKnown
                ? $"Match length {Clock(stats.LengthMs)}. The numbers come from the game itself. A combo is hits landed while the target is still in hitstun. The best in each row is in teal."
                : $"Match length {Clock(stats.LengthMs)}. The numbers come from the version of Brawlhalla that made this replay. Combos aren't counted for old versions yet. The best in each row is in teal.")
                + (stats.Unknown.Count > 0 ? " Stats this version of Brawlhalla didn't count yet are left out." : "")
                + (diverges is { } at ? $" From {Clock(at)} on, the replay plays out differently from the real match. That happens when it was played with Bifrost mods. These numbers follow the replay, not the real match." : "")), FooterWidth) },
        };
    }

    const double FooterWidth = 900;

    static TextBlock Capped(TextBlock text, double width)
    {
        text.MaxWidth = width;
        text.HorizontalAlignment = HorizontalAlignment.Left;
        return text;
    }

    public static Grid DodgeGridView(IReadOnlyList<int> grid, Color colour, double cell = 46)
    {
        var view = new Grid { HorizontalAlignment = HorizontalAlignment.Left, RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto") };
        var most = grid.DefaultIfEmpty(0).Max();
        var back = Look.Raised;
        for (var k = 0; k < 9; k++)
        {
            var heat = most > 0 ? grid[k] / (double)most : 0;
            var fill = Color.FromRgb((byte)(back.R + (colour.R - back.R) * heat), (byte)(back.G + (colour.G - back.G) * heat), (byte)(back.B + (colour.B - back.B) * heat));
            var box = new Border
            {
                Width = cell - 2, Height = cell - 2, Margin = new Thickness(1), Background = new SolidColorBrush(fill), CornerRadius = new CornerRadius(3),
                Child = new TextBlock { Text = grid[k].ToString(), FontSize = Math.Max(11, cell * 0.3), FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Text), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            ToolTip.SetTip(box, new[] { "Up-left", "Up", "Up-right", "Left", "No direction", "Right", "Down-left", "Down", "Down-right" }[k] + ": " + grid[k]);
            Grid.SetRow(box, k / 3);
            Grid.SetColumn(box, k % 3);
            view.Children.Add(box);
        }
        return view;
    }

    Control DamageTab(MatchStats stats)
    {
        var key = new WrapPanel { ItemSpacing = 16, LineSpacing = 6 };
        for (var i = 0; i < stats.Players.Count && i < DamageChart.Series.Length; i++)
            key.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(DamageChart.Series[i]), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = stats.Players[i].Name.Trim().ToUpperInvariant(), FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text) },
                },
            });
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Caption("EACH PLAYER'S DAMAGE OVER THE MATCH · THE LINE DROPS AT A KO"), key, new DamageChart(stats) { Height = 380 },
            },
        };
    }

    public static string AttackName(string raw)
    {
        var text = raw;
        var side = text.Length > 1 && char.IsLower(text[0]) && char.IsUpper(text[1]) ? text[0] switch { 'n' => "Neutral ", 's' => "Side ", 'd' => "Down ", _ => "" } : "";
        if (side.Length > 0) text = text[1..];
        text = System.Text.RegularExpressions.Regex.Replace(text, "([a-z])([A-Z])", "$1 $2");
        return side + text;
    }

    Control AttacksTab(MatchStats stats)
    {
        var columns = new Grid { ColumnSpacing = 18 };
        for (var c = 0; c < stats.Players.Count; c++)
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 220 });
            var p = stats.Players[c];
            var colour = PlayerColours[c % PlayerColours.Length];
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(colour) });
            var face = new Image { Width = 28, Height = 28 };
            var heroId = library.Names.HeroId(p.Hero, p.Legend);
            if (heroId > 0) _ = Fill(face, art.Legend(heroId, 28));
            stack.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = Look.Brush(Look.Raised), Child = face },
                    new TextBlock { Text = p.Name.Trim().ToUpperInvariant(), FontSize = 16, FontWeight = FontWeight.Black, Foreground = new SolidColorBrush(colour), VerticalAlignment = VerticalAlignment.Center },
                },
            });
            var shown = p.Attacks.Where(a => a.Damage > 0 || a.Hits > 0).ToList();
            var most = shown.Select(a => a.Damage).DefaultIfEmpty(1).Max();
            if (shown.Count == 0) stack.Children.Add(Dim("No hits."));
            foreach (var a in shown)
            {
                var icon = new Image { Width = 28, Height = 28 };
                _ = Fill(icon, art.Weapon(a.Weapon, 28));
                var text = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Join(" ", a.Weapon.Length > 0 ? ReplayNames.Spaced(a.Weapon) : "", AttackName(a.Name)), FontSize = 13, FontWeight = FontWeight.Bold,
                            Foreground = Look.Brush(Look.Text), TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        new TextBlock
                        {
                            Text = Math.Floor(a.Damage) + " damage, " + a.Hits + (a.Hits == 1 ? " hit" : " hits") + (a.Kos > 0 ? ", " + a.Kos + " KO" : "") + (a.Uses > 0 ? "  (used " + a.Uses + "x)" : ""),
                            FontSize = 11.5, Foreground = Look.Brush(Look.Dim),
                        },
                        DamageBar(a.Damage, most, colour, ""),
                    },
                };
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Margin = new Thickness(0, 2, 0, 4), Children = { icon, text } };
                icon.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(text, 1);
                stack.Children.Add(line);
            }
            Grid.SetColumn(stack, c);
            columns.Children.Add(stack);
        }
        return columns;
    }

    static Grid DamageBar(double value, double most, Color color, string text)
    {
        var track = new Border { Height = 10, CornerRadius = new CornerRadius(3), Background = Look.Brush(Look.Line), VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border { Height = 10, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var cell = new Panel { Children = { track, fill } };
        cell.SizeChanged += (_, s) => fill.Width = Math.Max(2, s.NewSize.Width * value / Math.Max(1, most));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var label = new TextBlock { Text = text, FontSize = 11.5, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        grid.Children.Add(cell);
        grid.Children.Add(label);
        return grid;
    }

    static Button Big(string text, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeight.Black }, MinWidth = 120, Padding = new Thickness(18, 9),
            HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(4), Background = Look.Brush(Look.Text),
            Foreground = Look.Brush(Look.Back), Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Click += (_, _) => click();
        return b;
    }

    static string? Recycle(string path)
    {
        if (!OperatingSystem.IsWindows()) return "Deleting only works on Windows.";
        var op = new ShFileOp { Func = 3, From = path + "\0\0", Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400 };
        var code = SHFileOperationW(ref op);
        return code == 0 && !op.Aborted ? null : "Couldn't move it to the Recycle Bin. Error code " + code + ".";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOp
    {
        public IntPtr Window;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr Mappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref ShFileOp op);

    public static string Clock(int ms) => $"{ms / 60000}:{ms / 1000 % 60:00}";

    static string Join(string sep, params string[] parts) => string.Join(sep, parts.Where(p => !string.IsNullOrEmpty(p)));

    static TextBlock Dim(string text) => new() { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Look.Brush(Look.Dim) };

    static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = 12, LetterSpacing = 1, FontWeight = FontWeight.Black, Foreground = Look.Brush(Look.Dim), Margin = new Thickness(0, 6, 0, 0),
    };

    static Border Pill(string text, Color color) => new()
    {
        Background = new SolidColorBrush(color, 0.2), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.Black, Foreground = new SolidColorBrush(color) },
    };
}

public sealed class KoTimeline : Control
{
    readonly ReplayEntry entry;
    readonly Color[] colours;

    public KoTimeline(ReplayEntry entry, Color[] colours)
    {
        this.entry = entry;
        this.colours = colours;
        Height = 46;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public event Action<int>? Picked;

    double End => entry.LengthMs + 2500.0;

    double X(int ms, double width) => 8 + (width - 16) * Math.Clamp(ms / End, 0, 1);

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        dc.DrawLine(new Pen(Look.Brush(Look.Line), 4), new Point(8, 14), new Point(w - 8, 14));
        foreach (var (who, ms) in entry.Kos)
        {
            var c = who >= 0 ? colours[who % colours.Length] : Look.Dim;
            dc.DrawEllipse(new SolidColorBrush(c), new Pen(Look.Brush(Look.Back), 2), new Point(X(ms, w), 14), 7, 7);
        }
        var font = new Typeface(FontFamily.Default);
        dc.DrawText(new FormattedText("0:00", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, 11, Look.Brush(Look.Dim)), new Point(6, 28));
        var end = new FormattedText(ReplaysPage.Clock(entry.LengthMs), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, 11, Look.Brush(Look.Dim));
        dc.DrawText(end, new Point(w - 8 - end.Width, 28));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var x = e.GetPosition(this).X;
        var near = entry.Kos.Select(k => (k.Ms, D: Math.Abs(X(k.Ms, Bounds.Width) - x))).OrderBy(k => k.D).FirstOrDefault();
        if (entry.Kos.Count > 0 && near.D <= 14) Picked?.Invoke(near.Ms);
    }
}
