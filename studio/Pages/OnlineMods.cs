using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BifrostStudio.Net;

namespace BifrostStudio;

public sealed partial class OnlinePage
{
    static readonly string[] ModKinds = ["All", "Featured", "Packs", "Modes", "Legends", "Skins", "Weapons", "Avatars", "Emotes", "Maps", "Map art", "Music", "Other"];

    string modPane = "community";
    string modKind = "All", modSearch = "";
    ModListing? library;
    JsonObject? modOpen;
    readonly HashSet<string> getting = [], sending = [], previewing = [];
    readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> previews = [];

    enum Have { None, Installed, Other }

    void ResetMods()
    {
        modPane = "community";
        library = modFrom = null;
        ResetShelf();
        modOpen = null;
        previews.Clear();
    }

    Have HaveOf(JsonObject mod)
    {
        var id = OnlineService.Text(mod["id"]);
        var entry = OnlineService.Text(mod["entry"]);
        var local = hooks.LocalMods?.Invoke() ?? [];
        if (local.Any(m => m.Identity != null && m.Identity == entry)) return Have.Installed;
        return local.Any(m => m.Id == id) ? Have.Other : Have.None;
    }

    static string Size(double bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024 / 1024:0.0} MB" : $"{Math.Max(1, bytes / 1024):0} KB";

