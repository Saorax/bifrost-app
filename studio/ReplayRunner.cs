using System.Diagnostics;

namespace BifrostStudio;

public sealed class ReplayRunner
{
    public const string RunnerFile = "replay-sim.hl";
    public const string HxmlFile = "build-replay-sim.hxml";
    public const string SourceHxml = "build-dx11.hxml";
    public const string MainClass = "port.engine.ReplaySimMain";
    public const string FixedClock = "-D replay_sim";
    public static readonly string[] MainSource = ["src", "port", "engine", "ReplaySimMain.hx"];
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DumpTimeout = TimeSpan.FromMinutes(10);

    public delegate Task<int> Runner(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancel);

    readonly Runner run;

    public ReplayRunner(string buildDir, Runner? run = null)
    {
        BuildDir = buildDir;
        this.run = run ?? ((start, timeout, cancel) => RunProcess(start, timeout, cancel, line => onLine?.Invoke(line)));
    }

    Action<string>? onLine;

    public ReplayRunner(string buildDir, string bin, Runner? run = null) : this(buildDir, run)
    {
        binOverride = bin;
    }

    readonly string? binOverride;

    public static string PlayerBuildDir => Path.Combine(EngineKit.Work, "engine-build");

    public string BuildDir { get; }
    public string Bin => binOverride ?? Path.Combine(BuildDir, "bin");
    public string RunnerPath => Path.Combine(Bin, RunnerFile);
    public string Hl => Path.Combine(Bin, "hl.exe");
    public string Haxe => Path.Combine(BuildDir, "tools", "haxe", "haxe.exe");
    public string Storage => Path.Combine(Path.GetTempPath(), "BifrostStudio", "replay-sim-storage");
    public string Log => Path.Combine(Storage, "replay-sim-boot.txt");

    public bool HasBuild => File.Exists(Haxe) && File.Exists(Path.Combine(BuildDir, SourceHxml));

    public bool HasSimulator => File.Exists(Path.Combine([BuildDir, .. MainSource]));

    public bool CanPrepare => HasBuild && HasSimulator;

    public static readonly string[] ViewSource = ["src", "port", "engine", "ReplayViewApp.hx"];

    public bool HasViewSource => File.Exists(Path.Combine([BuildDir, .. ViewSource]));

    public bool CanView => binOverride != null ? Prepared : Prepared && HasViewSource && File.GetLastWriteTimeUtc(RunnerPath) >= File.GetLastWriteTimeUtc(Path.Combine([BuildDir, .. ViewSource]));

    public bool Prepared
    {
        get
        {
            if (!File.Exists(RunnerPath) || !File.Exists(Hl)) return false;
            var game = Path.Combine(Bin, "game-dx11.hl");
            return !File.Exists(game) || File.GetLastWriteTimeUtc(RunnerPath) >= File.GetLastWriteTimeUtc(game);
        }
    }

