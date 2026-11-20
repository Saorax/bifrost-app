using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace BifrostStudio;

public sealed record HarnessCopy(string Dir, string Game, IReadOnlyList<int> ReplayVersions)
{
    public string Exe => Path.Combine(Dir, "Brawlhalla.exe");
    public string JobFile => Path.Combine(Dir, ReplayHarness.JobName);
}

public static class ReplayHarness
{
    public const string JobName = "bifrost-harness.json";
    public const string InfoName = "bifrost-harness-info.json";
    public static readonly TimeSpan StatsTimeout = TimeSpan.FromMinutes(20);
    static readonly SemaphoreSlim busy = new(1, 1);

    public static string? RootOverride { get; set; }

    public static string Root => RootOverride ?? Path.Combine(EngineKit.Work, "replay-harness");

    public static string Logs => Path.Combine(Path.GetTempPath(), "BifrostStudio", "replay-harness");

    public static IReadOnlyList<HarnessCopy> Copies()
    {
        var found = new List<HarnessCopy>();
        try
        {
            if (!Directory.Exists(Root)) return found;
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var info = Path.Combine(dir, InfoName);
                if (!File.Exists(info) || !File.Exists(Path.Combine(dir, "Brawlhalla.exe"))) continue;
                try
                {
                    var o = JsonNode.Parse(File.ReadAllText(info))!.AsObject();
                    var versions = o["replayVersions"]?.AsArray().Select(v => v!.GetValue<int>()).ToList() ?? [];
                    found.Add(new HarnessCopy(dir, o["game"]?.GetValue<string>() ?? Path.GetFileName(dir), versions));
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }

    public static HarnessCopy? For(int replayVersion) => Copies().FirstOrDefault(c => c.ReplayVersions.Contains(replayVersion));

    public static bool Running()
    {
        try
        {
            return Process.GetProcessesByName("Brawlhalla").Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static string WriteJob(HarnessCopy copy, Dictionary<string, object?> job)
    {
        var id = Guid.NewGuid().ToString("N");
        job["id"] = id;
        File.WriteAllText(copy.JobFile, JsonSerializer.Serialize(job));
        return id;
    }

    public static void ClearJob(HarnessCopy copy, string id)
    {
        try
        {
            if (File.Exists(copy.JobFile) && File.ReadAllText(copy.JobFile).Contains(id, StringComparison.Ordinal)) File.Delete(copy.JobFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public const string DesktopName = "BifrostReplayHarness";
    static IntPtr desktop;

    public static Process Start(HarnessCopy copy, Func<bool>? rendering = null)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("old versions only play on Windows");
        lock (busy)
        {
            if (desktop == IntPtr.Zero)
            {
                desktop = CreateDesktopW(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
                if (desktop == IntPtr.Zero) throw new IOException("the old game's hidden desktop couldn't be made. Error " + Marshal.GetLastWin32Error());
            }
        }
        var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = DesktopName };
        var command = new System.Text.StringBuilder($"\"{copy.Exe}\" -forceoffline");
        if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, copy.Dir, ref si, out var pi))
            throw new IOException("the old game didn't start. Error " + Marshal.GetLastWin32Error());
        try
        {
            return Process.GetProcessById((int)pi.dwProcessId);
        }
        finally
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    public static async Task<bool> WaitUntilClosed(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (Running())
        {
            if (DateTime.UtcNow > until) return false;
            await Task.Delay(250).ConfigureAwait(false);
        }
        return true;
    }

    const uint GenericAll = 0x10000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr attributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string? application, System.Text.StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    public static async Task<ReplayFrames> StatsAsync(HarnessCopy copy, string replay, string output, CancellationToken cancel = default)
    {
        var name = Path.GetFileName(replay);
        if (!await busy.WaitAsync(0, cancel).ConfigureAwait(false)) return ReplayFrames.Failed(name, "the old game is busy with another replay");
        string? job = null;
        try
        {
            if (!await WaitUntilClosed(TimeSpan.FromSeconds(15)).ConfigureAwait(false))
                return ReplayFrames.Failed(name, "Brawlhalla is already running, and only one can run at a time. Close it first");
            Directory.CreateDirectory(Logs);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var temp = output + ".harness";
            if (File.Exists(temp)) File.Delete(temp);
            var log = Path.Combine(Logs, "stats.log");
            if (File.Exists(log)) File.Delete(log);
            job = WriteJob(copy, new() { ["mode"] = "stats", ["replay"] = replay, ["out"] = temp, ["log"] = log, ["port"] = 0, ["width"] = 960, ["height"] = 540 });
            using var p = Start(copy);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(StatsTimeout);
            try
            {
                await p.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return ReplayFrames.Failed(name, "the old game took too long and was stopped");
            }
            if (!File.Exists(temp)) return ReplayFrames.Failed(name, $"the old game stopped without stats (exit {p.ExitCode}). Its log is in {Logs}");
            File.Move(temp, output, true);
            return ReplayFrames.Read(output);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return ReplayFrames.Failed(name, "the old game couldn't be run: " + e.Message);
        }
        finally
        {
            if (job != null) ClearJob(copy, job);
            busy.Release();
        }
    }
}
