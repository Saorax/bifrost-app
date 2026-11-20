using System.Text.Json;

namespace BifrostStudio;

public sealed class Project
{
    public Settings Settings { get; }

    Project(Settings settings) => Settings = settings;

    public static Project Find() => new(Settings.Load());

    public string Python => string.IsNullOrWhiteSpace(Settings.Python) ? (OperatingSystem.IsWindows() ? "python" : "python3") : Settings.Python!;
}

public sealed class Settings
{
    public string? Python { get; set; }
    public string? GameFolder { get; set; }
    public Dictionary<string, double>? StepSeconds { get; set; }
    public string? Mode { get; set; }
    public bool OfficialAccepted { get; set; }
    public string? InstallPlace { get; set; }

    static string FilePath => BifrostFolders.StudioFile("settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch (Exception) { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}
