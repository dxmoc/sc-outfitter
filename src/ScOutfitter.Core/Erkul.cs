using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScOutfitter.Core;

/// <summary>One slot override of an erkul build: what the player put into a port.</summary>
public sealed record ErkulOverride(string PortPath, string Kind, string ClassName);

/// <summary>A build as erkul.games stores it: ship class name plus per-port overrides. Unlisted ports keep stock.</summary>
public sealed class ErkulBuild
{
    public required string Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public required string ShipClass { get; init; }
    public string GameVersion { get; init; } = string.Empty;
    public bool GimbalLocked { get; init; }
    public List<ErkulOverride> Overrides { get; init; } = [];

    public static ErkulBuild FromJson(string id, JsonNode root)
    {
        JsonNode payload = root["payload"] ?? root;
        string ship = payload.Str("ship");
        if (ship.Length == 0)
        {
            throw new LookupException("This erkul build has no ship.");
        }

        var build = new ErkulBuild
        {
            Id = id,
            Name = root.Str("name"),
            ShipClass = ship,
            GameVersion = payload.Str("v"),
            GimbalLocked = GimbalMode(payload.Str("gimbalMode")),
        };
        foreach (KeyValuePair<string, JsonNode?> kv in payload["slotOverrides"] as JsonObject ?? [])
        {
            string kind = kv.Value.Str("kind");
            string cls = kv.Value.Str("className");
            if (kind.Length > 0)
            {
                build.Overrides.Add(new ErkulOverride(kv.Key, kind, cls));
            }
        }

        return build;
    }

    private static bool GimbalMode(string mode) => mode.Equals("lock", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Fetches shared and published builds from erkul.games.</summary>
/// <remarks>
/// Share links look like https://erkul.games/s/abc12345, published ones like
/// https://erkul.games/browse?q=@abc12345. Both endpoints answer with raw-deflate compressed JSON.
/// </remarks>
public sealed partial class ErkulClient
{
    public const string Api = "https://api.erkul.games";

    private static readonly HttpClient Http = CreateHttp();

    [GeneratedRegex(@"(?:/s/|q=@|^@?)([a-z0-9]{6,16})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex IdPattern();

    /// <summary>The build id out of a pasted link or a bare id; null when nothing looks like one.</summary>
    public static string? ParseId(string input)
    {
        string s = input.Trim().TrimEnd('/');
        Match m = IdPattern().Match(s);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public async Task<ErkulBuild> FetchAsync(string linkOrId, CancellationToken ct = default)
    {
        string id = ParseId(linkOrId) ?? throw new LookupException("That does not look like an erkul.games build link (expected erkul.games/s/... or browse?q=@...).");
        foreach (string url in new[] { $"{Api}/shares/{id}", $"{Api}/browse/{id}" })
        {
            using HttpResponseMessage response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            response.EnsureSuccessStatusCode();
            byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return ErkulBuild.FromJson(id, Decode(body));
        }

        throw new LookupException($"erkul.games knows no build '{id}'.");
    }

    /// <summary>Raw-deflate bytes to JSON. Falls back to plain JSON should the format ever change.</summary>
    public static JsonNode Decode(byte[] body)
    {
        try
        {
            using var input = new MemoryStream(body);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(deflate);
            return JsonNode.Parse(reader.ReadToEnd()) ?? throw new InvalidDataException("empty erkul payload");
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            return JsonNode.Parse(System.Text.Encoding.UTF8.GetString(body)) ?? throw new InvalidDataException("empty erkul payload");
        }
    }

    public static byte[] Encode(string json)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new StreamWriter(deflate))
        {
            writer.Write(json);
        }

        return output.ToArray();
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("sc-outfitter", "1.0"));
        // the API answers 403 unless the request looks like it comes from the site itself
        http.DefaultRequestHeaders.Add("Origin", "https://erkul.games");
        http.DefaultRequestHeaders.Referrer = new Uri("https://erkul.games/");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return http;
    }
}

/// <summary>Turns an erkul build into picks on the wiki hull: stock parts stay, overrides become purchases.</summary>
public static class ErkulImporter
{
    public sealed record Result(List<Pick> Picks, List<string> Unknown);

    public static Result ToPicks(ErkulBuild build, Ship ship, IReadOnlyDictionary<Kind, List<Component>> catalog)
    {
        Dictionary<string, Component> byClass = catalog.Values.SelectMany(c => c)
            .Where(c => c.ClassName.Length > 0)
            .GroupBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Component> byName = catalog.Values.SelectMany(c => c)
            .GroupBy(c => c.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var picks = new List<Pick>();
        var unknown = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Slot slot in ship.Slots)
        {
            List<ErkulOverride> mine = build.Overrides
                .Where(o => o.PortPath.Equals(slot.Port, StringComparison.OrdinalIgnoreCase)
                            || o.PortPath.StartsWith(slot.Port + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (ErkulOverride o in mine)
            {
                used.Add(o.PortPath);
            }

            Component? stock = slot.Equipped is not null && byName.TryGetValue(slot.Equipped, out Component? s) ? s : null;

            if (slot.Kind == Kind.MissileRack)
            {
                ErkulOverride? rackOverride = mine.FirstOrDefault(o => o.Kind == "rack");
                Component? rack = Resolve(rackOverride, byClass, unknown) ?? stock;
                if (rack is not null)
                {
                    bool keep = rackOverride is null || rack == stock;
                    picks.Add(new Pick { Slot = slot, Component = rack, Keep = keep, Stock = slot.Equipped, Fixed = slot.Fixed });
                }

                foreach (IGrouping<string, ErkulOverride> tubes in mine.Where(o => o.Kind == "missile").GroupBy(o => o.ClassName, StringComparer.OrdinalIgnoreCase))
                {
                    Component? missile = Resolve(tubes.First(), byClass, unknown);
                    if (missile is not null)
                    {
                        bool keep = missile.Name == slot.EquippedMissile;
                        picks.Add(new Pick { Slot = slot, Component = missile, Keep = keep, Quantity = tubes.Count(), Stock = slot.EquippedMissile });
                    }
                }

                continue;
            }

            ErkulOverride? part = mine.FirstOrDefault(o => o.Kind is "weapon" or "shield" or "powerplant" or "cooler" or "radar" or "quantum" or "quantumdrive");
            if (part is null)
            {
                if (stock is not null)
                {
                    picks.Add(new Pick { Slot = slot, Component = stock, Keep = true, Stock = slot.Equipped, Fixed = slot.Fixed });
                }

                continue;
            }

            Component? comp = Resolve(part, byClass, unknown);
            if (comp is null)
            {
                continue;
            }

            picks.Add(new Pick { Slot = slot, Component = comp, Keep = comp == stock, Stock = slot.Equipped });
        }

        foreach (ErkulOverride o in build.Overrides.Where(o => o.ClassName.Length > 0 && !used.Contains(o.PortPath)))
        {
            unknown.Add($"{o.PortPath}: {o.ClassName}");
        }

        return new Result(picks, unknown.Distinct(StringComparer.Ordinal).ToList());
    }

    private static Component? Resolve(ErkulOverride? o, Dictionary<string, Component> byClass, List<string> unknown)
    {
        if (o is null || o.ClassName.Length == 0)
        {
            return null;
        }

        if (byClass.TryGetValue(o.ClassName, out Component? c))
        {
            return c;
        }

        unknown.Add(o.ClassName);
        return null;
    }
}
