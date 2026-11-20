using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BifrostStudio;

public sealed partial class OnlinePage
{
    static readonly (string Key, string Name)[] ResetLadders = [("", "every ladder"), ("1v1", "1v1"), ("2v2", "2v2"), ("3v3", "3v3"), ("2v2team", "2v2 teams")];

    string adminView = "reports", adminFind = "";
    bool adminAllReports;
    JsonObject? adminPlayer;

    void ResetAdmin()
    {
        adminView = "reports";
        adminFind = "";
        adminAllReports = false;
        adminPlayer = null;
    }

    StackPanel AdminBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (key, name, icon) in new[] { ("reports", "Reports", Look.Logs), ("players", "Players", Look.Home), ("bans", "Banned", Look.Close), ("announcements", "Announcements", Look.Pulse) })
        {
            var b = Look.Nav(name, icon, () => { adminView = key; adminPlayer = null; Show("admin"); });
            b.Padding = new Thickness(12, 6);
            b.Classes.Set("selected", adminView == key);
            bar.Children.Add(b);
        }
        return bar;
    }

    async Task Admin(int mine)
    {
        if (!data.IsAdmin) { Put(Note("Only the server's admins see this.")); return; }
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(AdminBar());
        switch (adminView)
        {
            case "players":
                if (adminPlayer is { } picked) { stack.Children.Add(PlayerTools(picked)); break; }
                var found = adminFind.Length > 0 ? await data.Moderate("find", new JsonObject { ["name"] = adminFind }) : null;
                if (mine != version) return;
                stack.Children.Add(FindBox());
                if (found != null || adminFind.Length > 0) stack.Children.Add(Panel(PlayerList(found)));
                break;
            case "bans":
                var banned = await data.Moderate("bans", []);
                if (mine != version) return;
                stack.Children.Add(Bans(banned));
                break;
            case "announcements":
                var list = await data.Moderate("announcements", []);
                if (mine != version) return;
                stack.Children.Add(Announcements(list));
                break;
            default:
                var reports = await data.Moderate("reports", adminAllReports ? new JsonObject { ["all"] = true } : []);
                if (mine != version) return;
                stack.Children.Add(Reports(reports));
                break;
        }
        Put(stack);
    }

    string? AdminProblem(JsonNode? answer) =>
        answer == null ? "The server didn't answer." : OnlineService.Bool(answer["ok"]) ? null : OnlineService.Text(answer["message"]) ?? "The server refused.";

    Control Reports(JsonNode? answer)
    {
        var box = new StackPanel { Spacing = 8 };
        var all = Look.Link(adminAllReports ? "Only open reports" : "Show dismissed too", Look.Eye, () => { adminAllReports = !adminAllReports; Show("admin"); });
        box.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { all } });
        if (AdminProblem(answer) is { } why) { box.Children.Add(Note(why)); return box; }
        var rows = (answer!["reports"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Head("110,150,150,*,200", "WHEN", "REPORTED", "BY", "WHAT THEY WROTE", ""));
        foreach (var r in rows)
        {
            var id = (long)(OnlineService.Num(r["id"]) ?? 0);
            var target = OnlineService.Text(r["target"]) ?? "?";
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (OnlineService.Text(r["key"]) is { } key)
                buttons.Children.Add(Look.Link("Act on " + Short(target), Look.Wrench, () => { adminView = "players"; adminPlayer = new JsonObject { ["key"] = key, ["name"] = target }; Show("admin"); }));
            if (OnlineService.Bool(r["done"])) buttons.Children.Add(Cell("dismissed", 12, true));
            else buttons.Children.Add(Look.Link("Dismiss", Look.Check, () => _ = AdminAct("dismiss", new JsonObject { ["id"] = id })));
            var text = new TextBlock { Text = OnlineService.Text(r["text"]) ?? "", FontSize = 13, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            list.Children.Add(new Border
            {
                Padding = new Thickness(8, 4),
                Child = Row("110,150,150,*,200", Cell(OnlineService.Ago(OnlineService.When(r["time"]), hooks.Now()), dim: true), Cell(target, weight: FontWeight.SemiBold),
                    Cell(OnlineService.Text(r["reporter"]) ?? "?", dim: true), text, buttons),
            });
        }
        if (rows.Count == 0) list.Children.Add(Note(adminAllReports ? "No reports yet." : "No open reports."));
        box.Children.Add(Panel(list));
        return box;
    }

    static string Short(string name) => name.Length > 14 ? name[..13] + "…" : name;

    StackPanel FindBox()
    {
        var box = Field(adminFind, "player name");
        void Go()
        {
            adminFind = box.Text?.Trim() ?? "";
            Show("admin");
        }
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Go(); };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, Look.Secondary("Find", Look.Eye, Go) } };
    }

    Control PlayerList(JsonNode? answer)
    {
        if (AdminProblem(answer) is { } why) return Note(why);
        var rows = (answer!["players"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Head("*,120,90,160", "PLAYER", "", "ONLINE", "STATE"));
        foreach (var p in rows)
        {
            var state = string.Join(", ", new[] { OnlineService.Bool(p["banned"]) ? "banned" : null, OnlineService.Bool(p["muted"]) ? "muted" : null }.OfType<string>());
            var r = Row("*,120,90,160", Cell(OnlineService.Text(p["name"]) ?? "?", weight: FontWeight.SemiBold), Cell(OnlineService.Bool(p["account"]) ? "Discord account" : "guest", 12, true),
                Cell(OnlineService.Bool(p["online"]) ? "online" : "", dim: true), Cell(state, dim: true));
            list.Children.Add(Clickable(r, () => { adminPlayer = p; Show("admin"); }));
        }
        if (rows.Count == 0) list.Children.Add(Note("Nobody on this server has that name."));
        return list;
    }

    Control PlayerTools(JsonObject p)
    {
        var key = OnlineService.Text(p["key"]) ?? "";
        var name = OnlineService.Text(p["name"]) ?? key;
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { Look.Link("Back", Look.Home, () => { adminPlayer = null; Show("admin"); }), Cell(name, 22, weight: FontWeight.Bold) },
        });
        var state = new List<string> { key.StartsWith("d:") ? "Discord account" : "guest" };
        if (OnlineService.Bool(p["online"])) state.Add("online now");
        if (OnlineService.Bool(p["muted"])) state.Add("muted");
        if (OnlineService.Bool(p["banned"])) state.Add("banned");
        stack.Children.Add(Cell(string.Join(" · ", state), dim: true));
        var minutes = Field("", "minutes, blank for no limit", 220);
        var reason = Field("", "reason they'll see", 320);
        double Minutes() => double.TryParse(minutes.Text?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 0;
        JsonObject Args() => new() { ["key"] = key, ["minutes"] = Minutes(), ["reason"] = reason.Text?.Trim() ?? "" };
        var ladder = 0;
        var ladders = Combo(ResetLadders.Select(l => l.Name), 0, i => ladder = i);
        var tools = new StackPanel { Spacing = 10 };
        tools.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { minutes, reason } });
        tools.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                Look.Secondary("Mute", Look.Stop, () => _ = AdminAct("mute", Args())),
                Look.Secondary("Unmute", Look.Check, () => _ = AdminAct("unmute", Args())),
                Look.Secondary("Ban", Look.Close, () => { if (Confirmed("ban" + key, $"Click Ban again to sign {name} out and keep them off {(Minutes() > 0 ? $"for {Minutes():g} min" : "until you unban them")}.")) _ = AdminAct("ban", Args()); }),
                Look.Secondary("Unban", Look.Check, () => _ = AdminAct("unban", Args())),
            },
        });
        tools.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                Cell("Reset rating on", dim: true), ladders,
                Look.Secondary("Reset rating", Look.Refresh, () =>
                {
                    var l = ResetLadders[ladder];
                    if (Confirmed("reset" + key + l.Key, $"Click Reset rating again to reset {name} on {l.Name}. This can't be undone."))
                        _ = AdminAct("resetrating", new JsonObject { ["key"] = key, ["ladder"] = l.Key });
                }),
            },
        });
        stack.Children.Add(Panel(tools));
        stack.Children.Add(new TextBlock
        {
            Text = "Mute stops their chat. Ban signs them out and keeps them off until the time runs out, or until you unban them if minutes is blank.",
            FontSize = 12.5, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap,
        });
        return stack;
    }

    Control Bans(JsonNode? answer)
    {
        if (answer != null && !OnlineService.Bool(answer["ok"]) && (OnlineService.Text(answer["message"]) ?? "").StartsWith("Unknown action"))
            return Note("This server is too old to list bans here. Update it, or use the bans command in its console.");
        if (AdminProblem(answer) is { } why) return Note(why);
        var rows = (answer!["bans"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Head("*,150,*,140,90", "PLAYER", "UNTIL", "REASON", "BY", ""));
        foreach (var b in rows)
        {
            var key = OnlineService.Text(b["key"]) ?? "";
            var name = OnlineService.Text(b["name"]) ?? key;
            var open = Look.Link(Short(name), Look.Wrench, () => { adminView = "players"; adminPlayer = new JsonObject { ["key"] = key, ["name"] = name, ["banned"] = true }; Show("admin"); });
            var row = Row("*,150,*,140,90", open, Cell(BanLeft(OnlineService.When(b["until"]), hooks.Now()), dim: true),
                Cell(OnlineService.Text(b["reason"]) is { Length: > 0 } r ? r : "no reason given", dim: true), Cell(OnlineService.Text(b["by_name"]) ?? "", 12, true),
                Look.Link("Unban", Look.Check, () => _ = AdminAct("unban", new JsonObject { ["key"] = key })));
            list.Children.Add(new Border { Padding = new Thickness(8, 4), Child = row });
        }
        if (rows.Count == 0) list.Children.Add(Note("Nobody is banned."));
        return Panel(list);
    }

    public static string BanLeft(DateTime? until, DateTime now)
    {
        if (until is not { } u) return "until lifted";
        var left = u - now;
        if (left <= TimeSpan.Zero) return "ending";
        if (left < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)Math.Round(left.TotalMinutes))} min left";
        if (left < TimeSpan.FromDays(2)) return $"{(int)left.TotalHours} h left";
        return u.ToString("yyyy-MM-dd HH:mm");
    }

    Control Announcements(JsonNode? answer)
    {
        var box = new StackPanel { Spacing = 10 };
        var text = Field("", "announcement every player sees now and at each login", 520);
        text.MaxLength = 400;
        box.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                text,
                Look.Secondary("Announce", Look.Pulse, () =>
                {
                    var t = text.Text?.Trim() ?? "";
                    if (t.Length == 0) { said.Text = "Write the announcement first."; return; }
                    if (Confirmed("announce:" + t, "Click Announce again to show it to every player now and at each login until you remove it.")) _ = AdminAct("announce", new JsonObject { ["text"] = t });
                }),
            },
        });
        if (AdminProblem(answer) is { } why) { box.Children.Add(Note(why)); return box; }
        var rows = (answer!["announcements"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Head("110,*,100", "SINCE", "ANNOUNCEMENT", ""));
        foreach (var a in rows)
        {
            var id = (long)(OnlineService.Num(a["id"]) ?? 0);
            list.Children.Add(new Border
            {
                Padding = new Thickness(8, 4),
                Child = Row("110,*,100", Cell(OnlineService.Ago(OnlineService.When(a["time"]), hooks.Now()), dim: true),
                    new TextBlock { Text = OnlineService.Text(a["text"]) ?? "", FontSize = 13, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap },
                    Look.Link("Remove", Look.Trash, () => _ = AdminAct("unannounce", new JsonObject { ["id"] = id }))),
            });
        }
        if (rows.Count == 0) list.Children.Add(Note("No announcements are up."));
        box.Children.Add(Panel(list));
        return box;
    }

    async Task AdminAct(string action, JsonObject args)
    {
        var answer = await data.Moderate(action, args);
        said.Text = OnlineService.Text(answer?["message"]) ?? (answer == null ? "The server didn't answer." : OnlineService.Bool(answer["ok"]) ? "Done." : "The server refused.");
        if (adminPlayer != null && answer != null && OnlineService.Bool(answer["ok"]) && action is "mute" or "unmute" or "ban" or "unban")
        {
            adminPlayer["muted"] = action == "mute" || (action != "unmute" && OnlineService.Bool(adminPlayer["muted"]));
            adminPlayer["banned"] = action == "ban" || (action != "unban" && OnlineService.Bool(adminPlayer["banned"]));
        }
        if (tab == "admin") Show("admin");
    }
}
