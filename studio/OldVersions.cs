using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BifrostStudio;

public sealed record OldVersion(string Tag, string Label, IReadOnlyList<int> ReplayVersions, int App, int Depot, string Manifest, long Bytes);

public interface IOldVersionReport
{
    void Stage(string text, double part);
    void Qr(bool[,]? code);
}

public static partial class OldVersions
{
    public const string PackRelease = "replay-pack";
    const string DdUrl = "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_3.4.0/DepotDownloader-windows-x64.zip";
    const long DdSize = 33474005;
    const string DdSha = "41c9e9f0df54b3ad02e67a11726756e5c73283bd7c2e1b04acfa5ae4c2ed3767";
    static readonly TimeSpan PackAge = TimeSpan.FromDays(1);
    static readonly SemaphoreSlim busy = new(1, 1);

    public static string? RootOverride { get; set; }
    public static string? DepotDownloaderOverride { get; set; }
    public static string? PackUrlOverride { get; set; }

    public static string Root => RootOverride ?? Path.Combine(EngineKit.Work, "replay-pack");
    public static string Tools => Path.Combine(BifrostFolders.Build, "Tools", "DepotDownloader");
    static string DepotDownloader => DepotDownloaderOverride ?? Path.Combine(Tools, "DepotDownloader.exe");
    public static string? AccountFileOverride { get; set; }
    static string AccountFile => AccountFileOverride ?? BifrostFolders.StudioFile("steam-download.json");

    public static string? PackUrl => PackUrlOverride ?? (Releases.Repo(Product.App) is { } repo ? $"https://github.com/{repo}/releases/download/{PackRelease}/replay-pack.zip" : null);

    public static bool Busy => busy.CurrentCount == 0;

    public static IReadOnlyList<OldVersion> Index()
    {
        try
        {
            if (JsonFiles.Read(Path.Combine(Root, "index.json")) is not JsonObject o || o["versions"] is not JsonArray list) return [];
            return [.. list.OfType<JsonObject>().Select(v => new OldVersion(
                (string?)v["tag"] ?? "", (string?)v["label"] ?? (string?)v["tag"] ?? "",
                (v["replayVersions"] as JsonArray ?? []).Select(x => x!.GetValue<int>()).ToList(),
                v["app"]?.GetValue<int>() ?? 291550, v["depot"]?.GetValue<int>() ?? 291551, (string?)v["manifest"] ?? "", v["bytes"]?.GetValue<long>() ?? 0))
                .Where(v => v.Tag.Length > 0 && v.Manifest.Length > 0)];
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or NullReferenceException) { return []; }
    }

    public static OldVersion? For(int replayVersion) => Index().FirstOrDefault(v => v.ReplayVersions.Contains(replayVersion));

    public static IReadOnlyList<OldVersion> Missing() => [.. Index().Where(v => !v.ReplayVersions.Any(r => ReplayHarness.For(r) != null))];

