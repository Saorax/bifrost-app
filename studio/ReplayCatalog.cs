using System.Text.Json;

namespace BifrostStudio;

public sealed record ReplayInputs(int Jump, int Light, int Heavy, int Dodge, int Throw, int Moves)
{
    public static readonly ReplayInputs None = new(0, 0, 0, 0, 0, 0);

    public int Actions => Jump + Light + Heavy + Dodge + Throw;

    public int PerMinute(int lengthMs) => lengthMs <= 0 ? 0 : (int)Math.Round(Actions * 60000.0 / lengthMs);

    public static ReplayInputs Of(IReadOnlyList<uint> inputs)
    {
        int jump = 0, light = 0, heavy = 0, dodge = 0, thrown = 0, moves = 0;
        uint prev = 0;
        foreach (var b in inputs)
        {
            var rise = b & ~prev;
            prev = b;
            if ((rise & 16) != 0) jump++;
            if ((rise & 64) != 0) heavy++;
            if ((rise & 128) != 0) light++;
            if ((rise & 256) != 0) dodge++;
            if ((rise & 512) != 0 && (rise & 128) == 0) thrown++;
            if ((rise & 15) != 0) moves++;
        }
        return new ReplayInputs(jump, light, heavy, dodge, thrown, moves);
    }
}

public sealed record ReplaySummaryPlayer(string Name, uint Pid, uint Team, uint Place, uint Avatar, uint Colour,
    IReadOnlyList<uint> Heroes, IReadOnlyList<uint> Costumes, ReplayInputs Inputs);

public sealed record ReplaySummary(bool Ok, string Why, uint Version, uint Level, uint Playlist, string PlaylistName, bool Online,
    uint Scoring, uint Lives, uint Duration, bool Teams, int LengthMs, IReadOnlyList<ReplaySummaryPlayer> Players, IReadOnlyList<int[]> Kos)
{
    public static ReplaySummary Failed(string why) => new(false, why, 0, 0, 0, "", false, 0, 0, 0, false, 0, [], []);

    public static ReplaySummary Of(ReplayHeader h)
    {
        var order = h.Players.Select(p => p.Entity).ToList();
        var players = h.Players.Select(p => new ReplaySummaryPlayer(p.Name, p.Pid, p.Team, p.Place, p.Avatar, p.Colour,
            p.Heroes.Select(x => x.Hero).ToList(), p.Heroes.Select(x => x.Costume).ToList(), ReplayInputs.Of(p.Inputs))).ToList();
        var kos = h.Kos.Select(k => new[] { order.IndexOf(k.Entity), (int)k.Ms }).ToList();
        return new ReplaySummary(true, "", h.Version, h.Level, h.Playlist, h.PlaylistName, h.Online, h.Settings.Scoring, h.Settings.Lives,
            h.Settings.Duration, h.Settings.Teams, (int)h.LengthMs, players, kos);
    }
}

public sealed class ReplayCatalog
{
    public const int CacheVersion = 3;

    sealed record Cached(long Size, long Time, ReplaySummary Summary);

    sealed record CacheFile(int Version, Dictionary<string, Cached> Files);

    readonly string? cacheFile;
    readonly Dictionary<string, Cached> cache = new(StringComparer.OrdinalIgnoreCase);
    bool dirty;

    public ReplayCatalog(string? cacheFile)
    {
        this.cacheFile = cacheFile;
        if (cacheFile == null || !File.Exists(cacheFile)) return;
        try
        {
            var read = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cacheFile));
            if (read?.Version == CacheVersion && read.Files != null)
                foreach (var (k, v) in read.Files) cache[k] = v;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { }
    }

    public static string DefaultFile => BifrostFolders.StudioFile("replay-headers.json");

    public int Read { get; private set; }

    public ReplaySummary Get(string path, byte[]? key)
    {
        long size, time;
        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            time = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ReplaySummary.Failed("the file can't be opened"); }
        lock (cache)
        {
            if (cache.TryGetValue(path, out var hit) && hit.Size == size && hit.Time == time && (hit.Summary.Ok || hit.Summary.Why != "nokey" || key == null)) return hit.Summary;
        }
        ReplaySummary summary;
        try
        {
            var plain = ReplayFile.Unpack(File.ReadAllBytes(path), Path.GetFileName(path), key ?? []);
            summary = ReplaySummary.Of(ReplayFile.Read(plain));
        }
        catch (ReplayFileException e) when (e.Message == ReplayFile.NoKey) { return ReplaySummary.Failed("nokey"); }
        catch (ReplayFileException e) { summary = ReplaySummary.Failed(e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ReplaySummary.Failed("the file can't be opened"); }
        lock (cache)
        {
            cache[path] = new Cached(size, time, summary);
            dirty = true;
            Read++;
        }
        return summary;
    }

    public void Forget(IEnumerable<string> keep)
    {
        var alive = keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (cache)
        {
            foreach (var gone in cache.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                cache.Remove(gone);
                dirty = true;
            }
        }
    }

    public void Save()
    {
        if (cacheFile == null) return;
        Dictionary<string, Cached> copy;
        lock (cache)
        {
            if (!dirty) return;
            copy = new Dictionary<string, Cached>(cache, StringComparer.OrdinalIgnoreCase);
            dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var temp = cacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new CacheFile(CacheVersion, copy)));
            File.Move(temp, cacheFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
