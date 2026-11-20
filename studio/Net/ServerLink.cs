namespace BifrostStudio.Net;

public enum LinkState { Off, Connecting, Online, Waiting, Stopped }

public sealed class ServerLink(string host, int port, string app, string installId, Func<string?> token, Func<BifrostClient>? make = null) : IAsyncDisposable
{
    public static TimeSpan[] Backoff { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    readonly Func<BifrostClient> make = make ?? (() => new BifrostClient());
    readonly CancellationTokenSource stop = new();
    readonly object gate = new();
    CancellationTokenSource? wake;
    Task? loop;

    public string Host { get; } = host;
    public int Port { get; } = port;
    public LinkState State { get; private set; } = LinkState.Off;
    public string Message { get; private set; } = "";
    public bool Companion { get; private set; }
    public int Attempts { get; private set; }
    public BifrostClient? Client { get; private set; }

    public event Action? Changed;
    public event Action<BifrostClient>? Online;
    public event Action<SignInResult>? SignedIn;

    public void Start()
    {
        lock (gate) loop ??= Task.Run(Run);
    }

    public void ReconnectNow()
    {
        lock (gate) wake?.Cancel();
    }

    void Set(LinkState state, string message)
    {
        State = state;
        Message = message;
        Changed?.Invoke();
    }

    async Task Run()
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            Attempts++;
            Set(LinkState.Connecting, "");
            var c = make();
            var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            c.Closed += why => closed.TrySetResult(why);
            c.SignedIn += s => SignedIn?.Invoke(s);
            var r = await c.ConnectAsync(Host, Port, app, installId, token(), stop.Token);
            if (r.Ok)
            {
                failures = 0;
                Companion = r.Companion;
                Client = c;
                Set(LinkState.Online, r.Message);
                Online?.Invoke(c);
                var why = await Task.WhenAny(closed.Task, Task.Delay(Timeout.Infinite, stop.Token).ContinueWith(_ => "", TaskScheduler.Default));
                var reason = why == closed.Task ? closed.Task.Result : "";
                Client = null;
                await c.DisposeAsync();
                if (stop.IsCancellationRequested) break;
                if (c.WasKicked)
                {
                    Set(LinkState.Stopped, reason);
                    return;
                }
                Set(LinkState.Waiting, reason);
            }
            else
            {
                await c.DisposeAsync();
                if (stop.IsCancellationRequested) break;
                Set(LinkState.Waiting, r.Message);
                failures++;
            }
            await Pause(Backoff[Math.Clamp(failures - 1, 0, Backoff.Length - 1)]);
        }
        Set(LinkState.Off, "");
    }

    async Task Pause(TimeSpan delay)
    {
        CancellationTokenSource w;
        lock (gate)
        {
            wake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            w = wake;
        }
        try
        {
            await Task.Delay(delay, w.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (gate) wake = null;
            w.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        Task? running;
        lock (gate) running = loop;
        if (running != null) await running;
        if (Client is { } c) await c.DisposeAsync();
    }
}