    public static string RunnerHxml(string gameHxml)
    {
        var lines = gameHxml.Replace("\r\n", "\n").Split('\n').ToList();
        var main = false;
        var hl = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith("--main ", StringComparison.Ordinal))
            {
                lines[i] = "--main " + MainClass;
                main = true;
            }
            else if (l.StartsWith("--hl ", StringComparison.Ordinal))
            {
                lines[i] = "--hl bin/" + RunnerFile;
                hl = true;
            }
        }
        if (!main) lines.Add("--main " + MainClass);
        if (!hl) lines.Add("--hl bin/" + RunnerFile);
        if (!lines.Any(l => l.Trim() == FixedClock)) lines.Add(FixedClock);
        return "# Bifrost Studio's replay simulator: reads a .replay and writes every player's state per step, no window (" + MainClass +
               "), made from " + SourceHxml + "\n" +
               string.Join("\n", lines.Where(l => !l.TrimStart().StartsWith("# Bifrost on the Heaps engine", StringComparison.Ordinal))).TrimEnd() + "\n";
    }

    public async Task<string?> PrepareAsync(CancellationToken cancel = default)
    {
        if (!HasBuild) return "Bifrost's engine build isn't on this PC, so reinstall or update Bifrost first";
        if (!HasSimulator) return "this Bifrost has no replay simulator yet, so update Bifrost first";
        try
        {
            File.WriteAllText(Path.Combine(BuildDir, HxmlFile), RunnerHxml(File.ReadAllText(Path.Combine(BuildDir, SourceHxml))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "the simulator's build file couldn't be written: " + e.Message;
        }
        var start = new ProcessStartInfo(Haxe) { WorkingDirectory = BuildDir, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(HxmlFile);
        var code = await run(start, BuildTimeout, cancel);
        if (code == 0 && File.Exists(RunnerPath)) return null;
        return code == -1 ? "building the viewer took too long and was stopped" : $"building the viewer failed (exit {code})";
    }

    public ProcessStartInfo DumpStart(string game, string replay, string output)
    {
        var start = new ProcessStartInfo(Hl) { WorkingDirectory = Bin, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { RunnerPath, replay, output }) start.ArgumentList.Add(a);
        start.Environment["BH_PORT_APP_DIR"] = game;
        start.Environment["BH_PORT_STORAGE_DIR"] = Storage;
        start.Environment["BH_PORT_NO_SOUND"] = "1";
        start.Environment["BH_PORT_LOG"] = Log;
        start.RedirectStandardOutput = true;
        return start;
    }

    public async Task<ReplayFrames> DumpAsync(string game, string replay, string output, CancellationToken cancel = default, Action<string, double>? progress = null)
    {
        var name = Path.GetFileName(replay);
        if (!Prepared) return ReplayFrames.Failed(name, "the viewer isn't prepared yet");
        if (!File.Exists(replay)) return ReplayFrames.Failed(name, "the replay file is gone");
        try
        {
            Directory.CreateDirectory(Storage);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (File.Exists(output)) File.Delete(output);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ReplayFrames.Failed(name, "the match view's folder can't be written: " + e.Message);
        }
        var from = LogLength(Log);
        var clock = Stopwatch.StartNew();
        var menu = false;
        onLine = line =>
        {
            if (menu || !line.Contains("focused ScreenMainMenu", StringComparison.Ordinal)) return;
            menu = true;
            ReplayBoot.Remember("stats", clock.Elapsed);
            progress?.Invoke("Playing the match", 0.93);
        };
        progress?.Invoke("Starting the game", 0);
        using var stuck = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        string? trouble = null;
        var watch = Task.Run(async () =>
        {
            var started = DateTime.UtcNow;
            while (!stuck.IsCancellationRequested)
            {
                try { await Task.Delay(250, stuck.Token); }
                catch (OperationCanceledException) { return; }
                if (!menu) progress?.Invoke("Starting the game", StageBar.Starting(clock.Elapsed, "stats"));
                if (BootTrouble(Log, from, DateTime.UtcNow - started) is { } why)
                {
                    trouble = why;
                    stuck.Cancel();
                    return;
                }
            }
        });
        var code = await run(DumpStart(game, replay, output), DumpTimeout, stuck.Token);
        stuck.Cancel();
        _ = watch;
        onLine = null;
        if (trouble != null) return ReplayFrames.Failed(name, trouble);
        if (File.Exists(output)) return ReplayFrames.Read(output);
        return ReplayFrames.Failed(name, code switch
        {
            -1 => "making the match view took too long and was stopped",
            _ => $"the replay simulator stopped (exit {code}). Its log is in {Log}",
        });
    }

    public static long LogLength(string log)
    {
        try { return File.Exists(log) ? new FileInfo(log).Length : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public static readonly TimeSpan BootLimit = TimeSpan.FromSeconds(75);

    public static string? BootTrouble(string log, long from, TimeSpan running)
    {
        string text;
        try
        {
            if (!File.Exists(log)) return null;
            using var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length <= from) return null;
            f.Seek(from, SeekOrigin.Begin);
            var bytes = new byte[Math.Min(f.Length - from, 4 << 20)];
            var n = f.Read(bytes, 0, bytes.Length);
            text = System.Text.Encoding.UTF8.GetString(bytes, 0, n);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (text.Contains("main menu reached", StringComparison.Ordinal) || text.Contains("replay-sim: playing", StringComparison.Ordinal)
            || text.Contains("replay-view: playing", StringComparison.Ordinal)) return null;
        var errors = 0;
        string? first = null;
        foreach (var line in text.Split('\n'))
        {
            var at = line.IndexOf("uncaught in frame", StringComparison.Ordinal);
            if (at < 0) continue;
            errors++;
            first ??= line[(line.IndexOf(':', at) + 1)..].Trim();
        }
        var loading = text.Contains("game main not started", StringComparison.Ordinal);
        if (errors >= 120 || (loading && errors > 0))
            return "the replay player fails while loading the game files with " + Short(first) + ". That happens when Steam's Brawlhalla files "
                + "are a different version from the replay player. Its log is in " + log;
        if (running > BootLimit && loading) return "the replay player didn't get past loading the game in " + (int)BootLimit.TotalSeconds + " seconds. Its log is in " + log;
        return null;
    }

    static string Short(string? error)
    {
        if (string.IsNullOrEmpty(error)) return "an error on every frame";
        var cut = error.IndexOf(" [in ", StringComparison.Ordinal);
        return cut > 0 ? error[..cut] : error.Length > 120 ? error[..120] : error;
    }

    static async Task<int> RunProcess(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancel, Action<string>? line = null)
    {
        using var p = Process.Start(start);
        if (p == null) return -2;
        if (start.RedirectStandardOutput)
        {
            p.OutputDataReceived += (_, e) => { if (e.Data != null) line?.Invoke(e.Data); };
            p.BeginOutputReadLine();
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(limit.Token);
            return p.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            return -1;
        }
    }
}
