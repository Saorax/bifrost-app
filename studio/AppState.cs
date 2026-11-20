namespace BifrostStudio;

public sealed class AppState
{
    string? game;
    AppPaths? paths;
    Release? latest;
    OfficialRelease? latestOfficial;
    AppRelease? latestApp;

    public AppState(Project project, Func<bool>? gameRunning = null, string? queueFile = null)
    {
        Project = project;
        running = gameRunning ?? GameFinder.GameRunning;
        game = GameFinder.Find(project.Settings);
        Writes = new WriteQueue(queueFile ?? WriteQueue.DefaultFile, () => GameRunning);
        GameRunning = running();
    }

    readonly Func<bool> running;

    public Project Project { get; }
    public WriteQueue Writes { get; }
    public bool GameRunning { get; private set; }
    public event Action? Changed;

    public string? Game
    {
        get => game;
        set
        {
            if (Same(value, game)) return;
            game = value;
            paths = null;
            Changed?.Invoke();
        }
    }

    public AppPaths? Paths => game == null ? null : paths ??= AppPaths.For(game, EngineKit.EngineDir(game));

    public Release? Latest { get => latest; set => Set(ref latest, value); }
    public OfficialRelease? LatestOfficial { get => latestOfficial; set => Set(ref latestOfficial, value); }
    public AppRelease? LatestApp { get => latestApp; set => Set(ref latestApp, value); }

    public void Poll()
    {
        var found = GameFinder.IsGame(game) ? game : GameFinder.Find(Project.Settings);
        var now = running();
        var changed = now != GameRunning || !Same(found, game);
        game = found;
        paths = null;
        GameRunning = now;
        if (!now) Writes.Flush();
        if (changed) Changed?.Invoke();
    }

    void Set<T>(ref T field, T value) where T : class?
    {
        if (ReferenceEquals(field, value)) return;
        field = value;
        Changed?.Invoke();
    }

    static bool Same(string? a, string? b) =>
        a == null || b == null ? a == b : string.Equals(Path.GetFullPath(a).TrimEnd('/', Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}