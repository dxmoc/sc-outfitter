using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScOutfitter.Core;

public enum LocationKind
{
    Station,
    City,
    Outpost,
    Gateway,
    Body,
}

/// <summary>A place with a position: body, station, city, outpost or gateway.</summary>
public sealed record Location(string Name, string System, string Container, double X, double Y, double Z, LocationKind Kind)
{
    public double DistanceKm(Location other)
    {
        double dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>"Everus Harbor (Hurston)", or just the name for bodies and deep-space stations.</summary>
    public string Label => Name == Container || Container == System ? Name : $"{Name} ({Container})";

    public string Key => $"{System}/{Name}";
}

/// <summary>One quantum leg or jump-point transit of a cross-system path.</summary>
public sealed record Leg(Location From, Location To, bool IsJump);

/// <summary>
/// Positions of bodies, stations and outposts in Stanton, Pyro and Nyx (from scunpacked-data),
/// name lookup for UEX terminals and gateway paths between systems.
/// </summary>
public sealed partial class Starmap
{
    // names UEX uses that differ from the game data
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["green imperial housing exchange"] = "Grim HEX",
        ["grimhex"] = "Grim HEX",
        ["area 18"] = "Area18",
        ["port olisar"] = "Crusader",
        ["checkmate station"] = "Checkmate",
    };

    private static readonly string[] SystemOrder = ["Stanton", "Pyro", "Nyx"];

    private readonly Dictionary<string, List<Location>> _locations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Location>> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<(string From, string To), Location> _gateways = [];
    private readonly Dictionary<(string From, string To), Location> _gateExits = [];

    public Starmap(JsonNode raw)
    {
        foreach (KeyValuePair<string, JsonNode?> kv in raw["systems"] as JsonObject ?? [])
        {
            string system = kv.Key;
            var locs = new List<Location>();
            var idx = new Dictionary<string, Location>(StringComparer.Ordinal);
            foreach (JsonNode e in kv.Value.Arr())
            {
                string name = e.Str("name");
                string? parent = e.StrOrNull("parent");
                double[] pos = e.Arr("pos").Select(p => p.Num()).ToArray();
                var loc = new Location(name, system, string.IsNullOrEmpty(parent) ? name : parent,
                    pos.Length > 0 ? pos[0] : 0, pos.Length > 1 ? pos[1] : 0, pos.Length > 2 ? pos[2] : 0,
                    ParseKind(e.Str("kind")));
                locs.Add(loc);
                idx[Norm(name)] = loc;
            }

            _locations[system] = locs;
            _index[system] = idx;
        }

        foreach (JsonNode g in raw.Arr("gateways"))
        {
            string from = g.Str("from_system"), to = g.Str("to_system");
            if (_index.TryGetValue(from, out Dictionary<string, Location>? fi) && fi.TryGetValue(Norm(g.Str("from")), out Location? gate)
                && _index.TryGetValue(to, out Dictionary<string, Location>? ti) && ti.TryGetValue(Norm(g.Str("to")), out Location? exit))
            {
                _gateways[(from, to)] = gate;
                _gateExits[(from, to)] = exit;
            }
        }
    }