    public static async Task<string?> EnsurePackAsync(CancellationToken cancel = default)
    {
        var index = Path.Combine(Root, "index.json");
        if (File.Exists(index) && DateTime.UtcNow - File.GetLastWriteTimeUtc(index) < PackAge) return null;
        if (PackUrl is not { } url) return File.Exists(index) ? null : "This build of the app doesn't know where to get the replay pack.";
        var zip = Path.Combine(Path.GetTempPath(), "BifrostStudio", "replay-pack.zip");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BifrostApp/1.0");
                await using var from = await http.GetStreamAsync(url, cancel);
                await using var to = File.Create(zip);
                await from.CopyToAsync(to, cancel);
            }
            var fresh = Root + ".new";
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
            ZipFile.ExtractToDirectory(zip, fresh);
            if (!File.Exists(Path.Combine(fresh, "index.json"))) throw new InvalidDataException("the replay pack has no index");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Directory.Move(fresh, Root);
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (File.Exists(index)) return null;
            return "Couldn't get the list of old versions. " + e.Message;
        }
        finally
        {
            try { File.Delete(zip); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static async Task<string?> EnsureDepotDownloaderAsync(IOldVersionReport report, CancellationToken cancel)
    {
        if (File.Exists(DepotDownloader)) return null;
        var zip = Path.Combine(Tools, "DepotDownloader.zip");
        try
        {
            report.Stage("Getting Steam's downloader", 0);
            await Releases.DownloadAsync(new Asset("DepotDownloader-windows-x64.zip", DdUrl, DdSize, DdSha), zip,
                new Progress<double>(p => report.Stage("Getting Steam's downloader", p * 0.05)), cancel);
            ZipFile.ExtractToDirectory(zip, Tools, true);
            File.Delete(zip);
            return File.Exists(DepotDownloader) ? null : "Steam's downloader didn't unpack.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return "Couldn't get Steam's downloader. " + e.Message;
        }
    }

    public static string? SteamAccount
    {
        get
        {
            try { return (string?)JsonFiles.Read(AccountFile)?["username"] is { Length: > 0 } u ? u : null; }
            catch (Exception e) when (e is InvalidOperationException or FormatException) { return null; }
        }
        set
        {
            var o = JsonFiles.Read(AccountFile) as JsonObject ?? new JsonObject();
            if (value == null) o.Remove("username");
            else o["username"] = value;
            try { JsonFiles.Write(AccountFile, o); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public static async Task<string?> GetAsync(OldVersion v, IOldVersionReport report, CancellationToken cancel = default)
    {
        if (!await busy.WaitAsync(0, cancel)) return "Another old version is being set up. Try again when it's done.";
        try
        {
            if (await EnsurePackAsync(cancel) is { } packWhy) return packWhy;
            if (await EnsureDepotDownloaderAsync(report, cancel) is { } ddWhy) return ddWhy;
            if (EngineKit.Downloaded is not { } kit || !File.Exists(Path.Combine(kit, "python", "python.exe")))
                return "Install Bifrost first. Its tools make the old version's copy.";
            var game = Path.Combine(ReplayHarness.Root, "games", v.Tag);
            if (await DownloadAsync(v, game, report, cancel) is { } downloadWhy) return downloadWhy;
            report.Stage("Setting up " + v.Label, 0.92);
            return await MakeCopyAsync(v, game, kit, cancel);
        }
        finally
        {
            report.Qr(null);
            busy.Release();
        }
    }

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)%\s")]
    private static partial Regex Percent();

    [GeneratedRegex(@"login with -username (\S+) -remember-password")]
    private static partial Regex Remembered();

    static async Task<string?> DownloadAsync(OldVersion v, string game, IOldVersionReport report, CancellationToken cancel)
    {
        Directory.CreateDirectory(game);
        var list = Path.Combine(Path.GetTempPath(), "BifrostStudio", "old-version-files.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        await File.WriteAllTextAsync(list, "regex:^(?!audio[\\\\/]).+\nregex:^audio[\\\\/].*Init\\.bnk$\n", cancel);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var account = SteamAccount;
            var start = new ProcessStartInfo(DepotDownloader)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = System.Text.Encoding.Latin1, StandardErrorEncoding = System.Text.Encoding.Latin1,
            };
            foreach (var a in new[] { "-app", v.App.ToString(), "-depot", v.Depot.ToString(), "-manifest", v.Manifest, "-dir", game, "-filelist", list })
                start.ArgumentList.Add(a);
            if (account != null) foreach (var a in new[] { "-username", account, "-remember-password" }) start.ArgumentList.Add(a);
            else foreach (var a in new[] { "-qr", "-remember-password" }) start.ArgumentList.Add(a);
            report.Stage(account == null ? "Sign in to Steam to download " + v.Label : "Downloading " + v.Label + " from Steam", 0.05);
            var last = "";
            var qr = new List<string>();
            var readingQr = false;
            var asked = false;
            using var p = Process.Start(start) ?? throw new IOException("Steam's downloader didn't start");
            p.StandardInput.Close();
            using var stop = cancel.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } });
            var errors = p.StandardError.ReadToEndAsync(cancel);
            while (await p.StandardOutput.ReadLineAsync(cancel) is { } raw)
            {
                var line = raw.Replace("â\u0096\u0088", "#");
                if (line.Contains("sign in with this QR code", StringComparison.Ordinal) || line.StartsWith("The QR code has changed", StringComparison.Ordinal))
                {
                    readingQr = true;
                    qr.Clear();
                    continue;
                }
                if (readingQr)
                {
                    if (!line.Any(char.IsAsciiLetterOrDigit))
                    {
                        if (line.Trim().Length > 0) qr.Add(line);
                        if (qr.Count == 0 || qr.Count < QrSize(qr[0])) continue;
                    }
                    readingQr = false;
                    if (QrCode(qr) is { } code)
                    {
                        report.Qr(code);
                        report.Stage("Scan the code with the Steam app to sign in. You only do this once.", 0.05);
                    }
                    if (!line.Any(char.IsAsciiLetterOrDigit)) continue;
                }
                if (Remembered().Match(line) is { Success: true } m)
                {
                    SteamAccount = m.Groups[1].Value;
                    report.Qr(null);
                }
                if (line.Contains("Enter account password", StringComparison.Ordinal) || line.Contains("STEAM GUARD! Please enter", StringComparison.Ordinal)) asked = true;
                if (line.Contains("Use the Steam Mobile App to confirm", StringComparison.Ordinal)) report.Stage("Confirm the sign-in in the Steam app", 0.05);
                if (Percent().Match(line) is { Success: true } pct && double.TryParse(pct.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var done))
                    report.Stage("Downloading " + v.Label + " from Steam", 0.05 + done / 100 * 0.85);
                if (line.Trim().Length > 0) last = line.Trim();
            }
            await p.WaitForExitAsync(cancel);
            var err = (await errors).Trim();
            if (p.ExitCode == 0 && File.Exists(Path.Combine(game, "BrawlhallaAir.swf")))
            {
                try
                {
                    var cache = Path.Combine(game, ".DepotDownloader");
                    if (Directory.Exists(cache)) Directory.Delete(cache, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return null;
            }
            if (account != null && (asked || last.Contains("Access token was rejected", StringComparison.Ordinal) || last.Contains("InvalidPassword", StringComparison.Ordinal)))
            {
                SteamAccount = null;
                continue;
            }
            var why = err.Length > 0 ? err.Split('\n')[^1] : last;
            return "Steam's download stopped. " + (why.Contains("RateLimit", StringComparison.OrdinalIgnoreCase) ? "Steam is limiting sign-ins for now. Try again in an hour." : why);
        }
        return "Steam asked for a sign-in again and it didn't finish.";
    }

    public static int QrSize(string firstRow)
    {
        var text = firstRow.TrimEnd();
        var start = text.Length - text.TrimStart().Length;
        return (text.Length - start + 1) / 2;
    }

    public static bool[,]? QrCode(IReadOnlyList<string> lines)
    {
        var rows = lines.Where(l => l.Trim().Length > 0).ToList();
        if (rows.Count == 0) return null;
        var left = rows.Min(r => r.Length - r.TrimStart().Length);
        var width = rows.Max(r => r.TrimEnd().Length) - left;
        var cells = (width + 1) / 2;
        var code = new bool[rows.Count, cells];
        for (var y = 0; y < rows.Count; y++)
            for (var x = 0; x < cells; x++)
            {
                var at = left + x * 2;
                code[y, x] = at < rows[y].Length && rows[y][at] != ' ';
            }
        return code;
    }

    static async Task<string?> MakeCopyAsync(OldVersion v, string game, string kit, CancellationToken cancel)
    {
        var target = Path.Combine(ReplayHarness.Root, v.Tag);
        var partial = target + ".partial";
        try
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "The old copy's folder is in use. " + e.Message; }
        var start = new ProcessStartInfo(Path.Combine(kit, "python", "python.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-I", Path.Combine(Root, "replay_copy.py"), "--game", game, "--out", partial, "--version", v.Tag, "--label", v.Label,
                     "--names", Path.Combine(Root, "names", v.Tag + ".json"), "--harness", Path.Combine(Root, "harness"), "--tools", Path.Combine(kit, "air", "tools") })
            start.ArgumentList.Add(a);
        foreach (var r in v.ReplayVersions)
        {
            start.ArgumentList.Add("--replay-version");
            start.ArgumentList.Add(r.ToString());
        }
        using var p = Process.Start(start) ?? throw new IOException("Python didn't start");
        using var stop = cancel.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } });
        var output = p.StandardOutput.ReadToEndAsync(cancel);
        var errors = p.StandardError.ReadToEndAsync(cancel);
        await p.WaitForExitAsync(cancel);
        if (p.ExitCode != 0)
        {
            var why = ((await errors) + (await output)).Trim();
            return "Making the " + v.Label + " copy failed. " + (why.Length > 400 ? why[^400..] : why);
        }
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(partial, target);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "The old copy couldn't be put in place. " + e.Message; }
    }

    public static bool KeepAll
    {
        get
        {
            try { return JsonFiles.Read(AccountFile)?["all"]?.GetValue<bool>() == true; }
            catch (Exception e) when (e is InvalidOperationException or FormatException) { return false; }
        }
        set
        {
            var o = JsonFiles.Read(AccountFile) as JsonObject ?? new JsonObject();
            o["all"] = value;
            try { JsonFiles.Write(AccountFile, o); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public static async Task<string?> GetAllAsync(IOldVersionReport report, CancellationToken cancel = default)
    {
        if (await EnsurePackAsync(cancel) is { } why) return why;
        var missing = Missing();
        for (var i = 0; i < missing.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var v = missing[i];
            var one = new Scaled(report, i, missing.Count);
            if (await GetAsync(v, one, cancel) is { } failed)
            {
                if (!failed.Contains("limiting sign-ins", StringComparison.Ordinal)) return v.Label + ": " + failed;
                report.Stage("Steam is limiting sign-ins, so the next one starts in an hour", (double)i / missing.Count);
                await Task.Delay(TimeSpan.FromHours(1), cancel);
                i--;
            }
        }
        return null;
    }

    sealed class Scaled(IOldVersionReport inner, int index, int count) : IOldVersionReport
    {
        public void Stage(string text, double part) => inner.Stage($"{text} ({index + 1} of {count})", (index + part) / count);
        public void Qr(bool[,]? code) => inner.Qr(code);
    }

    public static class Job
    {
        static CancellationTokenSource? stop;
        static readonly object gate = new();

        public static event Action? Changed;
        public static string? Running { get; private set; }
        public static string Text { get; private set; } = "";
        public static double Part { get; private set; }
        public static bool[,]? Code { get; private set; }
        public static string? Problem { get; private set; }
        public static string? ProblemFor { get; private set; }

        public static bool Busy => Running != null;

        public static void Get(OldVersion v) => Start(v.Tag, (r, c) => GetAsync(v, r, c));

        public static void GetAll() => Start("all", GetAllAsync);

        public static void Stop()
        {
            lock (gate) stop?.Cancel();
        }

        static void Start(string what, Func<IOldVersionReport, CancellationToken, Task<string?>> work)
        {
            CancellationToken token;
            lock (gate)
            {
                if (Running != null) return;
                Running = what;
                stop = new CancellationTokenSource();
                token = stop.Token;
            }
            Text = "Starting";
            Part = 0;
            Code = null;
            Problem = null;
            ProblemFor = null;
            Changed?.Invoke();
            _ = Task.Run(async () =>
            {
                string? why;
                try { why = await work(new Reporter(), token); }
                catch (OperationCanceledException) { why = null; }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { why = e.Message; }
                lock (gate)
                {
                    Problem = why;
                    ProblemFor = why != null ? what : null;
                    Running = null;
                    Code = null;
                    stop = null;
                }
                Changed?.Invoke();
            });
        }

        sealed class Reporter : IOldVersionReport
        {
            public void Stage(string text, double part)
            {
                Text = text;
                Part = part;
                Changed?.Invoke();
            }

            public void Qr(bool[,]? code)
            {
                Code = code;
                Changed?.Invoke();
            }
        }
    }
}
