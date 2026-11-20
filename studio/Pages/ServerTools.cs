using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BifrostStudio;

public sealed record ServerToolsHooks(string Folder, Func<bool> Running, Func<bool> ConsoleReady, Func<DateTime> Now, Func<Task> Stop, Action<string> OpenFile, Action<string> OpenFolder, Func<string?> Python);

public sealed class ServerTools : UserControl
{
    public static readonly (string Key, string Name, string Icon)[] Sections =
    [
        ("console", "Console", Look.Logs), ("settings", "All settings", Look.Tune), ("join", "Who can join", Look.Eye),
        ("bugs", "Bug reports", Look.Wrench), ("flags", "Integrity", Look.Pulse), ("backups", "Backups", Look.Download),
    ];

    static readonly string[] Quick = ["who", "mutes", "bans", "reports", "mods", "announcements", "admins", "help"];

    readonly ServerToolsHooks hooks;
    readonly ServerConsole console;
    readonly TextBlock said = new() { FontSize = 12.5, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap };
    string section = "console";
    string? armed;
    DateTime armedUntil;
    string answer = "";
    string? bugListing;
    (int Id, string Text)? bugOpen;
    string flagsText = "";
    int flagDays = 1;
    int version;

    public ServerTools(ServerToolsHooks hooks)
    {
        this.hooks = hooks;
        console = new ServerConsole(Path.Combine(hooks.Folder, "server.log"));
    }

    public string Section => section;

    public void Show(string key)
    {
        section = key;
        said.Text = "";
        Content = Build();
    }

    public void Rebuild() => Content = Build();

    Control Build()
    {
        version++;
        (said.Parent as Panel)?.Children.Remove(said);
        return section switch
        {
            "settings" => Settings(),
            "join" => Join(),
            "bugs" => Bugs(),
            "flags" => Flags(),
            "backups" => Backups(),
            _ => ConsoleBox(),
        };
    }

    bool Confirmed(string key, string message)
    {
        if (armed == key && DateTime.UtcNow < armedUntil)
        {
            armed = null;
            return true;
        }
        armed = key;
        armedUntil = DateTime.UtcNow.AddSeconds(6);
        said.Text = message;
        return false;
    }

    static TextBlock Text(string text, double size = 13, bool dim = false, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = Look.Brush(dim ? Look.Dim : Look.Text), TextWrapping = TextWrapping.Wrap,
        FontWeight = weight ?? FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center,
    };

