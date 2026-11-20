using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BifrostStudio.Net;

namespace BifrostStudio;

public sealed partial class OnlinePage
{
    static readonly (string State, string Title)[] TournamentGroups = [("running", "Running now"), ("open", "Open for sign-up"), ("done", "Finished")];
    static readonly TimeSpan ConfirmFor = TimeSpan.FromSeconds(4);

    bool showCups;
    long? tourId;
    (long Id, int Round, int Slot)? tourMatch;
    uint? cupId, cupBracket;
    CupBoard? lastCups;
    bool creating;
    DateTime armedAt = DateTime.MinValue;
    string armed = "";

    void ResetTournaments()
    {
        showCups = creating = false;
        tourId = null;
        tourMatch = null;
        cupId = cupBracket = null;
        lastCups = null;
        armed = "";
        ResetMods();
    }

    public void ShowTournament(long? id)
    {
        showCups = false;
        tourId = id;
        tourMatch = null;
        creating = false;
        Show("tournaments");
    }

    public void ShowCups(uint? cup = null, uint? bracket = null)
    {
        showCups = true;
        cupId = cup;
        cupBracket = cup == null ? null : bracket;
        Show("tournaments");
    }

    static string Ord(double n) => (int)n % 100 is 11 or 12 or 13 ? $"{n:0}th" : ((int)n % 10) switch { 1 => $"{n:0}st", 2 => $"{n:0}nd", 3 => $"{n:0}rd", _ => $"{n:0}th" };

    static long? Id(JsonNode? n) => OnlineService.Num(n) is { } d ? (long)d : null;

