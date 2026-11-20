using System.Text.Json.Nodes;

namespace BifrostStudio;

public sealed record ReplayAccount(int Pid, string Name, IReadOnlyList<string> Names, int Replays, int Wins, int Losses, bool Added);

public static class ReplayAccounts
{
    public const int MinReplays = 3;

    public static string File => BifrostFolders.StudioFile("replay-accounts.json");

    public static string? FileOverride { get; set; }

    static string Path => FileOverride ?? File;

    public static List<int> Detect(IEnumerable<ReplayEntry> entries)
    {
        var left = entries.Where(e => e.Online && e.Indexed).Select(e => e.Players.Select(p => p.Pid).Where(p => p != 0).ToHashSet()).Where(s => s.Count > 0).ToList();
        var found = new List<int>();
        while (left.Count > 0)
        {
            var best = left.SelectMany(s => s).GroupBy(p => p).Select(g => (Pid: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ThenBy(g => g.Pid).FirstOrDefault();
            if (best.Count < MinReplays) break;
            found.Add(best.Pid);
            left.RemoveAll(s => s.Contains(best.Pid));
        }
        return found;
    }

    public static (HashSet<int> Added, HashSet<int> Removed) Choices()
    {
        var added = new HashSet<int>();
        var removed = new HashSet<int>();
        if (JsonFiles.Read(Path) is JsonObject o)
        {
            foreach (var n in o["added"] as JsonArray ?? []) if (n is JsonValue v && v.TryGetValue<int>(out var id)) added.Add(id);
            foreach (var n in o["removed"] as JsonArray ?? []) if (n is JsonValue v && v.TryGetValue<int>(out var id)) removed.Add(id);
        }
        return (added, removed);
    }

    public static string? Choose(int pid, bool mine)
    {
        var (added, removed) = Choices();
        if (mine) { removed.Remove(pid); added.Add(pid); }
        else { added.Remove(pid); removed.Add(pid); }
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            JsonFiles.Write(Path, new JsonObject
            {
                ["added"] = new JsonArray(added.Order().Select(a => (JsonNode)a).ToArray()),
                ["removed"] = new JsonArray(removed.Order().Select(a => (JsonNode)a).ToArray()),
            });
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Couldn't save that: " + e.Message;
        }
    }

    public static List<int> Mine(IEnumerable<ReplayEntry> entries)
    {
        var (added, removed) = Choices();
        var list = Detect(entries).Where(p => !removed.Contains(p)).ToList();
        list.AddRange(added.Where(p => !list.Contains(p)));
        return list;
    }

    public static IReadOnlyList<ReplayAccount> Summary(IReadOnlyList<ReplayEntry> entries, IReadOnlyList<int> pids)
    {
        var (added, _) = Choices();
        var result = new List<ReplayAccount>();
        foreach (var pid in pids)
        {
            var with = entries.Where(e => e.Players.Any(p => p.Pid == pid)).ToList();
            var names = with.SelectMany(e => e.Players.Where(p => p.Pid == pid)).Select(p => p.Name.Trim()).Where(n => n.Length > 0)
                .GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            var mineHere = with.Where(e => e.Me >= 0 && e.Me < e.Players.Count && e.Players[e.Me].Pid == pid).ToList();
            result.Add(new ReplayAccount(pid, names.FirstOrDefault() ?? "ID " + pid, names, with.Count,
                mineHere.Count(e => e.Result == "win"), mineHere.Count(e => e.Result == "loss"), added.Contains(pid)));
        }
        return result;
    }
}
