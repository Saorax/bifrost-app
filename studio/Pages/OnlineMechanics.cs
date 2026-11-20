using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BifrostStudio.Net;

namespace BifrostStudio;

public sealed partial class OnlinePage
{
    static readonly (string Label, string Key)[] LegendColumns =
        [("LEGEND", "legend"), ("PLAYED", "matches"), ("RECORD", "record"), ("WIN RATE", "win_rate"), ("KOS EACH", "kos")];
    string legendSort = "matches";

    static double LegendValue(JsonObject g, string key)
    {
        var games = OnlineService.Num(g["matches"]) ?? 0;
        return key switch
        {
            "matches" => games,
            "win_rate" or "record" => games > 0 ? (OnlineService.Num(g["wins"]) ?? 0) / games : 0,
            "kos" => games > 0 ? (OnlineService.Num(g["kos"]) ?? 0) / games : 0,
            _ => 0,
        };
    }

    Control LegendTable(JsonArray legends)
    {
        const string cols = "*,80,150,80,110";
        var holder = new ContentControl();
        void Draw()
        {
            var rows = legends.OfType<JsonObject>().ToList();
            rows = legendSort == "legend" ? rows.OrderBy(g => LegendShown(g["legend"], g["legend_name"]), StringComparer.CurrentCultureIgnoreCase).ToList()
                : rows.OrderByDescending(g => LegendValue(g, legendSort)).ToList();
            var t = new StackPanel { Spacing = 2 };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions(cols), ColumnSpacing = 10, Margin = new Thickness(8, 0) };
            for (var i = 0; i < LegendColumns.Length; i++)
            {
                var (label, key) = LegendColumns[i];
                var b = new Button { Classes = { "link" }, Background = Avalonia.Media.Brushes.Transparent, Padding = new Thickness(0), Content = Cell(label + (legendSort == key ? " ▾" : ""), 11, legendSort != key) };
                b.Click += (_, _) => { legendSort = key; Draw(); };
                Grid.SetColumn(b, i);
                head.Children.Add(b);
            }
            t.Children.Add(head);
            foreach (var g in rows.Take(12))
                t.Children.Add(new Border
                {
                    Padding = new Thickness(8, 4),
                    Child = Row(cols, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { LegendFace(g["legend"], 28), Cell(LegendShown(g["legend"], g["legend_name"]), weight: Avalonia.Media.FontWeight.SemiBold) } },
                        Cell($"{OnlineService.Num(g["matches"]) ?? 0:0}", dim: true),
                        Cell(OnlineService.Record(g), dim: true), Cell(LegendValue(g, "win_rate").ToString("P0"), dim: true), Cell(LegendValue(g, "kos").ToString("0.0"), dim: true)),
                });
            holder.Content = Panel(t);
        }
        Draw();
        return holder;
    }

    static readonly (string Label, string Key)[] MechanicColumns =
        [("DODGES", "dodges"), ("DASHES", "dashes"), ("JUMPS", "jumps"), ("COMBOS", "combos"), ("BEST COMBO", "best"), ("IN THE AIR", "air")];

    static string MechanicCell(JsonNode m, string key)
    {
        double N(string k) => OnlineService.Num(m[k]) ?? 0;
        return key switch
        {
            "best" => m["best_combo_hits"] == null ? "-" : $"{N("best_combo_hits"):0} hits, {N("best_combo_damage"):0}",
            "air" => m["air_ms"] == null ? "-" : (N("air_ms") / Math.Max(1, N("air_ms") + N("ground_ms") + N("wall_ms"))).ToString("P0"),
            "dodges" => m["dodges"] == null ? "-" : $"{N("dodges"):0}" + (m["air_dodges"] != null ? $" ({N("air_dodges"):0} air)" : ""),
            "combos" => m["combos"] == null ? "-" : $"{N("combos"):0} ({N("combo_damage"):0})",
            _ => m[key] == null ? "-" : $"{N(key):0}",
        };
    }

    static Control? MatchMechanics(JsonArray players)
    {
        var rows = players.OfType<JsonObject>().Where(x => x["mechanics"] is JsonObject).ToList();
        if (rows.Count == 0) return null;
        var stack = new StackPanel { Spacing = 8 };
        const string cols = "*,110,80,80,110,140,100";
        var table = new StackPanel { Spacing = 2 };
        table.Children.Add(Head(cols, ["PLAYER", .. MechanicColumns.Select(c => c.Label)]));
        foreach (var x in rows)
        {
            var m = x["mechanics"]!;
            var cells = new List<Control> { Cell(OnlineService.Text(x["name"]) ?? "?") };
            cells.AddRange(MechanicColumns.Select(c => (Control)Cell(MechanicCell(m, c.Key), dim: true)));
            table.Children.Add(new Border { Padding = new Thickness(8, 4), Child = Row(cols, [.. cells]) });
        }
        stack.Children.Add(Label("Mechanics"));
        stack.Children.Add(Panel(table));
        var lines = rows.Select(x => (Name: OnlineService.Text(x["name"]) ?? "?", Step: OnlineService.Num(x["mechanics"]!["step"]) ?? 0,
                Points: (x["mechanics"]!["timeline"] as JsonArray ?? []).Select(v => OnlineService.Num(v) ?? 0).ToList()))
            .Where(l => l.Step > 0 && l.Points.Count > 1).ToList();
        if (lines.Count > 0)
        {
            var step = lines.Max(l => l.Step);
            var longest = lines.Max(l => (int)Math.Ceiling(l.Points.Count * l.Step / step));
            var labels = Enumerable.Range(0, longest).Select(i => Clock(i * step / 1000)).ToList();
            var series = lines.Select((l, i) => new ChartSeries(l.Name + "'s damage", DamageChart.Series[i % DamageChart.Series.Length],
                Enumerable.Range(0, longest).Select(k => { var at = (int)(k * step / l.Step); return at < l.Points.Count ? l.Points[at] : 0; }).ToList())).ToList();
            stack.Children.Add(Label("Damage taken over the match"));
            stack.Children.Add(Charted(new LineChart(labels, series, height: 140)));
        }
        return stack;
    }

    async Task LoadMechanics(ContentControl holder, long account, int mine)
    {
        var m = await data.Stats("mechanics:" + account);
        if (mine != version || m == null || Error(m) != null || (OnlineService.Num(m["matches"]) ?? 0) <= 0) return;
        var per = m["per_match"] as JsonObject ?? [];
        var minute = m["per_minute"] as JsonObject ?? [];
        double P(string k) => OnlineService.Num(per[k]) ?? 0;
        double M(string k) => OnlineService.Num(minute[k]) ?? 0;
        var cards = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(string title, string big, string small) =>
            cards.Children.Add(new Border { Margin = new Thickness(0, 0, 10, 10), Width = 200, Child = Look.Card(title, Look.Pulse, new TextBlock { Text = big }, new TextBlock { Text = small }) });
        if (per["dodges"] != null) Add("Dodges", $"{P("dodges"):0.#} a match", $"{M("dodges"):0.#} a minute");
        if (per["dashes"] != null) Add("Dashes", $"{P("dashes"):0.#} a match", $"{M("dashes"):0.#} a minute");
        if (per["combos"] != null) Add("True combos", $"{P("combos"):0.#} a match", $"{P("combo_damage"):0} damage from combos");
        if (m["best_combo"] is JsonObject best) Add("Best combo", $"{OnlineService.Num(best["hits"]) ?? 0:0} hits", $"{OnlineService.Num(best["damage"]) ?? 0:0} damage");
        if (OnlineService.Num(m["air_share"]) is { } air) Add("In the air", air.ToString("P0"), $"{P("jumps"):0} jumps a match");
        if (cards.Children.Count == 0) return;
        var total = (int)(OnlineService.Num(m["matches"]) ?? 0);
        holder.Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Label("Your mechanics"), cards,
                Cell($"From {total} {(total == 1 ? "match" : "matches")} that sent them. Only you can see these unless the server makes them public.", 11.5, dim: true),
            },
        };
    }
}
