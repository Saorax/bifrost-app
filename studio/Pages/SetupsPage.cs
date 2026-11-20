using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace BifrostStudio;

public sealed class SetupsPage : UserControl
{
    readonly SetupLibrary library;
    readonly AppState state;
    readonly ListBox list = new() { Background = Brushes.Transparent };
    readonly Border detail = new() { Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 16) };
    readonly TextBlock count = new() { FontSize = 13, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock said = new() { FontSize = 12.5, Foreground = Look.Brush(Look.Accent), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock empty = new() { FontSize = 14, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 12) };
    readonly ComboBox legend = new() { MinWidth = 180 };
    readonly ComboBox tag = new() { MinWidth = 150 };
    readonly Button libraryButton, gameButton;
    List<Setup> all = [];
    Dictionary<string, List<Setup>> inGame = [];
    bool gameView;
    string? note;
    string? noteFor;

    public SetupsPage(SetupLibrary library, AppState state)
    {
        this.library = library;
        this.state = state;
        var head = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Children = { new TextBlock { Text = "Legend setups", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text) } },
        };
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                Look.Secondary("Paste codes", Look.Plus, () => _ = PasteCodes()),
                Look.Secondary("Import file", Look.Folder, () => _ = ImportFile()),
                Look.Secondary("Copy all codes", Look.Copy, () => _ = CopyText(SetupLibrary.Export(Shown()), $"Copied {Shown().Count} codes.")),
            },
        };
        count.Margin = new Thickness(14, 6, 0, 0);
        Grid.SetColumn(count, 1);
        Grid.SetColumn(tools, 2);
        head.Children.Add(count);
        head.Children.Add(tools);
        libraryButton = Look.Nav("Library", Look.Tag, () => { gameView = false; Reload(); });
        gameButton = Look.Nav("In the game", Look.Play, () => { gameView = true; Reload(); });
        foreach (var b in new[] { libraryButton, gameButton })
        {
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Padding = new Thickness(12, 7);
        }
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { libraryButton, gameButton } };
        legend.Margin = new Thickness(16, 0, 0, 0);
        bar.Children.Add(legend);
        bar.Children.Add(tag);
        legend.SelectionChanged += (_, _) => Apply();
        tag.SelectionChanged += (_, _) => Apply();
        list.ItemTemplate = new FuncDataTemplate<Setup>((s, _) => s == null ? new TextBlock() : Row(s));
        list.SelectionChanged += (_, _) => ShowDetail(list.SelectedItem as Setup);
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,360"), ColumnSpacing = 16,
            Children = { new Panel { Children = { list, empty } }, new ScrollViewer { Content = detail } },
        };
        Grid.SetColumn(body.Children[1], 1);
        var top = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 0, 8), Children = { head, bar, said } };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(body);
        Content = dock;
        Reload();
    }

    public void Reload()
    {
        all = library.Load();
        inGame = library.InGame();
        libraryButton.Classes.Set("selected", !gameView);
        gameButton.Classes.Set("selected", gameView);
        var source = Source();
        Refill(legend, "Any legend", source.Select(s => s.Legend).Distinct().Order(StringComparer.OrdinalIgnoreCase));
        Refill(tag, "Any tag", all.SelectMany(s => s.Tags).Distinct().Order(StringComparer.OrdinalIgnoreCase));
        tag.IsVisible = !gameView;
        Apply();
    }

    List<Setup> Source() => gameView ? inGame.SelectMany(kv => kv.Value).ToList() : all;

    public List<Setup> Shown()
    {
        var l = legend.SelectedIndex > 0 ? legend.SelectedItem as string : null;
        var t = tag.SelectedIndex > 0 && !gameView ? tag.SelectedItem as string : null;
        return Source().Where(s => (l == null || s.Legend == l) && (t == null || s.Tags.Contains(t)))
            .OrderBy(s => s.Legend, StringComparer.OrdinalIgnoreCase).ToList();
    }

    void Apply()
    {
        var keep = (list.SelectedItem as Setup)?.Code();
        var shown = Shown();
        list.ItemsSource = shown;
        count.Text = $"{all.Count} in the library, {inGame.Sum(kv => kv.Value.Count)} in the game";
        empty.Text = gameView ? "The game has no saved setups yet. Save one on the legend select screen or send one from the library."
            : all.Count == 0 ? "No setups yet. Paste BHSETUP1: codes, import a text file of them, or add the game's own from In the game."
            : "No setup fits these filters.";
        empty.IsVisible = shown.Count == 0;
        list.SelectedItem = shown.FirstOrDefault(s => s.Code() == keep) ?? shown.FirstOrDefault();
        if (list.SelectedItem == null) ShowDetail(null);
    }

    static void Refill(ComboBox box, string any, IEnumerable<string> items)
    {
        var keep = box.SelectedItem as string;
        var all = new List<string> { any };
        all.AddRange(items);
        box.ItemsSource = all;
        box.SelectedIndex = keep != null && all.Contains(keep) ? all.IndexOf(keep) : 0;
    }

    bool InGame(Setup s) => inGame.TryGetValue(s.Legend, out var l) && l.Any(x => x.Code() == (s with { Tags = [] }).Code());

    bool InLibrary(Setup s) => all.Any(x => x.Code() == s.Code());

    Control Row(Setup s)
    {
        var mark = gameView ? (InLibrary(s) ? "in the library" : "") : (InGame(s) ? "in the game" : "");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 6) };
        row.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = s.Legend + " · " + s.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Text), TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = string.Join(" · ", new[] { s.Skin, s.Colour, s.Tags.Count > 0 ? "#" + string.Join(" #", s.Tags) : null }.Where(x => !string.IsNullOrEmpty(x))),
                    FontSize = 11.5, Foreground = Look.Brush(Look.Dim), TextTrimming = TextTrimming.CharacterEllipsis },
            },
        });
        var m = new TextBlock { Text = mark, FontSize = 11.5, Foreground = Look.Brush(Look.Good), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(m, 1);
        row.Children.Add(m);
        return row;
    }

    void ShowDetail(Setup? s)
    {
        if (s == null)
        {
            detail.Child = new TextBlock { Text = "Pick a setup.", FontSize = 13, Foreground = Look.Brush(Look.Dim) };
            return;
        }
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = s.Name, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(Dim(s.Legend));
        foreach (var (label, value) in new[]
                 {
                     ("Skin", s.Skin), ("Colour", s.Colour), ("Weapon 1 skin", s.Weapon1), ("Weapon 2 skin", s.Weapon2), ("Sidekick", s.Sidekick),
                     ("Companion", s.Companion == "" ? "none" : s.Companion), ("KO effect", s.Emitter), ("Stance", s.Stance?.ToString()),
                 })
        {
            if (string.IsNullOrEmpty(value)) continue;
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
            line.Children.Add(Dim(label));
            var v = new TextBlock { Text = value, FontSize = 12.5, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(v, 1);
            line.Children.Add(v);
            stack.Children.Add(line);
        }
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Look.Secondary("Copy code", Look.Copy, () => _ = CopyText(s.Code(), "Code copied. Paste it in the game's setup list or send it to a friend.")));
        if (gameView)
        {
            if (!InLibrary(s))
                buttons.Children.Add(Look.Secondary("Add to library", Look.Plus, () =>
                {
                    library.Save(library.Load().Append(s));
                    Say("Added to the library.");
                }));
        }
        else
        {
            var tags = new TextBox { Text = string.Join(", ", s.Tags), PlaceholderText = "tags, comma separated", Width = 220 };
            stack.Children.Add(Dim("TAGS"));
            stack.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    tags,
                    Look.Secondary("Save", Look.Check, () =>
                    {
                        var list2 = library.Load();
                        var i = list2.FindIndex(x => x.Code() == s.Code());
                        if (i < 0) return;
                        list2[i] = s with { Tags = (tags.Text ?? "").Split(',').Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList() };
                        library.Save(list2);
                        Say("Tags saved.");
                    }),
                },
            });
            var there = InGame(s);
            buttons.Children.Add(Look.Secondary(there ? "Remove from the game" : "Send to the game", there ? Look.Close : Look.Play, () =>
            {
                var r = there ? library.RemoveFromGame(s) : library.SendToGame(s);
                Say(r switch
                {
                    null => "Bifrost isn't installed, so there is nowhere to send it.",
                    false => "Saved for later. It's written when Brawlhalla closes.",
                    _ => there ? "Taken out of the game's setups." : $"Sent. It shows in {s.Legend}'s setup list next time you start the game.",
                });
            }));
            buttons.Children.Add(Look.Secondary("Delete", Look.Trash, () =>
            {
                library.Save(library.Load().Where(x => x.Code() != s.Code()));
                Say("Deleted from the library. Any copy in the game stays.");
            }));
        }
        stack.Children.Add(buttons);
        if (noteFor != s.Code()) note = null;
        if (note != null) stack.Children.Add(Dim(note));
        detail.Child = stack;
    }

    void Say(string text)
    {
        note = text;
        noteFor = (list.SelectedItem as Setup)?.Code();
        Reload();
        ShowDetail(list.SelectedItem as Setup);
    }

    public void ImportText(string text)
    {
        var (added, skipped) = library.Import(text);
        said.Text = added == 0 && skipped == 0 ? "No BHSETUP1: codes found." : $"Imported {added}" + (skipped > 0 ? $", {skipped} skipped because they were already here or unreadable." : ".");
        Reload();
    }

    async Task PasteCodes()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
        ImportText(await clip.TryGetTextAsync() ?? "");
    }

    async Task ImportFile()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import setup codes", AllowMultiple = true });
        var text = string.Join("\n", files.Select(f => f.TryGetLocalPath()).OfType<string>().Select(p =>
        {
            try { return File.ReadAllText(p); } catch (IOException) { return ""; }
        }));
        if (files.Count > 0) ImportText(text);
    }

    async Task CopyText(string text, string done)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
        await clip.SetTextAsync(text);
        said.Text = done;
    }

    static TextBlock Dim(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Look.Brush(Look.Dim) };
}
