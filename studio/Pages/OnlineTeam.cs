using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BifrostStudio.Net;

namespace BifrostStudio;

public sealed partial class OnlinePage
{
    public static string TeamQuery(long a, long b) => $"team:{Math.Min(a, b)}-{Math.Max(a, b)}";

    static (long A, long B)? TeamOf(JsonNode? row)
    {
        if (row?["accounts"] is not JsonArray { Count: 2 } acc || OnlineService.Num(acc[0]) is not { } a || OnlineService.Num(acc[1]) is not { } b) return null;
        return ((long)Math.Min(a, b), (long)Math.Max(a, b));
    }

    async Task TeamProfile(string query, int mine)
    {
        var t = await data.Stats(query);
        if (mine != version) return;
        var problem = t == null ? "The server didn't answer." : Error(t) is { } e ? (e == "no such team" ? "These two haven't played a rated 2v2 match together this season." : e) : null;
        var back = Look.Link("Back to yours", Look.Home, () => ShowProfile("me"));
        if (problem != null || t == null || TeamOf(t) is not { } pair)
        {
            Put(new StackPanel { Spacing = 8, Children = { Note(problem ?? "The server sent a team without its players."), back } });
            return;
        }
        var stack = new StackPanel { Spacing = 10 };
        var members = (t["members"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var name = OnlineService.Text(t["name"]) ?? string.Join(" & ", members.Select(m => OnlineService.Text(m["name"]) ?? "Player"));
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Cell(name, 24, weight: FontWeight.Bold), back } });
        stack.Children.Add(Cell("2v2 team", 13, dim: true));
        var cards = new WrapPanel { Orientation = Orientation.Horizontal };
        var rating = t["rating"] as JsonObject;
        var rank = (int)(OnlineService.Num(rating?["rank"]) ?? 0);
        var sub = rating == null ? "no team rating yet" :
            $"peak {OnlineService.Num(rating["peak"]) ?? 0:0} · {OnlineService.Num(rating["games"]) ?? 0:0} games" + (rank > 0 ? $" · #{rank}" : " · in placement");
        cards.Children.Add(new Border { Margin = new Thickness(0, 0, 10, 10), Width = 220, Child = Look.Card("Team rating", Look.Pulse, new TextBlock { Text = OnlineService.Rating(rating) }, new TextBlock { Text = sub }) });
        cards.Children.Add(new Border
        {
            Margin = new Thickness(0, 0, 10, 10), Width = 220,
            Child = Look.Card("Together", Look.Play, new TextBlock { Text = OnlineService.Record(t) },
                new TextBlock { Text = $"{OnlineService.Num(t["matches"]) ?? 0:0} matches this season · {OnlineService.Num(t["falls"]) ?? 0:0} falls" }),
        });
        stack.Children.Add(cards);
        if (members.Count > 0)
        {
            var list = new StackPanel { Spacing = 2 };
            list.Children.Add(Head("*,200", "PLAYER", "MOST PLAYED"));
            var legendNames = LegendNames((t["recent"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(m => (m["players"] as JsonArray ?? []).OfType<JsonObject>()));
            foreach (var m in members)
            {
                var legend = OnlineService.Num(m["top_legend"]) is { } id ? legendNames.TryGetValue((long)id, out var n) ? n : "legend " + (long)id : "";
                var r = Row("*,200", Cell(OnlineService.Text(m["name"]) ?? "Player", weight: FontWeight.SemiBold), Cell(legend, dim: true));
                list.Children.Add(OnlineService.Num(m["id"]) is { } acc ? Clickable(r, () => ShowProfile("profile:a" + (long)acc)) : new Border { Padding = new Thickness(8, 6), Child = r });
            }
            stack.Children.Add(Label("Players"));
            stack.Children.Add(Panel(list));
        }
        var holder = new ContentControl { Content = Note("Loading the team's rating history...") };
        stack.Children.Add(Label("Team rating history"));
        stack.Children.Add(Panel(holder));
        _ = LoadHistory(holder, $"history:{pair.A}-{pair.B}:2v2team", mine);
        if (t["recent"] is JsonArray { Count: > 0 } recent)
        {
            stack.Children.Add(Label("Recent matches together"));
            stack.Children.Add(Panel(TeamMatches(recent, pair.A)));
        }
        Put(stack);
    }

    StackPanel TeamMatches(JsonArray matches, long account)
    {
        var list = new StackPanel { Spacing = 2 };
        foreach (var m in matches.OfType<JsonObject>())
        {
            var players = (m["players"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var me = players.FirstOrDefault(x => (long?)OnlineService.Num(x["account"]) == account);
            var side = me?["team"]?.ToJsonString();
            var them = string.Join(" & ", players.Where(x => x["team"]?.ToJsonString() != side).Select(x => OnlineService.Text(x["name"]) ?? "?"));
            var outcome = me != null ? OnlineService.Text(me["outcome"]) ?? "" : "";
            var tr = m["team_rating"];
            var change = OnlineService.Num(tr?["rating_before"]) is { } b && OnlineService.Num(tr?["rating_after"]) is { } a ? $"{a - b:+0;-0;0}" : "";
            var r = Row("120,*,150,80,60",
                Cell(OnlineService.Ago(OnlineService.When(m["time"]), hooks.Now()), dim: true),
                Cell(them.Length > 0 ? "vs " + them : ""),
                Cell(OnlineService.Text(m["map"]) ?? OnlineService.Text(m["mode"]) ?? "", dim: true),
                Cell(outcome.ToUpperInvariant(), weight: FontWeight.SemiBold),
                Cell(change, dim: true));
            ToolTip.SetTip(r, "Team rating change");
            list.Children.Add(Clickable(r, () => Put(MatchDetail(m))));
        }
        return list;
    }
}
