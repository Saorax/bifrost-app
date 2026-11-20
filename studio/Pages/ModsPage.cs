using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace BifrostStudio;

public sealed class ModsPage : UserControl
{
    static readonly (string Label, string Icon, Func<ModInfo, bool> Keep)[] Filters =
    [
        ("All", Look.Folder, _ => true),
        ("Gameplay", Look.Wrench, m => m.Gameplay),
        ("Visual", Look.Eye, m => !m.Gameplay && m.Problem == null),
        ("Problems", Look.Close, m => m.State == ModState.Skipped || m.Problem != null || m.Conflicts.Count > 0),
    ];

    static readonly Color Warn = Color.Parse("#FFB547");
    static readonly Color Bad = Color.Parse("#FF6B81");

    readonly ModLibrary library;
    readonly AppState state;
    readonly ListBox list = new() { Background = Brushes.Transparent };
    readonly Border detail = new() { Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 16) };
    readonly TextBlock count = new() { FontSize = 13, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock empty = new() { FontSize = 14, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 12) };
    readonly List<Button> filterButtons = [];
    readonly TextBlock pending = new() { FontSize = 12.5, Foreground = Look.Brush(Look.Accent), TextWrapping = TextWrapping.Wrap };
    readonly Button convertNow;
    readonly ContentControl main = new();
    readonly Grid body;
    readonly Button asideButton;
    readonly Button mapArtButton;
    IReadOnlyList<ModInfo> all = [];
    int filter;
    bool aside;
    bool mapArt;
    string? mapArtNote;
    string? asideNote;
    string? note;
    string? noteFolder;

    public ModsPage(ModLibrary library, AppState state)
    {
        this.library = library;
        this.state = state;
        var head = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Children = { new TextBlock { Text = "Mods", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text) } },
        };
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                Look.Secondary("Import .bmod", Look.Plus, () => _ = PickBmods()),
                Look.Secondary("Refresh", Look.Refresh, Reload),
                Look.Secondary("Open folder", Look.Folder, () => Ui.Open(library.Folder, create: true, missing: "Brawlhalla wasn't found. Pick the game's folder in Settings.")),
            },
        };
        count.Margin = new Thickness(14, 6, 0, 0);
        Grid.SetColumn(count, 1);
        Grid.SetColumn(tools, 2);
        head.Children.Add(count);
        head.Children.Add(tools);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (var i = 0; i < Filters.Length; i++)
        {
            var at = i;
            var b = Look.Nav(Filters[i].Label, Filters[i].Icon, () => Pick(at));
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Padding = new Thickness(12, 7);
            filterButtons.Add(b);
            bar.Children.Add(b);
        }
        mapArtButton = Look.Nav("Map art", Look.Monitor, ShowMapArt);
        mapArtButton.HorizontalAlignment = HorizontalAlignment.Left;
        mapArtButton.Padding = new Thickness(12, 7);
        mapArtButton.Margin = new Thickness(12, 0, 0, 0);
        bar.Children.Add(mapArtButton);
        asideButton = Look.Nav("Set aside", Look.Trash, ShowAside);
        asideButton.HorizontalAlignment = HorizontalAlignment.Left;
        asideButton.Padding = new Thickness(12, 7);
        bar.Children.Add(asideButton);
        list.ItemTemplate = new FuncDataTemplate<ModInfo>((m, _) => m == null ? new TextBlock() : Row(m));
        list.SelectionChanged += (_, _) => ShowDetail(list.SelectedItem as ModInfo);
        body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,360"), ColumnSpacing = 16,
            Children = { new Panel { Children = { list, empty } }, new ScrollViewer { Content = detail } },
        };
        Grid.SetColumn(body.Children[1], 1);
        convertNow = Look.Secondary("Convert now", Look.Refresh, () => _ = ConvertWaitingAsync());
        convertNow.VerticalAlignment = VerticalAlignment.Center;
        convertNow.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(convertNow, Dock.Right);
        pending.VerticalAlignment = VerticalAlignment.Center;
        var pendingRow = new DockPanel { Children = { convertNow, pending } };
        var top = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 0, 8), Children = { head, bar, pendingRow } };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        main.Content = body;
        dock.Children.Add(main);
        Content = dock;
        Reload();
    }

    public void Reload()
    {
        var keep = (list.SelectedItem as ModInfo)?.Folder;
        all = library.Load();
        var bmods = library.PendingBmods();
        pending.Text = bmods.Count == 0 ? "" : (bmods.Count == 1 ? "1 .bmod waits" : $"{bmods.Count} .bmod files wait") +
            " for Brawlhalla to convert at its next start: " + string.Join(", ", bmods.Select(b => Path.GetFileName(b)));
        pending.IsVisible = bmods.Count > 0;
        convertNow.IsVisible = bmods.Count > 0;
        Apply(keep);
        if (aside) main.Content = AsideView();
        if (mapArt) main.Content = MapArtView();
    }

    public void ShowMapArt()
    {
        mapArt = true;
        aside = false;
        Reload();
    }

    Control MapArtView()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Dim("Pick the art each map uses from the map art packs that are on. " +
            "Shuffle picks one pack each match, and Mass random mixes their pieces."));
        if (mapArtNote != null) stack.Children.Add(new TextBlock { Text = mapArtNote, FontSize = 12.5, Foreground = Look.Brush(Look.Accent), TextWrapping = TextWrapping.Wrap });
        var maps = library.MapArt(all);
        if (maps.Count == 0) stack.Children.Add(new TextBlock { Text = "No map art packs are on.", FontSize = 14, Foreground = Look.Brush(Look.Dim), Margin = new Thickness(4, 12) });
        foreach (var m in maps)
        {
            var values = new List<string> { "default" };
            values.AddRange(m.Packs.Select(p => p.Id));
            values.AddRange(["shuffle", "mass"]);
            var box = new ComboBox { MinWidth = 240, ItemsSource = values.Select(v => ModLibrary.MapArtChoiceName(v, m.Packs)).ToList(), SelectedIndex = values.IndexOf(m.Choice) };
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedIndex < 0 || values[box.SelectedIndex] == m.Choice) return;
                var queued = library.SetMapArt(m.Level, values[box.SelectedIndex]);
                mapArtNote = queued == false ? $"{m.Name} is saved for later. It's written when Brawlhalla closes." : $"{m.Name}: {box.SelectedItem}.";
                main.Content = MapArtView();
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = m.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Text) },
                    Dim(m.Level + " · " + string.Join(", ", m.Packs.Select(p => p.Name))),
                },
            });
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            stack.Children.Add(new Border { Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Child = row });
        }
        return new ScrollViewer { Content = stack };
    }

    public string ImportFiles(IEnumerable<string> files)
    {
        var (said, ids) = ConvertFiles(files);
        foreach (var id in ids) library.SwitchOn(id);
        Reload();
        return said;
    }

    (string Said, List<string> Ids) ConvertFiles(IEnumerable<string> files) => Summarize(files.Select(library.ImportBmod).ToList());

    public string ConvertWaiting()
    {
        var (said, ids) = Summarize(library.PendingBmods().Select(library.ConvertWaiting).ToList());
        foreach (var id in ids) library.SwitchOn(id);
        Reload();
        return said;
    }

    async Task ConvertWaitingAsync()
    {
        var waiting = library.PendingBmods();
        if (waiting.Count == 0) return;
        convertNow.IsEnabled = false;
        pending.Text = "Converting " + (waiting.Count == 1 ? Path.GetFileName(waiting[0]) : waiting.Count + " .bmod files") + "...";
        var (said, ids) = await Task.Run(() => Summarize(waiting.Select(library.ConvertWaiting).ToList()));
        foreach (var id in ids) library.SwitchOn(id);
        convertNow.IsEnabled = true;
        Reload();
        pending.Text = said + (string.IsNullOrEmpty(pending.Text) ? "" : " " + pending.Text);
        pending.IsVisible = true;
    }

    static (string Said, List<string> Ids) Summarize(List<Bmod.BmodConvertResult> results)
    {
        var ids = new List<string>();
        var made = new List<string>();
        var waiting = 0;
        var problems = new List<string>();
        foreach (var r in results)
        {
            switch (r.Outcome)
            {
                case Bmod.BmodOutcome.Converted:
                case Bmod.BmodOutcome.Already:
                    made.Add(r.Message);
                    if (r.Outcome == Bmod.BmodOutcome.Converted && r.Id != null) ids.Add(r.Id);
                    break;
                case Bmod.BmodOutcome.ForTheGame:
                    waiting++;
                    break;
                default:
                    problems.Add(r.File + ": " + r.Message.TrimEnd('.') + ".");
                    break;
            }
        }
        var parts = new List<string>();
        if (made.Count > 0) parts.Add(string.Join(" ", made));
        if (waiting > 0) parts.Add($"Imported {waiting}. Brawlhalla converts {(waiting == 1 ? "it" : "them")} into mods at its next start.");
        parts.AddRange(problems);
        return (string.Join(" ", parts), ids);
    }

    async Task PickBmods()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import .bmod mods", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Brawlhalla mods") { Patterns = ["*.bmod"] }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;
        pending.Text = "Converting " + (paths.Count == 1 ? Path.GetFileName(paths[0]) : paths.Count + " .bmod files") + "...";
        pending.IsVisible = true;
        var (said, ids) = await Task.Run(() => ConvertFiles(paths));
        foreach (var id in ids) library.SwitchOn(id);
        Reload();
        pending.Text = said + (string.IsNullOrEmpty(pending.Text) ? "" : " " + pending.Text);
        pending.IsVisible = true;
    }

    void Pick(int i)
    {
        filter = i;
        aside = false;
        mapArt = false;
        main.Content = body;
        Apply((list.SelectedItem as ModInfo)?.Folder);
    }

    public void ShowAside()
    {
        aside = true;
        mapArt = false;
        Reload();
    }

    Control AsideView()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Dim("Mods you removed in the game, or that a newer download replaced, are kept here. " +
            "Restore puts one back and sets aside whatever was in its place."));
        if (asideNote != null) stack.Children.Add(new TextBlock { Text = asideNote, FontSize = 12.5, Foreground = Look.Brush(Look.Accent), TextWrapping = TextWrapping.Wrap });
        var items = library.Aside();
        if (items.Count == 0) stack.Children.Add(new TextBlock { Text = "Nothing set aside.", FontSize = 14, Foreground = Look.Brush(Look.Dim), Margin = new Thickness(4, 12) });
        foreach (var a in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = a.Name + (a.Version.Length > 0 ? "  v" + a.Version : ""), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Text) },
                    Dim((a.Kind == "removed" ? "Removed " : "Replaced ") + a.When.ToString("d MMM yyyy, HH:mm") + " · folder " + a.Folder),
                },
            });
            var restore = Look.Secondary("Restore", Look.Refresh, () =>
            {
                asideNote = library.Restore(a) ?? $"{a.Name} is back in the mods folder. Brawlhalla loads it at its next start.";
                Reload();
                main.Content = AsideView();
            });
            restore.IsEnabled = !state.GameRunning;
            var show = Look.Secondary("Show folder", Look.Folder, () => Ui.Open(a.Path));
            Grid.SetColumn(restore, 1);
            Grid.SetColumn(show, 2);
            row.Children.Add(restore);
            row.Children.Add(show);
            stack.Children.Add(new Border { Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Child = row });
        }
        if (state.GameRunning && items.Count > 0) stack.Children.Add(Dim("Close Brawlhalla to restore mods."));
        return new ScrollViewer { Content = stack };
    }

    void Apply(string? keep)
    {
        asideButton.Classes.Set("selected", aside);
        mapArtButton.Classes.Set("selected", mapArt);
        mapArtButton.IsVisible = mapArt || library.MapArt(all).Count > 0;
        for (var i = 0; i < filterButtons.Count; i++) filterButtons[i].Classes.Set("selected", !aside && !mapArt && i == filter);
        var shown = all.Where(Filters[filter].Keep).ToList();
        list.ItemsSource = shown;
        var on = all.Count(m => m.State == ModState.Loaded);
        count.Text = all.Count == 0 ? "" : $"{all.Count} mod{(all.Count == 1 ? "" : "s")}, {on} loaded";
        empty.Text = library.Folder == null ? "Bifrost isn't installed yet. Install it from Home first."
            : all.Count == 0 ? "No mods yet. Press Open folder and put a mod's folder, with its mod.json, in there."
            : shown.Count == 0 ? "No mod fits this filter." : "";
        empty.IsVisible = shown.Count == 0;
        list.SelectedItem = shown.FirstOrDefault(m => m.Folder == keep) ?? shown.FirstOrDefault();
        if (list.SelectedItem == null) ShowDetail(null);
    }

    Control Row(ModInfo m)
    {
        var (text, color) = Chip(m);
        var chip = new Border
        {
            Background = new SolidColorBrush(color, 0.16), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = new SolidColorBrush(color) },
        };
        var kind = new TextBlock
        {
            Text = m.IsPack ? (m.RoomsRequired ? "Modpack · rooms" : "Modpack") : m.Problem != null ? "" : m.Gameplay ? "Gameplay" : "Visual",
            FontSize = 11.5, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        var parts = new[]
        {
            m.IsPack ? $"{m.Members!.Count} mod{(m.Members.Count == 1 ? "" : "s")}" : "", m.InPack != null ? "in " + m.PackName : "",
            m.Version.Length > 0 ? "v" + m.Version : "", m.Author, m.Id ?? m.Folder,
        };
        var title = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = m.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Text), TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock
                {
                    Text = string.Join(" · ", parts.Where(s => s.Length > 0)),
                    FontSize = 11.5, Foreground = Look.Brush(Look.Dim), TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(m.InPack != null ? 26 : 4, 6, 4, 6), Children = { title, kind, chip },
        };
        if (m.InPack != null)
        {
            var line = new Border { Width = 2, Background = Look.Brush(Look.Raised), Margin = new Thickness(-16, -6, 0, -6), HorizontalAlignment = HorizontalAlignment.Left };
            row.Children.Insert(0, line);
        }
        Grid.SetColumn(kind, 1);
        Grid.SetColumn(chip, 2);
        chip.Margin = new Thickness(10, 0, 0, 0);
        return row;
    }

    static (string, Color) Chip(ModInfo m) => m.State switch
    {
        ModState.Loaded when m.Off => ("Off at next start", Warn),
        ModState.Loaded => ("Loaded", Look.Good),
        ModState.Off when !m.Off && m.StateText.Contains("switched off") => ("On at next start", Look.Accent),
        ModState.Off => ("Off", Look.Dim),
        ModState.Skipped => ("Not loaded", Bad),
        _ => (m.Off ? "Off" : "New", Look.Accent),
    };

    void Select(string folder)
    {
        var x = all.FirstOrDefault(m => m.Folder == folder);
        if (x == null) return;
        if (aside || mapArt || !Filters[filter].Keep(x))
        {
            filter = 0;
            aside = false;
            mapArt = false;
            main.Content = body;
        }
        Apply(folder);
    }

    Control MemberLine(int n, ModInfo x)
    {
        var (text, color) = Chip(x);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        g.Children.Add(new TextBlock { Text = n + ".", FontSize = 12.5, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock
        {
            Text = x.Name + (x.Version.Length > 0 ? "  v" + x.Version : ""), FontSize = 13, Foreground = Look.Brush(Look.Text),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        var st = new TextBlock { Text = text, FontSize = 11.5, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(st, 2);
        g.Children.Add(name);
        g.Children.Add(st);
        var b = new Button
        {
            Classes = { "link" }, Background = Brushes.Transparent, Padding = new Thickness(8, 5), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = g,
        };
        b.Click += (_, _) => Select(x.Folder);
        return b;
    }

    void ShowDetail(ModInfo? m)
    {
        if (m == null)
        {
            detail.Child = new TextBlock { Text = "Pick a mod.", FontSize = 13, Foreground = Look.Brush(Look.Dim) };
            return;
        }
        var stack = new StackPanel { Spacing = 8 };
        if (m.Preview != null)
        {
            try
            {
                stack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10), ClipToBounds = true, MaxHeight = 180,
                    Child = new Image { Source = new Bitmap(m.Preview), Stretch = Stretch.UniformToFill },
                });
            }
            catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
            }
        }
        stack.Children.Add(new TextBlock { Text = m.Name, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(Dim(string.Join(" · ", new[] { m.Version.Length > 0 ? "Version " + m.Version : "", m.Author.Length > 0 ? "by " + m.Author : "",
            m.IsPack ? $"Modpack of {m.Members!.Count} mod{(m.Members.Count == 1 ? "" : "s")}" : m.Problem != null ? "" : m.Gameplay ? "Gameplay mod" : "Visual mod" }
            .Where(s => s.Length > 0))));
        if (m.Description.Length > 0) stack.Children.Add(new TextBlock { Text = m.Description, FontSize = 13, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap });
        var (chip, color) = Chip(m);
        stack.Children.Add(new TextBlock { Text = Status(m, chip), FontSize = 13, Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.Wrap });
        if (m.Problem != null) stack.Children.Add(Dim("Its mod.json: " + m.Problem));
        if (m.ChangedSinceReport) stack.Children.Add(Dim("Its mod.json changed since the game last started. The game reads it at its next start."));
        if (m.RoomsRequired)
            stack.Children.Add(Dim((m.IsPack ? "Required in rooms. Everyone in an online room needs this pack at the same version. " :
                "Its pack is required in rooms. Everyone in an online room needs that pack. ") + "It counts as a gameplay mod, so queues don't take it."));
        else if (m.Gameplay) stack.Children.Add(Dim("Gameplay mods change matches. Online, everyone in a room needs the same ones."));
        if (m.IsPack)
        {
            stack.Children.Add(Section("MODS IN THIS PACK, IN LOAD ORDER"));
            var n = 0;
            foreach (var name in m.Members!)
            {
                n++;
                if (all.FirstOrDefault(x => x.Folder == m.Folder + "/" + name) is { } member) stack.Children.Add(MemberLine(n, member));
            }
            stack.Children.Add(Dim("A later mod wins where two change the same thing. " + (m.RoomsRequired ? "" :
                "The pack isn't required in rooms, so only the gameplay mods in it have to match.")));
            if (m.LoadOrder != null) stack.Children.Add(Dim("The game loaded: " + m.LoadOrder + "."));
        }
        if (m.InPack != null && all.FirstOrDefault(x => x.IsPack && x.Id == m.InPack && m.Folder.StartsWith(x.Folder + "/")) is { } pack)
        {
            var at = pack.Members!.ToList().IndexOf(m.Folder[(pack.Folder.Length + 1)..]) + 1;
            stack.Children.Add(Section("IN A MODPACK"));
            stack.Children.Add(Dim($"Part of {pack.Name}, number {at} of {pack.Members!.Count} in its load order. " +
                (pack.Off ? "The pack is switched off, so this mod is off too. Switch the pack on first." : "Switching the pack off switches this mod off too.")));
            var open = Look.Link("Show the pack", Look.Left, () => Select(pack.Folder));
            open.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(open);
        }
        if (m.Requires.Count > 0)
        {
            stack.Children.Add(Section("REQUIRES"));
            foreach (var r in m.Requires)
            {
                var have = all.FirstOrDefault(x => x.Id == r);
                stack.Children.Add(Dim(r + (have == null ? ": missing" : have.Disabled ? ": switched off" : "")));
            }
        }
        var needed = all.Where(x => x.Requires.Contains(m.Id ?? "\0")).Select(x => x.Name).ToList();
        if (needed.Count > 0) stack.Children.Add(Dim("Needed by " + string.Join(", ", needed) + (m.Off ? "" : ". Switching this off switches them off too.")));
        if (m.Conflicts.Count > 0)
        {
            stack.Children.Add(Section("OVERRIDES OTHER MODS"));
            foreach (var c in m.Conflicts.Take(8)) stack.Children.Add(Dim(c));
            if (m.Conflicts.Count > 8) stack.Children.Add(Dim($"and {m.Conflicts.Count - 8} more"));
        }
        if (m.Identity != null) stack.Children.Add(Dim("Identity " + m.Identity));
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        if (m.Id != null && !m.PackOff)
        {
            buttons.Children.Add(Look.Secondary((m.Disabled ? "Switch on" : "Switch off") + (m.IsPack ? " the pack" : ""), m.Disabled ? Look.Check : Look.Stop, () =>
            {
                var queued = library.SetDisabled(m, !m.Disabled);
                noteFolder = m.Folder;
                note = queued switch
                {
                    null => "Bifrost isn't installed, so there is nowhere to save it.",
                    false => "Saved for later. It's written when Brawlhalla closes and used at its next start.",
                    _ => "Saved. Brawlhalla uses it at its next start.",
                };
                Reload();
            }));
        }
        buttons.Children.Add(Look.Secondary("Show folder", Look.Folder, () => Ui.Open(m.Path)));
        stack.Children.Add(buttons);
        if (noteFolder != m.Folder) note = null;
        if (note != null) stack.Children.Add(Dim(note));
        detail.Child = stack;
    }

    static string Status(ModInfo m, string chip) => m.State switch
    {
        ModState.Loaded when m.Off => "Off at next start. The game has it loaded now.",
        ModState.Loaded => "Loaded by the game",
        ModState.Off when chip == "On at next start" => "On at next start. The game has it off now.",
        ModState.Off => "Off: " + m.StateText.Replace("off: ", ""),
        ModState.Skipped => "Not loaded: " + m.StateText.Replace("not loaded: ", ""),
        _ => m.Off ? "Off: " + m.StateText : "New: " + m.StateText,
    };

    static TextBlock Dim(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Look.Brush(Look.Dim) };

    static TextBlock Section(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Dim), Margin = new Thickness(0, 8, 0, 0),
    };
}
