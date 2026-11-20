namespace BifrostStudio;

public static class DocsLayout
{
    public const string Settings = "Settings";
    public const string Logs = "Logs";
    public const string Marker = "bh-session.txt";
    public const string Earlier = "Earlier";
    const StringComparison Nocase = StringComparison.OrdinalIgnoreCase;
    static readonly string[] TopFolders = ["videos", "screenshots", "mods", "old", "workshop", "bh-autopilot", "bh-smoke"];

    public static bool Split(string docs) => Directory.Exists(Path.Combine(docs, Settings));

    public static bool IsReport(string name) =>
        name.IndexOfAny(['/', '\\']) < 0 && name.StartsWith("bh-", Nocase) && name.EndsWith(".txt", Nocase)
        && !name.Equals(LaunchRequest, Nocase) && !name.Equals(Marker, Nocase);

    public const string LaunchRequest = "bh-launch-request.txt";

    public static string File(string docs, string name)
    {
        if (!Split(docs)) return Path.Combine(docs, name);
        if (name.Length == 0) return docs;
        name = name.Replace('/', Path.DirectorySeparatorChar);
        if (name.Equals("logs", Nocase)) return Path.Combine(docs, Logs);
        if (name.Equals(Marker, Nocase)) return Path.Combine(docs, Logs, Marker);
        if (IsReport(name)) return Path.Combine(CurrentRun(docs), name);
        var first = name.Split('/', '\\')[0].ToLowerInvariant();
        return TopFolders.Contains(first) ? Path.Combine(docs, name) : Path.Combine(docs, Settings, name);
    }

    public static string CurrentRun(string docs)
    {
        if (!Split(docs)) return docs;
        var logs = Path.Combine(docs, Logs);
        if (MarkerDay(logs) is { } day) return Path.Combine(logs, day);
        return Days(logs).LastOrDefault() ?? Path.Combine(logs, DateTime.Now.ToString("yyyy-MM-dd"));
    }

    public static List<(string Label, string Dir)> Runs(string docs)
    {
        var list = new List<(string, string)>();
        if (!Split(docs))
        {
            if (HasReports(docs)) list.Add(("latest run", docs));
            foreach (var dir in Folders(Path.Combine(docs, "logs"), _ => true).AsEnumerable().Reverse())
                list.Add((Stamp(Path.GetFileName(dir)), dir));
            return list;
        }
        var current = CurrentRun(docs);
        foreach (var day in Days(Path.Combine(docs, Logs)).AsEnumerable().Reverse())
        {
            if (HasReports(day))
                list.Add((string.Equals(day, current, Nocase) ? "latest run" : Path.GetFileName(day) + "  last run", day));
            foreach (var run in Folders(Path.Combine(day, Earlier), _ => true).AsEnumerable().Reverse())
                list.Add((Stamp(Path.GetFileName(run)), run));
        }
        return list;
    }

    static string Stamp(string name)
    {
        var at = name.IndexOf('_');
        return at > 0 ? name[..at] + "  " + name[(at + 1)..].Replace('-', ':') : name;
    }

    static bool HasReports(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.GetFiles(dir, "bh-*.txt").Any(f => IsReport(Path.GetFileName(f)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static string? MarkerDay(string logs)
    {
        try
        {
            var path = Path.Combine(logs, Marker);
            if (!System.IO.File.Exists(path)) return null;
            var text = System.IO.File.ReadAllText(path).Trim();
            return text.Length >= 10 && System.Text.RegularExpressions.Regex.IsMatch(text[..10], @"^\d{4}-\d\d-\d\d$") ? text[..10] : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static List<string> Days(string logs) =>
        Folders(logs, n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{4}-\d\d-\d\d$"));

    static List<string> Folders(string parent, Func<string, bool> keep)
    {
        try
        {
            return Directory.Exists(parent)
                ? Directory.GetDirectories(parent).Where(d => keep(Path.GetFileName(d))).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
