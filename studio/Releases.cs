using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace BifrostStudio;

public enum Product { App, Bifrost, Official }

public sealed record Asset(string Name, string Url, long Size, string Sha256);

public sealed record Release(string Name, string Game, string Server, Asset Kit, Asset? ServerPackage, Asset? App,
                             string? SwfSha256 = null, string? GameFiles = null, string? PortCommit = null);

public sealed record OfficialRelease(string Name, Asset Package, string? GameFiles, string? PortCommit);

public sealed record AppRelease(string Name, Asset Exe);

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

    static async Task<(JsonNode Manifest, Dictionary<string, string> Urls)?> LatestManifestAsync(Product product)
    {
        if (ListUrl(product) is not { } url) return null;
        try
        {
            using var http = Client();
            http.Timeout = TimeSpan.FromSeconds(15);
            var answer = JsonNode.Parse(await http.GetStringAsync(url))!;
            var list = answer is JsonArray array ? array : new JsonArray(answer.DeepClone());
            var names = product == Product.Bifrost ? new[] { Manifest(product), "release.json" } : [Manifest(product)];
            foreach (var release in list.OfType<JsonObject>())
            {
                if ((bool?)release["draft"] == true || (bool?)release["prerelease"] == true) continue;
                var urls = release["assets"]!.AsArray().ToDictionary(a => (string)a!["name"]!, a => (string)a!["browser_download_url"]!);
                if (names.FirstOrDefault(urls.ContainsKey) is not { } name) continue;
                return (JsonNode.Parse(await http.GetStringAsync(urls[name]))!, urls);
            }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                      or NullReferenceException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    static Asset? Read(JsonNode? node, Dictionary<string, string> urls) =>
        node?["asset"] is { } name && urls.TryGetValue((string)name!, out var u)
            ? new Asset((string)name!, u, (long)node["size"]!, (string)node["sha256"]!) : null;

    public static async Task<Release?> LatestAsync()
    {
        if (await LatestManifestAsync(Product.Bifrost) is not var (m, urls)) return null;
        try
        {
            var kit = Read(m, urls);
            return kit == null ? null
                : new Release((string)m["release"]!, (string)m["game"]!, (string)m["server"]!, kit, Read(m["server_package"], urls),
                    Read(m["app"], urls), (string?)m["swf_sha256"], (string?)m["game_files"], (string?)m["port_commit"]);
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException or FormatException) { return null; }
    }

    public static async Task<OfficialRelease?> LatestOfficialAsync()
    {
        if (await LatestManifestAsync(Product.Official) is not var (m, urls)) return null;
        try
        {
            return Read(m, urls) is { } package
                ? new OfficialRelease((string)m["release"]!, package, (string?)m["game_files"], (string?)m["port_commit"]) : null;
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
