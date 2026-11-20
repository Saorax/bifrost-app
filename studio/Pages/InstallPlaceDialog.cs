using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BifrostStudio;

public static class InstallPlaceDialog
{
    public static Window Make(Action<string> picked)
    {
        var dialog = new Window
        {
            Title = "Where to install Bifrost", Width = 680, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Look.Brush(Look.Panel),
        };
        Look.Apply(dialog.Styles);
        Button Choice(string place, string head, string icon)
        {
            var button = new Button
            {
                Classes = { "secondary" }, Background = Look.Brush(Look.Raised), Foreground = Look.Brush(Look.Text),
                CornerRadius = new CornerRadius(16), Padding = new Thickness(22, 26), Height = 210,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        Look.Icon(icon, 42, Look.Accent),
                        new TextBlock { Text = head, FontSize = 18, FontWeight = FontWeight.Bold, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock
                        {
                            Text = InstallPlace.Describe(place), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                            Foreground = Look.Brush(Look.Dim), MaxWidth = 240,
                        },
                    },
                },
            };
            button.Click += (_, _) =>
            {
                picked(place);
                dialog.Close();
            };
            return button;
        }
        var own = Choice(InstallPlace.Own, "Install in its own folder", Look.Folder);
        var steam = Choice(InstallPlace.Steam, "Install on Steam", Look.Play);
        var choices = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*"), Children = { own, steam } };
        Grid.SetColumn(steam, 2);
        var cancel = Look.Link("Cancel", Look.Close, dialog.Close);
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(28, 26, 18, 14), Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = "Where should Bifrost go?", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = Look.Brush(Look.Text),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new Border { Margin = new Thickness(0, 0, 10, 0), Child = choices },
                new TextBlock
                {
                    Text = "Either way, Bifrost's engine, saves and settings stay out of Steam's folder. You can switch later under Advanced in the gear menu.",
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12, Foreground = Look.Brush(Look.Dim),
                    Margin = new Thickness(30, 0, 40, 0),
                },
                cancel,
            },
        };
        return dialog;
    }
}
