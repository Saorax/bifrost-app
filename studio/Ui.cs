using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace BifrostStudio;

public sealed class LogView : UserControl
{
    const int MaxLines = 4000;
    readonly TextBox box;
    readonly ConcurrentQueue<string> pending = new();
    readonly LinkedList<string> lines = new();
    bool clear;

    public LogView()
    {
        box = new TextBox
        {
            Classes = { "log" },
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Ui.Mono,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        Content = box;
        new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => Flush()).Start();
    }

    public void Append(string line) => pending.Enqueue(line);

    public void Clear()
    {
        pending.Clear();
        clear = true;
    }

    public void SetText(string text)
    {
        Clear();
        lines.Clear();
        clear = false;
        box.Text = text;
        ToEnd(text);
    }

    void ToEnd(string text) => box.CaretIndex = text.TrimEnd('\n').LastIndexOf('\n') + 1;

    public string Text => box.Text ?? "";

    void Flush()
    {
        if (clear)
        {
            clear = false;
            lines.Clear();
            box.Text = "";
        }
        if (pending.IsEmpty) return;
        while (pending.TryDequeue(out var line))
        {
            lines.AddLast(line);
            if (lines.Count > MaxLines) lines.RemoveFirst();
        }
        var text = string.Join('\n', lines);
        box.Text = text;
        ToEnd(text);
    }
}

public sealed class FileTail(int tailBytes = 256 * 1024)
{
    long lastLength = -1;
    DateTime lastWrite;

    public void Reset() => lastLength = -1;

    public string? Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            if (lastLength == -2) return null;
            lastLength = -2;
            return $"No file yet at {path}.";
        }
        if (file.Length == lastLength && file.LastWriteTimeUtc == lastWrite) return null;
        lastLength = file.Length;
        lastWrite = file.LastWriteTimeUtc;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - tailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (start > 0)
            {
                var cut = text.IndexOf('\n');
                text = "(earlier lines not shown)\n" + (cut >= 0 ? text[(cut + 1)..] : text);
            }
            return text.Replace("\r\n", "\n");
        }
        catch (Exception e)
        {
            return $"Couldn't read {path}: {e.Message}";
        }
    }
}

public static class Ui
{
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    public static void OnUi(Action action) => Dispatcher.UIThread.Post(action);

    public static event Action<string>? Said;

    public static Func<System.Diagnostics.ProcessStartInfo, bool> Starter { get; set; } = start =>
    {
        System.Diagnostics.Process.Start(start)?.Dispose();
        return true;
    };

    public static void Say(string text) => Said?.Invoke(text);

    public static bool Open(string? dir, bool create = false, string? missing = null)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            Say(missing ?? "Brawlhalla wasn't found, so that folder isn't known yet.");
            return false;
        }
        if (!Directory.Exists(dir))
        {
            if (!create)
            {
                Say(missing ?? $"That folder isn't there: {dir}");
                return false;
            }
            if (AppPaths.InOldEngineStorage(dir))
            {
                Say(AppPaths.OldStorageNote);
                return false;
            }
            try { Directory.CreateDirectory(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Say($"Couldn't make {dir}: {e.Message}");
                return false;
            }
        }
        var opener = OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        return Start(new System.Diagnostics.ProcessStartInfo(opener, [dir]), dir);
    }

    public static bool OpenFile(string file)
    {
        if (!File.Exists(file))
        {
            Say($"That file isn't there any more: {file}");
            return false;
        }
        return Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true }, file);
    }

    public static bool Reveal(string file)
    {
        if (!File.Exists(file))
        {
            var dir = Path.GetDirectoryName(file);
            if (dir != null && Directory.Exists(dir))
            {
                Say($"{file} isn't there any more. Opening its folder instead.");
                return Open(dir);
            }
            Say($"That file isn't there any more: {file}");
            return false;
        }
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return Open(Path.GetDirectoryName(file));
        var start = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("explorer", $"/select,\"{file}\"")
            : new System.Diagnostics.ProcessStartInfo("open", ["-R", file]);
        return Start(start, file);
    }

    static bool Start(System.Diagnostics.ProcessStartInfo start, string what)
    {
        try
        {
            if (Starter(start)) return true;
        }
        catch (Exception e)
        {
            Say($"Couldn't open {what}: {e.Message}");
            return false;
        }
        Say($"Couldn't open {what}.");
        return false;
    }

    public static void Browse(string? url)
    {
        if (url == null || !url.StartsWith("https://", StringComparison.Ordinal)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }
}
