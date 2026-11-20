namespace BifrostStudio;

public static class BifrostFolders
{
    static string? localRoot, documentsRoot, roamingRoot, profileRoot;

    public static void UseRoots(string? local, string? documents, string? roaming = null, string? profile = null)
    {
        localRoot = local;
        documentsRoot = documents;
        roamingRoot = roaming;
        profileRoot = profile;
    }

    static string LocalAppData => localRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string DocumentsRoot => documentsRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    static string RoamingAppData => roamingRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string Profile => profileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ReplaysRoot => Path.Combine(Profile, "BrawlhallaReplays");
    public static string Replays => Path.Combine(ReplaysRoot, "Bifrost");

    public static string Home => Path.Combine(LocalAppData, "Bifrost");
    public static string Engine => Path.Combine(Home, "Engine");
    public static string Game => Path.Combine(Home, "Game");
    public static string Saves => Path.Combine(Home, "Saves");
    public static string Studio => Path.Combine(Home, "Studio");
    public static string Server => Path.Combine(Home, "Server");
    public static string Cache => Path.Combine(Home, "Cache");
    public static string Build => Path.Combine(Home, "Build");
    public static string Kit => Path.Combine(Build, "Kit");
    public static string Work => Path.Combine(Build, "Work");
    public static string OfficialLayer => Path.Combine(Build, "Official");
    public static string GameFilesCache => Path.Combine(Cache, "game-files-cache.json");
    public static string Downloads => Path.Combine(Home, "Downloads");
    public static string EngineIndex => Path.Combine(Home, "engines.txt");

    public static string OldKit => Path.Combine(Home, "kit");
    public static string OldWork => Path.Combine(Home, "engine-work");
    public static string OldOfficialLayer => Path.Combine(Home, "official");
    public static string OldGameFilesCache => Path.Combine(Home, "game-files-cache.json");

    public static string Current(string now, string old) => Directory.Exists(now) || !Directory.Exists(old) ? now : old;

    public static string CurrentFile(string now, string old) => File.Exists(now) || !File.Exists(old) ? now : old;

    public static string Documents => Path.Combine(DocumentsRoot, "Bifrost");
    public static string SettingsDir => Path.Combine(Documents, "Settings");
    public static string Mods => Path.Combine(Documents, "Mods");
    public static string Screenshots => Path.Combine(Documents, "Screenshots");
    public static string Videos => Path.Combine(Documents, "Videos");
    public static string Workshop => Path.Combine(Documents, "Workshop");
    public static string Logs => Path.Combine(Documents, "Logs");
    public static string Old => Path.Combine(Documents, "Old");

    public static string OldStudio => Path.Combine(RoamingAppData, "BifrostStudio");

    public static string StudioFile(string name) => Path.Combine(Studio, name);

    public const string LauncherVulkanCopy = "Brawlhalla.exe";
    public const string OldLauncherVulkanCopy = "bifrost-vulkan.exe";
}
