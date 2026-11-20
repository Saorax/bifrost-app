using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BifrostStudio.Net;

namespace BifrostStudio;

public sealed partial class OnlinePage
{
    ModListing? shelf;
    string? collectionOpen, creatorOpen;
    bool editingCollection, makingCollection;
    string collectionPick = "";

    void ResetShelf()
    {
        shelf = null;
        collectionOpen = creatorOpen = null;
        editingCollection = makingCollection = false;
    }

    static List<JsonObject> Objects(JsonNode? n) => (n as JsonArray ?? []).OfType<JsonObject>().ToList();

    List<JsonObject> MyCollections() => Objects(shelf?.Body?["collections"]).Where(c => OnlineService.Bool(c["mine"])).ToList();

    JsonObject? Listed(string id) =>
        Objects(shelf?.Body?["rows"]).Concat(Objects(shelf?.Body?["news"])).Concat(Objects(shelf?.Body?["creator"]?["mods"]))
            .Concat(Objects(library?.Body?["mods"])).FirstOrDefault(r => OnlineService.Text(r["id"]) == id);

    async Task<ModListing?> AskShelf(string action, JsonObject? args = null)
    {
        var next = await data.Workshop(action, args ?? []);
        if (next != null && next.Error == null) shelf = next;
        return next;
    }

    async Task WorkshopPane(int mine)
    {
        var got = shelf;
        var fresh = got == null || (creatorOpen != null && OnlineService.Text(got.Body?["creator"]?["tag"]) != creatorOpen);
        if (fresh)
            got = await AskShelf(creatorOpen != null ? "creator" : "list", creatorOpen != null ? new JsonObject { ["tag"] = creatorOpen } : null);
        if (mine != version) return;
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(ModBar());
        if (got == null || got.Error != null)
        {
            stack.Children.Add(Note(got?.Error ?? "The server didn't answer."));
            Put(stack);
            return;
        }
        if (fresh && got.Message is { } msg) said.Text = msg;
        if (creatorOpen != null) stack.Children.Add(CreatorView(got));
        else if (modPane == "following") stack.Children.Add(FollowingView(got));
        else if (collectionOpen != null && Objects(got.Body?["collections"]).FirstOrDefault(c => OnlineService.Text(c["id"]) == collectionOpen) is { } open)
            stack.Children.Add(CollectionView(open, got));
        else stack.Children.Add(CollectionsView(got));
        Put(stack);
    }

    static string Ago(string? at, DateTime now) => at is { } a && DateTime.TryParse(a, out var when) ? OnlineService.Ago(when, now) : "";

