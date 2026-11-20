using System.Text.Json.Nodes;

namespace BifrostStudio;

public sealed record StatsAttack(string Weapon, string Name, double Damage, int Uses, int Hits, int Kos);

public sealed record StatsPlayer(
    string Name, string Legend, int Team, double Dealt, double Taken, int Kos, int Falls, int Sds, int Dodges, int AirDodges,
    int Dashes, int Jumps, int AirMs, int GroundMs, int WallMs, int Clashes, int Combos, double ComboDamage, int BestHits,
    double BestDamage, IReadOnlyList<double> Damage, IReadOnlyList<StatsAttack> Attacks)
{
    public IReadOnlyList<int>? DodgeGrid { get; init; }
    public IReadOnlyList<int>? FallTimes { get; init; }
    public string Hero { get; init; } = "";
}

public sealed record MatchStats(int LengthMs, int BucketMs, bool Teams, string Mode, string Saved, IReadOnlyList<StatsPlayer> Players)
{
    public bool CombosKnown { get; init; } = true;

    public IReadOnlySet<string> Unknown { get; init; } = new HashSet<string>();

    public static MatchStats? Read(string path) => Parse(JsonFiles.Read(path));

    public sealed record Combo(int Attacker, int Victim, int Hits, double Damage, uint Start);

    public static List<Combo> Combos(ReplayFrames frames)
    {
        var found = new List<Combo>();
        for (var v = 0; v < frames.Players.Count; v++)
        {
            float? last = null;
            var hits = 0;
            var damage = 0.0;
            var by = new Dictionary<int, int>();
            uint start = 0;
            var held = true;
            var rising = false;
            void Close()
            {
                if (hits >= 2 && by.Count > 0) found.Add(new Combo(by.OrderByDescending(b => b.Value).First().Key, v, hits, damage, start));
                hits = 0;
                damage = 0;
                by.Clear();
            }
            for (var s = 0; s < frames.Rows.Length; s++)
            {
                if (v >= frames.Rows[s].Length || frames.Rows[s][v] is not { } f) continue;
                if (f.KnockedOut)
                {
                    Close();
                    last = null;
                    continue;
                }
                if (last is { } before && f.Damage > before + 0.001f)
                {
                    if (!rising)
                    {
                        if (hits == 0 || !held) { Close(); start = frames.Times[s]; }
                        hits++;
                        if (f.HitBy >= 0 && f.HitBy != v) by[f.HitBy] = by.GetValueOrDefault(f.HitBy) + 1;
                    }
                    damage += f.Damage - before;
                    held = true;
                    rising = true;
                }
                else
                {
                    rising = false;
                    if (hits > 0 && !f.Stunned) held = false;
                }
                if (last is { } prev && f.Damage < prev - 0.001f) Close();
                last = f.Damage;
            }
            Close();
        }
        return found;
    }

    public static MatchStats? FromFrames(ReplayFrames frames, string mode = "", bool teams = false)
    {
        if (frames.StatsJson == null || JsonNode.Parse(frames.StatsJson) is not JsonObject o || o["players"] is not JsonArray list) return null;
        var combos = Combos(frames);
        o["combosKnown"] = frames.Rows.Any(r => r.Any(x => x != null));
        foreach (var item in list)
        {
            if (item is not JsonObject p) continue;
            var i = Int(p["index"]);
            var fp = i >= 0 && i < frames.Players.Count ? frames.Players[i] : null;
            p["name"] = fp?.Name ?? "Player " + (i + 1);
            p["legend"] = fp?.Legend ?? "";
            p["hero"] = fp?.Hero ?? "";
            var mine = combos.Where(c => c.Attacker == i).ToList();
            p["combos"] = mine.Count;
            p["comboDamage"] = mine.Sum(c => c.Damage);
            if (mine.OrderByDescending(c => c.Hits).ThenByDescending(c => c.Damage).FirstOrDefault() is { } best)
                p["best"] = new JsonObject { ["hits"] = best.Hits, ["damage"] = Math.Round(best.Damage, 1) };
        }
        o["mode"] = mode;
        o["teams"] = teams;
        return Parse(o);
    }

