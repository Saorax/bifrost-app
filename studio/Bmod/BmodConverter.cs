using System.Text.Json;
using System.Text.Json.Nodes;
using BifrostStudio.Render;

namespace BifrostStudio.Bmod;

public enum BmodOutcome { Converted, Already, ForTheGame, Failed }

public sealed record BmodConvertResult(BmodOutcome Outcome, string File, string Message, string? Folder = null, string? Id = null,
    string? Kind = null, IReadOnlyList<string>? Lines = null);

public static class BmodConverter
{
    public const int Converter = 2;

    static string? SourceHashOf(string dir)
    {
        try
        {
            var m = Path.Combine(dir, "mod.json");
            if (!File.Exists(m)) return null;
            return JsonNode.Parse(File.ReadAllText(m).TrimStart('﻿'))?["source"]?["hash"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string? ImportedAs(string modsRoot, string hash)
    {
        if (!Directory.Exists(modsRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(modsRoot))
            if (SourceHashOf(dir) == hash) return dir;
        return null;
    }

    public static string AsideTarget(string asideRoot, string name)
    {
        var target = Path.Combine(asideRoot, name);
        for (var n = 2; Directory.Exists(target) || File.Exists(target); n++) target = Path.Combine(asideRoot, name + "-" + n);
        return target;
    }

    static List<string> FileNames(BmodFile mod) =>
        mod.Meta["files"] is JsonObject f ? f.Select(kv => kv.Value?.GetValueKind() == JsonValueKind.String ? kv.Value.GetValue<string>() : kv.Value?.ToJsonString() ?? "").ToList() : [];

    public static bool IsAvatars(BmodFile mod, IReadOnlyList<string> names) =>
        names.Any(n => n.StartsWith("a_AvatarIcon_", StringComparison.Ordinal)) ||
        (names.Count == 0 && FileNames(mod).Any(n => n.StartsWith("a_AvatarIcon_", StringComparison.Ordinal) || n.StartsWith("a_Flag", StringComparison.Ordinal)));

    public static bool IsMapArt(BmodFile mod, IReadOnlyList<string> names) => !IsAvatars(mod, names) && names.Count == 0 && FileNames(mod).Count > 0;

    static void CopyInto(string file, string folder)
    {
        var target = Path.Combine(folder, Path.GetFileName(file));
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) File.Copy(file, target, true);
    }

    public static BmodConvertResult Convert(string file, string modsRoot, string replacedAside, string game)
    {
        var name = Path.GetFileName(file);
        byte[] data;
        try { data = File.ReadAllBytes(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(BmodOutcome.Failed, name, "it couldn't be read (" + e.Message + ")");
        }
        var hash = BmodItemImport.HashOf(data);
        if (ImportedAs(modsRoot, hash) is { } already)
        {
            try { CopyInto(file, already); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return new(BmodOutcome.Already, name, name + " is already imported.", already, Path.GetFileName(already));
        }
        BmodFile mod;
        try { mod = BmodSwf.ReadBmod(data); }
        catch (Exception e) when (e is BmodException or InvalidDataException or IndexOutOfRangeException or ArgumentException or JsonException)
        {
            return new(BmodOutcome.Failed, name, "it isn't a .bmod this can read (" + e.Message + ")");
        }
        var names = BmodTables.SpriteNames(mod.Meta);
        if (names.Count == 0 && !IsAvatars(mod, names) && !IsMapArt(mod, names))
            return new(BmodOutcome.Failed, name, "it redraws no sprites and has no files. Only skins, weapon skins, map art and avatars can be imported");
        GameTables tables;
        try { tables = GameTables.Of(game); }
        catch (Exception e) when (e is SwzException or IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return new(BmodOutcome.ForTheGame, name, "the game's tables couldn't be read here (" + e.Message + "), so Brawlhalla converts it at its next start");
        }
        string? made = null;
        (string From, string To)? movedAside = null;
        try
        {
            var gameData = BmodGameData.Of(game, tables);
            IBmodMade imported;
            var reportAt = 0;
            if (IsAvatars(mod, names)) imported = BmodAvatarImport.Make(mod, name, gameData);
            else if (IsMapArt(mod, names))
            {
                var art = BmodMapArtImport.Make(mod, name, BmodLevelData.Of(game, tables));
                reportAt = art.MovedLineAt;
                imported = art;
            }
            else
            {
                var plan = BmodItemPlan.Make(mod, Path.GetFileNameWithoutExtension(name), names, gameData.Tables, game);
                var art = plan.Art(mod);
                IReadOnlyList<(string Bone, Matrix M)> pose = [];
                if (plan.IsCostume && plan.BodySuffix != null)
                {
                    try { pose = BmodHeadshot.Pose(game, BmodHeadshot.BoneNames(tables.RawXml("BoneTypes"))); }
                    catch (Exception e) when (e is not OutOfMemoryException) { pose = []; }
                }
                var icon = plan.IsCostume ? plan.SkinIcon(art, game, pose) : null;
                var swf = plan.BonesSwf(mod, art, icon);
                reportAt = plan.Report.Count;
                imported = BmodItemImport.Make(mod, name, plan, names, swf, icon?.Name, gameData);
            }
            Directory.CreateDirectory(modsRoot);
            var folder = Path.Combine(modsRoot, imported.ModId);
            if (Directory.Exists(folder))
            {
                Directory.CreateDirectory(replacedAside);
                var aside = AsideTarget(replacedAside, imported.ModId);
                Directory.Move(folder, aside);
                movedAside = (aside, folder);
                imported.Report.Insert(reportAt, "the earlier mods\\" + imported.ModId + " moved to " + aside);
            }
            Directory.CreateDirectory(folder);
            made = folder;
            imported.WriteTo(folder, BmodItemImport.Source(name, hash, data.Length, Converter));
            try { CopyInto(file, folder); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { imported.Report.Add("the .bmod wasn't copied into the mod: " + e.Message); }
            return new(BmodOutcome.Converted, name, imported.Message, folder, imported.ModId, imported.Kind, imported.Report);
        }
        catch (Exception e) when (e is InvalidDataException or BmodException or SwzException or IOException or UnauthorizedAccessException
            or IndexOutOfRangeException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            var lines = new List<string>();
            try
            {
                if (made != null && Directory.Exists(made))
                {
                    Directory.Delete(made, true);
                    lines.Add("the half-written mods\\" + Path.GetFileName(made) + " removed");
                }
                if (movedAside is { } m)
                {
                    Directory.Move(m.From, m.To);
                    lines.Add("the earlier mods\\" + Path.GetFileName(m.To) + " put back");
                }
            }
            catch (Exception u) when (u is IOException or UnauthorizedAccessException) { lines.Add("couldn't undo: " + u.Message); }
            return new(BmodOutcome.Failed, name, e.Message, Lines: lines);
        }
    }
}
