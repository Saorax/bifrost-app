using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BifrostStudio;

public sealed record SettingsView(
    string? Game, string? Installed, string? Latest, string? OfficialInstalled, string? LatestOfficial, string? LatestApp,
    bool AppUpdateShown, bool EverythingArmed,
    Action Locate, Action UseOriginal, Action CopyFromSteam, Action Remove, Action RemoveOfficial, Action AppNotes,
    Action RemoveEverything);

public sealed class SettingsPage : UserControl
{
    public SettingsPage(SettingsView v)
    {
        var bifrostSub = v.Latest == null ? "" : v.Installed == v.Latest ? "Up to date" : $"Latest {v.Latest}";
        var remove = Look.Secondary("Remove", Look.Trash, v.Remove);
        remove.IsVisible = v.Installed != null;
        var officialSub = v.LatestOfficial == null ? "" : v.OfficialInstalled == v.LatestOfficial ? "Up to date" : $"Latest {v.LatestOfficial}";
        var removeOfficial = Look.Secondary("Remove", Look.Trash, v.RemoveOfficial);
        removeOfficial.IsVisible = v.OfficialInstalled != null;
        var appSub = v.LatestApp == null ? "" : v.AppUpdateShown ? $"Update to {v.LatestApp}" : "Up to date";
        var everything = Look.Secondary(v.EverythingArmed ? "Click again to remove everything" : "Remove everything", Look.Trash, v.RemoveEverything);
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 16, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    new TextBlock { Text = "Settings", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text) },
                    Look.Card("Brawlhalla", Look.Folder, new TextBlock { Text = v.Game ?? "Not found" }, new TextBlock(), MainWindow.Row(
                        Look.Secondary("Change", Look.Folder, v.Locate), Look.Secondary("Open", Look.Folder, () => Ui.Open(v.Game, missing: "Brawlhalla wasn't found. Use Change to pick its folder.")),
                        Look.Secondary("Use original game", Look.Folder, v.UseOriginal),
                        Look.Secondary(GameFinder.IsSteamInstall(v.Game) ? "Separate copy" : "Update from Steam", Look.Download, v.CopyFromSteam))),
                    Look.Card("Bifrost", Look.Tag, new TextBlock { Text = MainWindow.InstalledText(v.Installed) }, new TextBlock { Text = bifrostSub },
                        MainWindow.Row(remove)),
                    EngineKit.OfficialPaused
                        ? Look.Card("Official servers", Look.Tag, new TextBlock { Text = "Paused" },
                            new TextBlock { Text = "Official servers are paused for now. Bifrost works as usual." })
                        : Look.Card("Official servers", Look.Tag, new TextBlock { Text = MainWindow.InstalledText(v.OfficialInstalled) },
                            new TextBlock { Text = officialSub }, MainWindow.Row(removeOfficial)),
                    Look.Card("App", Look.Tag, new TextBlock { Text = Releases.AppVersion ?? "Development build" }, new TextBlock { Text = appSub },
                        MainWindow.Row(Look.Secondary("What's new", Look.Logs, v.AppNotes))),
                    Look.Card("App data", Look.Folder, new TextBlock { Text = EngineKit.Home }, new TextBlock(),
                        MainWindow.Row(Look.Secondary("Open", Look.Folder, () => Ui.Open(EngineKit.Home, create: true)))),
                    Look.Card("Remove everything", Look.Trash, new TextBlock { Text = "Bifrost, its saves and its files" },
                        new TextBlock { Text = "The game goes back to Steam's own. Your server's data is deleted too." }, MainWindow.Row(everything)),
                },
            },
        };
    }
}