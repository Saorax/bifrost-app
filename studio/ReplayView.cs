using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BifrostStudio;

public sealed record ReplayViewFrame(byte[] Pixels, int Width, int Height, bool Bgra, int NowMs, int LengthMs, int Speed, bool Boxes, bool Ended, int StageWidth, int StageHeight);

public sealed class ReplayView : IDisposable
{
    public const string Magic = "BRV1";
    public const int KindFrame = 1;
    public const int KindNote = 2;
    public const int FrameHeader = 36;
    public static readonly string[] SpeedNames = ["-5x", "-2x", "-1x", "-0.5x", "-0.25x", "Paused", "0.25x", "0.5x", "1x", "2x", "5x"];
    public const int Paused = 5;
    public const int Normal = 8;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    const uint LockForeground = 1;
    const uint UnlockForeground = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool LockSetForegroundWindow(uint code);

    readonly CancellationTokenSource stop = new();
    public static readonly TimeSpan HarnessConnectTimeout = TimeSpan.FromSeconds(60);
    HarnessCopy? harness;
    string? jobId;
    TcpListener? listener;
    NetworkStream? socket;
    Process? process;
    NamedPipeServerStream? pipe;
    readonly object gate = new();

    public event Action<JsonElement>? Note;
    public event Action<ReplayViewFrame>? Frame;
    public event Action<string>? Stopped;

    public string State { get; private set; } = "starting";
    public string? Error { get; private set; }
    public int Frames { get; private set; }
    public int ProcessId => process?.Id ?? 0;

    public static ProcessStartInfo StartInfo(ReplayRunner runner, string game, string replay, string pipeName, int width, int height, bool sound)
    {
        var start = new ProcessStartInfo(runner.Hl)
        {
            WorkingDirectory = runner.Bin, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { runner.RunnerPath, "--view", replay, @"\\.\pipe\" + pipeName, width.ToString(), height.ToString() }) start.ArgumentList.Add(a);
        start.Environment["BH_PORT_APP_DIR"] = game;
        start.Environment["BH_PORT_STORAGE_DIR"] = Storage(runner);
        start.Environment["BH_PORT_LOG"] = Path.Combine(Storage(runner), "replay-view-boot.txt");
        if (!sound) start.Environment["BH_PORT_NO_SOUND"] = "1";
        else start.Environment.Remove("BH_PORT_NO_SOUND");
        return start;
    }

    public static string Storage(ReplayRunner runner) => runner.Storage + "-view";

    public void Start(ReplayRunner runner, string game, string replay, int width, int height, bool sound)
    {
        var name = "bifrost-replay-view-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8];
        pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 22, 0);
        Directory.CreateDirectory(Storage(runner));
        var log = Path.Combine(Storage(runner), "replay-view-boot.txt");
        var logFrom = ReplayRunner.LogLength(log);
        var locked = OperatingSystem.IsWindows() && LockSetForegroundWindow(LockForeground);
        try
        {
            process = Process.Start(StartInfo(runner, game, replay, name, width, height, sound)) ?? throw new IOException("the replay player didn't start");
        }
        catch
        {
            if (locked) LockSetForegroundWindow(UnlockForeground);
            throw;
        }
        if (locked)
        {
            void Unlock() => LockSetForegroundWindow(UnlockForeground);
            var once = 0;
            Note += _ => { if (Interlocked.Exchange(ref once, 1) == 0) Unlock(); };
            _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => { if (Interlocked.Exchange(ref once, 1) == 0) Unlock(); });
        }
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = Task.Run(() => WatchBoot(log, logFrom));
        var from = pipe;
        _ = Task.Run(() => Read(async c =>
        {
            await from.WaitForConnectionAsync(c);
            return from;
        }, ConnectTimeout, stop.Token));
    }

    public string? Game { get; private set; }