    static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    StackPanel TourBar(bool mayCreate)
    {
        var tours = Look.Nav("Tournaments", Look.Trophy, () => ShowTournament(null));
        var cups = Look.Nav("Ranked cups", Look.Pulse, () => ShowCups());
        tours.Classes.Set("selected", !showCups);
        cups.Classes.Set("selected", showCups);
        foreach (var b in new[] { tours, cups }) b.Padding = new Thickness(12, 6);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { tours, cups } };
        if (!showCups && mayCreate) bar.Children.Add(Look.Secondary("New tournament", Look.Plus, () => { creating = true; tourId = null; Show("tournaments"); }));
        bar.Children.Add(Look.Secondary("Refresh", Look.Refresh, () => Show("tournaments")));
        return bar;
    }

    void DropConfirm()
    {
        if (armed.Length == 0) return;
        armed = "";
        said.Text = "";
    }

    bool Confirmed(string what, string ask)
    {
        var now = hooks.Now();
        if (armed == what && now - armedAt < ConfirmFor)
        {
            armed = "";
            return true;
        }
        armed = what;
        armedAt = now;
        said.Text = ask;
        return false;
    }

    async Task ActTour(string action, JsonObject args, long? open = null)
    {
        said.Text = "Sending...";
        var r = await data.Tournament(action, args);
        said.Text = r == null ? "The server didn't answer." : OnlineService.Text(r["message"]) ?? (OnlineService.Bool(r["ok"]) ? "Done." : "Refused.");
        if (r != null && OnlineService.Bool(r["ok"]) && open is { } id) tourId = id;
        else if (r != null && action == "create" && OnlineService.Bool(r["ok"]) && Id(r["id"]) is { } made) tourId = made;
        if (r != null && action == "create" && OnlineService.Bool(r["ok"])) creating = false;
        Show("tournaments");
    }

    async Task Tournaments(int mine)
    {
        if (showCups) { await CupsView(mine); return; }
        if (tourMatch is { } m) { await TournamentMatch(mine, m.Id, m.Round, m.Slot); return; }
        if (tourId is { } id) { await TournamentDetail(mine, id); return; }
        var t = await data.Stats("tournaments");
        if (mine != version) return;
        var stack = new StackPanel { Spacing = 10 };
        var mayCreate = OnlineService.Bool(t?["may_create"]);
        stack.Children.Add(TourBar(mayCreate));
        if (creating && mayCreate) stack.Children.Add(CreateForm());
        var problem = t == null ? "The server didn't answer." : Error(t);
        if (problem != null)
        {
            stack.Children.Add(Note(problem));
            Put(stack);
            return;
        }
        if (!OnlineService.Bool(t!["signed_in"])) stack.Children.Add(Note("Sign in with Discord at the top right to sign up. You can still browse tournaments."));
        var rows = (t["tournaments"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var (state, title) in TournamentGroups)
        {
            var group = rows.Where(r => OnlineService.Text(r["state"]) == state).ToList();
            if (group.Count == 0) continue;
            var list = new StackPanel { Spacing = 2 };
            list.Children.Add(Head("*,110,90,230", "TOURNAMENT", "MODE", "PLAYERS", state == "done" ? "WINNER" : "WHEN"));
            foreach (var r in group)
            {
                var tid = Id(r["id"]);
                var name = (OnlineService.Text(r["name"]) ?? "Tournament") + (OnlineService.Bool(r["signed_up"]) ? "  (signed up)" : "") + (OnlineService.Bool(r["mine"]) ? "  (yours)" : "");
                var row = Row("*,110,90,230",
                    Cell(name, weight: OnlineService.Bool(r["signed_up"]) ? FontWeight.SemiBold : FontWeight.Normal),
                    Cell($"{OnlineService.Text(r["mode"]) ?? "1v1"} · Bo{OnlineService.Num(r["best_of"]) ?? 3:0}", dim: true),
                    Cell($"{OnlineService.Num(r["players"]) ?? 0:0}/{OnlineService.Num(r["max_players"]) ?? 0:0}", dim: true),
                    Cell(When(r), dim: state != "done"));
                list.Children.Add(tid is { } i ? Clickable(row, () => ShowTournament(i)) : new Border { Padding = new Thickness(8, 6), Child = row });
            }
            stack.Children.Add(Label(title + $" ({group.Count})"));
            stack.Children.Add(Panel(list));
        }
        if (rows.Count == 0) stack.Children.Add(Note(mayCreate ? "No tournaments on this server yet. Click New tournament to make one." : "No tournaments on this server yet."));
        Put(stack);
    }

    string When(JsonObject t)
    {
        var now = hooks.Now();
        return OnlineService.Text(t["state"]) switch
        {
            "open" => OnlineService.When(t["starts"]) is { } s ? (s > now ? "starts " + s.ToString("ddd d MMM HH:mm") : "starting now") : "starts when the host starts it",
            "running" => "started " + OnlineService.Ago(OnlineService.When(t["started"]), now),
            "done" => OnlineService.Text(t["winner_name"]) ?? "no winner",
            _ => "cancelled",
        };
    }

    Control CreateForm()
    {
        var name = Field("", "tournament name", 300);
        var about = Field("", "optional description", 420);
        int[] sizes = [4, 8, 16, 32, 64, 128];
        int[] bestOf = [1, 3, 5];
        string[] modes = ["1v1", "2v2"];
        int mode = 0, best = 1, size = 2;
        var go = Look.Primary("Create", Look.Check, () =>
        {
            var n = name.Text?.Trim() ?? "";
            if (n.Length == 0) { said.Text = "Give the tournament a name first."; return; }
            _ = ActTour("create", new JsonObject
            {
                ["name"] = n, ["mode"] = modes[mode], ["best_of"] = bestOf[best], ["max_players"] = sizes[size], ["about"] = about.Text?.Trim() ?? "",
            });
        });
        return Panel(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Cell("New tournament", 17, weight: FontWeight.SemiBold),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { name, Combo(modes, mode, i => mode = i), Combo(bestOf.Select(b => "Best of " + b), best, i => best = i), Combo(sizes.Select(s => s + " max"), size, i => size = i) } },
                about,
                new TextBlock { Text = "Sign-up opens right away, and it starts when you click Start now. Set rules, maps, check-in and pools in the game's Tournaments window.", FontSize = 12.5, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { go, Look.Secondary("Cancel", Look.Close, () => { creating = false; Show("tournaments"); }) } },
            },
        });
    }

    async Task TournamentDetail(int mine, long id)
    {
        var v = await data.Stats("tournament:" + id);
        if (mine != version) return;
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(TourBar(false));
        var problem = v == null ? "The server didn't answer." : Error(v);
        if (problem != null || v == null)
        {
            stack.Children.Add(Look.Link("All tournaments", Look.Trophy, () => ShowTournament(null)));
            stack.Children.Add(Note(problem ?? ""));
            Put(stack);
            return;
        }
        var state = OnlineService.Text(v["state"]) ?? "";
        var paused = OnlineService.Num(v["paused"]) is > 0;
        var mode = OnlineService.Text(v["mode"]) ?? "1v1";
        var me = Id(v["me"]);
        var mySide = Id(v["my_side"]);
        var manage = OnlineService.Bool(v["manage"]);
        var entrants = (v["entrants"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { Look.Link("All tournaments", Look.Trophy, () => ShowTournament(null)), Cell(OnlineService.Text(v["name"]) ?? "Tournament", 22, weight: FontWeight.Bold) },
        });
        var facts = new List<string>
        {
            mode, $"{entrants.Count}/{OnlineService.Num(v["max_players"]) ?? 0:0} {(mode == "2v2" ? "teams" : "players")}",
            BestOfText(v), OnlineService.Num(v["losers"]) is > 0 ? "double elimination" : "single elimination",
            state switch { "open" => "open for sign-up", "running" => paused ? "paused" : "running", "done" => "finished", _ => state },
            "by " + (OnlineService.Text(v["owner_name"]) ?? "?"),
        };
        var now = hooks.Now();
        if (state == "open" && OnlineService.When(v["starts"]) is { } starts) facts.Add("starts " + starts.ToString("ddd d MMM HH:mm"));
        if (state == "running" && OnlineService.When(v["started"]) is { } started) facts.Add("started " + OnlineService.Ago(started, now));
        if (state == "done" && OnlineService.When(v["finished"]) is { } finished) facts.Add("finished " + OnlineService.Ago(finished, now));
        stack.Children.Add(Cell(string.Join(" · ", facts), dim: true));
        var rules = new List<string>();
        if (OnlineService.Num(v["stocks"]) is { } stocks) rules.Add($"{stocks:0} stocks");
        if (OnlineService.Num(v["minutes"]) is { } minutes) rules.Add($"{minutes:0} min");
        if (OnlineService.Num(v["checkin"]) is > 0 and var ci) rules.Add($"check-in {ci:0} min");
        if (OnlineService.Num(v["min_rating"]) is > 0 and var lo) rules.Add($"rating from {lo:0}");
        if (OnlineService.Num(v["max_rating"]) is > 0 and var hi) rules.Add($"rating up to {hi:0}");
        foreach (var (key, title) in new[] { ("region", "region"), ("prize", "prize"), ("seeding", "seeding") })
            if (OnlineService.Text(v[key]) is { } text) rules.Add($"{title} {text}");
        if (OnlineService.Text(v["legend_rule"]) is { } lr) rules.Add($"legends: {lr}");
        if (OnlineService.Text(v["weapon_rule"]) is { } wr && OnlineService.Text(v["weapons"]) is { } ws) rules.Add($"weapons {(wr == "ban" ? "banned" : "only")}: {ws}");
        if (rules.Count > 0) stack.Children.Add(Cell(string.Join(" · ", rules), 12.5, dim: true));
        foreach (var key in new[] { "about", "rules" })
            if (OnlineService.Text(v[key]) is { } text)
                stack.Children.Add(new TextBlock { Text = text, FontSize = 13.5, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap });
        if (OnlineService.Text(v["stream"]) is { } stream)
        {
            if (stream.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || stream.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                stack.Children.Add(Look.Link("Watch the stream", Look.Monitor, () => hooks.Open(stream)));
            else stack.Children.Add(Cell("Stream: " + stream, dim: true));
        }
        if (OnlineService.Text(v["winner_name"]) is { } winner) stack.Children.Add(Cell("Winner: " + winner, 16, weight: FontWeight.SemiBold));
        stack.Children.Add(Actions(v, id, state, paused, mode, me, mySide, manage));
        if (state == "running" && MyMatch(v, mySide) is { } myMatch) stack.Children.Add(myMatch);
        if (v["pools"] is JsonArray { Count: > 0 } pools) stack.Children.Add(Pools(pools));
        if (v["rounds"] is JsonArray { Count: > 0 } rounds) stack.Children.Add(Bracket(id, rounds, mySide));
        stack.Children.Add(Entrants(entrants, v["waitlist"] as JsonArray, mySide, mode));
        if (v["admins"] is JsonArray { Count: > 0 } admins)
            stack.Children.Add(Cell("Admins: " + string.Join(", ", admins.Select(a => OnlineService.Text(a?["name"]) ?? "?")), 12.5, dim: true));
        Put(stack);
    }

    static string BestOfText(JsonNode v)
    {
        var text = $"best of {OnlineService.Num(v["best_of"]) ?? 3:0}";
        if (OnlineService.Num(v["best_of_top8"]) is > 0 and var top) text += $", top 8 Bo{top:0}";
        if (OnlineService.Num(v["best_of_final"]) is > 0 and var fin) text += $", final Bo{fin:0}";
        return text;
    }

    Control Actions(JsonNode v, long id, string state, bool paused, string mode, long? me, long? mySide, bool manage)
    {
        var box = new StackPanel { Spacing = 8 };
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(Button b)
        {
            b.Margin = new Thickness(0, 0, 8, 6);
            row.Children.Add(b);
        }
        JsonObject Args() => new() { ["id"] = id };
        if (me == null)
        {
            box.Children.Add(Note("Sign in with Discord at the top right to take part."));
        }
        else if (state == "open")
        {
            if (mySide == null) Add(Look.Primary(mode == "2v2" ? "Join (team up below)" : "Join", Look.Plus, () => _ = ActTour("join", Args(), id)));
            else Add(Look.Secondary("Leave", Look.Close, () => { if (Confirmed("leave" + id, "Click Leave again to leave the tournament.")) _ = ActTour("leave", Args(), id); }));
        }
        if (me != null && state == "running" && CheckInPending(v, mySide, me.Value)) Add(Look.Primary("Check in", Look.Check, () => _ = ActTour("checkin", Args(), id)));
        if (manage && state == "open") Add(Look.Secondary("Start now", Look.Play, () => { if (Confirmed("start" + id, "Click Start now again to close sign-up and make the bracket.")) _ = ActTour("start", Args(), id); }));
        if (manage && state == "running") Add(Look.Secondary(paused ? "Resume" : "Pause", paused ? Look.Play : Look.Stop, () => _ = ActTour(paused ? "resume" : "pause", Args(), id)));
        if (manage && state is "open" or "running") Add(Look.Secondary("Cancel tournament", Look.Trash, () => { if (Confirmed("cancel" + id, "Click Cancel tournament again to cancel it for everyone.")) _ = ActTour("cancel", Args(), id); }));
        if (row.Children.Count > 0) box.Children.Add(row);
        if (me != null && mode == "2v2" && state == "open")
        {
            foreach (var inv in (v["invites_in"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var from = Id(inv["from_acc"]);
                if (from == null) continue;
                box.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        Cell((OnlineService.Text(inv["from_name"]) ?? "Someone") + " asks you to team up."),
                        Look.Link("Accept", Look.Check, () => _ = ActTour("team_answer", new JsonObject { ["id"] = id, ["inviter"] = from, ["accept"] = true }, id)),
                        Look.Link("Decline", Look.Close, () => _ = ActTour("team_answer", new JsonObject { ["id"] = id, ["inviter"] = from, ["accept"] = false }, id)),
                    },
                });
            }
            if (v["invites_out"] is JsonArray { Count: > 0 } outs)
                box.Children.Add(Cell("Waiting for: " + string.Join(", ", outs.Select(o => OnlineService.Text(o) ?? "?")), dim: true));
            if (mySide == null)
            {
                var partner = Field("", "partner's player name");
                box.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        partner,
                        Look.Secondary("Ask to team up", Look.Plus, () => { if (partner.Text?.Trim() is { Length: > 0 } n) _ = ActTour("team_invite", new JsonObject { ["id"] = id, ["name"] = n }, id); }),
                    },
                });
            }
        }
        return box;
    }

    static IEnumerable<JsonObject> AllMatches(JsonNode v)
    {
        foreach (var r in (v["rounds"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var m in (r["matches"] as JsonArray ?? []).OfType<JsonObject>())
                yield return m;
        if (v["my_pool_match"] is JsonObject pm) yield return pm;
    }

    static bool Mine(JsonObject m, long? side) => side != null && (Id(m["a"]) == side || Id(m["b"]) == side);

    static bool CheckInPending(JsonNode v, long? side, long me) =>
        AllMatches(v).Any(m => Mine(m, side) && m["winner"] == null && OnlineService.Num(m["checkin_left"]) != null &&
                               !(m["checked"] as JsonArray ?? []).Any(c => Id(c) == me));

    Control? MyMatch(JsonNode v, long? side)
    {
        var m = AllMatches(v).FirstOrDefault(x => Mine(x, side) && x["winner"] == null && x["a"] != null && x["b"] != null);
        if (m == null) return null;
        var vs = $"{OnlineService.Text(m["a_name"]) ?? "?"} vs {OnlineService.Text(m["b_name"]) ?? "?"}";
        var line = $"Your match: {vs}  {OnlineService.Num(m["wins_a"]) ?? 0:0} - {OnlineService.Num(m["wins_b"]) ?? 0:0}";
        if (OnlineService.Num(m["checkin_left"]) is { } left) line += $" · check-in closes in {Clock(left)}";
        return Panel(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Cell(line, 15, weight: FontWeight.SemiBold),
                new TextBlock { Text = "Play your matches in the game. Its Tournaments window takes you to the room.", FontSize = 12.5, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap },
            },
        });
    }

    Control Pools(JsonArray pools)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var p in pools.OfType<JsonObject>())
        {
            var list = new StackPanel { Spacing = 2 };
            list.Children.Add(Cell($"{OnlineService.Text(p["name"]) ?? "Pool"} · {OnlineService.Num(p["played"]) ?? 0:0}/{OnlineService.Num(p["total"]) ?? 0:0} played", 14, weight: FontWeight.SemiBold));
            list.Children.Add(Head("30,*,60,70", "#", "NAME", "W-L", "GAMES"));
            foreach (var s in (p["standings"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var through = OnlineService.Bool(s["advances"]);
                list.Children.Add(new Border
                {
                    Padding = new Thickness(8, 3),
                    Child = Row("30,*,60,70",
                        Cell($"{OnlineService.Num(s["place"]) ?? 0:0}", dim: true),
                        new TextBlock { Text = OnlineService.Text(s["name"]) ?? "?", FontSize = 13.5, Foreground = Look.Brush(through ? Look.Good : Look.Text), TextTrimming = TextTrimming.CharacterEllipsis },
                        Cell($"{OnlineService.Num(s["wins"]) ?? 0:0}-{OnlineService.Num(s["losses"]) ?? 0:0}", dim: true),
                        Cell($"{OnlineService.Num(s["games_won"]) ?? 0:0}-{OnlineService.Num(s["games_lost"]) ?? 0:0}", dim: true)),
                });
            }
            wrap.Children.Add(new Border { Width = 330, Margin = new Thickness(0, 0, 10, 10), Child = Panel(list) });
        }
        return new StackPanel { Children = { Label("Pools"), wrap } };
    }

    Control Bracket(long id, JsonArray rounds, long? mySide)
    {
        var all = rounds.OfType<JsonObject>().ToList();
        var box = new StackPanel { Spacing = 6 };
        foreach (var (key, title) in new[] { ("w", "Bracket"), ("l", "Losers side"), ("g", "Grand final") })
        {
            var part = all.Where(r => OnlineService.Text(r["bracket"]) == key).ToList();
            if (part.Count == 0) continue;
            var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            foreach (var r in part)
            {
                var col = new StackPanel { Spacing = 6, Width = 230 };
                var rnd = (int)(OnlineService.Num(r["round"]) ?? 0);
                col.Children.Add(Cell($"{OnlineService.Text(r["name"]) ?? "Round"} · Bo{OnlineService.Num(r["best_of"]) ?? 0:0}", 12, true, FontWeight.SemiBold));
                foreach (var m in (r["matches"] as JsonArray ?? []).OfType<JsonObject>())
                    col.Children.Add(MatchCard(id, rnd, m, mySide));
                columns.Children.Add(col);
            }
            box.Children.Add(Label(title));
            box.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = columns });
        }
        return box;
    }

    Control MatchCard(long id, int round, JsonObject m, long? mySide)
    {
        var winner = Id(m["winner"]);
        var slot = (int)(OnlineService.Num(m["slot"]) ?? 0);
        Control Side(string s)
        {
            var who = Id(m[s]);
            var name = OnlineService.Text(m[s + "_name"]) ?? (who == null ? "-" : "?");
            var won = winner != null && who == winner;
            var color = who == null ? Look.Dim : won ? Look.Good : Look.Text;
            return Row("*,26",
                new TextBlock { Text = name + (who != null && who == mySide ? "  (you)" : ""), FontSize = 13, FontWeight = won ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Look.Brush(color), TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = $"{OnlineService.Num(m["wins_" + s]) ?? 0:0}", FontSize = 13, Foreground = Look.Brush(color), HorizontalAlignment = HorizontalAlignment.Right });
        }
        var inner = new StackPanel { Spacing = 2, Children = { Side("a"), Side("b") } };
        var note = OnlineService.Text(m["note"]);
        if (OnlineService.Num(m["checkin_left"]) is { } left) note = (note != null ? note + " · " : "") + $"check-in {Clock(left)} left";
        if (note != null) inner.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Look.Brush(Look.Dim) });
        var card = new Border { Background = Look.Brush(Mine(m, mySide) ? Look.Raised : Look.Panel), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7), Child = inner };
        return m["results"] is JsonArray { Count: > 0 } ? Clickable(card, () => { tourMatch = (id, round, slot); Show("tournaments"); }) : card;
    }

    Control Entrants(List<JsonObject> entrants, JsonArray? waitlist, long? mySide, string mode)
    {
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Head("60,*,90", "SEED", mode == "2v2" ? "TEAM" : "PLAYER", "PLACE"));
        foreach (var e in entrants)
        {
            var dq = OnlineService.Bool(e["disqualified"]);
            var you = mySide != null && Id(e["account"]) == mySide;
            list.Children.Add(new Border
            {
                Padding = new Thickness(8, 3),
                Child = Row("60,*,90",
                    Cell(OnlineService.Num(e["seed"]) is { } seed ? $"{seed:0}" : "-", dim: true),
                    Cell((OnlineService.Text(e["name"]) ?? "?") + (you ? "  (you)" : "") + (dq ? "  (disqualified)" : ""), weight: you ? FontWeight.SemiBold : FontWeight.Normal, dim: dq),
                    Cell(OnlineService.Num(e["placement"]) is { } place ? Ord(place) : "", dim: true)),
            });
        }
        if (entrants.Count == 0) list.Children.Add(Note("Nobody has signed up yet."));
        var box = new StackPanel { Children = { Label($"{(mode == "2v2" ? "Teams" : "Players")} ({entrants.Count})"), Panel(list) } };
        if (waitlist is { Count: > 0 })
            box.Children.Add(Cell("Waitlist: " + string.Join(", ", waitlist.Select(w => OnlineService.Text(w?["name"]) ?? "?")), 12.5, dim: true));
        return box;
    }

    async Task TournamentMatch(int mine, long id, int round, int slot)
    {
        var g = await data.Stats($"tmatch:{id}:{round}:{slot}");
        if (mine != version) return;
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(TourBar(false));
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                Look.Link("Back to the tournament", Look.Trophy, () => ShowTournament(id)),
                Cell($"{OnlineService.Text(g?["a_name"]) ?? "?"} vs {OnlineService.Text(g?["b_name"]) ?? "?"}", 20, weight: FontWeight.Bold),
            },
        });
        var problem = g == null ? "The server didn't answer." : Error(g);
        var games = (g?["games"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (problem != null || games.Count == 0)
        {
            stack.Children.Add(Note(problem ?? (OnlineService.Text(g?["note"]) is { } n ? "No games saved: " + n + "." : "No games saved for this match.")));
            Put(stack);
            return;
        }
        var k = 0;
        foreach (var game in games)
        {
            k++;
            var won = OnlineService.Text(game["won"]);
            var table = new StackPanel { Spacing = 2 };
            var mapName = OnlineService.Text(game["map"]) ?? "";
            table.Children.Add(Cell($"Game {k}" + (mapName.Length > 0 ? " · " + mapName : "") + $" · {Clock(OnlineService.Num(game["seconds"]) ?? 0)}", 14, weight: FontWeight.SemiBold));
            table.Children.Add(Head("*,150,60,60", "PLAYER", "LEGEND", "KOS", "FALLS"));
            foreach (var side in new[] { "a", "b" })
                foreach (var p in (game[side] as JsonArray ?? []).OfType<JsonObject>())
                    table.Children.Add(new Border
                    {
                        Padding = new Thickness(8, 3),
                        Child = Row("*,150,60,60",
                            new TextBlock { Text = (OnlineService.Text(p["name"]) ?? "?") + (won == side ? "  (won)" : ""), FontSize = 13.5, Foreground = Look.Brush(won == side ? Look.Good : Look.Text), TextTrimming = TextTrimming.CharacterEllipsis },
                            Cell(OnlineService.Text(p["legend"]) ?? "", dim: true),
                            Cell($"{OnlineService.Num(p["kos"]) ?? 0:0}"),
                            Cell($"{OnlineService.Num(p["falls"]) ?? 0:0}")),
                    });
            stack.Children.Add(Panel(table));
        }
        Put(stack);
    }

    static string CupState(int state) => state switch { BifrostClient.CupOpen => "Open", BifrostClient.CupRunning => "Running", BifrostClient.CupDone => "Finished", _ => "?" };

    async Task CupsView(int mine)
    {
        if (cupId is { } cup && cupBracket is { } bracket) { await CupBracketView(mine, cup, bracket); return; }
        if (cupId is { } c) { await CupHistory(mine, c); return; }
        var board = await data.Cups();
        if (mine != version) return;
        if (board != null) lastCups = board;
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(TourBar(false));
        stack.Children.Add(new TextBlock { Text = "Ranked cups are the game's scheduled 8-player double-elimination brackets. Register here and play the matches in the game.", FontSize = 12.5, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap });
        if (board == null)
        {
            stack.Children.Add(Note("The server didn't answer."));
            Put(stack);
            return;
        }
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Head("*,90,200,Auto", "CUP", "STATE", "WHEN", ""));
        var now = hooks.Now();
        foreach (var cupRow in board.Cups.OrderBy(x => x.State == BifrostClient.CupDone).ThenBy(x => x.When ?? DateTime.MaxValue))
        {
            var registered = board.Registered.Contains(cupRow.Id);
            var when = cupRow.When is { } w ? (cupRow.State == BifrostClient.CupOpen ? "starts " : "ends ") + (w > now ? w.ToString("ddd d MMM HH:mm") : "now") : "";
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (cupRow.State == BifrostClient.CupOpen && data.SignedIn)
                buttons.Children.Add(registered
                    ? Look.Link("Withdraw", Look.Close, () => _ = SignUpCup(cupRow.Id, false))
                    : Look.Link("Register", Look.Plus, () => _ = SignUpCup(cupRow.Id, true)));
            if (cupRow.State != BifrostClient.CupOpen) buttons.Children.Add(Look.Link("Brackets", Look.Eye, () => ShowCups(cupRow.Id)));
            var name = cupRow.Name + (cupRow.Official ? "" : "  (community)") + (registered ? "  (registered)" : "");
            list.Children.Add(new Border
            {
                Padding = new Thickness(8, 4),
                Child = Row("*,90,200,Auto", Cell(name, weight: registered ? FontWeight.SemiBold : FontWeight.Normal), Cell(CupState(cupRow.State), dim: true), Cell(when, dim: true), buttons),
            });
        }
        if (board.Cups.Count == 0) list.Children.Add(Note("This server has no cups scheduled."));
        else if (!data.SignedIn) list.Children.Add(Note("Sign in with Discord at the top right to register for a cup."));
        stack.Children.Add(Panel(list));
        Put(stack);
    }

    async Task SignUpCup(uint cup, bool on)
    {
        said.Text = on ? "Registering..." : "Withdrawing...";
        var board = await data.CupSignUp(cup, on);
        said.Text = board == null ? "The server didn't answer." : board.Registered.Contains(cup) == on
            ? (on ? "Registered. The game tells you when your first set is ready." : "Withdrawn.")
            : "The server refused. The cup may have closed.";
        ShowCups();
    }

    async Task CupHistory(int mine, uint cup)
    {
        var rows = await data.CupBrackets(cup);
        if (mine != version) return;
        var name = lastCups?.Cups.FirstOrDefault(c => c.Id == cup)?.Name;
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(TourBar(false));
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { Look.Link("All cups", Look.Pulse, () => ShowCups()), Cell(name ?? $"Cup {cup}", 20, weight: FontWeight.Bold) },
        });
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Head("90,90,*,*,*", "BRACKET", "RATING", "1ST", "2ND", "3RD"));
        foreach (var b in rows ?? [])
            list.Children.Add(Clickable(Row("90,90,*,*,*", Cell($"#{b.Id}"), Cell($"{b.Rating}", dim: true), Cell(b.First), Cell(b.Second, dim: true), Cell(b.Third, dim: true)), () => ShowCups(cup, b.Id)));
        if (rows == null) list.Children.Add(Note("The server didn't answer."));
        else if (rows.Count == 0) list.Children.Add(Note("No brackets yet. They're made when the cup starts."));
        stack.Children.Add(Panel(list));
        Put(stack);
    }

    static string SetGroup(uint sub, uint round) => sub switch
    {
        0 => round switch { 0 => "Quarter-finals", 1 => "Semi-finals", _ => "Winners final" },
        1 => $"Losers round {round + 1}",
        _ => "Grand final",
    };

    async Task CupBracketView(int mine, uint cup, uint bracket)
    {
        var view = await data.CupBracket(cup, bracket);
        if (mine != version) return;
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(TourBar(false));
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                Look.Link("Brackets", Look.Pulse, () => ShowCups(cup)),
                Cell($"Bracket #{bracket}" + (view != null ? $" · average rating {view.Rating}" + (view.State == BifrostClient.CupDone ? " · finished" : "") : ""), 20, weight: FontWeight.Bold),
            },
        });
        if (view == null)
        {
            stack.Children.Add(Note("The server didn't answer."));
            Put(stack);
            return;
        }
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var group in view.Sets.GroupBy(s => (s.Sub, s.Round)).OrderBy(g => g.Key.Sub).ThenBy(g => g.Key.Round))
        {
            var col = new StackPanel { Spacing = 6, Width = 220 };
            col.Children.Add(Cell(SetGroup(group.Key.Sub, group.Key.Round), 12, true, FontWeight.SemiBold));
            foreach (var s in group.OrderBy(x => x.Position))
            {
                Control Side(CupSide? side, uint score)
                {
                    var won = side != null && s.Winner != 0 && side.Entrant == s.Winner;
                    var color = side == null ? Look.Dim : won ? Look.Good : Look.Text;
                    return Row("*,26",
                        new TextBlock { Text = side == null ? "bye" : $"{side.Name} ({side.Seed})", FontSize = 13, FontWeight = won ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Look.Brush(color), TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = score.ToString(), FontSize = 13, Foreground = Look.Brush(color), HorizontalAlignment = HorizontalAlignment.Right });
                }
                col.Children.Add(new Border { Background = Look.Brush(Look.Panel), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7), Child = new StackPanel { Spacing = 2, Children = { Side(s.A, s.ScoreA), Side(s.B, s.ScoreB) } } });
            }
            columns.Children.Add(col);
        }
        if (view.Sets.Count == 0) stack.Children.Add(Note("No sets yet."));
        stack.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = columns });
        Put(stack);
    }
}