    public static MatchStats? Parse(JsonNode? node)
    {
        if (node is not JsonObject o || o["players"] is not JsonArray list) return null;
        var players = new List<StatsPlayer>();
        foreach (var item in list)
        {
            if (item is not JsonObject p) continue;
            var attacks = new List<StatsAttack>();
            foreach (var a in p["attacks"] as JsonArray ?? [])
            {
                if (a is JsonObject x)
                    attacks.Add(new StatsAttack(Str(x["weapon"]), Str(x["name"]), Num(x["damage"]), Int(x["uses"]), Int(x["hits"]), Int(x["kos"])));
            }
            var best = p["best"] as JsonObject;
            players.Add(new StatsPlayer(Str(p["name"]), Str(p["legend"]), Int(p["team"]), Num(p["dealt"]), Num(p["taken"]), Int(p["kos"]),
                Int(p["falls"]), Int(p["sds"]), Int(p["dodges"]), Int(p["airDodges"]), Int(p["dashes"]), Int(p["jumps"]), Int(p["air"]),
                Int(p["ground"]), Int(p["wall"]), Int(p["clashes"]), Int(p["combos"]), Num(p["comboDamage"]), Int(best?["hits"]),
                Num(best?["damage"]), (p["damage"] as JsonArray ?? []).Select(Num).ToList(), attacks)
            {
                DodgeGrid = p["dodgeGrid"] is JsonArray g && g.Count == 9 ? g.Select(Int).ToList() : null,
                FallTimes = p["fallTimes"] is JsonArray ft ? ft.Select(Int).ToList() : null,
                Hero = Str(p["hero"]),
            });
        }
        return players.Count == 0 ? null : new MatchStats(Int(o["length"]), Math.Max(1, Int(o["bucket"])), GameSettings.Flag(o["teams"]) ?? false,
            Str(o["mode"]), Str(o["saved"]), players)
        {
            CombosKnown = GameSettings.Flag(o["combosKnown"]) ?? true,
            Unknown = (o["unknown"] as JsonArray ?? []).Select(Str).Where(s => s.Length > 0).ToHashSet(),
        };
    }

    public static int? Diverges(MatchStats stats, IReadOnlyList<(int Player, int Ms)> kos, int lengthMs)
    {
        if (stats.Players.Count == 0 || stats.Players.Any(p => p.FallTimes == null)) return null;
        int? first = null;
        for (var i = 0; i < stats.Players.Count; i++)
        {
            var recorded = kos.Where(k => k.Player == i && k.Ms < lengthMs - 20).Select(k => k.Ms).OrderBy(x => x).ToList();
            var played = stats.Players[i].FallTimes!.Where(t => t < lengthMs - 20).OrderBy(x => x).ToList();
            for (var n = 0; n < Math.Max(recorded.Count, played.Count); n++)
            {
                var a = n < recorded.Count ? recorded[n] : int.MaxValue;
                var b = n < played.Count ? played[n] : int.MaxValue;
                if (Math.Abs((long)a - b) <= 20) continue;
                var at = Math.Min(a, b);
                first = first is { } f ? Math.Min(f, at) : at;
                break;
            }
        }
        return first;
    }

    public static Dictionary<int, string> LearnLegends(ReplayEntry entry, MatchStats stats, Dictionary<int, string>? into = null)
    {
        into ??= [];
        foreach (var p in entry.Players)
        {
            if (p.Legends.Count != 1 || into.ContainsKey(p.Legends[0])) continue;
            var match = stats.Players.Where(s => s.Name == p.Name && s.Legend.Length > 0).ToList();
            if (match.Count == 1) into[p.Legends[0]] = match[0].Legend;
        }
        return into;
    }

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    static int Int(JsonNode? n) => GameSettings.Whole(n) ?? 0;

    static double Num(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : 0;
        return v.TryGetValue<System.Text.Json.JsonElement>(out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Number && e.TryGetDouble(out var x) ? x : 0;
    }
}

public sealed record CareerSummary(int Matches, int Wins, int Losses, IReadOnlyList<(int Legend, int Matches, int Wins)> Legends,
    IReadOnlyList<(string Map, int Matches, int Wins)> Maps)
{
    public IReadOnlyList<bool> Form { get; init; } = [];
    public IReadOnlyList<(DateTime Month, int Matches, int Wins)> Months { get; init; } = [];

    public static CareerSummary Of(IEnumerable<ReplayEntry> entries)
    {
        var mine = entries.Where(e => e.Ok && e.Me >= 0)
            .GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderBy(e => e.Folder == ReplayFolder.Favorites).First()).ToList();
        var decided = mine.Where(e => e.Result.Length > 0).OrderBy(e => e.Time).ToList();
        var form = decided.Skip(Math.Max(0, decided.Count - 20)).Select(e => e.Result == "win").ToList();
        var months = mine.GroupBy(e => new DateTime(e.Time.Year, e.Time.Month, 1)).OrderByDescending(g => g.Key)
            .Select(g => (g.Key, g.Count(), g.Count(e => e.Result == "win"))).ToList();
        return Build(mine) with { Form = form, Months = months };
    }

    static CareerSummary Build(List<ReplayEntry> mine)
    {
        var legends = mine.SelectMany(e => e.Players[e.Me].Legends.Distinct().Select(l => (l, e.Result == "win")))
            .GroupBy(x => x.l).Select(g => (g.Key, g.Count(), g.Count(x => x.Item2))).OrderByDescending(x => x.Item2).ThenBy(x => x.Key).ToList();
        var maps = mine.GroupBy(e => e.MapName.Length > 0 ? e.MapName : e.Map).Select(g => (g.Key, g.Count(), g.Count(e => e.Result == "win")))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
        return new CareerSummary(mine.Count, mine.Count(e => e.Result == "win"), mine.Count(e => e.Result == "loss"), legends, maps);
    }
}