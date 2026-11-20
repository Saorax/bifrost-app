using System.Text.Json;
using System.Text.Json.Nodes;

namespace BifrostStudio;

public static class JsonFiles
{
    static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static JsonNode? Read(string path) => Read(path, out _);

    public static JsonNode? Read(string path, out string? problem)
    {
        problem = null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { problem = Missing; }
        catch (JsonException e) { problem = "not valid JSON (" + e.Message + ")"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = e.Message; }
        return null;
    }

    public const string Missing = "missing";

    public static void Write(string path, JsonNode? node)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".studio-tmp";
        File.WriteAllText(temp, node?.ToJsonString(Indented) ?? "null");
        File.Move(temp, full, true);
    }

    public static JsonNode? Merge(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject changes) return patch?.DeepClone();
        var result = target is JsonObject t ? (JsonObject)t.DeepClone() : new JsonObject();
        foreach (var (key, value) in changes)
        {
            if (value is null) result.Remove(key);
            else result[key] = Merge(result[key], value);
        }
        return result;
    }

    public static JsonObject Compose(JsonObject? first, JsonObject second)
    {
        var result = first == null ? new JsonObject() : (JsonObject)first.DeepClone();
        foreach (var (key, value) in second)
        {
            result[key] = value is JsonObject inner && result[key] is JsonObject before ? Compose(before, inner) : value?.DeepClone();
        }
        return result;
    }
}

public sealed class WriteQueue(string file, Func<bool> gameRunning)
{
    readonly object gate = new();

    public static string DefaultFile => BifrostFolders.StudioFile("pending-writes.json");

    public event Action<string, string?>? Written;

    public IReadOnlyList<string> Pending
    {
        get { lock (gate) return Load().Select(e => e.Key).ToList(); }
    }

    public bool Waiting(string path)
    {
        lock (gate) return Find(Load(), path) != null;
    }

    public JsonObject? PendingFor(string path)
    {
        lock (gate)
        {
            var list = Load();
            return Find(list, Path.GetFullPath(path)) is { } key && list[key] is JsonObject patch ? (JsonObject)patch.DeepClone() : null;
        }
    }

    public bool Merge(string path, JsonObject patch)
    {
        var full = Path.GetFullPath(path);
        lock (gate)
        {
            var list = Load();
            var key = Find(list, full) ?? full;
            list[key] = JsonFiles.Compose(list[key] as JsonObject, patch);
            Save(list);
        }
        Flush();
        return !Waiting(full);
    }

    public int Flush()
    {
        if (gameRunning()) return 0;
        var done = new List<(string, string?)>();
        lock (gate)
        {
            var list = Load();
            if (list.Count == 0) return 0;
            foreach (var key in list.Select(e => e.Key).ToList())
            {
                var current = JsonFiles.Read(key, out var problem);
                if (problem != null && problem != JsonFiles.Missing)
                {
                    done.Add((key, problem));
                    continue;
                }
                try
                {
                    JsonFiles.Write(key, JsonFiles.Merge(current, list[key]));
                    list.Remove(key);
                    done.Add((key, null));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    done.Add((key, e.Message));
                }
            }
            Save(list);
        }
        foreach (var (key, problem) in done) Written?.Invoke(key, problem);
        return done.Count(d => d.Item2 == null);
    }

    static string? Find(JsonObject list, string path) =>
        list.Select(e => e.Key).FirstOrDefault(k => string.Equals(k, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    JsonObject Load() => JsonFiles.Read(file) as JsonObject ?? new JsonObject();

    void Save(JsonObject list)
    {
        try
        {
            if (list.Count == 0) File.Delete(file);
            else JsonFiles.Write(file, list);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}