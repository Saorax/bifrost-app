using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace BifrostStudio;

public static class HostedServer
{
    public static string Folder => Path.Combine(EngineKit.Home, "server");
    public static string Log => Path.Combine(Folder, "server.log");
    static string LocalConfig => Path.Combine(Folder, "config.local.json");
    static string? startedWith;

    public static bool Downloaded => File.Exists(Path.Combine(Folder, "bh_server", "__main__.py"));

    static string PackageFile => Path.Combine(Folder, ".package");

    public static bool UpdateAvailable(Asset? package)
    {
        if (package == null || !Downloaded) return false;
        try { return File.ReadAllText(PackageFile).Trim() != package.Sha256; }
        catch (Exception) { return true; }
    }

    public static string? Version
    {
        get
        {
            try { return File.ReadAllText(Path.Combine(Folder, "SERVER.txt")).Split(' ', '\n')[1].Trim(); }
            catch (Exception) { return null; }
        }
    }

    public static int Port
    {
        get
        {
            var port = 7910;
            foreach (var name in new[] { "config.json", "config.local.json" })
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, name)))?["game_port"]?.GetValue<int>() is int p) port = p;
                }
                catch (Exception) { }
            }
            return port;
        }
    }

    public static string Address => $"127.0.0.1:{Port}";

    public static async Task DownloadAsync(Asset package, IProgress<double> progress)
    {
        var zip = Path.Combine(EngineKit.Home, "downloads", package.Name);
        await Releases.DownloadAsync(package, zip, progress, CancellationToken.None);
        Directory.CreateDirectory(Folder);
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, Folder, overwriteFiles: true));
        File.Delete(zip);
        File.WriteAllText(PackageFile, package.Sha256);
    }

    public static async Task UpdateAsync(Asset package, string? python, IProgress<double> progress)
    {
        var restart = Running;
        if (restart) await StopAsync();
        try { await DownloadAsync(package, progress); }
        finally { if (restart && python != null) Start(python); }
    }

    static bool WrapperRunning
    {
        get
        {
            try
            {
                if (!int.TryParse(File.ReadAllText(Log + ".pid").Trim(), out var pid)) return false;
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch (Exception) { return false; }
        }
    }

    public static bool Listening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch (Exception) { return false; }
    }

    public static bool Running => Downloaded && (WrapperRunning || ListenerIsOurs(Port));

    public static bool PortTaken => !Running && Listening(Port);

    static bool ListenerIsOurs(int port)
    {
        if (!OperatingSystem.IsWindows() || ListenerPid(port) is not { } pid) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            var exe = p.MainModule?.FileName;
            return exe != null && (exe.StartsWith(EngineKit.Home, StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(exe, startedWith, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    static int? ListenerPid(int port)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return null;
            var rows = Marshal.ReadInt32(table);
            for (var i = 0; i < rows; i++)
            {
                var row = table + 4 + i * 24;
                var raw = Marshal.ReadInt32(row, 8);
                if (((raw & 0xFF) << 8 | (raw >> 8) & 0xFF) == port) return Marshal.ReadInt32(row, 20);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sorted, int family, int tableClass, uint reserved);

    public static async Task<List<(string Name, string Address)>> AddressesAsync()
    {
        var port = Port;
        var list = new List<(string, string)> { ("This PC", $"127.0.0.1:{port}") };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var zerotier = (nic.Description + nic.Name).Contains("ZeroTier", StringComparison.OrdinalIgnoreCase);
                var props = nic.GetIPProperties();
                if (!zerotier && props.GatewayAddresses.Count == 0) continue;
                foreach (var a in props.UnicastAddresses)
                {
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                        list.Add((zerotier ? "ZeroTier" : "Local network", $"{a.Address}:{port}"));
                }
            }
        }
        catch (NetworkInformationException) { }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            if (IPAddress.TryParse(ip, out var address) && await Reachable(address, port)) list.Add(("Internet", $"{ip}:{port}"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        return list;
    }

    static async Task<bool> Reachable(IPAddress address, int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(address, port).WaitAsync(TimeSpan.FromSeconds(3));
            return true;
        }
        catch (Exception e) when (e is SocketException or TimeoutException) { return false; }
    }

    public static string? Setting(string key)
    {
        foreach (var name in new[] { "config.local.json", "config.json" })
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, name)))?[key] is { } v) return v.ToString();
            }
            catch (Exception) { }
        }
        return null;
    }

    public static void SetSettings(string? name, int port, string? clientId, string? secret, string? publicUrl)
    {
        JsonObject cfg;
        try { cfg = JsonNode.Parse(File.ReadAllText(LocalConfig)) as JsonObject ?? []; }
        catch (Exception) { cfg = []; }
        void Put(string key, JsonNode? value)
        {
            if (value == null || value.ToString().Length == 0) cfg.Remove(key);
            else cfg[key] = value;
        }
        Put("server_name", name);
        Put("game_port", port);
        Put("web_port", port + 1);
        Put("discord_client_id", clientId);
        Put("discord_client_secret", secret);
        Put("public_url", publicUrl);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(LocalConfig, cfg.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Start(string python)
    {
        if (!Downloaded) throw new InvalidOperationException("Download the server first.");
        if (Running) return;
        startedWith = python;
        if (OperatingSystem.IsWindows())
        {
            foreach (var std in new[] { -10, -11, -12 }) SetHandleInformation(GetStdHandle(std), 1, 0);
        }
        var info = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Folder,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-I", Path.Combine(Folder, "studio_run.py"), "--log", Log, "--cwd", Folder, "--", python, "-m", "bh_server" })
            info.ArgumentList.Add(a);
        using var p = Process.Start(info);
        p?.StandardInput.Close();
    }

    public static async Task StopAsync()
    {
        File.WriteAllText(Log + ".stop", "");
        for (var i = 0; i < 48 && Running; i++) await Task.Delay(250);
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll")]
    static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}

public sealed record ServerInfo(string Name, int Players, IReadOnlyList<(string Activity, int Count)> Activity);

public static class ServerStatus
{
    static readonly string[] Shown = ["menu", "training", "casual", "ranked", "custom", "tournament", "couch", "spectating"];

    public static async Task<ServerInfo?> FetchAsync(string address)
    {
        var at = address.LastIndexOf(':');
        if (at < 0 || !int.TryParse(address[(at + 1)..], out var port)) return null;
        var host = address[..at];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        foreach (var scheme in new[] { "http", "https" })
        {
            try
            {
                var info = JsonNode.Parse(await http.GetStringAsync($"{scheme}://{host}:{port + 1}/info"))!;
                var activity = info["activity"] as JsonObject;
                var shown = Shown.Select(k => (k, activity?[k]?.GetValue<int>() ?? 0)).Where(a => a.Item2 > 0).ToList();
                return new ServerInfo((string?)info["name"] ?? "Bifrost server", info["players"]?.GetValue<int>() ?? 0, shown);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                          or InvalidOperationException or FormatException or NullReferenceException) { }
        }
        return null;
    }
}
