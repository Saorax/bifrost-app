using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BifrostStudio;

public sealed record Setup(
    string Legend, string Name, string? Skin, string? Colour, string? Weapon1, string? Weapon2, string? Sidekick, string? Companion,
    string? Emitter, int? Stance, IReadOnlyList<string> Tags)
{
    public const int MaxName = 24;
    public const string Prefix = "BHSETUP1:";
    const string B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    public JsonObject GameEntry()
    {
        var o = new JsonObject();
        if (Skin != null) o["skin"] = Skin;
        if (Colour != null) o["colour"] = Colour;
        if (Weapon1 != null) o["weapon1"] = Weapon1;
        if (Weapon2 != null) o["weapon2"] = Weapon2;
        if (Sidekick != null) o["sidekick"] = Sidekick;
        if (Companion != null) o["companion"] = Companion;
        if (Emitter != null) o["emitter"] = Emitter;
        if (Stance != null) o["stance"] = Stance;
        o["name"] = Name;
        return o;
    }

    public static Setup FromGame(string legend, JsonObject o, IReadOnlyList<string>? tags = null) => new(
        legend, Clip(Str(o["name"]) ?? "SETUP"), Str(o["skin"]), Str(o["colour"]), Str(o["weapon1"]), Str(o["weapon2"]), Str(o["sidekick"]),
        Str(o["companion"]), Str(o["emitter"]), o["stance"] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : null, tags ?? []);

    public string Code()
    {
        var o = GameEntry();
        o["legend"] = Legend;
        var bytes = Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var sb = new StringBuilder(Prefix);
        for (var i = 0; i < bytes.Length; i += 3)
        {
            var a = bytes[i];
            var b = i + 1 < bytes.Length ? bytes[i + 1] : 0;
            var c = i + 2 < bytes.Length ? bytes[i + 2] : 0;
            var n = a << 16 | b << 8 | c;
            sb.Append(B64[n >> 18 & 63]).Append(B64[n >> 12 & 63]);
            if (i + 1 < bytes.Length) sb.Append(B64[n >> 6 & 63]);
            if (i + 2 < bytes.Length) sb.Append(B64[n & 63]);
        }
        return sb.ToString();
    }

    public static Setup? Decode(string? text)
    {
        if (text == null) return null;
        var t = string.Concat(text.Where(ch => !char.IsWhiteSpace(ch)));
        var at = t.IndexOf(Prefix, StringComparison.Ordinal);
        if (at < 0) return null;
        t = t[(at + Prefix.Length)..];
        var bytes = new List<byte>();
        int bits = 0, held = 0;
        foreach (var ch in t)
        {
            var v = B64.IndexOf(ch);
            if (v < 0) return null;
            held = (held << 6 | v) & 0xFFFF;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(held >> bits & 255));
            }
        }
        try
        {
            if (JsonNode.Parse(Encoding.UTF8.GetString([.. bytes])) is not JsonObject o || Str(o["legend"]) is not { Length: > 0 } legend) return null;
            return FromGame(legend, o);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IEnumerable<string> CodesIn(string text)
    {
        var at = 0;
        while ((at = text.IndexOf(Prefix, at, StringComparison.Ordinal)) >= 0)
        {
            var end = at + Prefix.Length;
            while (end < text.Length && B64.Contains(text[end])) end++;
            yield return text[at..end];
            at = end;
        }
    }

    public static string Clip(string name) => name.Length <= MaxName ? name : name[..MaxName];

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

public sealed class SetupLibrary(AppState state, string? file = null)
{
    public static string DefaultFile => BifrostFolders.StudioFile("setups.json");

    readonly GameSettings settings = new(state);
    public string File => file ?? DefaultFile;

    public List<Setup> Load()
    {
        var list = new List<Setup>();
        foreach (var item in JsonFiles.Read(File)?["setups"] as JsonArray ?? [])
        {
            if (item is not JsonObject o || o["legend"] is not JsonValue lv || !lv.TryGetValue<string>(out var legend) || o["setup"] is not JsonObject s) continue;
            var tags = (o["tags"] as JsonArray ?? []).OfType<JsonValue>().Select(t => t.TryGetValue<string>(out var x) ? x : "").Where(x => x.Length > 0).ToList();
            list.Add(Setup.FromGame(legend, s, tags));
        }
        return list;
    }

    public void Save(IEnumerable<Setup> setups)
    {
        var arr = new JsonArray();
        foreach (var s in setups)
            arr.Add(new JsonObject { ["legend"] = s.Legend, ["tags"] = new JsonArray(s.Tags.Select(t => (JsonNode)t).ToArray()), ["setup"] = s.GameEntry() });
        JsonFiles.Write(File, new JsonObject { ["version"] = 1, ["setups"] = arr });
    }

    public (int Added, int Skipped) Import(string text)
    {
        var list = Load();
        int added = 0, skipped = 0;
        foreach (var code in Setup.CodesIn(text))
        {
            if (Setup.Decode(code) is not { } s) { skipped++; continue; }
            if (list.Any(x => x.Code() == s.Code())) { skipped++; continue; }
            list.Add(s);
            added++;
        }
        if (added > 0) Save(list);
        return (added, skipped);
    }

    public static string Export(IEnumerable<Setup> setups) =>
        string.Join("\n", setups.Select(s => $"# {s.Legend}: {s.Name}\n{s.Code()}")) + "\n";

    public Dictionary<string, List<Setup>> InGame()
    {
        var all = new Dictionary<string, List<Setup>>();
        if (settings.Mods()["legendSetups"] is not JsonObject game) return all;
        foreach (var (legend, node) in game)
            all[legend] = (node as JsonArray ?? []).OfType<JsonObject>().Select(o => Setup.FromGame(legend, o)).ToList();
        return all;
    }

    public bool? SendToGame(Setup s)
    {
        var current = InGame().TryGetValue(s.Legend, out var l) ? l : [];
        if (current.Any(x => x.Code() == s.Code())) return true;
        var arr = new JsonArray(current.Select(x => (JsonNode)x.GameEntry()).Append(s.GameEntry()).ToArray());
        return settings.Change(new JsonObject { ["legendSetups"] = new JsonObject { [s.Legend] = arr } });
    }

    public bool? RemoveFromGame(Setup s)
    {
        var current = InGame().TryGetValue(s.Legend, out var l) ? l : [];
        var kept = current.Where(x => x.Code() != s.Code()).ToList();
        if (kept.Count == current.Count) return true;
        return settings.Change(new JsonObject
        {
            ["legendSetups"] = new JsonObject { [s.Legend] = kept.Count == 0 ? null : new JsonArray(kept.Select(x => (JsonNode)x.GameEntry()).ToArray()) },
        });
    }
}
