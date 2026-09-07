using System.Text.Json.Nodes;

namespace ScOutfitter.Core;

/// <summary>Travel model of one quantum drive.</summary>
public sealed record QuantumDrive(string Name, double SpeedMps, double AccelMps2, double SpoolS, double CooldownS, double FuelPerMetre)
{
    public const double CalibrationS = 5.0;

    public static QuantumDrive From(Component c)
    {
        double speed = c.Stat("speed");
        double accel = c.Stat("accel");
        return new QuantumDrive(c.Name, speed, accel > 0 ? accel : speed / 10, c.Stat("spool"), c.Stat("cooldown"), c.Stat("fuel_rate"));
    }

    /// <summary>(seconds, fuel units) for one quantum jump of the given length.</summary>
    public (double Seconds, double Fuel) Hop(double distanceM)
    {
        if (distanceM <= 0)
        {
            return (0, 0);
        }

        double ramp = SpeedMps * SpeedMps / (2 * AccelMps2); // distance to reach full speed
        double travel = distanceM >= 2 * ramp
            ? 2 * SpeedMps / AccelMps2 + (distanceM - 2 * ramp) / SpeedMps
            : 2 * Math.Sqrt(distanceM / AccelMps2);
        return (SpoolS + CalibrationS + travel + CooldownS, distanceM * FuelPerMetre);
    }
}

public sealed record Buy(string Item, string Shop, int Quantity, int Price);

public sealed class Stop
{
    public required Location Location { get; init; }
    public List<Buy> Buys { get; init; } = [];
    public double LegSeconds { get; init; }
    public double LegFuel { get; init; }
    public double LegKm { get; init; }
    /// <summary>System jumps on the way here.</summary>
    public int Jumps { get; init; }
    /// <summary>false: not in the coordinate data, listed without distance or order.</summary>
    public bool Planned { get; init; } = true;
    public string System => Location.System;
    public int Cost => Buys.Sum(b => b.Price);
}

public sealed class Trip
{
    public required Location Start { get; init; }
    public List<Stop> Stops { get; init; } = [];
    /// <summary>Items not sold anywhere at all.</summary>
    public List<string> Unavailable { get; init; } = [];

    public IEnumerable<Stop> Planned => Stops.Where(s => s.Planned);
    public IEnumerable<Stop> Extra => Stops.Where(s => !s.Planned);
    public double Seconds => Planned.Sum(s => s.LegSeconds);
    public double Fuel => Planned.Sum(s => s.LegFuel);
    public double Km => Planned.Sum(s => s.LegKm);
    public int Cost => Stops.Sum(s => s.Cost);
}

/// <summary>Turns a shopping list into the shortest shopping trip through Stanton, Pyro and Nyx.</summary>
public static class Router
{
    /// <summary>Fixed overhead per stop: approach after quantum exit, landing, walk to the shop, take-off.</summary>
    private static double Overhead(LocationKind kind) => kind switch
    {
        LocationKind.Station => 180,
        LocationKind.City => 420,
        LocationKind.Outpost => 240,
        LocationKind.Gateway => 180,
        _ => 300,
    };

    /// <summary>Line up, enter and traverse a jump point.</summary>
    public const double JumpSeconds = 120;

    private sealed record Option(Location Location, string Shop, int UnitPrice);

