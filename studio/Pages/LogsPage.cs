using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace BifrostStudio;

public sealed class LogsPage : UserControl
{
    const string Errors = "bh-errors.txt";
    readonly LogView view = new();
    readonly LogView installLog;
    readonly FileTail tail = new();
    readonly ComboBox runs = Combo(230);
    readonly ComboBox files = Combo(170);
    readonly List<(string Name, string Dir)> runDirs = [("Last install", "")];
    readonly string? game;
    readonly Button save;
    readonly TextBlock saved = new() { FontSize = 12, Foreground = Look.Brush(Look.Dim), Margin = new Thickness(0, 0, 0, 10), TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };

    public LogsPage(string? game, LogView installLog)
    {
        this.game = game;
        this.installLog = installLog;
        if (Docs(game) is { } docs) runDirs.AddRange(Sessions(docs));
        if (game != null)
        {
            var engine = EngineKit.EngineDir(game);
            var official = Path.Combine(engine, "storage-official");
            if (Directory.Exists(official) && Directory.GetFiles(official, "bh-*.txt").Length > 0)
                runDirs.Insert(Math.Min(2, runDirs.Count), (Mark("Official latest run", official), official));
            var engineLogs = Path.Combine(engine, "logs");
            if (Directory.Exists(engineLogs)) runDirs.Add(("Engine logs", engineLogs));
        }
        runs.ItemsSource = runDirs.Select(r => r.Name).ToList();
        runs.SelectionChanged += (_, _) => FillFiles();
        files.SelectionChanged += (_, _) => Show(true);
        save = Look.Secondary("Save report", Look.Download, () => _ = SaveReport());
        save.IsVisible = game != null;
        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10),
            Children = { runs, files, Look.Link("Folder", Look.Folder, () => Ui.Open(Dir ?? EngineKit.Home)), save },
        };
        var heading = new TextBlock { Text = "Logs", FontSize = 30, FontWeight = Avalonia.Media.FontWeight.Bold, Foreground = Look.Brush(Look.Text), Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(saved, Dock.Top);
        Content = new DockPanel { Children = { heading, top, saved, view } };
        runs.SelectedIndex = runDirs.Count > 1 ? 1 : 0;
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => Show(false));
        AttachedToVisualTree += (_, _) => timer.Start();
        DetachedFromVisualTree += (_, _) => timer.Stop();
    }

    static ComboBox Combo(double width) => new() { MinWidth = width, VerticalAlignment = VerticalAlignment.Center };

    public static string? Docs(string? game)
    {
        var storage = game == null ? null : Path.Combine(EngineKit.EngineDir(game), "storage");
        return storage != null && Directory.Exists(storage)
            ? Directory.GetDirectories(storage).Select(d => Path.Combine(d, "Documents", "Bifrost")).Where(Directory.Exists)
                .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    public static List<(string Name, string Dir)> Sessions(string docs)
    {
        var list = new List<(string, string)>();
        if (Directory.GetFiles(docs, "bh-*.txt").Length > 0) list.Add((Mark("Bifrost latest run", docs), docs));
        var logs = Path.Combine(docs, "logs");
        if (Directory.Exists(logs))
        {
            foreach (var dir in Directory.GetDirectories(logs).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                var at = name.IndexOf('_');
                list.Add((Mark(at > 0 ? name[..at] + "  " + name[(at + 1)..].Replace('-', ':') : name, dir), dir));
            }
        }
        return list;
    }

    static string Mark(string name, string dir) => HasErrors(dir) ? name + "   (errors)" : name;

    static bool HasErrors(string dir)
    {
        try
        {
            var path = Path.Combine(dir, Errors);
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(stream).ReadToEnd().Contains("uncaught error", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    string? Dir => runs.SelectedIndex > 0 && runs.SelectedIndex < runDirs.Count ? runDirs[runs.SelectedIndex].Dir : null;

    void FillFiles()
    {
        var names = Dir is { } dir
            ? Readable(dir).Select(Path.GetFileName).OfType<string>()
                .OrderBy(n => n == Errors || n == "boot-official.txt" ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal).ToList()
            : [];
        files.ItemsSource = names;
        files.IsVisible = names.Count > 0;
        files.SelectedIndex = names.Count > 0 ? 0 : -1;
        Show(true);
    }

    static IEnumerable<string> Readable(string dir)
    {
        var run = Directory.GetFiles(dir, "bh-*.txt");
        return run.Length > 0
            ? run.Concat(Directory.GetFiles(dir, "engine.json"))
            : Directory.GetFiles(dir, "*.txt").Concat(Directory.GetFiles(dir, "*.log"));
    }

    async Task SaveReport()
    {
        if (game == null) return;
        save.IsEnabled = false;
        Say("Saving...");
        var name = LogReport.DefaultName();
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
            int count;
            try
            {
                count = await Task.Run(() => LogReport.Save(game, path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (await PickZip(name) is not { } picked)
                {
                    Say("Not saved.");
                    return;
                }
                path = picked;
                count = await Task.Run(() => LogReport.Save(game, path));
            }
            if (count == 0)
            {
                Say("No logs to save yet: run the game first.");
                return;
            }
            Say($"Saved {count} files to {path}.");
            Ui.Reveal(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Say($"Couldn't save the logs: {e.Message}");
        }
        finally
        {
            save.IsEnabled = true;
        }
    }

    void Say(string text)
    {
        saved.Text = text;
        saved.IsVisible = true;
    }

    async Task<string?> PickZip(string name)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return null;
        var picked = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save logs for a report",
            SuggestedFileName = name,
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("Zip") { Patterns = ["*.zip"] }],
        });
        return picked?.TryGetLocalPath();
    }

    void Show(bool fresh)
    {
        if (runs.SelectedIndex == 0)
        {
            view.SetText(installLog.Text.Length > 0 ? installLog.Text : "Nothing installed since the app opened.");
            return;
        }
        if (fresh) tail.Reset();
        if (Dir is { } dir && files.SelectedItem is string name && tail.Read(Path.Combine(dir, name)) is { } text) view.SetText(text);
    }
}
