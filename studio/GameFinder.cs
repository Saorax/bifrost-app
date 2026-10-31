namespace BifrostStudio;

public static class GameFinder
{
    public const string SteamAppId = "291550";

    public static List<string> FindAll()
    {
        var steams = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var (hive, key, value) in new[]
                     {
                         (Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                         (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     })
            {
                try
                {
                    if (hive.OpenSubKey(key)?.GetValue(value) is string path) steams.Add(path.Replace('/', '\\'));
                }
                catch (Exception) { }
            }
            steams.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            steams.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            steams.Add(Path.Combine(home, ".steam", "steam"));
            steams.Add(Path.Combine(home, ".local", "share", "Steam"));
            steams.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
        }
        var libraries = new List<string>();
        foreach (var steam in steams)
        {
            libraries.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (var line in File.ReadAllLines(vdf))
                {
                    var parts = line.Trim().Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2 && parts[0] == "path") libraries.Add(parts[^1].Replace(@"\\", @"\"));
                }
            }
            catch (IOException) { }
        }
        return libraries.Select(l => Path.Combine(l, "steamapps", "common", "Brawlhalla"))
            .Where(IsGame).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsGame(string? dir) => dir != null && File.Exists(Path.Combine(dir, "BrawlhallaAir.swf"));

    public static string? Find(Settings settings) =>
        IsGame(settings.GameFolder) ? settings.GameFolder : FindAll().FirstOrDefault();

    public static bool GameRunning() =>
        new[] { "Brawlhalla", "hl", "bifrost-vulkan" }.Any(n => System.Diagnostics.Process.GetProcessesByName(n).Length > 0);

    public static void Play()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"steam://rungameid/{SteamAppId}")
            { UseShellExecute = true })?.Dispose();
        }
        catch (Exception) { }
    }
}