    /// <summary>The map bundled with the app (src/ScOutfitter.Core/Data/starmap.json).</summary>
    public static Starmap LoadEmbedded()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("starmap.json")
                              ?? throw new FileNotFoundException("embedded starmap.json missing");
        using var reader = new StreamReader(stream);
        return new Starmap(JsonNode.Parse(reader.ReadToEnd()) ?? throw new InvalidDataException("starmap.json is empty"));
    }

    private static LocationKind ParseKind(string kind) => kind switch
    {
        "station" => LocationKind.Station,
        "city" => LocationKind.City,
        "outpost" => LocationKind.Outpost,
        "gateway" => LocationKind.Gateway,
        _ => LocationKind.Body,
    };

    [GeneratedRegex(@"\s*\((stanton|pyro|nyx)( system)?\)\s*$")]
    private static partial Regex SystemSuffix();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    private static string Norm(string s) => NonAlnum().Replace(SystemSuffix().Replace(s.ToLowerInvariant(), ""), " ").Trim();

    // ---------- browsing ----------

    public IReadOnlyList<string> Systems() =>
        SystemOrder.Where(_locations.ContainsKey)
            .Concat(_locations.Keys.Where(s => !SystemOrder.Contains(s)).OrderBy(s => s, StringComparer.Ordinal)).ToList();

    public IReadOnlyList<Location> LocationsOf(string system) =>
        _locations.TryGetValue(system, out List<Location>? l) ? l : [];

    private string StarOf(string system) =>
        LocationsOf(system).FirstOrDefault(l => l.Kind == LocationKind.Body && l.Name == l.Container)?.Name ?? system;

    public static string DeepSpace(string star) => $"{star} (deep space)";

    /// <summary>Planets with their moons indented after them, plus the star for deep-space stations.</summary>
    public IReadOnlyList<string> Bodies(string system)
    {
        string star = StarOf(system);
        IReadOnlyList<Location> all = LocationsOf(system);
        var result = new List<string>();
        foreach (Location planet in all.Where(l => l.Kind == LocationKind.Body && l.Container == star && l.Name != star)
                     .OrderBy(l => l.Name, StringComparer.Ordinal))
        {
            result.Add(planet.Name);
            result.AddRange(all.Where(l => l.Kind == LocationKind.Body && l.Container == planet.Name)
                .Select(l => l.Name).OrderBy(n => n, StringComparer.Ordinal));
        }

        result.Add(DeepSpace(star));
        return result;
    }

    /// <summary>Stations/cities (optionally outposts) around a body, best-known first.</summary>
    public IReadOnlyList<string> Places(string system, string body, bool includeOutposts = false)
    {
        body = body.Replace(" (deep space)", "", StringComparison.Ordinal);
        return LocationsOf(system)
            .Where(l => l.Container == body && (l.Kind is LocationKind.Station or LocationKind.City or LocationKind.Gateway
                                                || (includeOutposts && l.Kind == LocationKind.Outpost)))
            .OrderBy(l => l.Kind switch { LocationKind.Station or LocationKind.City => 0, LocationKind.Gateway => 1, LocationKind.Outpost => 2, _ => 3 })
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .Select(l => l.Name).ToList();
    }

    // ---------- lookup ----------

    /// <summary>Find by name. "Pyro/Checkmate" pins the system; otherwise Stanton is searched first.</summary>
    public Location? Locate(string name, string? system = null)
    {
        if (system is null && name.Contains('/'))
        {
            int i = name.IndexOf('/');
            system = Capitalize(name[..i].Trim());
            name = name[(i + 1)..];
        }

        string n = Norm(name);
        if (Aliases.TryGetValue(n, out string? alias))
        {
            n = Norm(alias);
        }

        IEnumerable<string> order = system is not null ? [system] : Systems();
        foreach (string s in order)
        {
            if (_index.TryGetValue(s, out Dictionary<string, Location>? idx) && idx.TryGetValue(n, out Location? loc))
            {
                return loc;
            }
        }

        // "R&R HUR-L5 High Course Station" vs "HUR-L5 High Course Station"
        foreach (string s in order)
        {
            if (!_index.TryGetValue(s, out Dictionary<string, Location>? idx))
            {
                continue;
            }

            foreach ((string key, Location loc) in idx)
            {
                if (key.Length > 3 && n.Length > 0 && (n.Contains(key, StringComparison.Ordinal) || key.Contains(n, StringComparison.Ordinal)))
                {
                    return loc;
                }
            }
        }

        return null;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    /// <summary>Best position for a UEX terminal record, most specific name first.</summary>
    public Location? LocateTerminal(JsonNode term)
    {
        string sys = term.Str("star_system_name");
        string? system = sys.Length > 0 ? Capitalize(sys) : null;
        foreach (string field in new[] { "space_station_name", "city_name", "outpost_name", "moon_name", "planet_name", "orbit_name" })
        {
            string val = term.Str(field);
            if (val.Length > 0 && Locate(val, system) is { } loc)
            {
                return loc;
            }
        }

        return null;
    }

    // ---------- travel ----------

    /// <summary>Quantum legs from a to b; cross-system goes through the gateways.</summary>
    public IReadOnlyList<Leg> Path(Location a, Location b)
    {
        if (a.System == b.System)
        {
            return [new Leg(a, b, false)];
        }

        List<string>? chain = SystemChain(a.System, b.System);
        if (chain is null)
        {
            return [new Leg(a, b, false)];
        }

        var legs = new List<Leg>();
        Location cur = a;
        for (int i = 0; i + 1 < chain.Count; i++)
        {
            Location gate = _gateways[(chain[i], chain[i + 1])];
            Location arrive = _gateExits[(chain[i], chain[i + 1])];
            legs.Add(new Leg(cur, gate, false));
            legs.Add(new Leg(gate, arrive, true));
            cur = arrive;
        }

        legs.Add(new Leg(cur, b, false));
        return legs;
    }

    private List<string>? SystemChain(string start, string goal)
    {
        var frontier = new Queue<List<string>>();
        frontier.Enqueue([start]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        while (frontier.Count > 0)
        {
            List<string> chain = frontier.Dequeue();
            if (chain[^1] == goal)
            {
                return chain;
            }

            foreach ((string from, string to) in _gateways.Keys)
            {
                if (from == chain[^1] && seen.Add(to))
                {
                    frontier.Enqueue([.. chain, to]);
                }
            }
        }

        return null;
    }
}