    WrapPanel ModBar()
    {
        Button Pane(string text, string icon, string key)
        {
            var b = Look.Nav(text, icon, () => { modPane = key; modOpen = null; collectionOpen = null; creatorOpen = null; said.Text = ""; Show("mods"); });
            b.Classes.Set("selected", modPane == key);
            b.Padding = new Thickness(12, 6);
            return b;
        }
        var all = Pane("Community", Look.Folder, "community");
        var collections = Pane("Collections", Look.Tag, "collections");
        var following = Pane("Following", Look.Eye, "following");
        var mineButton = Pane("Your uploads", Look.Download, "mine");
        var key = Field("", "mod key like ABCD-2345", 190);
        void Find()
        {
            if (key.Text?.Trim() is { Length: > 0 } k) _ = ByKey(k);
        }
        key.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Find(); };
        return new WrapPanel
        {
            ItemSpacing = 6, LineSpacing = 6,
            Children = { all, collections, following, mineButton, key, Look.Secondary("Find", Look.Eye, Find), Look.Secondary("Refresh", Look.Refresh, () => { library = null; shelf = null; previews.Clear(); Show("mods"); }) },
        };
    }

    async Task CommunityMods(int mine)
    {
        if (modPane == "mine") { await MyUploads(mine); return; }
        if (modOpen is { } open) { Put(ModDetail(open, modFrom ?? library)); return; }
        if (modPane is "collections" or "following") { await WorkshopPane(mine); return; }
        var got = library ?? await data.ModLibrary();
        if (mine != version) return;
        if (got?.Error == null) library = got;
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(ModBar());
        if (got == null || got.Error != null)
        {
            stack.Children.Add(Note(got?.Error ?? "The server didn't answer."));
            Put(stack);
            return;
        }
        if (got.Message is { } msg) said.Text = msg;
        var search = Field(modSearch, "find a mod");
        void Go()
        {
            modSearch = search.Text?.Trim() ?? "";
            Show("mods");
        }
        search.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Go(); };
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { Combo(ModKinds, Math.Max(0, Array.IndexOf(ModKinds, modKind)), i => { modKind = ModKinds[i]; Show("mods"); }), search, Look.Secondary("Search", Look.Eye, Go) },
        });
        var mods = (got.Body?["mods"] as JsonArray ?? []).OfType<JsonObject>().Where(m =>
            (modKind == "All" || (modKind == "Featured" ? OnlineService.Bool(m["featured"]) : (m["kinds"] as JsonArray ?? []).Any(k => OnlineService.Text(k) == modKind))) &&
            (modSearch.Length == 0 || $"{OnlineService.Text(m["name"])} {OnlineService.Text(m["author"])} {OnlineService.Text(m["description"])} {OnlineService.Text(m["id"])}"
                .Contains(modSearch, StringComparison.OrdinalIgnoreCase))).ToList();
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Head("*,140,70,80,160", "MOD", "KIND", "LIKES", "SIZE", ""));
        foreach (var m in mods)
        {
            var have = HaveOf(m);
            var title = (OnlineService.Text(m["name"]) ?? OnlineService.Text(m["id"]) ?? "Mod") + " " + (OnlineService.Text(m["version"]) ?? "");
            var by = OnlineService.Text(m["author"]) is { } a ? "by " + a : "";
            if (PackLine(m) is { } packLine) by = by.Length > 0 ? by + " · " + packLine : packLine;
            var who = new StackPanel { Spacing = 1, Children = { Cell(title, weight: FontWeight.SemiBold), Cell(by, 12, true) } };
            if (OnlineService.Bool(m["featured"])) who.Children.Insert(0, FeaturedMark(m, 11.5));
            var row = Row("*,140,70,80,160", who,
                Cell(string.Join(", ", (m["kinds"] as JsonArray ?? []).Select(k => OnlineService.Text(k) ?? "")), dim: true),
                Cell($"{(OnlineService.Bool(m["liked"]) ? "♥" : "♡")} {OnlineService.Num(m["likes"]) ?? 0:0}", dim: true),
                Cell(Size(OnlineService.Num(m["size"]) ?? 0), dim: true),
                Cell(have switch { Have.Installed => "Installed", Have.Other => "Other version installed", _ => "" }, 12, true));
            list.Children.Add(Clickable(row, () => { modOpen = m; modFrom = got; Show("mods"); }));
        }
        if (mods.Count == 0)
            list.Children.Add(Note(modKind == "Featured" && modSearch.Length == 0 ? "No mods are featured yet."
                : modSearch.Length > 0 || modKind != "All" ? "No shared mod matches." : "Nobody has shared a mod on this server yet."));
        stack.Children.Add(Panel(list));
        Put(stack);
    }

    static TextBlock FeaturedMark(JsonObject m, double size)
    {
        var note = OnlineService.Text(m["featured_note"]);
        return new TextBlock
        {
            Text = "★ FEATURED" + (note is { Length: > 0 } ? " · " + note : ""), FontSize = size, FontWeight = FontWeight.SemiBold,
            Foreground = Look.Brush(Look.Accent), TextWrapping = TextWrapping.Wrap,
        };
    }

    static string? PackLine(JsonObject m)
    {
        if (m["pack"] is not JsonObject pack) return null;
        var count = (int)(OnlineService.Num(pack["count"]) ?? 0);
        return $"Modpack of {count} mod{(count == 1 ? "" : "s")}" + (OnlineService.Bool(pack["rooms"]) ? " · required in rooms" : "");
    }

    static Border PackBox(JsonObject pack)
    {
        var count = (int)(OnlineService.Num(pack["count"]) ?? 0);
        var members = (pack["mods"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(Cell($"Modpack of {count} mod{(count == 1 ? "" : "s")}, loaded in this order", weight: FontWeight.SemiBold));
        var n = 0;
        foreach (var member in members)
        {
            n++;
            var missing = OnlineService.Bool(member["missing"]);
            var title = OnlineService.Text(member["name"]) ?? OnlineService.Text(member["id"]) ?? "Mod";
            var kinds = string.Join(", ", (member["kinds"] as JsonArray ?? []).Select(k => OnlineService.Text(k) ?? ""));
            var line = $"{n}. {title}" + (OnlineService.Text(member["version"]) is { Length: > 0 } v ? " " + v : "")
                + (missing ? " · missing from the pack" : kinds == "Packs" ? " · a pack inside a pack" : kinds.Length > 0 ? " · " + kinds : "");
            list.Children.Add(Cell(line, 13, dim: !missing));
        }
        if (count > members.Count) list.Children.Add(Cell($"and {count - members.Count} more", 12.5, true));
        list.Children.Add(Wrapped(Cell(OnlineService.Bool(pack["rooms"])
            ? "Required in rooms. Everyone in the room needs this exact version, and queues won't take it because it counts as a gameplay mod."
            : "Not required in rooms. Only the gameplay mods in it have to match online.", 12.5, true)));
        if (OnlineService.Num(pack["broken"]) is > 0)
            list.Children.Add(Wrapped(Cell("The game won't load this pack because one of its mods is missing or is a pack itself.", 12.5, weight: FontWeight.SemiBold)));
        list.Children.Add(Wrapped(Cell("You get the whole pack, and its mods turn on and off together.", 12.5, true)));
        return Panel(list);
    }

    static TextBlock Wrapped(TextBlock t)
    {
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        return t;
    }

    Control ModDetail(JsonObject m, ModListing? from)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(ModBar());
        var name = OnlineService.Text(m["name"]) ?? OnlineService.Text(m["id"]) ?? "Mod";
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { Look.Link(modPane == "community" ? "All mods" : "Back", Look.Folder, () => { modOpen = null; Show("mods"); }), Cell(name + " " + (OnlineService.Text(m["version"]) ?? ""), 22, weight: FontWeight.Bold) },
        });
        var facts = new List<string>();
        if (OnlineService.Text(m["author"]) is { } author) facts.Add("by " + author);
        if ((m["kinds"] as JsonArray)?.Select(k => OnlineService.Text(k)).OfType<string>().ToList() is { Count: > 0 } kinds) facts.Add(string.Join(", ", kinds));
        facts.Add($"{OnlineService.Num(m["files"]) ?? 0:0} files, {Size(OnlineService.Num(m["size"]) ?? 0)}");
        if (OnlineService.Num(m["likes"]) is { } likes) facts.Add($"{likes:0} likes");
        if (OnlineService.Num(m["downloads"]) is { } downloads) facts.Add($"{downloads:0} downloads");
        if (OnlineService.Text(m["at"]) is { } at && DateTime.TryParse(at, out var when)) facts.Add("shared " + OnlineService.Ago(when, hooks.Now()));
        if (OnlineService.Text(m["key"]) is { } key) facts.Add("key " + key);
        if (OnlineService.Bool(m["featured"])) stack.Children.Add(FeaturedMark(m, 13));
        stack.Children.Add(Cell(string.Join(" · ", facts), dim: true));
        if (m["pack"] is JsonObject pack) stack.Children.Add(PackBox(pack));
        var entry = OnlineService.Text(m["entry"]) ?? "";
        if (OnlineService.Bool(m["preview"]) && from != null && entry.Length > 0)
        {
            if (previews.TryGetValue(entry, out var picture))
            {
                if (picture != null)
                    stack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(10), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new Image { Source = picture, MaxWidth = 480, MaxHeight = 270, Stretch = Stretch.Uniform },
                    });
            }
            else
            {
                stack.Children.Add(Cell("Loading the picture...", 12, true));
                _ = LoadPreview(m, from, entry);
            }
        }
        if (OnlineService.Text(m["description"]) is { } about)
            stack.Children.Add(new TextBlock { Text = about, FontSize = 13.5, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap });
        var have = HaveOf(m);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (have == Have.Installed) buttons.Children.Add(Cell("Installed in your game.", weight: FontWeight.SemiBold));
        else if (from != null && entry.Length > 0 && hooks.Install != null)
        {
            var get = Look.Primary(getting.Contains(entry) ? "Downloading..." : have == Have.Other ? "Replace yours with this one" : "Get it", Look.Download, () => _ = GetMod(m, from));
            get.IsEnabled = !getting.Contains(entry);
            buttons.Children.Add(get);
        }
        if (OnlineService.Text(m["id"]) is { } modId && from?.Type == BifrostClient.Library && data.SignedIn)
        {
            var liked = OnlineService.Bool(m["liked"]);
            buttons.Children.Add(Look.Secondary(liked ? "Unlike" : "Like", Look.Check, () => _ = Like(modId, !liked)));
        }
        stack.Children.Add(buttons);
        if (from?.Type is BifrostClient.Library or BifrostClient.WorkshopShelf && OnlineService.Text(m["id"]) is { } listedId)
            stack.Children.Add(WorkshopBox(m, listedId));
        if (OnlineService.Text(m["id"]) is { } pickId && from?.Type == BifrostClient.Library && OnlineService.Bool(from.Body?["reviewer"]))
            stack.Children.Add(FeatureBox(m, pickId));
        stack.Children.Add(new TextBlock
        {
            Text = have == Have.Other
                ? "You have another version of this mod. Yours moves to the replaced mods on the Mods page, so nothing gets deleted."
                : "It goes in your mods folder and stays up to date, like mods from the game's Community tab. Mods load when the game starts.",
            FontSize = 12.5, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap,
        });
        return stack;
    }

    async Task GetMod(JsonObject m, ModListing from)
    {
        var entry = OnlineService.Text(m["entry"]) ?? "";
        if (hooks.Install == null || entry.Length == 0 || !getting.Add(entry)) return;
        var name = OnlineService.Text(m["name"]) ?? entry;
        said.Text = "Downloading " + name + "...";
        if (tab == "mods") Show("mods");
        try
        {
            var (bytes, problem) = await data.DownloadMod(from, entry);
            if (bytes == null && from.Type == BifrostClient.Library && await data.ModLibrary() is { Error: null } fresh)
            {
                library = fresh;
                if (modFrom == from) modFrom = fresh;
                (bytes, problem) = await data.DownloadMod(fresh, entry);
            }
            else if (bytes == null && from.Type == BifrostClient.ModKey && OnlineService.Text(m["key"]) is { } key && await data.ModByKey(key) is { Error: null } again)
            {
                if (modFrom == from) modFrom = again;
                (bytes, problem) = await data.DownloadMod(again, entry);
            }
            var why = bytes == null ? problem : hooks.Install(entry, bytes, OnlineService.Text(m["uploader"]) ?? "");
            said.Text = why == null ? name + " is in your mods folder. Restart the game to load it." : name + " wasn't installed: " + why + ".";
        }
        finally
        {
            getting.Remove(entry);
        }
        if (tab == "mods") Show("mods");
    }

    async Task LoadPreview(JsonObject m, ModListing from, string entry)
    {
        if (!previewing.Add(entry)) return;
        Avalonia.Media.Imaging.Bitmap? picture = null;
        try
        {
            if (await data.ModPreview(from, entry) is { } bytes)
                try
                {
                    picture = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(bytes));
                }
                catch (Exception)
                {
                    picture = null;
                }
        }
        finally
        {
            previewing.Remove(entry);
        }
        previews[entry] = picture;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (tab == "mods" && (modOpen == m || (modPane == "mine" && modOpen == null))) Show("mods");
        });
    }

    async Task Like(string modId, bool on)
    {
        var next = await data.SetMod(modId, on ? "like" : "unlike");
        if (next?.Error == null && next?.Type == BifrostClient.Library)
        {
            library = next;
            if (modFrom?.Type == BifrostClient.Library) modFrom = next;
            if (modOpen is { } open && OnlineService.Text(open["id"]) == modId)
                modOpen = (next.Body?["mods"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => OnlineService.Text(x["id"]) == modId) ?? open;
        }
        said.Text = next?.Message ?? next?.Error ?? (next == null ? "The server didn't answer." : on ? "Liked." : "Like removed.");
        Show("mods");
    }

    Control FeatureBox(JsonObject m, string modId)
    {
        var on = OnlineService.Bool(m["featured"]);
        var list = new StackPanel { Spacing = 6 };
        list.Children.Add(Label("Staff pick"));
        list.Children.Add(new TextBlock
        {
            Text = on
                ? $"Featured since {OnlineService.Text(m["featured_on"]) ?? "?"}, so it shows first on the Community tab. Feature it again to change its line or move it back to the top."
                : "Feature puts this mod first on the Community tab with your line under its name. Up to 12 mods can be featured at once.",
            FontSize = 12.5, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap,
        });
        var note = Field(OnlineService.Text(m["featured_note"]) ?? "", "optional line to show with it", 360);
        note.MaxLength = 120;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { note, Look.Secondary(on ? "Feature again" : "Feature", Look.Trophy, () => _ = Feature(modId, true, note.Text?.Trim() ?? "")) } };
        if (on) row.Children.Add(Look.Secondary("Take off Featured", Look.Close, () => _ = Feature(modId, false, "")));
        list.Children.Add(row);
        return Panel(list);
    }

    async Task Feature(string modId, bool on, string note)
    {
        var next = await data.FeatureMod(modId, on, note);
        if (next?.Error == null && next?.Type == BifrostClient.Library)
        {
            library = next;
            if (modFrom?.Type == BifrostClient.Library) modFrom = next;
            if (modOpen is { } open && OnlineService.Text(open["id"]) == modId)
                modOpen = (next.Body?["mods"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => OnlineService.Text(x["id"]) == modId) ?? open;
        }
        said.Text = next?.Message ?? next?.Error ?? (next == null ? "The server didn't answer." : on ? "Featured." : "Taken off Featured.");
        Show("mods");
    }

    async Task ByKey(string key)
    {
        said.Text = "Looking for " + key + "...";
        var got = await data.ModByKey(key);
        if (got?.Body?["mod"] is JsonObject mod)
        {
            said.Text = "";
            modPane = "community";
            modOpen = mod;
            modFrom = got;
            Put(ModDetail(mod, got));
            return;
        }
        said.Text = got?.Error ?? "The server didn't answer.";
    }

    ModListing? modFrom;

    async Task MyUploads(int mine)
    {
        var got = await data.MyMods();
        if (mine != version) return;
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(ModBar());
        if (got?.Message is { } msg) said.Text = msg;
        if (got == null || got.Error != null)
        {
            stack.Children.Add(Note(got?.Error ?? "The server didn't answer."));
            Put(stack);
            return;
        }
        var b = got.Body!;
        stack.Children.Add(Cell($"{OnlineService.Num(b["used"]) ?? 0:0} of {OnlineService.Num(b["max"]) ?? 0:0} mods, {Size(OnlineService.Num(b["bytes"]) ?? 0)} of {Size(OnlineService.Num(b["max_bytes"]) ?? 0)} on this server.", 12.5, true));
        var stats = b["stats"]?["mods"] as JsonObject;
        if (UploadTotals(b["stats"] as JsonObject) is { } totals) stack.Children.Add(Cell(totals, 13, weight: FontWeight.SemiBold));
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Head("*,100,140,220", "MOD", "KEY", "SHOWN TO", ""));
        var rows = (b["mods"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var counted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in rows)
        {
            var pid = OnlineService.Text(m["pid"]) ?? "";
            var vis = OnlineService.Text(m["visibility"]) ?? "private";
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (vis is "private" or "rejected") buttons.Children.Add(Look.Link("Make public", Look.Eye, () => _ = SetUpload(pid, "public")));
            if (vis is "public" or "review") buttons.Children.Add(Look.Link("Make private", Look.Close, () => _ = SetUpload(pid, "private")));
            buttons.Children.Add(Look.Link("Delete", Look.Trash, () => { if (Confirmed("delete" + pid, "Click Delete again to take it off the server.")) _ = SetUpload(pid, "delete"); }));
            var title = new StackPanel { Spacing = 1, Children = { Cell($"{OnlineService.Text(m["name"]) ?? "Mod"} {OnlineService.Text(m["version"]) ?? ""}", weight: FontWeight.SemiBold) } };
            if (OnlineService.Text(m["review_note"]) is { } note) title.Children.Add(Cell("Review: " + note, 12, true));
            if (OnlineService.Text(m["id"]) is { } statId && counted.Add(statId) && stats?[statId] is JsonObject one)
                title.Children.Add(Wrapped(Cell(UploadLine(one, OnlineService.Num(b["stats"]?["of"]) ?? 0), 12, true)));
            list.Children.Add(new Border
            {
                Padding = new Thickness(8, 4),
                Child = Row("*,100,140,220", title, Cell(OnlineService.Text(m["key"]) ?? "", dim: true),
                    Cell(vis switch
                    {
                        "public" => OnlineService.Bool(b["stats"]?["mods"]?[OnlineService.Text(m["id"]) ?? ""]?["featured"]) ? "everyone, ★ featured" : "everyone",
                        "review" => "waiting for review", "rejected" => "refused", _ => "anyone with the key",
                    }, dim: true), buttons),
            });
        }
        if (rows.Count == 0) list.Children.Add(Note("You haven't put a mod on this server yet."));
        stack.Children.Add(Panel(list));
        if (hooks.LocalMods?.Invoke() is { Count: > 0 } local) stack.Children.Add(SendList(local, rows));
        if (OnlineService.Bool(b["reviewer"])) stack.Children.Add(ReviewQueue((b["review"] as JsonArray ?? []).OfType<JsonObject>().ToList(), got));
        Put(stack);
    }

    static double Week(JsonNode? days) => (days as JsonArray ?? []).TakeLast(7).Sum(d => OnlineService.Num(d) ?? 0);

    static string Count(double n, string one, string many) => $"{n:#,0} {(n == 1 ? one : many)}";

    static string UploadLine(JsonObject one, double of)
    {
        var week = Week(one["got_days"]);
        var place = OnlineService.Num(one["place"]) ?? 0;
        return Count(OnlineService.Num(one["downloads"]) ?? 0, "download", "downloads") + (week > 0 ? $" (+{week:#,0} this week)" : "")
            + " · " + Count(OnlineService.Num(one["likes"]) ?? 0, "like", "likes")
            + (place > 0 ? $" · #{place:0} of {of:0} on the Community tab" : "");
    }

    static string? UploadTotals(JsonObject? stats)
    {
        if (stats?["mods"] is not JsonObject mods || mods.Count == 0) return null;
        var all = mods.Select(p => p.Value).OfType<JsonObject>().ToList();
        var downloads = all.Sum(o => OnlineService.Num(o["downloads"]) ?? 0);
        var week = all.Sum(o => Week(o["got_days"]));
        var likes = all.Sum(o => OnlineService.Num(o["likes"]) ?? 0);
        var line = "Your mods: " + Count(downloads, "download", "downloads") + (week > 0 ? $" (+{week:#,0} this week)" : "") + ", " + Count(likes, "like", "likes");
        if (OnlineService.Num(stats["followers"]) is { } followers) line += " · " + (followers == 1 ? "1 player follows you" : $"{followers:#,0} players follow you");
        return line + ".";
    }

    Control ReviewQueue(List<JsonObject> queue, ModListing from)
    {
        var list = new StackPanel { Spacing = 8 };
        list.Children.Add(Label($"Waiting for review ({queue.Count})"));
        list.Children.Add(new TextBlock
        {
            Text = "Approve puts a mod on the Community tab for everyone. Reject keeps it off and sends the uploader your note.",
            FontSize = 12.5, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap,
        });
        foreach (var m in queue)
        {
            var pid = OnlineService.Text(m["pid"]) ?? "";
            var entry = OnlineService.Text(m["entry"]) ?? "";
            var card = new StackPanel { Spacing = 4 };
            card.Children.Add(Cell($"{OnlineService.Text(m["name"]) ?? "Mod"} {OnlineService.Text(m["version"]) ?? ""}", 15, weight: FontWeight.SemiBold));
            var facts = new List<string> { "from " + (OnlineService.Text(m["owner_name"]) ?? "?") };
            if ((m["kinds"] as JsonArray)?.Select(k => OnlineService.Text(k)).OfType<string>().ToList() is { Count: > 0 } kinds) facts.Add(string.Join(", ", kinds));
            facts.Add($"{OnlineService.Num(m["files"]) ?? 0:0} files, {Size(OnlineService.Num(m["size"]) ?? 0)}");
            if (OnlineService.Text(m["at"]) is { } at && DateTime.TryParse(at, out var when)) facts.Add("sent " + OnlineService.Ago(when, hooks.Now()));
            card.Children.Add(Cell(string.Join(" · ", facts), 12.5, true));
            if (m["pack"] is JsonObject queuedPack) card.Children.Add(PackBox(queuedPack));
            if (OnlineService.Text(m["description"]) is { } about)
                card.Children.Add(new TextBlock { Text = about, FontSize = 13, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap });
            if (OnlineService.Bool(m["preview"]) && entry.Length > 0)
            {
                if (previews.TryGetValue(entry, out var picture))
                {
                    if (picture != null) card.Children.Add(new Image { Source = picture, MaxWidth = 320, MaxHeight = 180, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
                }
                else _ = LoadPreview(m, from, entry);
            }
            if (m["file_list"] is JsonArray files && files.Count > 0)
                card.Children.Add(new TextBlock
                {
                    Text = "Files: " + string.Join(", ", files.Select(f => OnlineService.Text(f) ?? "")) + (OnlineService.Num(m["files"]) > files.Count ? ", ..." : ""),
                    FontSize = 12, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap,
                });
            if (OnlineService.Text(m["sha256"]) is { } sha) card.Children.Add(Cell("sha256 " + sha, 11, true));
            var note = Field("", "note for the uploader if you reject it", 360);
            card.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    note,
                    Look.Secondary("Approve", Look.Check, () => _ = Review(pid, true, note.Text?.Trim() ?? "")),
                    Look.Secondary("Reject", Look.Close, () => { if (Confirmed("reject" + pid, "Click Reject again to keep it off the Community tab. The uploader sees your note.")) _ = Review(pid, false, note.Text?.Trim() ?? ""); }),
                },
            });
            list.Children.Add(new Border { Background = Look.Brush(Look.Raised), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10), Child = card });
        }
        if (queue.Count == 0) list.Children.Add(Note("Nothing is waiting for review."));
        return Panel(list);
    }

    async Task Review(string pid, bool approve, string note)
    {
        var next = await data.ReviewMod(pid, approve, note);
        said.Text = next?.Message ?? next?.Error ?? (next == null ? "The server didn't answer." : approve ? "Approved." : "Rejected.");
        library = null;
        if (tab == "mods") Show("mods");
    }

    Control SendList(IReadOnlyList<ModInfo> local, List<JsonObject> onServer)
    {
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Label("Send a mod from your game"));
        list.Children.Add(new TextBlock
        {
            Text = "Only send mods you made or are allowed to share. Send keeps it private to people with its key, and Send for review asks to put it on the Community tab.",
            FontSize = 12.5, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        list.Children.Add(Head("*,170,240", "MOD", "ON THE SERVER", ""));
        foreach (var mod in local.Where(m => m.InPack == null))
        {
            var copy = mod.Identity == null ? null : onServer.FirstOrDefault(r => OnlineService.Text(r["entry"]) == mod.Identity);
            var older = copy == null && mod.Id != null && onServer.Any(r => OnlineService.Text(r["id"]) == mod.Id);
            var where = copy != null
                ? (OnlineService.Text(copy["visibility"]) switch { "public" => "yes, for everyone", "review" => "yes, waiting for review", "rejected" => "yes, refused", _ => "yes, with its key" })
                : older ? "another version" : "";
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (mod.Problem != null) buttons.Children.Add(Cell(mod.Problem, 12, true));
            else if (sending.Contains(mod.Path)) buttons.Children.Add(Cell("Sending...", 12, true));
            else if (copy == null)
            {
                buttons.Children.Add(Look.Link("Send", Look.Download, () => _ = SendMod(mod, false)));
                buttons.Children.Add(Look.Link("Send for review", Look.Eye, () => _ = SendMod(mod, true)));
            }
            var title = new StackPanel { Spacing = 1, Children = { Cell($"{mod.Name} {mod.Version}", weight: FontWeight.SemiBold), Cell(mod.Folder, 12, true) } };
            list.Children.Add(new Border { Padding = new Thickness(8, 4), Child = Row("*,170,240", title, Cell(where, dim: true), buttons) });
        }
        return Panel(list);
    }

    async Task SendMod(ModInfo mod, bool makePublic)
    {
        if (!sending.Add(mod.Path)) return;
        said.Text = "Packing " + mod.Name + "...";
        if (tab == "mods" && modPane == "mine") Show("mods");
        try
        {
            var up = await Task.Run(() => ModPackage.ForUpload(mod.Path));
            if (up.Problem != null || up.Entry == null || up.Package == null)
            {
                said.Text = mod.Name + " wasn't sent: " + (up.Problem ?? "it couldn't be packed") + ".";
                return;
            }
            said.Text = "Asking the server to take " + mod.Name + "...";
            var answer = await data.ShareMod(up.Entry, makePublic);
            if (answer?.Upload is not { } request || !request.Entries.Contains(up.Entry))
            {
                said.Text = answer?.Mine?.Message ?? answer?.Mine?.Error
                    ?? "The server didn't take " + mod.Name + ". It may already have this version, or you sent it in the last two minutes.";
                return;
            }
            said.Text = $"Sending {mod.Name} ({Size(up.Package.Length)})...";
            var why = await data.UploadMod(request, up.Entry, up.Package);
            var left = up.LeftOut.Count > 0 ? " Skipped names the server doesn't allow: " + string.Join(", ", up.LeftOut.Take(5)) + (up.LeftOut.Count > 5 ? ", ..." : "") + "." : "";
            said.Text = why != null
                ? mod.Name + " wasn't sent: " + why + "."
                : (makePublic ? mod.Name + " is on the server. It shows on the Community tab once a reviewer approves it." : mod.Name + " is on the server. Share its key below so people can get it.") + left;
        }
        finally
        {
            sending.Remove(mod.Path);
            if (tab == "mods" && modPane == "mine") Show("mods");
        }
    }

    async Task SetUpload(string pid, string action)
    {
        var next = await data.SetMod(pid, action);
        said.Text = next?.Message ?? next?.Error ?? (next == null ? "The server didn't answer." : "Done.");
        Show("mods");
    }
}