    public void StartHarness(HarnessCopy copy, string replay, int width, int height)
    {
        if (ReplayHarness.Running()) throw new IOException("Brawlhalla is already running, and only one can run at a time. Close it first");
        Game = copy.Game;
        harness = copy;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Directory.CreateDirectory(ReplayHarness.Logs);
        var viewLog = Path.Combine(ReplayHarness.Logs, "view.log");
        try { File.Delete(viewLog); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        jobId = ReplayHarness.WriteJob(copy, new() { ["mode"] = "view", ["replay"] = replay, ["log"] = viewLog, ["port"] = port, ["width"] = width, ["height"] = height });
        process = ReplayHarness.Start(copy);
        var server = listener;
        _ = Task.Run(() => Read(async c =>
        {
            var client = await server.AcceptTcpClientAsync(c);
            client.NoDelay = true;
            socket = client.GetStream();
            return socket;
        }, HarnessConnectTimeout, stop.Token));
    }

    async Task Read(Func<CancellationToken, Task<Stream>> connect, TimeSpan connectTimeout, CancellationToken cancel)
    {
        FineTimer(true);
        try
        {
            Stream from;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                wait.CancelAfter(connectTimeout);
                from = await connect(wait.Token);
            }
            var head = new byte[12];
            var frameHead = new byte[FrameHeader];
            while (!cancel.IsCancellationRequested)
            {
                await from.ReadExactlyAsync(head, cancel);
                if (Encoding.ASCII.GetString(head, 0, 4) != Magic) throw new InvalidDataException("the replay player sent something unexpected");
                var kind = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
                if (kind == KindNote)
                {
                    var n = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
                    if (n < 0 || n > 1 << 20) throw new InvalidDataException("a note from the replay player is too long");
                    var text = new byte[n];
                    await from.ReadExactlyAsync(text, cancel);
                    var note = JsonDocument.Parse(text).RootElement.Clone();
                    if (note.TryGetProperty("state", out var s) && s.GetString() is { } state) State = state;
                    if (note.TryGetProperty("error", out var err)) Error = err.ToString();
                    Note?.Invoke(note);
                    continue;
                }
                if (kind != KindFrame) throw new InvalidDataException("the replay player sent an unknown packet");
                head.AsSpan(8, 4).CopyTo(frameHead);
                await from.ReadExactlyAsync(frameHead.AsMemory(4), cancel);
                int I(int i) => BinaryPrimitives.ReadInt32LittleEndian(frameHead.AsSpan(i * 4));
                var w = I(0);
                var h = I(1);
                if (w <= 0 || h <= 0 || w > 4096 || h > 4096) throw new InvalidDataException("a frame from the replay player has a bad size");
                var pixels = Rent(w * h * 4);
                await from.ReadExactlyAsync(pixels.AsMemory(0, w * h * 4), cancel);
                Frames++;
                var now = paced.Elapsed.TotalMilliseconds;
                if (ackedAt > 0) latency = latency * 0.9 + Math.Clamp(now - ackedAt, 0, 50) * 0.1;
                var wait = FrameMs - latency;
                if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait), cancel);
                ackedAt = paced.Elapsed.TotalMilliseconds;
                Send("ack");
                var flags = I(6);
                var format = I(2);
                if (format == FormatArgb) ArgbToBgra(pixels, w * h * 4);
                Frame?.Invoke(new ReplayViewFrame(pixels, w, h, format != 0, I(3), I(4), I(5), (flags & 1) != 0, (flags & 2) != 0, I(7), I(8)));
            }
        }
        catch (OperationCanceledException)
        {
            if (!cancel.IsCancellationRequested) Finish("the replay player didn't start in time");
            return;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or JsonException or ObjectDisposedException)
        {
            if (cancel.IsCancellationRequested) return;
            Finish(Error ?? (process is { HasExited: true } p ? $"the replay player stopped (exit {p.ExitCode})" : "the replay player stopped: " + e.Message));
            return;
        }
        Finish(Error ?? "the replay player stopped");
    }

    public const int FormatArgb = 2;

    public static void ArgbToBgra(byte[] pixels) => ArgbToBgra(pixels, pixels.Length);

    public static void ArgbToBgra(byte[] pixels, int length)
    {
        for (var i = 0; i + 3 < length; i += 4)
        {
            var a = pixels[i];
            var r = pixels[i + 1];
            pixels[i] = pixels[i + 3];
            pixels[i + 1] = pixels[i + 2];
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }
    }

    async Task WatchBoot(string log, long from)
    {
        var started = DateTime.UtcNow;
        while (!stop.IsCancellationRequested && State == "starting" && Frames == 0)
        {
            try { await Task.Delay(1000, stop.Token); }
            catch (OperationCanceledException) { return; }
            if (State != "starting" || Frames > 0) return;
            if (ReplayRunner.BootTrouble(log, from, DateTime.UtcNow - started) is not { } why) continue;
            Error = why;
            try { process?.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            Finish(why);
            return;
        }
    }

    int finished;

    void Finish(string why)
    {
        if (Interlocked.Exchange(ref finished, 1) == 1) return;
        State = "stopped";
        Error ??= why;
        Stopped?.Invoke(why);
    }

    public void Send(string command)
    {
        lock (gate)
        {
            try
            {
                if (harness != null)
                {
                    if (socket is { } s)
                    {
                        var bytes = Encoding.UTF8.GetBytes(command + "\n");
                        s.Write(bytes);
                        s.Flush();
                    }
                }
                else if (process is { HasExited: false } p)
                {
                    p.StandardInput.WriteLine(command);
                    p.StandardInput.Flush();
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
        }
    }

    public void Ack() { }

    public const double FrameMs = 1000.0 / 60;
    readonly Stopwatch paced = Stopwatch.StartNew();
    double ackedAt, latency = 8;

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint ms);

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint ms);

    int fineTimer;

    void FineTimer(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (on && Interlocked.Exchange(ref fineTimer, 1) == 0) timeBeginPeriod(1);
        else if (!on && Interlocked.Exchange(ref fineTimer, 0) == 1) timeEndPeriod(1);
    }

    readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> spare = new();

    byte[] Rent(int size)
    {
        while (spare.TryDequeue(out var b))
            if (b.Length == size) return b;
        return new byte[size];
    }

    public void Recycle(byte[] pixels)
    {
        if (spare.Count < 4) spare.Enqueue(pixels);
    }
    public void Seek(int ms) => Send("seek " + Math.Max(0, ms));
    public void SetSpeed(int step) => Send("speed " + Math.Clamp(step, 0, SpeedNames.Length - 1));
    public void SetBoxes(bool on) => Send("boxes " + (on ? "on" : "off"));

    public void SetOption(string name, bool on) => Send($"option {name} {(on ? "on" : "off")}");
    public void Restart() => Send("restart");
    public void Size(int w, int h) => Send($"size {w} {h}");
    public void Mouse(string kind, int button, double x, double y) => Send(FormattableString.Invariant($"mouse {kind} {button} {x:0.#} {y:0.#}"));
    public void Wheel(double delta, double x, double y) => Send(FormattableString.Invariant($"wheel {delta:0.##} {x:0.#} {y:0.#}"));
    public void Key(bool down, int code) => Send($"key {(down ? "down" : "up")} {code}");

    public void Dispose()
    {
        FineTimer(false);
        Send("quit");
        stop.Cancel();
        var p = process;
        process = null;
        var copy = harness;
        var id = jobId;
        if (p != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(copy != null ? 10 : 3));
                    await p.WaitForExitAsync(limit.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch (InvalidOperationException) { }
                }
                p.Dispose();
                if (copy != null && id != null) ReplayHarness.ClearJob(copy, id);
            });
        }
        try { pipe?.Dispose(); } catch (IOException) { }
        pipe = null;
        try { socket?.Dispose(); } catch (IOException) { }
        listener?.Stop();
    }
}