    Control CollectionsView(ModListing got)
    {
        collectionOpen = null;
        var list = new StackPanel { Spacing = 6 };
        list.Children.Add(new TextBlock
        {
            Text = "Collections are lists of Community mods that players on this server make, like \"Best Bödvar skins\". Yours come first.",
            FontSize = 12.5, Foreground = Look.Brush(Look.Dim), TextWrapping = TextWrapping.Wrap,
        });
        if (OnlineService.Bool(got.Body?["signed_in"]))
            list.Children.Add(makingCollection ? CollectionForm(null) : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { Look.Secondary("New collection", Look.Plus, () => { makingCollection = true; Show("mods"); }) },
            });
        else list.Children.Add(Cell("Sign in to make your own collections.", 12.5, true));
        var all = Objects(got.Body?["collections"]);
        list.Children.Add(Head("*,90,130", "COLLECTION", "MODS", "UPDATED"));
        foreach (var c in all)
        {
            var id = OnlineService.Text(c["id"]) ?? "";
            var title = OnlineService.Text(c["title"]) ?? "Collection";
            var by = OnlineService.Bool(c["mine"]) ? "yours" : "by " + (OnlineService.Text(c["by"]) is { Length: > 0 } b ? b : "a player");
            var who = new StackPanel { Spacing = 1, Children = { Cell(title, weight: FontWeight.SemiBold), Cell(by + (OnlineService.Text(c["note"]) is { Length: > 0 } n ? " · " + n.Split('\n')[0] : ""), 12, true) } };
            var row = Row("*,90,130", who, Cell($"{(c["mods"] as JsonArray)?.Count ?? 0}", dim: true), Cell(Ago(OnlineService.Text(c["at"]), hooks.Now()), dim: true));
            list.Children.Add(Clickable(row, () => { collectionOpen = id; editingCollection = false; Show("mods"); }));
        }
        if (all.Count == 0) list.Children.Add(Note("Nobody has made a collection on this server yet."));
        return Panel(list);
    }

    Control CollectionForm(JsonObject? c)
    {
        var form = new StackPanel { Spacing = 6 };
        var title = Field(OnlineService.Text(c?["title"]) ?? "", "collection name like Best Bödvar skins", 340);
        title.MaxLength = 60;
        var note = Field(OnlineService.Text(c?["note"]) ?? "", "optional description", 520);
        note.MaxLength = 300;
        note.AcceptsReturn = true;
        note.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(title);
        form.Children.Add(note);
        var mods = (c?["mods"] as JsonArray ?? []).Select(m => OnlineService.Text(m) ?? "").ToList();
        form.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                Look.Primary(c == null ? "Make it" : "Save", Look.Check, () => _ = SaveCollection(OnlineService.Text(c?["id"]) ?? "", title.Text?.Trim() ?? "", note.Text?.Trim() ?? "", mods)),
                Look.Secondary("Cancel", Look.Close, () => { makingCollection = editingCollection = false; Show("mods"); }),
            },
        });
        return form;
    }

    async Task SaveCollection(string id, string title, string note, List<string> mods)
    {
        var args = new JsonObject { ["title"] = title, ["note"] = note, ["mods"] = new JsonArray(mods.Select(m => (JsonNode)m).ToArray()) };
        if (id.Length > 0) args["id"] = id;
        var next = await AskShelf("save", args);
        said.Text = next?.Message ?? next?.Error ?? (next == null ? "The server didn't answer." : "");
        if (OnlineService.Text(next?.Body?["saved"]) is { } saved)
        {
            makingCollection = editingCollection = false;
            collectionOpen = saved;
        }
        Show("mods");
    }

    Control CollectionView(JsonObject c, ModListing got)
    {
        var stack = new StackPanel { Spacing = 8 };
        var id = OnlineService.Text(c["id"]) ?? "";
        var title = OnlineService.Text(c["title"]) ?? "Collection";
        var mine = OnlineService.Bool(c["mine"]);
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { Look.Link("All collections", Look.Tag, () => { collectionOpen = null; editingCollection = false; Show("mods"); }), Cell(title, 22, weight: FontWeight.Bold) },
        });
        var ids = (c["mods"] as JsonArray ?? []).Select(m => OnlineService.Text(m) ?? "").Where(m => m.Length > 0).ToList();
        var facts = new List<string> { mine ? "yours" : "by " + (OnlineService.Text(c["by"]) is { Length: > 0 } b ? b : "a player"), ids.Count == 1 ? "1 mod" : $"{ids.Count} mods" };
        if (Ago(OnlineService.Text(c["at"]), hooks.Now()) is { Length: > 0 } ago) facts.Add("updated " + ago);
        stack.Children.Add(Cell(string.Join(" · ", facts), dim: true));
        if (mine && editingCollection) stack.Children.Add(Panel(CollectionForm(c)));
        else if (OnlineService.Text(c["note"]) is { Length: > 0 } note)
            stack.Children.Add(new TextBlock { Text = note, FontSize = 13.5, Foreground = Look.Brush(Look.Text), TextWrapping = TextWrapping.Wrap });
        if (mine && !editingCollection)
            stack.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    Look.Secondary("Rename or describe", Look.Wrench, () => { editingCollection = true; Show("mods"); }),
                    Look.Secondary("Delete collection", Look.Trash, () => { if (Confirmed("dropcollection" + id, "Click Delete collection again to delete it for everyone. The mods stay on the server.")) _ = ShelfAction("delete", new JsonObject { ["id"] = id }, true); }),
                },
            });
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Head(mine ? "*,140,70,110" : "*,140,70", mine ? ["MOD", "KIND", "LIKES", ""] : ["MOD", "KIND", "LIKES"]));
        foreach (var modId in ids)
        {
            var row = Listed(modId);
            if (row == null)
            {
                var gone = new List<Control> { Cell(modId + " · not on the Community tab any more", 13, true), Cell(""), Cell("") };
                if (mine) gone.Add(Look.Link("Take out", Look.Close, () => _ = ShelfAction("remove", new JsonObject { ["id"] = id, ["mod"] = modId })));
                list.Children.Add(new Border { Padding = new Thickness(8, 6), Child = Row(mine ? "*,140,70,110" : "*,140,70", gone.ToArray()) });
                continue;
            }
            var name = (OnlineService.Text(row["name"]) ?? modId) + " " + (OnlineService.Text(row["version"]) ?? "");
            var who = new StackPanel { Spacing = 1, Children = { Cell(name, weight: FontWeight.SemiBold), Cell(OnlineService.Text(row["author"]) is { } a ? "by " + a : "", 12, true) } };
            var kinds = Cell(string.Join(", ", (row["kinds"] as JsonArray ?? []).Select(k => OnlineService.Text(k) ?? "")), dim: true);
            var likes = Cell($"♡ {OnlineService.Num(row["likes"]) ?? 0:0}", dim: true);
            void Open() { modOpen = row; modFrom = got; Show("mods"); }
            list.Children.Add(mine
                ? new Border { Padding = new Thickness(0, 0, 8, 0), Child = Row("*,140,70,110", Clickable(who, Open), kinds, likes, Look.Link("Take out", Look.Close, () => _ = ShelfAction("remove", new JsonObject { ["id"] = id, ["mod"] = modId }))) }
                : Clickable(Row("*,140,70", who, kinds, likes), Open));
        }
        if (ids.Count == 0)
            list.Children.Add(Note(mine ? "Empty for now. Open a mod on the Community tab and click Add to a collection." : "This collection is empty."));
        stack.Children.Add(Panel(list));
        return stack;
    }

    async Task ShelfAction(string action, JsonObject args, bool closeCollection = false)
    {
        var next = await AskShelf(action, args);
        said.Text = next?.Message ?? next?.Error ?? (next == null ? "The server didn't answer." : "");
        if (closeCollection && next?.Error == null) collectionOpen = null;
        Show("mods");
    }

    Control WorkshopBox(JsonObject m, string modId)
    {
        var box = new StackPanel { Spacing = 6 };
        if (OnlineService.Text(m["uploader"]) is { Length: > 0 } tag)
            box.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { Look.Link("More from " + (OnlineService.Text(m["author"]) is { Length: > 0 } a ? a : "this creator"), Look.Eye, () => { modPane = "following"; modOpen = null; creatorOpen = tag; Show("mods"); }) },
            });
        if (!data.SignedIn) return box;
        if (shelf == null)
        {
            box.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { Look.Secondary("Add to a collection", Look.Plus, () => _ = LoadShelfThenShow()) },
            });
            return box;
        }
        var mine = MyCollections();
        var choices = mine.Select(c => (Id: OnlineService.Text(c["id"]) ?? "", Title: OnlineService.Text(c["title"]) ?? "Collection",
            Has: (c["mods"] as JsonArray ?? []).Any(x => OnlineService.Text(x) == modId))).ToList();
        var holding = choices.Where(c => c.Has).Select(c => c.Title).ToList();
        if (holding.Count > 0) box.Children.Add(Cell("In your collection" + (holding.Count == 1 ? " " : "s ") + string.Join(", ", holding) + ".", 12.5, true));
        var open = choices.Where(c => !c.Has).ToList();
        var names = open.Select(c => c.Title).Append("A new collection...").ToList();
        var pick = Math.Max(0, open.FindIndex(c => c.Id == collectionPick));
        var combo = Combo(names, pick, i => collectionPick = i < open.Count ? open[i].Id : "");
        var name = Field("", "new collection's name", 240);
        name.MaxLength = 60;
        name.IsVisible = open.Count == 0 || pick >= open.Count;
        combo.SelectionChanged += (_, _) => name.IsVisible = combo.SelectedIndex >= open.Count;
        box.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                Cell("Add to", dim: true), combo, name,
                Look.Secondary("Add", Look.Plus, () =>
                {
                    var i = combo.SelectedIndex;
                    if (i >= 0 && i < open.Count) _ = AddToCollection(open[i].Id, "", modId);
                    else if (name.Text?.Trim() is { Length: > 0 } t) _ = AddToCollection("", t, modId);
                    else said.Text = "Give the new collection a name first.";
                }),
            },
        });
        return box;
    }

    async Task LoadShelfThenShow()
    {
        var next = await AskShelf("list");
        if (next == null || next.Error != null) said.Text = next?.Error ?? "The server didn't answer.";
        Show("mods");
    }

    async Task AddToCollection(string id, string newTitle, string modId)
    {
        var next = id.Length > 0
            ? await AskShelf("add", new JsonObject { ["id"] = id, ["mod"] = modId })
            : await AskShelf("save", new JsonObject { ["title"] = newTitle, ["note"] = "", ["mods"] = new JsonArray(modId) });
        said.Text = next?.Message ?? next?.Error ?? (next == null ? "The server didn't answer." : "");
        if (OnlineService.Text(next?.Body?["saved"]) is { } saved) collectionPick = saved;
        Show("mods");
    }

    Control FollowingView(ModListing got)
    {
        var stack = new StackPanel { Spacing = 10 };
        var signedIn = OnlineService.Bool(got.Body?["signed_in"]);
        var news = Objects(got.Body?["news"]);
        var newsList = new StackPanel { Spacing = 2 };
        newsList.Children.Add(Label("New from creators you follow"));
        if (!signedIn) newsList.Children.Add(Note("Sign in to follow creators. Their new mods show up here."));
        else
        {
            foreach (var r in news)
            {
                var name = (OnlineService.Text(r["name"]) ?? OnlineService.Text(r["id"]) ?? "Mod") + " " + (OnlineService.Text(r["version"]) ?? "");
                var who = new StackPanel { Spacing = 1, Children = { Cell(name, weight: FontWeight.SemiBold), Cell(OnlineService.Text(r["author"]) is { } a ? "by " + a : "", 12, true) } };
                var row = Row("*,140,130", who, Cell(string.Join(", ", (r["kinds"] as JsonArray ?? []).Select(k => OnlineService.Text(k) ?? "")), dim: true),
                    Cell(Ago(OnlineService.Text(r["since"]), hooks.Now()), dim: true));
                newsList.Children.Add(Clickable(row, () => { modOpen = r; modFrom = got; Show("mods"); }));
            }
            if (news.Count > 0)
                newsList.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0),
                    Children = { Look.Secondary("Mark all as seen", Look.Check, () => _ = ShelfAction("seen", [])) },
                });
            else newsList.Children.Add(Note("Nothing new since you last looked."));
        }
        stack.Children.Add(Panel(newsList));
        if (!signedIn) return stack;
        var follows = Objects(got.Body?["follows"]);
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Label("Creators you follow"));
        list.Children.Add(Head("*,90,90,110", "CREATOR", "MODS", "NEW", ""));
        foreach (var f in follows)
        {
            var tag = OnlineService.Text(f["tag"]) ?? "";
            var row = Row("*,90,90,110", Clickable(Cell(OnlineService.Text(f["name"]) ?? "A creator", weight: FontWeight.SemiBold), () => { creatorOpen = tag; Show("mods"); }),
                Cell($"{OnlineService.Num(f["mods"]) ?? 0:0}", dim: true), Cell(OnlineService.Num(f["news"]) is > 0 and var n ? $"{n:0} new" : "", dim: true),
                Look.Link("Unfollow", Look.Close, () => _ = ShelfAction("unfollow", new JsonObject { ["tag"] = tag })));
            list.Children.Add(new Border { Padding = new Thickness(0, 0, 8, 0), Child = row });
        }
        if (follows.Count == 0) list.Children.Add(Note("You don't follow anyone yet. Open a mod, click More from its creator, then Follow."));
        stack.Children.Add(Panel(list));
        return stack;
    }

    Control CreatorView(ModListing got)
    {
        var stack = new StackPanel { Spacing = 8 };
        var back = Look.Link(modPane == "following" ? "Following" : "Back", Look.Eye, () => { creatorOpen = null; Show("mods"); });
        if (got.Body?["creator"] is not JsonObject c)
        {
            stack.Children.Add(back);
            stack.Children.Add(Note("This creator has no mods on the Community tab."));
            return stack;
        }
        var tag = OnlineService.Text(c["tag"]) ?? "";
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { back, Cell(OnlineService.Text(c["name"]) ?? "A creator", 22, weight: FontWeight.Bold) } });
        var mods = Objects(c["mods"]);
        var followers = OnlineService.Num(c["followers"]) ?? 0;
        stack.Children.Add(Cell(string.Join(" · ", new[]
        {
            mods.Count == 1 ? "1 mod" : $"{mods.Count} mods", $"{OnlineService.Num(c["downloads"]) ?? 0:0} downloads", $"{OnlineService.Num(c["likes"]) ?? 0:0} likes",
            followers == 1 ? "1 follower" : $"{followers:0} followers",
        }), dim: true));
        if (OnlineService.Bool(c["you"])) stack.Children.Add(Cell("This is you. Players who follow you see your new mods under Following.", 12.5, true));
        else if (data.SignedIn)
        {
            var following = OnlineService.Bool(c["following"]);
            stack.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { following
                    ? Look.Secondary("Unfollow", Look.Close, () => _ = ShelfAction("unfollow", new JsonObject { ["tag"] = tag }))
                    : Look.Primary("Follow", Look.Plus, () => _ = ShelfAction("follow", new JsonObject { ["tag"] = tag })) },
            });
        }
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Head("*,140,70,80", "MOD", "KIND", "LIKES", "SIZE"));
        foreach (var m in mods)
        {
            var who = new StackPanel { Spacing = 1, Children = { Cell((OnlineService.Text(m["name"]) ?? "Mod") + " " + (OnlineService.Text(m["version"]) ?? ""), weight: FontWeight.SemiBold) } };
            if (OnlineService.Bool(m["featured"])) who.Children.Insert(0, FeaturedMark(m, 11.5));
            var row = Row("*,140,70,80", who, Cell(string.Join(", ", (m["kinds"] as JsonArray ?? []).Select(k => OnlineService.Text(k) ?? "")), dim: true),
                Cell($"♡ {OnlineService.Num(m["likes"]) ?? 0:0}", dim: true), Cell(Size(OnlineService.Num(m["size"]) ?? 0), dim: true));
            list.Children.Add(Clickable(row, () => { modOpen = m; modFrom = got; Show("mods"); }));
        }
        stack.Children.Add(Panel(list));
        var lists = Objects(c["lists"]);
        if (lists.Count > 0)
        {
            var inLists = new StackPanel { Spacing = 2 };
            inLists.Children.Add(Label("In collections"));
            foreach (var l in lists)
            {
                var id = OnlineService.Text(l["id"]) ?? "";
                inLists.Children.Add(Clickable(Cell((OnlineService.Text(l["title"]) ?? "Collection") + "  ·  by " + (OnlineService.Text(l["by"]) is { Length: > 0 } b ? b : "a player"), weight: FontWeight.SemiBold),
                    () => { modPane = "collections"; creatorOpen = null; collectionOpen = id; Show("mods"); }));
            }
            stack.Children.Add(Panel(inLists));
        }
        return stack;
    }
}
