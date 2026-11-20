namespace BifrostStudio;

public static class InstallPlace
{
    public const string Steam = "steam";
    public const string Own = "own";

    public static string Of(string? game) => GameFinder.IsSteamInstall(game) ? Steam : Own;

    public static bool IsOwnFolder(string? game) =>
        game != null && string.Equals(Path.GetFullPath(game).TrimEnd('\\', '/'), Path.GetFullPath(BifrostFolders.Game).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    public static string Describe(string place) => place == Steam
        ? "Steam's Play button starts Bifrost. Only Bifrost's Brawlhalla.exe goes in Steam's folder."
        : "Bifrost runs from its own copy of the game, started from this app. Steam's folder isn't touched.";

    public static bool NeedsAsking(Settings settings, string? game, bool updating) =>
        !updating && settings.InstallPlace == null && game != null && GameFinder.IsSteamInstall(game);

    public static string? Target(string place, string? steam) => place == Steam ? steam : BifrostFolders.Game;
}