    public static string FormatDuration(double seconds)
    {
        int total = (int)Math.Round(seconds);
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}h {m:00}m" : $"{m}m {s:00}s";
    }

    /// <summary>
    /// Picks the set of shops and the visiting order that minimizes travel time (plus price, if
    /// <paramref name="auecPerMinute"/> is set). The search space is small (a dozen shop locations,
    /// a handful of items), so subsets up to <paramref name="maxStops"/> and all orders are enumerated.
    /// Items only sold at places missing from the map are appended as unordered extra stops.
    /// </summary>
    public static Trip Plan(IReadOnlyList<Pick> picks, IReadOnlyDictionary<int, JsonNode> terminals, Starmap starmap,
        Location start, QuantumDrive qd, double auecPerMinute = 0, int maxStops = 5)
    {
        // what to buy, and where each item is on offer
        var needed = new Dictionary<string, int>(StringComparer.Ordinal);
        var comps = new Dictionary<string, Component>(StringComparer.Ordinal);
        foreach (Pick p in picks.Where(p => !p.Keep))
        {
            needed[p.Component.Name] = needed.GetValueOrDefault(p.Component.Name) + p.Quantity;
            comps[p.Component.Name] = p.Component;
        }

        var mapped = new Dictionary<string, Dictionary<string, Option>>(StringComparer.Ordinal);   // item -> location key -> option
        var unmapped = new Dictionary<string, Dictionary<(string, string), (string Shop, int Price)>>(StringComparer.Ordinal);
        var unavailable = new List<string>();
        foreach ((string name, Component comp) in comps)
        {
            var perLoc = new Dictionary<string, Option>(StringComparer.Ordinal);
            var perPlace = new Dictionary<(string, string), (string, int)>();
            foreach (Offer o in comp.Offers)
            {
                if (!terminals.TryGetValue(o.TerminalId, out JsonNode? term))
                {
                    continue;
                }

                string shop = term.Str("name") is { Length: > 0 } tn ? tn : o.TerminalName;
                Location? loc = starmap.LocateTerminal(term);
                if (loc is not null)
                {
                    if (!perLoc.TryGetValue(loc.Key, out Option? cur) || o.Price < cur.UnitPrice)
                    {
                        perLoc[loc.Key] = new Option(loc, shop, o.Price);
                    }
                }
                else
                {
                    (string, string) key = (term.Str("star_system_name") is { Length: > 0 } s ? s : "?", TerminalPlace(term));
                    if (!perPlace.TryGetValue(key, out (string Shop, int Price) cur) || o.Price < cur.Price)
                    {
                        perPlace[key] = (shop, o.Price);
                    }
                }
            }

            if (perLoc.Count > 0)
            {
                mapped[name] = perLoc;
            }
            else if (perPlace.Count > 0)
            {
                unmapped[name] = perPlace;
            }
            else
            {
                unavailable.Add(name);
            }
        }

        var stops = new List<Stop>();
        var locations = new Dictionary<string, Location>(StringComparer.Ordinal);
        foreach (Dictionary<string, Option> perLoc in mapped.Values)
        {
            foreach ((string key, Option opt) in perLoc)
            {
                locations[key] = opt.Location;
            }
        }

        (double Seconds, double Fuel, double Km, int Jumps) LegTo(Location a, Location b)
        {
            double secs = 0, fuel = 0, km = 0;
            int jumps = 0;
            foreach (Leg leg in starmap.Path(a, b))
            {
                if (leg.IsJump)
                {
                    secs += JumpSeconds;
                    jumps++;
                    continue;
                }

                double m = leg.From.DistanceKm(leg.To) * 1000;
                (double s, double f) = qd.Hop(m);
                secs += s;
                fuel += f;
                km += m / 1000;
            }

            return (secs + Overhead(b.Kind), fuel, km, jumps);
        }

        List<string> names = locations.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        double bestObjective = double.PositiveInfinity;
        List<Stop>? bestRoute = null;

        for (int n = 1; n <= Math.Min(maxStops, names.Count); n++)
        {
            foreach (string[] subset in Combinations(names, n))
            {
                if (!mapped.Keys.All(item => subset.Any(l => mapped[item].ContainsKey(l))))
                {
                    continue;
                }

                var buys = subset.ToDictionary(l => l, _ => new List<Buy>(), StringComparer.Ordinal);
                int priceTotal = 0;
                foreach ((string item, Dictionary<string, Option> options) in mapped)
                {
                    string l = subset.Where(options.ContainsKey).MinBy(x => options[x].UnitPrice)!;
                    Option opt = options[l];
                    int qty = needed[item];
                    buys[l].Add(new Buy(item, opt.Shop, qty, opt.UnitPrice * qty));
                    priceTotal += opt.UnitPrice * qty;
                }

                if (buys.Values.Any(b => b.Count == 0))
                {
                    continue; // a smaller subset covers the same shops
                }

                foreach (string[] order in Permutations(subset))
                {
                    var route = new List<Stop>();
                    Location prev = start;
                    double totalS = 0;
                    foreach (string l in order)
                    {
                        Location loc = locations[l];
                        (double secs, double fuel, double km, int jumps) = LegTo(prev, loc);
                        route.Add(new Stop { Location = loc, Buys = buys[l], LegSeconds = secs, LegFuel = fuel, LegKm = km, Jumps = jumps });
                        totalS += secs;
                        prev = loc;
                    }

                    // price is converted into minutes when the user values their time
                    double objective = totalS + (auecPerMinute > 0 ? priceTotal / auecPerMinute * 60 : 0);
                    if (objective < bestObjective)
                    {
                        bestObjective = objective;
                        bestRoute = route;
                    }
                }
            }
        }

        if (bestRoute is not null)
        {
            stops.AddRange(bestRoute);
        }

        // items only sold at places missing from the coordinate data: cheapest place per item
        var extra = new Dictionary<(string, string), Stop>();
        foreach ((string item, Dictionary<(string, string), (string Shop, int Price)> places) in unmapped)
        {
            ((string system, string place), (string shop, int price)) = places.MinBy(kv => kv.Value.Price);
            if (!extra.TryGetValue((system, place), out Stop? stop))
            {
                stop = new Stop { Location = new Location(place, system, place, 0, 0, 0, LocationKind.Station), Planned = false };
                extra[(system, place)] = stop;
            }

            int qty = needed[item];
            stop.Buys.Add(new Buy(item, shop, qty, price * qty));
        }

        stops.AddRange(extra.Values);
        return new Trip { Start = start, Stops = stops, Unavailable = unavailable };
    }

    private static string TerminalPlace(JsonNode term)
    {
        foreach (string f in new[] { "space_station_name", "city_name", "outpost_name", "moon_name", "planet_name", "orbit_name" })
        {
            string v = term.Str(f);
            if (v.Length > 0)
            {
                return v;
            }
        }

        return term.Str("name") is { Length: > 0 } n ? n : "?";
    }

    public static IEnumerable<string[]> Combinations(IReadOnlyList<string> items, int k)
    {
        int n = items.Count;
        if (k > n)
        {
            yield break;
        }

        int[] idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return idx.Select(i => items[i]).ToArray();
            int pos = k - 1;
            while (pos >= 0 && idx[pos] == n - k + pos)
            {
                pos--;
            }

            if (pos < 0)
            {
                yield break;
            }

            idx[pos]++;
            for (int j = pos + 1; j < k; j++)
            {
                idx[j] = idx[j - 1] + 1;
            }
        }
    }

    public static IEnumerable<string[]> Permutations(string[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (int i = 0; i < items.Length; i++)
        {
            string[] rest = items.Where((_, j) => j != i).ToArray();
            foreach (string[] tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }
}
