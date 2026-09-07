using System.Globalization;
using System.Text.Json.Nodes;

namespace ScOutfitter.Core;

/// <summary>Null-tolerant readers for the loosely shaped wiki/UEX JSON.</summary>
public static class J
{
    public static JsonNode? At(this JsonNode? node, params string[] path)
    {
        JsonNode? cur = node;
        foreach (string key in path)
        {
            if (cur is not JsonObject obj || !obj.TryGetPropertyValue(key, out cur))
            {
                return null;
            }
        }

        return cur;
    }

    public static string Str(this JsonNode? node, params string[] path)
    {
        JsonNode? v = node.At(path);
        return v is JsonValue val && val.TryGetValue(out string? s) ? s : v?.ToString() ?? string.Empty;
    }

    public static string? StrOrNull(this JsonNode? node, params string[] path)
    {
        JsonNode? v = node.At(path);
        return v is null ? null : v.Str();
    }

    public static double Num(this JsonNode? node, params string[] path)
    {
        JsonNode? v = node.At(path);
        if (v is not JsonValue val)
        {
            return 0;
        }

        // JsonElement-backed values (parsed text) convert freely; values built in code (tests)
        // only answer to their exact CLR type, hence the ladder
        if (val.TryGetValue(out double d))
        {
            return d;
        }

        if (val.TryGetValue(out long l))
        {
            return l;
        }

        if (val.TryGetValue(out int i))
        {
            return i;
        }

        if (val.TryGetValue(out float f))
        {
            return f;
        }

        if (val.TryGetValue(out decimal m))
        {
            return (double)m;
        }

        if (val.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return 0;
    }

    public static int Int(this JsonNode? node, params string[] path) => (int)Math.Round(node.Num(path));

    public static bool Bool(this JsonNode? node, params string[] path)
    {
        JsonNode? v = node.At(path);
        if (v is not JsonValue val)
        {
            return false;
        }

        if (val.TryGetValue(out bool b))
        {
            return b;
        }

        return val.TryGetValue(out long l) ? l != 0 : false;
    }

    /// <summary>true when the property exists and is not false/0/null.</summary>
    public static bool BoolOr(this JsonNode? node, bool fallback, params string[] path) =>
        node.At(path) is null ? fallback : node.Bool(path);

    public static IEnumerable<JsonNode> Arr(this JsonNode? node, params string[] path)
    {
        if (node.At(path) is JsonArray arr)
        {
            foreach (JsonNode? item in arr)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }

    public static HashSet<string> StrSet(this JsonNode? node, params string[] path) =>
        node.Arr(path).Select(x => x.Str()).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
}
