using System.Collections;
using System.Globalization;
using System.Text;

namespace BifrostStudio.Bmod;

public readonly record struct PyFloat(double Value);

public static class BmodJson
{
    public static OrderedDictionary<string, object?> Map() => new(StringComparer.Ordinal);

    public static SortedDictionary<string, object?> Sorted() => new(StringComparer.Ordinal);

    public static string Write(object? v, string indent = "")
    {
        var inner = indent + "  ";
        switch (v)
        {
            case null:
                return "null";
            case string s:
                return Str(s);
            case bool b:
                return b ? "true" : "false";
            case PyFloat f:
                return Float(f.Value);
            case int or long or short or byte:
                return Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case double d:
                return Number(d);
            case IDictionary map:
                if (map.Count == 0) return "{}";
                var parts = new List<string>();
                foreach (DictionaryEntry e in map) parts.Add(inner + Str(Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "") + ": " + Write(e.Value, inner));
                return "{\n" + string.Join(",\n", parts) + "\n" + indent + "}";
            case IEnumerable list:
                var items = new List<string>();
                foreach (var item in list) items.Add(inner + Write(item, inner));
                return items.Count == 0 ? "[]" : "[\n" + string.Join(",\n", items) + "\n" + indent + "]";
            default:
                return Str(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
        }
    }

    public static string Number(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsInfinity(d)) return d > 0 ? "Infinity" : "-Infinity";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string Float(double x)
    {
        var k = BmodBounds.PyRound(x * 10000);
        if (k == 0) return x < 0 ? "-0.0" : "0.0";
        var neg = k < 0;
        if (neg) k = -k;
        var whole = Math.Floor(k / 10000);
        var frac = Number(k - whole * 10000);
        while (frac.Length < 4) frac = "0" + frac;
        while (frac.Length > 1 && frac[^1] == '0') frac = frac[..^1];
        return (neg ? "-" : "") + Number(whole) + "." + frac;
    }

    public static string Str(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
