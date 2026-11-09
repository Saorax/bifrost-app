using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace BifrostStudio;

public enum Product { App, Bifrost, Official }

public sealed record Asset(string Name, string Url, long Size, string Sha256);

public sealed record Release(string Name, string Game, string Server, Asset Kit, Asset? ServerPackage, Asset? App,
                             string? SwfSha256 = null, string? GameFiles = null, string? PortCommit = null,
                             IReadOnlyDictionary<string, string>? GameFileList = null);

public sealed record OfficialRelease(string Name, Asset Package, string? GameFiles, string? PortCommit,
                                     IReadOnlyDictionary<string, string>? GameFileList = null);

public sealed record AppRelease(string Name, Asset Exe);

public sealed record ReleaseNotes(string Title, string Body, DateTimeOffset? Published, string? Url);

public static class Releases
{
    static string? Metadata(string key) => typeof(Releases).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : null;

    public static readonly string? AppVersion = Metadata("AppVersion");

    public static string? Repo(Product product) => Metadata(product + "Repo") ?? Metadata("ReleaseRepo");

    public static string Manifest(Product product) => product.ToString().ToLowerInvariant() + ".json";

    static string? ListUrl(Product product) =>
        Repo(product) is { } repo ? $"https://api.github.com/repos/{repo}/releases?per_page=40" : null;

    public static string? LatestUrl => ListUrl(Product.Bifrost);

