using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BifrostStudio;

public static class LogReport
{
    const int MaxBytes = 32 * 1024 * 1024;
    const string User = "<user>";
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly string[] EngineLogs = ["boot.txt", "boot-previous.txt", "boot-official.txt", "boot-official-previous.txt", "launcher.txt", "starts.txt"];
    static readonly string[] PerRun = ["bh-errors.txt", "bh-controllers.txt"];
    static readonly Regex UserFolder = new(@"((?:\b[A-Za-z]:)?[\\/]+(?:Users|home)[\\/]+)[^\\/\r\n""'<>|:*?]+", Options);
    static readonly Regex Secret = new(@"((?:token|ticket|secret|password|authorization)[""']?\s*[:=]\s*[""']?(?:bearer\s+)?)[^\s""',;&]+", Options);
    static readonly Regex Player = new(@"(input display: (?:P\d+\s+)?).+?( panel \(| shows 0x)", Options);
    static readonly Regex Profile = new(@"(profile: player \S+ \().+(\))", Options);
    static readonly Regex? UserName = NameRule();

    static Regex? NameRule()
    {
        var names = new[] { Environment.UserName, Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) }
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape).ToList();
        return names.Count > 0 ? new Regex(@"(?<=[\\/])(?:" + string.Join('|', names) + @")(?![\w.-])", Options) : null;
    }

    public static string DefaultName() => $"Bifrost-logs-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip";

    public static int Save(string game, string zip)
    {
        var files = Files(game);
        if (!files.Any(f => File.Exists(f.Path))) return 0;
        var list = new StringBuilder()
            .AppendLine("Bifrost log report")
            .AppendLine($"saved {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"app {Releases.AppVersion ?? "unknown"}")
            .AppendLine(RuntimeInformation.OSDescription)
            .AppendLine($"user names in paths and player names are replaced with {User}, tokens are removed")
            .AppendLine();
        var saved = 0;
        var part = zip + ".part";
        try
        {
            using (var archive = ZipFile.Open(part, ZipArchiveMode.Create))
            {
                foreach (var (entry, path) in files)
                {
                    var file = new FileInfo(path);
                    if (!file.Exists)
                    {
                        list.AppendLine($"{entry}: missing");
                        continue;
                    }
                    try
                    {
                        Add(archive, entry, Clean(Read(path)), file.LastWriteTime);
                        list.AppendLine($"{entry}: {file.Length} bytes, changed {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                        saved++;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        list.AppendLine($"{entry}: couldn't read: {e.Message}");
                    }
                }
                Add(archive, "contents.txt", Clean(list.ToString()), DateTime.Now);
            }
            File.Move(part, zip, true);
        }
        finally
        {
            File.Delete(part);
        }
        return saved;
    }

    public static string Clean(string text)
    {
        text = UserFolder.Replace(text, "$1" + User);
        if (UserName != null) text = UserName.Replace(text, User);
        text = Secret.Replace(text, "$1<removed>");
        text = Player.Replace(text, "$1<player>$2");
        return Profile.Replace(text, "$1<player>$2");
    }

    static List<(string Entry, string Path)> Files(string game)
    {
        var engine = EngineKit.EngineDir(game);
        var logs = Path.Combine(engine, "logs");
        var storage = Path.Combine(engine, "storage");
        var official = Path.Combine(engine, "storage-official");
        return
        [
            .. EngineLogs.Concat(Found(logs, "bifrost-vulkan_*.log")).Select(n => ($"logs/{n}", Path.Combine(logs, n))),
            .. Run("official", official), ("official/engine.json", Path.Combine(official, "engine.json")),
            .. Run("bifrost", LogsPage.Docs(game) ?? storage), ("bifrost/engine.json", Path.Combine(storage, "engine.json")),
        ];
    }

    static IEnumerable<(string, string)> Run(string name, string dir) =>
        PerRun.Concat(Found(dir, "bh-*.txt")).Distinct(StringComparer.OrdinalIgnoreCase).Select(n => ($"{name}/{n}", Path.Combine(dir, n)));

    static IEnumerable<string> Found(string dir, string pattern) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, pattern).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
            : [];

    static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var cut = stream.Length > MaxBytes;
        if (cut) stream.Seek(-MaxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        return cut ? "(earlier lines not saved)\n" + text : text;
    }

    static void Add(ZipArchive archive, string name, string text, DateTime changed)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = changed;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
