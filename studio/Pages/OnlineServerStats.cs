using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BifrostStudio.Net;

namespace BifrostStudio;

public sealed partial class OnlinePage
{
    static readonly int[] OnlineRanges = [1, 7, 30];
    static readonly string[] OnlineRangeNames = ["Last day", "Last week", "Last month"];
    int onlineDays = 1;

    static double N(JsonNode? n) => OnlineService.Num(n) ?? 0;

    static Control Charted(LineChart chart)
    {
        var key = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var s in chart.Series)
            key.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(s.Color), VerticalAlignment = VerticalAlignment.Center }, Cell(s.Name, 12, dim: true) },
            });
        return Panel(new StackPanel { Spacing = 6, Children = { chart, key } });
    }

    async Task ServerStats(int mine, ContentControl into)
    {
        var s = await data.Stats("server:stats");
        if (mine != version) return;
        if (s == null || Error(s) != null)
        {
            var why = Error(s);
            into.Content = Note(why == "unknown query" ? "This server doesn't keep server-wide stats yet." : why ?? "The server didn't answer.");
            return;
        }
        var online = await data.Stats($"online:{onlineDays}");
        var movers = await data.Stats("movers:1v1");
        if (mine != version) return;
        var stack = new StackPanel { Spacing = 10 };
        var cards = new WrapPanel { Orientation = Orientation.Horizontal };
        void Card(string title, string icon, string big, string small) =>
            cards.Children.Add(new Border { Margin = new Thickness(0, 0, 10, 10), Width = 200, Child = Look.Card(title, icon, new TextBlock { Text = big }, new TextBlock { Text = small }) });
        Card("Matches", Look.Play, $"{N(s["matches"]):0}", $"{N(s["rated"]):0} rated this season");
        Card("Players", Look.Home, $"{N(s["players"]):0}", "played this season");
        Card("Match length", Look.Pulse, Clock(N(s["average_duration"]) / 1000), "on average");
        if (s["peak_online"] is JsonObject peak)
            Card("Peak online", Look.Monitor, $"{N(peak["online"]):0}", OnlineService.When(peak["time"]) is { } at ? at.ToLocalTime().ToString("d MMM yyyy HH:mm") : "");
        stack.Children.Add(cards);

        var range = Combo(OnlineRangeNames, Math.Max(0, Array.IndexOf(OnlineRanges, onlineDays)), i => { if (OnlineRanges[i] != onlineDays) { onlineDays = OnlineRanges[i]; Show("server"); } });
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Label("Players online"), range } });
        var samples = (online?["points"] as JsonArray ?? []).OfType<JsonArray>().Where(p => p.Count >= 3).ToList();
        if (samples.Count > 0)
        {
            var labels = samples.Select(p => OnlineService.When(p[0]) is { } t ? t.ToLocalTime().ToString(onlineDays <= 1 ? "HH:mm" : "d MMM HH:mm") : "").ToList();
            stack.Children.Add(Charted(new LineChart(labels,
            [
                new ChartSeries("Online", Look.Accent, samples.Select(p => N(p[1])).ToList()),
                new ChartSeries("In a match or queue", Look.Good, samples.Select(p => N(p[2])).ToList()),
            ])));
        }
        else stack.Children.Add(Panel(Note(Error(online) ?? "No samples yet. The server checks who's online every 5 minutes.")));

        if (s["days"] is JsonArray { Count: > 0 } days)
        {
            var rows = days.OfType<JsonObject>().ToList();
            stack.Children.Add(Label("Matches per day"));
            stack.Children.Add(Charted(new LineChart(
                rows.Select(d => DateTime.TryParse(OnlineService.Text(d["day"]), out var day) ? day.ToString("d MMM") : OnlineService.Text(d["day"]) ?? "").ToList(),
                [new ChartSeries("Matches", DamageChart.Series[0], rows.Select(d => N(d["matches"])).ToList()),
                 new ChartSeries("Players", DamageChart.Series[3], rows.Select(d => N(d["players"])).ToList())], bars: false, height: 140)));
        }

        var up = (movers?["up"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var down = (movers?["down"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (up.Count + down.Count > 0)
        {
            stack.Children.Add(Label("1v1 movers this week"));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
            var left = Panel(MoverList(up, true));
            var right = Panel(MoverList(down, false));
            Grid.SetColumn(right, 1);
            grid.Children.Add(left);
            grid.Children.Add(right);
            stack.Children.Add(grid);
        }

        if (s["legends"] is JsonArray { Count: > 0 } legends)
        {
            const string cols = "*,80,80,90,150";
            var t = new StackPanel { Spacing = 2 };
            t.Children.Add(Head(cols, "LEGEND", "PICKS", "PLAYERS", "WIN RATE", "RANKED 1V1"));
            foreach (var l in legends.OfType<JsonObject>().Take(25))
            {
                var ranked = N(l["ranked"]);
                t.Children.Add(new Border
                {
                    Padding = new Thickness(8, 4),
                    Child = Row(cols, Cell(OnlineService.Text(l["name"]) ?? "?"), Cell($"{N(l["picks"]):0}", dim: true), Cell($"{N(l["players"]):0}", dim: true),
                        Cell(N(l["win_rate"]).ToString("P0"), dim: true), Cell(ranked > 0 ? $"{ranked:0} games · {N(l["ranked_win_rate"]):P0}" : "", dim: true)),
                });
            }
            stack.Children.Add(Label("Legends this season"));
            stack.Children.Add(Panel(t));
        }
        var side = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        var maps = Table("MAP", s["maps"] as JsonArray, m => OnlineService.Text(m["map"]) ?? "?", 12);
        var modes = Table("MODE", s["modes"] as JsonArray, m => (OnlineService.Text(m["mode"]) ?? "?") + (OnlineService.Text(m["kind"]) is { } k ? " · " + k : ""), 12);
        if (maps != null) side.Children.Add(Panel(maps));
        if (modes != null)
        {
            var p = Panel(modes);
            Grid.SetColumn(p, maps != null ? 1 : 0);
            side.Children.Add(p);
        }
        if (side.Children.Count > 0) stack.Children.Add(side);
        into.Content = stack;
    }

    static StackPanel? Table(string what, JsonArray? rows, Func<JsonObject, string> name, int max)
    {
        if (rows is not { Count: > 0 }) return null;
        const string cols = "*,80,70";
        var t = new StackPanel { Spacing = 2 };
        t.Children.Add(Head(cols, what, "MATCHES", "LENGTH"));
        foreach (var r in rows.OfType<JsonObject>().Take(max))
            t.Children.Add(new Border { Padding = new Thickness(8, 4), Child = Row(cols, Cell(name(r)), Cell($"{N(r["matches"]):0}", dim: true), Cell(Clock(N(r["average_duration"]) / 1000), dim: true)) });
        return t;
    }

    StackPanel MoverList(List<JsonObject> rows, bool up)
    {
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Cell(up ? "Climbed" : "Dropped", 12, dim: true));
        foreach (var r in rows)
        {
            var was = (int)N(r["was"]);
            var now = (int)N(r["rating"]?["rank"]);
            var change = Cell(up ? $"▲{was - now}" : $"▼{now - was}", 12.5);
            change.Foreground = up ? Look.Brush(Look.Good) : Look.Brush(Loss);
            var row = Row("*,110,50", Cell(OnlineService.Text(r["name"]) ?? "?"), Cell($"#{was} → #{now}", dim: true), change);
            list.Children.Add(OnlineService.Num(r["id"]) is { } id ? Clickable(row, () => ShowProfile("profile:a" + (long)id)) : new Border { Padding = new Thickness(8, 4), Child = row });
        }
        if (rows.Count == 0) list.Children.Add(Cell("Nobody", dim: true));
        return list;
    }
}