    static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BifrostApp/1.0");
        return http;
    }

    static readonly bool[] reached = new bool[Enum.GetValues<Product>().Length];

    public static bool Reached(Product product) => reached[(int)product];

    static readonly ReleaseNotes?[] notes = new ReleaseNotes?[Enum.GetValues<Product>().Length];

    public static ReleaseNotes? Notes(Product product) => notes[(int)product];

    static ReleaseNotes NotesOf(JsonObject release) => new(
        (string?)release["name"] is { Length: > 0 } title ? title : (string?)release["tag_name"] ?? "",
        ((string?)release["body"] ?? "").Trim(),
        DateTimeOffset.TryParse((string?)release["published_at"], out var at) ? at : null,
        (string?)release["html_url"] is { } url && url.StartsWith("https://github.com/", StringComparison.Ordinal) ? url : null);

    static readonly string?[] problems = new string?[Enum.GetValues<Product>().Length];
    public static string? Problem(Product product) => problems[(int)product];

    static string Describe(Exception e) => e switch
    {
        TaskCanceledException => "no answer within 15 seconds",
        HttpRequestException { InnerException: { } inner } h when !h.Message.Contains(inner.Message) => $"{h.Message} ({inner.Message})",
        _ => e.Message,
    };
    
    static async Task<string> GetTextAsync(HttpClient http, string url)
    {
        using var answer = await http.GetAsync(url);
        if (answer.IsSuccessStatusCode) return await answer.Content.ReadAsStringAsync();
        var status = $"{(int)answer.StatusCode} {answer.ReasonPhrase}";
        if (answer.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0")
        {
            var reset = answer.Headers.TryGetValues("X-RateLimit-Reset", out var r) && long.TryParse(r.FirstOrDefault(), out var at)
                ? $", it resets at {DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime():HH:mm}" : "";
            status += $": GitHub's hourly limit for this network is used up{reset}";
        }
        throw new HttpRequestException(status, null, answer.StatusCode);
    }

    static async Task<(JsonNode Manifest, Dictionary<string, string> Urls)?> LatestManifestAsync(Product product)
    {
        if (ListUrl(product) is not { } url) return null;
        reached[(int)product] = false;
        string apiProblem;
        try
        {
            using var http = Client();
            http.Timeout = TimeSpan.FromSeconds(15);
            var answer = JsonNode.Parse(await GetTextAsync(http, url))!;
            reached[(int)product] = true;
            problems[(int)product] = null;
            return await FromListAsync(product, http, answer);
        }
        catch (Exception e) when (!reached[(int)product]
                                  && e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            apiProblem = $"api.github.com: {Describe(e)}";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                      or NullReferenceException or InvalidOperationException or ArgumentException)
        {
            problems[(int)product] = $"read the release list, but not the latest release: {Describe(e)}";
            return null;
        }
        var mirror = MirrorFolder(product);
        var fromMirror = mirror == null ? null : LatestFromMirrorAsync(product, mirror);
        string githubProblem;
        try
        {
            var found = await LatestByDownloadAsync(product, Repo(product)!);
            reached[(int)product] = true;
            problems[(int)product] = $"{apiProblem}; got the latest release from github.com instead";
            return found;
        }
        catch (Exception e) when (Fallible(e))
        {
            githubProblem = $"github.com: {Describe(e)}";
        }
        if (fromMirror == null)
        {
            problems[(int)product] = $"{apiProblem}; {githubProblem}";
            return null;
        }
        var host = new Uri(mirror!).Host;
        try
        {
            var found = await fromMirror;
            reached[(int)product] = true;
            problems[(int)product] = $"{apiProblem}; {githubProblem}; got the latest release from {host} instead";
            return found;
        }
        catch (Exception e) when (Fallible(e))
        {
            problems[(int)product] = $"{apiProblem}; {githubProblem}; {host}: {Describe(e)}";
            return null;
        }
    }

    static bool Fallible(Exception e) => e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
        or InvalidOperationException or ArgumentException or UriFormatException;

    static string? MirrorFolder(Product product) =>
        Metadata("ReleaseMirror") is { } mirror && Repo(product) is { } repo
            ? $"{mirror.TrimEnd('/')}/{repo[(repo.LastIndexOf('/') + 1)..]}/" : null;

    static async Task<(JsonNode Manifest, Dictionary<string, string> Urls)> LatestFromMirrorAsync(Product product, string mirror)
    {
        using var http = Client();
        http.Timeout = TimeSpan.FromSeconds(15);
        var tag = (await GetTextAsync(http, mirror + "latest.txt")).Trim();
        if (tag.Length == 0 || tag[0] == '.' || tag.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new HttpRequestException("latest.txt doesn't name a release");
        return await FromFolderAsync(http, $"{mirror}{tag}/", Manifest(product));
    }

    static async Task<(JsonNode Manifest, Dictionary<string, string> Urls)> FromFolderAsync(HttpClient http, string folder, string name)
    {
        var manifest = JsonNode.Parse(await GetTextAsync(http, folder + name))!;
        var urls = AssetNames(manifest).Distinct().ToDictionary(asset => asset, asset => folder + Uri.EscapeDataString(asset));
        return (manifest, urls);
    }

    static async Task<(JsonNode Manifest, Dictionary<string, string> Urls)?> FromListAsync(Product product, HttpClient http, JsonNode answer)
    {
        var list = answer is JsonArray array ? array : new JsonArray(answer.DeepClone());
        var names = product == Product.Bifrost ? new[] { Manifest(product), "release.json" } : [Manifest(product)];
        foreach (var release in list.OfType<JsonObject>())
        {
            if ((bool?)release["draft"] == true || (bool?)release["prerelease"] == true) continue;
            var urls = release["assets"]!.AsArray().ToDictionary(a => (string)a!["name"]!, a => (string)a!["browser_download_url"]!);
            if (names.FirstOrDefault(urls.ContainsKey) is not { } name) continue;
            var manifest = JsonNode.Parse(await GetTextAsync(http, urls[name]))!;
            notes[(int)product] = NotesOf(release);
            return (manifest, urls);
        }
        return null;
    }
    static async Task<(JsonNode Manifest, Dictionary<string, string> Urls)?> LatestByDownloadAsync(Product product, string repo)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BifrostApp/1.0");
        string target;
        using (var redirect = await http.GetAsync($"https://github.com/{repo}/releases/latest/download/{Manifest(product)}"))
        {
            if (redirect.Headers.Location is not { } location)
                throw new HttpRequestException($"{(int)redirect.StatusCode} {redirect.ReasonPhrase}", null, redirect.StatusCode);
            target = (location.IsAbsoluteUri ? location : new Uri(new Uri("https://github.com/"), location)).AbsoluteUri;
        }
        var expected = $"https://github.com/{repo}/releases/download/";
        if (!target.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || target.IndexOf('/', expected.Length) < 0)
            throw new HttpRequestException($"unexpected redirect to {target}");
        using var follow = Client();
        follow.Timeout = TimeSpan.FromSeconds(15);
        var cut = target.LastIndexOf('/') + 1;
        return await FromFolderAsync(follow, target[..cut], target[cut..]);
    }

    static IEnumerable<string> AssetNames(JsonNode? node) => node switch
    {
        JsonObject o => (o["asset"] is JsonValue v && v.TryGetValue<string>(out var name) ? [name] : Enumerable.Empty<string>())
            .Concat(o.Select(kv => kv.Value).SelectMany(AssetNames)),
        JsonArray a => a.SelectMany(AssetNames),
        _ => [],
    };

    static Asset? Read(JsonNode? node, Dictionary<string, string> urls) =>
        node?["asset"] is { } name && urls.TryGetValue((string)name!, out var u)
            ? new Asset((string)name!, u, (long)node["size"]!, (string)node["sha256"]!) : null;

    static Dictionary<string, string>? FileList(JsonNode? node) =>
        node is JsonObject list ? list.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => (string)kv.Value!) : null;

    public static async Task<Release?> LatestAsync()
    {
        if (await LatestManifestAsync(Product.Bifrost) is not var (m, urls)) return null;
        try
        {
            var kit = Read(m, urls);
            return kit == null ? null
                : new Release((string)m["release"]!, (string)m["game"]!, (string)m["server"]!, kit, Read(m["server_package"], urls),
                    Read(m["app"], urls), (string?)m["swf_sha256"], (string?)m["game_files"], (string?)m["port_commit"],
                    FileList(m["game_file_list"]));
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException or FormatException) { return null; }
    }

    public static async Task<OfficialRelease?> LatestOfficialAsync()
    {
        if (await LatestManifestAsync(Product.Official) is not var (m, urls)) return null;
        try
        {
            return Read(m, urls) is { } package
                ? new OfficialRelease((string)m["release"]!, package, (string?)m["game_files"], (string?)m["port_commit"],
                    FileList(m["game_file_list"])) : null;
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException or FormatException) { return null; }
    }

    public static async Task<AppRelease?> LatestAppAsync()
    {
        if (await LatestManifestAsync(Product.App) is not var (m, urls)) return null;
        try
        {
            return Read(m, urls) is { } exe ? new AppRelease((string)m["release"]!, exe) : null;
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException or FormatException) { return null; }
    }

    public static async Task DownloadAsync(Asset asset, string file, IProgress<double> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file) && new FileInfo(file).Length == asset.Size && await HashAsync(file, cancel) == asset.Sha256)
        {
            progress.Report(1);
            return;
        }
        using var http = Client();
        using var answer = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
        answer.EnsureSuccessStatusCode();
        var part = file + ".part";
        await using (var from = await answer.Content.ReadAsStreamAsync(cancel))
        await using (var to = File.Create(part))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            int n;
            while ((n = await from.ReadAsync(buffer, cancel)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), cancel);
                done += n;
                progress.Report(asset.Size > 0 ? (double)done / asset.Size : 0);
            }
        }
        if (new FileInfo(part).Length != asset.Size || await HashAsync(part, cancel) != asset.Sha256)
        {
            File.Delete(part);
            throw new IOException("The download is damaged. Try again.");
        }
        File.Move(part, file, overwrite: true);
    }

    public static async Task<string> HashAsync(string file, CancellationToken cancel = default)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel));
    }
}
