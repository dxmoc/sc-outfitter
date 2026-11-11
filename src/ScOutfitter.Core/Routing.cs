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

        string[] names = locations.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        string[] items = mapped.Keys.ToArray();
        int count = names.Length;
        // offers[item][location]: what the item costs there, null when that place does not sell it
        Option?[][] offers = items.Select(item => names.Select(l => mapped[item].GetValueOrDefault(l)).ToArray()).ToArray();
        int[] quantity = items.Select(item => needed[item]).ToArray();

        // seconds of every leg, worked out on first use; row `count` is the start
        double[] legSeconds = new double[(count + 1) * count];
        Array.Fill(legSeconds, double.NaN);
        double Seconds(int from, int to)
        {
            ref double s = ref legSeconds[from * count + to];
            if (double.IsNaN(s))
            {
                s = LegTo(from == count ? start : locations[names[from]], locations[names[to]]).Seconds;
            }

            return s;
        }

        double bestObjective = double.PositiveInfinity;
        int[]? bestOrder = null;   // locations in visiting order
        int[]? bestShop = null;    // per item: the location it is bought at
        int[] shopOf = new int[items.Length];

        for (int n = 1; n <= Math.Min(maxStops, count); n++)
        {
            int[] subset = Enumerable.Range(0, n).ToArray();
            int[] buysAt = new int[n];
            int[] order = new int[n];
            bool[] used = new bool[n];
            double priceSeconds = 0;

            // all visiting orders of the subset, first to last
            void Visit(int depth, int prev, double totalS)
            {
                // legs never take negative time, so an order that is already too slow cannot recover
                if (!(totalS + priceSeconds < bestObjective))
                {
                    return;
                }

                if (depth == n)
                {
                    bestObjective = totalS + priceSeconds;
                    bestOrder = [.. order];
                    bestShop = [.. shopOf];
                    return;
                }

                for (int pos = 0; pos < n; pos++)
                {
                    if (used[pos])
                    {
                        continue;
                    }

                    used[pos] = true;
                    order[depth] = subset[pos];
                    Visit(depth + 1, subset[pos], totalS + Seconds(prev, subset[pos]));
                    used[pos] = false;
                }
            }

            do
            {
                // every item at the cheapest place of the subset, the first one on a tie
                int priceTotal = 0;
                bool covered = true;
                Array.Clear(buysAt);
                for (int i = 0; i < items.Length && covered; i++)
                {
                    int at = -1;
                    for (int pos = 0; pos < n; pos++)
                    {
                        Option? o = offers[i][subset[pos]];
                        if (o is not null && (at < 0 || o.UnitPrice < offers[i][subset[at]]!.UnitPrice))
                        {
                            at = pos;
                        }
                    }

                    covered = at >= 0;
                    if (covered)
                    {
                        shopOf[i] = subset[at];
                        buysAt[at]++;
                        priceTotal += offers[i][shopOf[i]]!.UnitPrice * quantity[i];
                    }
                }

                if (!covered || buysAt.Contains(0))
                {
                    continue; // an item is not sold here, or a smaller subset covers the same shops
                }

                // price is converted into minutes when the user values their time
                priceSeconds = auecPerMinute > 0 ? priceTotal / auecPerMinute * 60 : 0;
                Visit(0, count, 0);
            }
            while (NextCombination(subset, count));
        }

        if (bestOrder is not null && bestShop is not null)
        {
            var buys = bestOrder.ToDictionary(l => l, _ => new List<Buy>());
            for (int i = 0; i < items.Length; i++)
            {
                Option opt = offers[i][bestShop[i]]!;
                buys[bestShop[i]].Add(new Buy(items[i], opt.Shop, quantity[i], opt.UnitPrice * quantity[i]));
            }

            Location prev = start;
            foreach (int l in bestOrder)
            {
                Location loc = locations[names[l]];
                (double secs, double fuel, double km, int jumps) = LegTo(prev, loc);
                stops.Add(new Stop { Location = loc, Buys = buys[l], LegSeconds = secs, LegFuel = fuel, LegKm = km, Jumps = jumps });
                prev = loc;
            }
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

    /// <summary>Advances idx to the next k-of-count combination in lexicographic order; false after the last.</summary>
    private static bool NextCombination(int[] idx, int count)
    {
        int k = idx.Length;
        int pos = k - 1;
        while (pos >= 0 && idx[pos] == count - k + pos)
        {
            pos--;
        }

        if (pos < 0)
        {
            return false;
        }

        idx[pos]++;
        for (int j = pos + 1; j < k; j++)
        {
            idx[j] = idx[j - 1] + 1;
        }

        return true;
    }
}
