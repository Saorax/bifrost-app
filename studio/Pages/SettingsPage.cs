using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BifrostStudio;

public sealed record SettingsView(
    string? Game, string? Installed, string? Latest, string? OfficialInstalled, string? LatestOfficial, string? LatestApp,
    bool AppUpdateShown, bool EverythingArmed,
    Action Locate, Action UseOriginal, Action CopyFromSteam, Action Remove, Action RemoveOfficial, Action AppNotes,
    Action RemoveEverything, bool Separate = false, bool SeparateArmed = false, Action? DeleteSeparate = null);

public sealed class SettingsPage : UserControl
{
    public SettingsPage(SettingsView v)
    {
        var bifrostSub = v.Latest == null ? "" : v.Installed == v.Latest ? "Up to date" : $"Latest {v.Latest}";
        var remove = Look.Secondary("Remove", Look.Trash, v.Remove);
        remove.IsVisible = v.Installed != null && !v.Separate;
        var officialSub = v.LatestOfficial == null ? "" : v.OfficialInstalled == v.LatestOfficial ? "Up to date" : $"Latest {v.LatestOfficial}";
        var removeOfficial = Look.Secondary("Remove", Look.Trash, v.RemoveOfficial);
        removeOfficial.IsVisible = v.OfficialInstalled != null && (!v.Separate || v.Installed != null);
        var appSub = v.LatestApp == null ? "" : v.AppUpdateShown ? $"Update to {v.LatestApp}" : "Up to date";
        var gameButtons = MainWindow.Row(
            Look.Secondary("Change", Look.Folder, v.Locate), Look.Secondary("Open", Look.Folder, () => Ui.Open(v.Game, missing: "Brawlhalla wasn't found. Use Change to pick its folder.")));
        if (!v.Separate) gameButtons.Children.Add(Look.Secondary("Use original game", Look.Folder, v.UseOriginal));
        gameButtons.Children.Add(Look.Secondary(GameFinder.IsSteamInstall(v.Game) ? "Separate copy" : "Update from Steam", Look.Download, v.CopyFromSteam));
        if (v.Separate && v.DeleteSeparate != null)
            gameButtons.Children.Add(Look.Secondary(v.SeparateArmed ? "Click again to delete" : "Delete separate install", Look.Trash, v.DeleteSeparate));
        var everything = Look.Secondary(v.EverythingArmed ? "Click again to remove everything" : "Remove everything", Look.Trash, v.RemoveEverything);
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 16, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    new TextBlock { Text = "Settings", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text) },
                    Look.Card("Brawlhalla", Look.Folder, new TextBlock { Text = v.Game ?? "Not found" },
                        new TextBlock { Text = v.Separate ? "Separate install. Steam's folder isn't touched." : "" }, gameButtons),
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
                        new TextBlock { Text = v.Separate ? "The separate install is deleted. Your server's data is deleted too." : "The game goes back to Steam's own. Your server's data is deleted too." }, MainWindow.Row(everything)),
                },
            },
        };
    }
}