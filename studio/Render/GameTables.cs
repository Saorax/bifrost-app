using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BifrostStudio.Render;

public sealed class GameTables
{
    static readonly object gate = new();
    static string? cachedFor;
    static GameTables? cached;

    static readonly Regex Declaration = new(@"^\s*<\?xml\b[^>]*>\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Element = new(@"<([A-Za-z_][\w.-]*)", RegexOptions.CultureInvariant);
    static readonly Regex Comments = new(@"<!--[\s\S]*?-->|<!DOCTYPE[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly Dictionary<string, string> texts;
    readonly HashSet<string> xmlNames;
    readonly Dictionary<string, XElement?> parsed = new(StringComparer.Ordinal);

    GameTables(Dictionary<string, string> texts, HashSet<string> xmlNames)
    {
        this.texts = texts;
        this.xmlNames = xmlNames;
    }

    public IReadOnlyCollection<string> Names => texts.Keys;

    public bool Has(string name) => texts.ContainsKey(name);

    public string? RawText(string name) => texts.GetValueOrDefault(name);

    public XElement? RawXml(string name)
    {
        if (!xmlNames.Contains(name)) return null;
        lock (parsed)
        {
            if (parsed.TryGetValue(name, out var root)) return root;
            try { root = XDocument.Parse(texts[name]).Root; }
            catch (System.Xml.XmlException) { root = null; }
            parsed[name] = root;
            return root;
        }
    }

    static string Stamp(string game) =>
        string.Join("|", Swz.Archives.Select(a => Path.Combine(game, a + ".swz")).Append(Path.Combine(game, "BrawlhallaAir.swf")).Where(File.Exists)
            .Select(f => f + ":" + File.GetLastWriteTimeUtc(f).Ticks + ":" + new FileInfo(f).Length));

    public static GameTables Of(string game)
    {
        var stamp = Stamp(game);
        lock (gate)
        {
            if (cachedFor == stamp && cached != null) return cached;
        }
        var swf = Path.Combine(game, "BrawlhallaAir.swf");
        if (!File.Exists(swf)) throw new SwzException("BrawlhallaAir.swf isn't in the game folder");
        var archives = Swz.Archives.Select(a => Path.Combine(game, a + ".swz")).Where(File.Exists).Select(File.ReadAllBytes).ToList();
        if (archives.Count == 0) throw new SwzException("the game's .swz archives aren't in the game folder");
        var key = Swz.FindKey(File.ReadAllBytes(swf), archives);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var xml = new HashSet<string>(StringComparer.Ordinal);
        foreach (var archive in archives)
        {
            foreach (var entry in Swz.Read(archive, key))
            {
                var text = Encoding.UTF8.GetString(entry).TrimStart('﻿');
                if (Name(text) is not { } named) continue;
                if (texts.TryAdd(named.Name, text) && named.Xml) xml.Add(named.Name);
            }
        }
        var tables = new GameTables(texts, xml);
        lock (gate)
        {
            cachedFor = stamp;
            cached = tables;
        }
        return tables;
    }

    public static (string Name, bool Xml)? Name(string text)
    {
        var body = text;
        var declared = Declaration.Match(body);
        if (declared.Success) body = body[declared.Length..];
        if (!body.StartsWith('<'))
        {
            var newline = body.IndexOf('\n');
            var first = (newline < 0 ? body : body[..newline]).Trim();
            return first.Length > 0 ? (first, false) : null;
        }
        var head = body.Length > 400 ? body[..400] : body;
        if (head.Contains("<LevelDesc", StringComparison.Ordinal) || head.Contains("<CutsceneType", StringComparison.Ordinal)) return null;
        return Element.Match(Comments.Replace(head, "")) is { Success: true } e ? (e.Groups[1].Value, true) : null;
    }
}

public static class CsvTable
{
    public static List<string> Records(string text)
    {
        var lines = text.Split('\n');
        if (!text.Contains('"')) return [.. lines];
        var output = new List<string>();
        string? open = null;
        foreach (var line in lines)
        {
            var odd = line.Contains('"') && (line.Count(c => c == '"') & 1) == 1;
            if (open != null)
            {
                open += "\n" + line;
                if (odd)
                {
                    output.Add(open);
                    open = null;
                }
            }
            else if (odd) open = line;
            else output.Add(line);
        }
        if (open != null) output.Add(open);
        return output;
    }

    public static List<string> Split(string line)
    {
        if (!line.Contains('"')) return [.. line.Split(',')];
        var output = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                field.Append(line, start, i - start);
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else quoted = !quoted;
                start = i + 1;
            }
            else if (line[i] == ',' && !quoted)
            {
                output.Add(field.Append(line, start, i - start).ToString());
                field.Clear();
                start = i + 1;
            }
        }
        output.Add(field.Append(line, start, line.Length - start).ToString());
        return output;
    }
}
