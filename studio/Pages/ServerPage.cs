using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace BifrostStudio;

public sealed class ServerPage : UserControl
{
    readonly EngineKit engine;
    readonly Func<string?> game;
    readonly Func<Release?> release;
    readonly TextBlock hostValue = new(), hostSub = new();
    readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
    readonly ProgressBar download = new() { Height = 5, Minimum = 0, Maximum = 1, IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
    readonly LogView log = new() { Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock logHead = new() { Text = "SERVER LOG", FontSize = 11, LetterSpacing = 1.5, FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Dim), Margin = new Thickness(0, 6, 0, 0) };
    readonly FileTail tail = new();
    readonly Grid addresses = new() { ColumnDefinitions = new ColumnDefinitions("130,Auto,Auto"), RowSpacing = 4, Margin = new Thickness(0, 4, 0, 0) };
    readonly ContentControl settings = new();
    DateTime addressesAt = DateTime.MinValue;
    bool busy;

    public ServerPage(EngineKit engine, Func<string?> game, Func<Release?> release)
    {
        this.engine = engine;
        this.game = game;
        this.release = release;
        var card = Look.Card("Your server", Look.Server, hostValue, hostSub, new StackPanel { Children = { addresses, download, buttons } });
        var top = new StackPanel
        {
            Spacing = 16, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left,
            Children = { new TextBlock { Text = "Server", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text) }, card, settings, logHead },
        };
        DockPanel.SetDock(top, Dock.Top);
        Content = new DockPanel { Children = { top, log } };
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => { Refresh(); ShowLog(); });
        AttachedToVisualTree += (_, _) => { timer.Start(); Refresh(); ShowLog(); };
        DetachedFromVisualTree += (_, _) => timer.Stop();
    }

    void Refresh()
    {
        if (busy) return;
        buttons.Children.Clear();
        log.IsVisible = logHead.IsVisible = HostedServer.Downloaded;
        if (!HostedServer.Downloaded) settings.Content = null;
        else if (settings.Content == null) settings.Content = SettingsCard();
        if (!HostedServer.Downloaded)
        {
            hostValue.Text = "Not installed";
            hostSub.Text = engine.Python == null ? "Install Bifrost first: the server runs on its tools."
                : release()?.ServerPackage == null ? "Not in this release." : "";
            var get = Look.Secondary("Download server", Look.Download, () => _ = Download());
            get.IsEnabled = release()?.ServerPackage != null && engine.Python != null;
            buttons.Children.Add(get);
            return;
        }
        var running = HostedServer.Running;
        var taken = !running && HostedServer.PortTaken;
        hostValue.Text = running ? $"Running on port {HostedServer.Port}" : taken ? $"Port {HostedServer.Port} is in use" : "Stopped";
        hostSub.Text = taken ? "Another program uses this port." : "";
        addresses.IsVisible = running;
        if (running && DateTime.Now - addressesAt > TimeSpan.FromMinutes(1)) _ = ShowAddresses();
        if (!running) addressesAt = DateTime.MinValue;
        var newer = HostedServer.UpdateAvailable(release()?.ServerPackage);
        if (newer) hostSub.Text = taken ? "Another program uses this port. A server update is ready." : "A server update is ready.";
        if (running && newer) buttons.Children.Add(Look.Secondary("Update", Look.Refresh, () => _ = Download()));
        if (running) buttons.Children.Add(Look.Secondary("Stop", Look.Stop, () => _ = Act(HostedServer.StopAsync)));
        else
        {
            var start = Look.Secondary("Start", Look.Play, () => _ = Act(() =>
            {
                HostedServer.Start(engine.Python!);
                if (game() is { } g) EngineKit.AddOwnServer(g);
                return Task.Delay(2500);
            }));
            start.IsEnabled = engine.Python != null && !taken;
            buttons.Children.Add(start);
            if (release()?.ServerPackage != null) buttons.Children.Add(Look.Secondary("Update", Look.Refresh, () => _ = Download()));
        }
        buttons.Children.Add(Look.Secondary("Open folder", Look.Folder, () => Ui.Open(HostedServer.Folder)));
    }

    Control SettingsCard()
    {
        if (!HostedServer.Downloaded) return new Panel { IsVisible = false };
        var name = Field(HostedServer.Setting("server_name") ?? "", "Bifrost server");
        var port = Field(HostedServer.Port.ToString(), "7910");
        var id = Field(HostedServer.Setting("discord_client_id") ?? "", "Discord application ID");
        var secret = Field(HostedServer.Setting("discord_client_secret") ?? "", "Discord client secret");
        secret.PasswordChar = '•';
        var url = Field(HostedServer.Setting("public_url") ?? "", "http://your-address:7911");
        var state = new TextBlock { FontSize = 12, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center };
        var save = Look.Secondary("Save", Look.Check, () => _ = Save());
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (label, box) in new (string, TextBox)[] { ("Name", name), ("Port", port), ("Discord app ID", id), ("Discord secret", secret), ("Sign-in address", url) })
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var text = new TextBlock { Text = label, FontSize = 13, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(text);
            grid.Children.Add(box);
        }
        return Look.Card("Settings", Look.Tune, new TextBlock { Text = HostedServer.Setting("server_name") ?? "Bifrost server" },
            new TextBlock(), new StackPanel { Children = { grid, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
                Margin = new Thickness(0, 8, 0, 0), Children = { save, state } } } });

        async Task Save()
        {
            if (!int.TryParse(port.Text, out var p) || p is < 1024 or > 65534) { state.Text = "Port: 1024-65534"; return; }
            HostedServer.SetSettings(name.Text?.Trim(), p, id.Text?.Trim(), secret.Text?.Trim(), url.Text?.Trim());
            if (game() is { } g) EngineKit.AddOwnServer(g);
            if (HostedServer.Running && engine.Python != null)
            {
                state.Text = "Restarting the server...";
                await HostedServer.StopAsync();
                HostedServer.Start(engine.Python);
                await Task.Delay(2500);
            }
            state.Text = "Saved";
        }
    }

    static TextBox Field(string text, string hint) => new()
    {
        Text = text, PlaceholderText = hint, FontSize = 13, MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left,
        Background = Look.Brush(Look.Raised), Foreground = Look.Brush(Look.Text), BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7),
    };

    async Task ShowAddresses()
    {
        addressesAt = DateTime.Now;
        var list = await HostedServer.AddressesAsync();
        addresses.Children.Clear();
        addresses.RowDefinitions.Clear();
        foreach (var (name, address) in list)
        {
            var row = addresses.RowDefinitions.Count;
            addresses.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var copy = Look.Link("Copy", Look.Copy, () => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(address));
            foreach (var (c, column) in new (Control, int)[]
                     {
                         (new TextBlock { Text = name, FontSize = 13, Foreground = Look.Brush(Look.Dim), VerticalAlignment = VerticalAlignment.Center }, 0),
                         (new SelectableTextBlock { Text = address, FontSize = 14, FontFamily = Ui.Mono, Foreground = Look.Brush(Look.Text), VerticalAlignment = VerticalAlignment.Center }, 1),
                         (copy, 2),
                     })
            {
                c.Margin = new Thickness(column == 2 ? 14 : 0, 0, 0, 0);
                Grid.SetRow(c, row);
                Grid.SetColumn(c, column);
                addresses.Children.Add(c);
            }
        }
    }

    async Task Act(Func<Task> work)
    {
        busy = true;
        buttons.IsEnabled = false;
        try { await work(); }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { hostSub.Text = e.Message; }
        busy = false;
        buttons.IsEnabled = true;
        Refresh();
    }

    async Task Download()
    {
        if (release()?.ServerPackage is not { } package) return;
        download.IsVisible = true;
        await Act(async () =>
        {
            await HostedServer.UpdateAsync(package, engine.Python, new Progress<double>(p => download.Value = p));
            if (game() is { } g) EngineKit.AddOwnServer(g);
        });
        download.IsVisible = false;
    }

    void ShowLog()
    {
        if (HostedServer.Downloaded && tail.Read(HostedServer.Log) is { } text) log.SetText(text);
    }
}