    static TextBlock Head(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11, LetterSpacing = 1.5, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Dim), Margin = new Thickness(0, 6, 0, 0),
    };

    static Border Panel(Control child) => new() { Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12), Child = child };

    static TextBox Field(string text, string hint, double width = 320) => new()
    {
        Text = text, PlaceholderText = hint, FontSize = 13, Width = width, HorizontalAlignment = HorizontalAlignment.Left,
        Background = Look.Brush(Look.Raised), Foreground = Look.Brush(Look.Text), BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7),
    };

    static SelectableTextBlock Mono(string text) => new()
    {
        Text = text, FontFamily = Ui.Mono, FontSize = 12.5, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap,
    };

    static StackPanel Line(params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    async Task<string> Ask(string line)
    {
        if (!hooks.ConsoleReady()) return "The server wasn't started from this app, so you can't reach its console.";
        var text = await console.AskAsync(line, TimeSpan.FromSeconds(5));
        return text.Length > 0 ? text : "No answer after 5 seconds. Check the log.";
    }

    Control ConsoleBox()
    {
        var ready = hooks.ConsoleReady();
        var box = Field("", ready ? "a console command, like say Restarting in 5 minutes" : "start the server to use its console", 520);
        box.FontFamily = Ui.Mono;
        box.IsEnabled = ready;
        var mine = version;
        async Task Go(string line)
        {
            line = line.Trim();
            if (line.Length == 0) return;
            box.Text = "";
            answer = $"> {line}\n" + await Ask(line);
            if (mine == version) Content = Build();
        }
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) _ = Go(box.Text ?? ""); };
        var send = Look.Secondary("Send", Look.Play, () => _ = Go(box.Text ?? ""));
        send.IsEnabled = ready;
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var q in Quick)
        {
            var b = Look.Link(q, Look.Logs, () => _ = Go(q));
            b.IsEnabled = ready;
            chips.Children.Add(b);
        }
        var stack = new StackPanel { Spacing = 8, Children = { Line(box, send), chips } };
        if (answer.Length > 0) stack.Children.Add(Panel(Mono(answer)));
        stack.Children.Add(Text("Commands go straight to the server's console, and the answer also shows in the log below. "
                                + "Send help to list them all.", 12.5, true));
        return stack;
    }

    Control Settings()
    {
        var folder = hooks.Folder;
        var running = hooks.Running();
        var local = ServerSettings.Local(folder, out var problem);
        var shared = ServerSettings.Shared(folder);
        var stack = new StackPanel { Spacing = 10 };
        if (local == null)
        {
            stack.Children.Add(Text($"config.local.json can't be read ({problem}). Fix it by hand or delete it. Saving is off until then.", dim: true));
            return stack;
        }
        var initial = new Dictionary<string, string>();
        var read = new Dictionary<string, Func<string>>();
        foreach (var group in ServerSettings.Groups)
        {
            stack.Children.Add(Head(group));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,340,*"), RowSpacing = 6, ColumnSpacing = 12 };
            foreach (var s in ServerSettings.All.Where(s => s.Group == group))
            {
                var current = ServerSettings.Show(s, local[s.Key]);
                initial[s.Key] = current;
                var fallback = ServerSettings.Show(s, shared[s.Key]) is { Length: > 0 } sh ? sh : s.Default;
                var (editor, value) = Editor(s, current, fallback);
                editor.IsEnabled = !running;
                read[s.Key] = value;
                var row = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var name = new TextBlock { Text = s.Key, FontFamily = Ui.Mono, FontSize = 12.5, Foreground = Look.Brush(Look.Text), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var help = Text(s.Help, 12, true);
                Grid.SetRow(name, row);
                Grid.SetRow(editor, row);
                Grid.SetColumn(editor, 1);
                Grid.SetRow(help, row);
                Grid.SetColumn(help, 2);
                grid.Children.Add(name);
                grid.Children.Add(editor);
                grid.Children.Add(help);
            }
            stack.Children.Add(Panel(grid));
        }
        var other = local.Select(p => p.Key).Where(k => ServerSettings.Find(k) == null).ToList();
        if (other.Count > 0) stack.Children.Add(Text("Also in config.local.json, kept as they are: " + string.Join(", ", other), 12.5, true));
        var save = Look.Secondary("Save settings", Look.Check, () =>
        {
            var edits = read.Where(p => p.Value() != initial[p.Key]).ToDictionary(p => p.Key, p => p.Value());
            if (edits.Count == 0) { said.Text = "Nothing changed."; return; }
            if (hooks.Running()) { said.Text = "Stop the server first. It only reads its settings when it starts."; return; }
            var why = ServerSettings.Save(folder, edits);
            said.Text = why ?? $"Saved {edits.Count} setting{(edits.Count == 1 ? "" : "s")} to config.local.json. They take effect the next time the server starts.";
            if (why == null) Content = Build();
        });
        save.IsEnabled = !running;
        var bar = Line(save);
        if (running) bar.Children.Add(Look.Secondary("Stop the server", Look.Stop, () => _ = StopThen()));
        bar.Children.Add(Look.Link("Open config.local.json", Look.Folder, () => hooks.OpenFile(ServerSettings.LocalFile(folder))));
        stack.Children.Insert(0, new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Text(running ? "The server is running. Stop it to change these, since it only reads them when it starts."
                    : "Leave a field empty to use the default shown in grey. Changes go into config.local.json, and any other keys in it are kept.", 12.5, true),
                bar, said,
            },
        });
        return new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    async Task StopThen()
    {
        said.Text = "Stopping the server...";
        await hooks.Stop();
        Content = Build();
    }

    static (Control Editor, Func<string> Value) Editor(ServerSetting s, string current, string fallback)
    {
        if (s.Kind is SettingKind.Toggle or SettingKind.Choice)
        {
            var options = s.Kind == SettingKind.Toggle ? new[] { "true", "false" } : s.Choices!;
            var labels = new List<string> { $"default ({Label(s, fallback)})" };
            labels.AddRange(options.Select(o => Label(s, o)));
            var combo = new ComboBox { ItemsSource = labels, Width = 340, FontSize = 13 };
            var at = Array.IndexOf(options, current);
            if (at < 0 && current.Length > 0)
            {
                labels.Add(current);
                combo.SelectedIndex = labels.Count - 1;
                return (combo, () => combo.SelectedIndex == 0 ? "" : combo.SelectedIndex <= options.Length ? options[combo.SelectedIndex - 1] : current);
            }
            combo.SelectedIndex = at + 1;
            return (combo, () => combo.SelectedIndex <= 0 ? "" : options[combo.SelectedIndex - 1]);
        }
        var box = Field(current, fallback.Length > 0 ? fallback : "(none)", 340);
        if (s.Kind == SettingKind.Secret) box.PasswordChar = '•';
        if (s.Kind is SettingKind.Json or SettingKind.Ids) box.FontFamily = Ui.Mono;
        return (box, () => box.Text?.Trim() ?? "");
    }

    static string Label(ServerSetting s, string value) => s.Kind == SettingKind.Toggle ? value == "true" ? "on" : value == "false" ? "off" : value : value;

    Control Join()
    {
        var folder = hooks.Folder;
        var stack = new StackPanel { Spacing = 10 };
        var ids = ServerSettings.Effective(folder, "whitelist") as JsonArray ?? [];
        var guilds = ServerSettings.Effective(folder, "whitelist_discord_servers") as JsonArray ?? [];
        var file = ServerWhitelist.FilePath(folder);
        var on = ids.Count > 0 || guilds.Count > 0 || file != null;
        stack.Children.Add(Text(on ? "Only the Discord accounts listed here can play on this server. Guests can't."
            : "Everyone can play on this server. Turn on a whitelist to let in only the Discord accounts you list.", 13.5));
        if (ids.Count > 0 || guilds.Count > 0)
            stack.Children.Add(Text($"All settings also lets in {ids.Count} account{(ids.Count == 1 ? "" : "s")} from whitelist and members of {guilds.Count} Discord server{(guilds.Count == 1 ? "" : "s")} from whitelist_discord_servers.", 12.5, true));
        if (file == null)
        {
            var use = Look.Secondary("Use a whitelist file", Look.Plus, () =>
            {
                if (hooks.Running()) { said.Text = "Stop the server first. It only picks up a new whitelist file when it starts."; return; }
                if (!Confirmed("whitelist-on", "Click again to confirm. After the next start, only accounts in the file can play, and your admins go in it first.")) return;
                var why = ServerSettings.Save(folder, new Dictionary<string, string> { ["whitelist_file"] = "whitelist.txt" });
                if (why == null)
                {
                    var path = ServerWhitelist.FilePath(folder)!;
                    var admins = (ServerSettings.Effective(folder, "admin_discord_ids") as JsonArray ?? []).Select(v => v?.ToString() ?? "");
                    ServerWhitelist.Write(path, ServerWhitelist.Read(path).Concat(admins.Select(a => new WhitelistEntry(a, "admin"))));
                }
                said.Text = why ?? "The whitelist file is whitelist.txt in the server folder. Start the server to use it.";
                Content = Build();
            });
            stack.Children.Add(Line(use));
            stack.Children.Add(said);
            return stack;
        }
        var entries = ServerWhitelist.Read(file);
        var id = Field("", "Discord ID, 17 to 20 digits", 220);
        id.FontFamily = Ui.Mono;
        var note = Field("", "a note only you see", 260);
        void Add()
        {
            var value = id.Text?.Trim() ?? "";
            if (!ServerSettings.DiscordId().IsMatch(value)) { said.Text = "A Discord ID is 5 to 25 digits. Turn on Developer Mode in Discord's Advanced settings, then right-click a name and pick Copy User ID."; return; }
            if (entries.Any(e => e.Id == value)) { said.Text = "Already on the list."; return; }
            ServerWhitelist.Write(file, [.. entries, new WhitelistEntry(value, note.Text?.Trim() ?? "")]);
            said.Text = "Added. The server picks this up without a restart.";
            Content = Build();
        }
        id.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Add(); };
        stack.Children.Add(Line(id, note, Look.Secondary("Add", Look.Plus, Add)));
        stack.Children.Add(said);
        var list = new StackPanel { Spacing = 2 };
        foreach (var e in entries)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*,110") };
            var a = new SelectableTextBlock { Text = e.Id, FontFamily = Ui.Mono, FontSize = 13, Foreground = Look.Brush(Look.Text), VerticalAlignment = VerticalAlignment.Center };
            var b = Text(e.Note, 13, true);
            var remove = Look.Link("Remove", Look.Trash, () =>
            {
                if (!Confirmed("wl-" + e.Id, $"Click Remove again to confirm. {e.Id} won't be able to sign in, but anyone already playing stays until they leave.")) return;
                ServerWhitelist.Write(file, entries.Where(x => x.Id != e.Id));
                said.Text = "Removed.";
                Content = Build();
            });
            Grid.SetColumn(b, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(a);
            row.Children.Add(b);
            row.Children.Add(remove);
            list.Children.Add(new Border { Padding = new Thickness(6, 2), Child = row });
        }
        if (entries.Count == 0) list.Children.Add(Text("The file is empty. Only accounts the other two settings let in can sign in.", 12.5, true));
        stack.Children.Add(Panel(list));
        stack.Children.Add(Line(Text(file, 12, true), Look.Link("Open file", Look.Folder, () => hooks.OpenFile(file))));
        return new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    Control Bugs()
    {
        var folder = hooks.Folder;
        var stack = new StackPanel { Spacing = 10 };
        var ready = hooks.ConsoleReady();
        if (bugOpen is { } open)
        {
            stack.Children.Add(Line(Look.Link("Back", Look.Home, () => { bugOpen = null; Content = Build(); }), Text($"Bug report #{open.Id}", 18, weight: FontWeight.Bold),
                Look.Link("Open file", Look.Folder, () => hooks.OpenFile(ServerBugReports.FileFor(folder, open.Id)))));
            stack.Children.Add(Panel(Mono(open.Text)));
            return new ScrollViewer { Content = stack };
        }
        var mine = version;
        if (ready && bugListing == null)
        {
            stack.Children.Add(Text("Asking the server...", 12.5, true));
            _ = Task.Run(async () =>
            {
                var text = await Ask("bugs");
                Ui.OnUi(() => { bugListing = text; if (mine == version) Content = Build(); });
            });
            return stack;
        }
        stack.Children.Add(Text(ready ? "Reports players sent with the game's bug report button. Opening one saves it to data/bug-reports."
            : "Start the server to list every report. Reports already opened are below.", 13));
        var rows = bugListing == null ? [] : ServerBugReports.ParseListing(bugListing);
        var written = ServerBugReports.Written(folder);
        var list = new StackPanel { Spacing = 2 };
        foreach (var r in rows)
            list.Children.Add(BugRow(r.Id, r.When, r.Who, r.Note, r.Size, ready));
        foreach (var w in written.Where(w => rows.All(r => r.Id != w.Id)))
            list.Children.Add(BugRow(w.Id, w.Written.ToString("yyyy-MM-dd HH:mm"), "", "opened before", "", false));
        if (list.Children.Count == 0)
        {
            var raw = bugListing?.Trim() ?? "";
            list.Children.Add(raw.Length > 0 && !raw.Contains("no bug reports") ? Mono(raw) : Text(ready ? "No bug reports." : "None opened yet.", 12.5, true));
        }
        stack.Children.Add(Panel(list));
        if (ready) stack.Children.Add(Line(Look.Link("Refresh", Look.Refresh, () => { bugListing = null; Content = Build(); })));
        stack.Children.Add(said);
        return new ScrollViewer { Content = stack };
    }

    Control BugRow(int id, string when, string who, string note, string size, bool ask)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("60,140,150,*,70,80") };
        var cells = new Control[] { Text($"#{id}", 13, true), Text(when, 12.5, true), Text(who, 13, weight: FontWeight.SemiBold), Text(note, 13), Text(size, 12, true),
            Look.Link("Open", Look.Eye, () => _ = OpenBug(id, ask)) };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            row.Children.Add(cells[i]);
        }
        return new Border { Padding = new Thickness(6, 2), Child = row };
    }

    async Task OpenBug(int id, bool ask)
    {
        var file = ServerBugReports.FileFor(hooks.Folder, id);
        if (ask)
        {
            said.Text = "Asking the server...";
            said.Text = await Ask($"bugs {id}");
        }
        string text;
        try
        {
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(f);
            var buffer = new char[200_001];
            var n = await r.ReadBlockAsync(buffer, 0, buffer.Length);
            text = n < buffer.Length ? new string(buffer, 0, n) : new string(buffer, 0, n - 1) + "\n... Open the file to read the rest.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { said.Text = $"Couldn't read {file}: {e.Message}"; return; }
        bugOpen = (id, text);
        Content = Build();
    }

    Control Flags()
    {
        var stack = new StackPanel { Spacing = 10 };
        var ready = hooks.ConsoleReady();
        stack.Children.Add(Text("Checks ranked play for pairs who face each other a lot, one-sided series, players hitting the daily rating cap, and impossible stats. "
                                + "A flag means look closer. It isn't proof.", 13));
        var days = new ComboBox { ItemsSource = new[] { "last day", "last 7 days", "last 30 days" }, SelectedIndex = flagDays switch { 7 => 1, 30 => 2, _ => 0 }, Width = 160 };
        var check = Look.Secondary("Check", Look.Pulse, () => _ = Run());
        check.IsEnabled = days.IsEnabled = ready;
        stack.Children.Add(Line(days, check));
        if (!ready) stack.Children.Add(Text("Start the server to run a check. It reads the server's own database.", 12.5, true));
        if (flagsText.Length > 0) stack.Children.Add(Panel(Mono(flagsText)));
        return new ScrollViewer { Content = stack };

        async Task Run()
        {
            flagDays = days.SelectedIndex switch { 1 => 7, 2 => 30, _ => 1 };
            flagsText = "Checking...";
            Content = Build();
            flagsText = await Ask($"flags {flagDays}");
            if (section == "flags") Content = Build();
        }
    }

    Control Backups()
    {
        var folder = hooks.Folder;
        var running = hooks.Running();
        var stack = new StackPanel { Spacing = 10 };
        var hour = ServerSettings.Effective(folder, "backup_hour")?.ToString() ?? "4";
        var keep = ServerSettings.Effective(folder, "backup_keep_days")?.ToString() ?? "14";
        stack.Children.Add(Text($"While it runs, the server backs up its accounts database every day after {hour}:00 and keeps {keep} days of copies. That database holds accounts, ratings, friends, clans and tournaments.", 13));
        var now = Look.Secondary("Back up now", Look.Download, () => _ = BackUp());
        stack.Children.Add(Line(now, Look.Link("Open folder", Look.Folder, () => { Directory.CreateDirectory(ServerBackups.Dir(folder)); hooks.OpenFolder(ServerBackups.Dir(folder)); })));
        stack.Children.Add(said);
        var list = new StackPanel { Spacing = 2 };
        foreach (var b in ServerBackups.List(folder))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("140,100,*") };
            var restore = Look.Link("Restore", Look.Refresh, () =>
            {
                if (hooks.Running()) { said.Text = "Stop the server first. You can't restore while it's running."; return; }
                if (!Confirmed("restore-" + b.Path, $"Click Restore again to go back to {b.Day:yyyy-MM-dd}. Everything since then is moved aside to data/, not deleted.")) return;
                _ = RestoreIt(b);
            });
            restore.IsEnabled = !running;
            var cells = new Control[] { Text(b.Day.ToString("yyyy-MM-dd"), 13, weight: FontWeight.SemiBold), Text($"{Math.Max(1, b.Size / 1024)} KB", 12.5, true), restore };
            for (var i = 0; i < cells.Length; i++)
            {
                Grid.SetColumn(cells[i], i);
                row.Children.Add(cells[i]);
            }
            restore.HorizontalAlignment = HorizontalAlignment.Left;
            list.Children.Add(new Border { Padding = new Thickness(6, 2), Child = row });
        }
        if (list.Children.Count == 0) list.Children.Add(Text("No backups yet.", 12.5, true));
        stack.Children.Add(Panel(list));
        if (running) stack.Children.Add(Text("Stop the server to restore a backup.", 12.5, true));
        return new ScrollViewer { Content = stack };

        async Task BackUp()
        {
            if (hooks.ConsoleReady())
            {
                said.Text = "Asking the server...";
                said.Text = await Ask("backup");
            }
            else if (hooks.Running()) said.Text = "The server wasn't started from this app. Use the backup command in its own console.";
            else
            {
                said.Text = "Copying...";
                var (python, at) = (hooks.Python(), hooks.Now());
                var (made, why) = await Task.Run(() => ServerBackups.CopyNow(folder, at, python));
                said.Text = why ?? $"Backup written: {Path.GetFileName(made)}";
            }
            if (section == "backups") Content = Build();
        }

        async Task RestoreIt(ServerBackup b)
        {
            said.Text = "Restoring...";
            var (python, at) = (hooks.Python(), hooks.Now());
            var (kept, why) = await Task.Run(() => ServerBackups.Restore(folder, b, at, python));
            said.Text = why ?? $"Restored {b.Day:yyyy-MM-dd}. The old database is saved as {Path.GetFileName(kept)}.";
            if (section == "backups") Content = Build();
        }
    }
}
