namespace ScOutfitter.Core;

/// <summary>What "best" means: a weight per goal name.</summary>
public sealed class Goals : Dictionary<string, double>
{
    public Goals() : base(StringComparer.Ordinal)
    {
    }

    public static readonly IReadOnlyList<(string Name, string Description)> All =
    [
        ("dps", "gun damage per second"),
        ("damage", "missile payload (count x damage per rack)"),
        ("range", "gun and missile range"),
        ("tank", "shield hit points"),
        ("regen", "shield regeneration"),
        ("speed", "quantum drive speed"),
        ("fuel", "quantum fuel efficiency"),
        ("detection", "radar aim-assist range"),
        ("stealth", "low EM signature (radar)"),
        ("cheap", "prefer cheaper parts"),
    ];

    /// <summary>Goals that only count when chosen explicitly; never part of the balanced fallback.</summary>
    public static readonly HashSet<string> OptIn = new(StringComparer.Ordinal) { "stealth", "cheap" };

    public static Goals Default() => new()
    {
        ["dps"] = 1, ["damage"] = 1, ["tank"] = 1, ["regen"] = 0.5, ["speed"] = 1,
    };

    public string Describe() =>
        string.Join(", ", this.Select(kv => kv.Value == 1 ? kv.Key : kv.Key + " x" + kv.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
}

/// <summary>The chosen part for one slot.</summary>
public sealed class Pick
{
    public required Slot Slot { get; init; }
    public required Component Component { get; init; }
    /// <summary>Already fitted, nothing to buy.</summary>
    public bool Keep { get; init; }
    /// <summary>Missiles: one per rack tube.</summary>
    public int Quantity { get; init; } = 1;
    /// <summary>What the wiki says is fitted in this slot by default.</summary>
    public string? Stock { get; init; }
    /// <summary>Slot cannot be changed in game (or nothing that fits is sold).</summary>
    public bool Fixed { get; init; }

    public int Price => Keep ? 0 : (Component.Cheapest ?? 0) * Quantity;
}

public sealed record Budget(double PowerGeneration, double PowerUsage, double CoolingGeneration, double CoolingUsage);

public sealed record Totals(double Dps, double ShieldHp, double MissileDamage, int Cost);

/// <summary>
/// Picks the best purchasable component for every slot according to user-chosen goals.
/// </summary>
/// <remarks>
/// Every goal maps to one stat per component kind. A component's score is the weighted sum of its
/// stats, each normalized by the best value among the candidates for that slot, so goals with
/// different units (dps vs. shield hp) can be mixed. Kinds that none of the chosen goals touch fall
/// back to a balanced score over all their non-opt-in stats.
/// </remarks>
public static class Optimizer
{
    private static readonly Dictionary<Kind, Dictionary<string, string>> KindGoals = new()
    {
        [Kind.Gun] = new() { ["dps"] = "dps", ["range"] = "range" },
        [Kind.Shield] = new() { ["tank"] = "hp", ["regen"] = "regen" },
        [Kind.QuantumDrive] = new() { ["speed"] = "speed", ["fuel"] = "efficiency" },
        [Kind.Radar] = new() { ["detection"] = "aim_range", ["stealth"] = "low_em" },
        [Kind.Missile] = new() { ["damage"] = "damage", ["range"] = "range" },
        [Kind.MissileRack] = new() { ["damage"] = "payload", ["range"] = "range" },
        [Kind.PowerPlant] = new() { ["_"] = "power" },
        [Kind.Cooler] = new() { ["_"] = "coolant" },
    };

    private static double Stat(Component c, string key) => key switch
    {
        "efficiency" => c.Stat("fuel_rate") > 0 ? 1.0 / c.Stat("fuel_rate") : 0,
        "low_em" => c.Stat("em") > 0 ? 1.0 / c.Stat("em") : 0,
        _ => c.Stat(key),
    };

    /// <summary>Weighted, normalized score of comp among cands (comp should be part of cands).</summary>
    public static double Score(Component comp, Goals goals, IReadOnlyList<Component> cands)
    {
        if (!KindGoals.TryGetValue(comp.Kind, out Dictionary<string, string>? mapping))
        {
            return 0;
        }

        Dictionary<string, double> active = goals
            .Where(kv => mapping.ContainsKey(kv.Key) && kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (active.Count == 0)
        {
            active = mapping.Keys.Where(g => !Goals.OptIn.Contains(g)).ToDictionary(g => g, _ => 1.0, StringComparer.Ordinal);
        }

        double total = 0;
        foreach ((string goal, double weight) in active)
        {
            string key = mapping[goal];
            double best = cands.Count == 0 ? 0 : cands.Max(c => Stat(c, key));
            if (best > 0)
            {
                total += weight * Stat(comp, key) / best;
            }
        }

        if (goals.TryGetValue("cheap", out double cheap) && cheap > 0)
        {
            int top = cands.Where(c => c.Buyable).Select(c => c.Cheapest ?? 0).DefaultIfEmpty(0).Max();
            if (top > 0)
            {
                total += cheap * (1 - (comp.Cheapest ?? 0) / (double)top);
            }
        }

        return total;
    }

    /// <summary>
    /// Bespoke ports (e.g. the Stingray's Merlin_Nose / Wolf_Gun) only take parts with those tags,
    /// and bespoke parts only go into ports that offer their tag.
    /// </summary>
    public static bool Fits(Component comp, Slot slot) =>
        slot.RequiredTags.IsSubsetOf(comp.Tags) && comp.RequiredTags.IsSubsetOf(slot.PortTags);

    private static List<Component> Candidates(IEnumerable<Component> comps, Slot slot, bool exact) =>
        comps.Where(c => (exact ? c.Size == slot.Size : c.Size <= slot.Size) && c.Buyable && Fits(c, slot)).ToList();

    private static List<Component> MissileCandidates(IEnumerable<Component> missiles, int size) =>
        missiles.Where(c => c.Size == size && c.Buyable && c.RequiredTags.Count == 0).ToList();

    private static Component? Best(IReadOnlyList<Component> cands, Goals goals) =>
        cands.Count == 0 ? null
            : cands.OrderByDescending(c => Score(c, goals, cands)).ThenBy(c => c.Cheapest ?? 0).First();

    /// <summary>
    /// Gives every rack a payload/range stat from the best missile of its tube size and returns
    /// {rack name: best missile}.
    /// </summary>
    private static Dictionary<string, Component> AttachRackStats(List<Component> racks, List<Component> missiles, Goals goals)
    {
        var best = new Dictionary<string, Component>(StringComparer.Ordinal);
        foreach (Component rack in racks)
        {
            Component? m = Best(MissileCandidates(missiles, (int)rack.Stat("missile_size")), goals);
            if (m is null)
            {
                rack.Stats["payload"] = 0;
                rack.Stats["range"] = 0;
                continue;
            }

            best[rack.Name] = m;
            rack.Stats["payload"] = rack.Stat("count") * m.Stat("damage");
            rack.Stats["range"] = m.Stat("range");
        }

        return best;
    }

    /// <summary>One Pick per slot, plus one per missile rack for its missiles.</summary>
    /// <param name="keepEqual">Keep the stock part when it scores at least as well as the best buy.</param>
    /// <param name="maxGrade">Cap the component grade (A best).</param>
    /// <param name="trustStock">
    /// false ignores what the wiki lists as fitted (its default loadouts are not always what a
    /// ship spawns with) and buys every editable slot.
    /// </param>
    public static List<Pick> Choose(Ship ship, IReadOnlyDictionary<Kind, List<Component>> catalog, Goals goals,
        bool keepEqual = true, string? maxGrade = null, bool trustStock = true)
    {
        var picks = new List<Pick>();
        Dictionary<string, Component> byName = catalog.Values.SelectMany(c => c)
            .GroupBy(c => c.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        List<Component> missiles = catalog.TryGetValue(Kind.Missile, out List<Component>? ms) ? ms : [];
        List<Component> racks = catalog.TryGetValue(Kind.MissileRack, out List<Component>? rs) ? rs : [];
        Dictionary<string, Component> bestMissiles = AttachRackStats(racks, missiles, goals);

        foreach (Slot slot in ship.Slots)
        {
            List<Component> comps = catalog.TryGetValue(slot.Kind, out List<Component>? list) ? list : [];
            List<Component> cands = Candidates(comps, slot, exact: slot.Kind != Kind.Gun);
            if (maxGrade is not null)
            {
                List<Component> capped = cands.Where(c => string.CompareOrdinal(c.Grade, maxGrade) <= 0).ToList();
                if (capped.Count > 0)
                {
                    cands = capped;
                }
            }

            Component? stock = slot.Equipped is not null && byName.TryGetValue(slot.Equipped, out Component? sc) ? sc : null;
            Component? chosen;
            bool keep;

            if (slot.Fixed)
            {
                // welded-on part (Stingray racks): nothing to choose, but missiles below may still be swapped
                if (stock is null)
                {
                    continue;
                }

                chosen = stock;
                keep = true;
                picks.Add(new Pick { Slot = slot, Component = chosen, Keep = true, Stock = slot.Equipped, Fixed = true });
            }
            else
            {
                Component? current = trustStock ? stock : null;
                if (current is not null && !cands.Contains(current))
                {
                    cands = [.. cands, current]; // stock part competes even if it is not sold
                }

                Component? best = Best(cands, goals);
                if (best is null)
                {
                    if (stock is not null)
                    {
                        // nothing sold fits this port: show the stock part as-is
                        picks.Add(new Pick { Slot = slot, Component = stock, Keep = true, Stock = slot.Equipped, Fixed = true });
                    }

                    continue;
                }

                keep = current is not null && (current.Name == best.Name ||
                       (keepEqual && Score(current, goals, cands) >= Score(best, goals, cands)));
                chosen = keep ? current! : best;
                picks.Add(new Pick { Slot = slot, Component = chosen, Keep = keep, Stock = slot.Equipped });
            }

            if (slot.Kind == Kind.MissileRack && bestMissiles.TryGetValue(chosen.Name, out Component? missile))
            {
                int count = (int)chosen.Stat("count");
                bool missileKeep = keep && trustStock && slot.EquippedMissile == missile.Name;
                picks.Add(new Pick
                {
                    Slot = slot, Component = missile, Keep = missileKeep, Quantity = count, Stock = slot.EquippedMissile,
                });
            }
        }

        return picks;
    }

    /// <summary>Power/cooling demand of the chosen loadout vs. what plants/coolers produce.</summary>
    public static Budget BudgetOf(Ship ship, IReadOnlyList<Pick> picks)
    {
        double powerGen = picks.Where(p => p.Component.Kind == Kind.PowerPlant).Sum(p => p.Component.Stat("power"));
        double coolGen = picks.Where(p => p.Component.Kind == Kind.Cooler).Sum(p => p.Component.Stat("coolant"));
        double powerUse = picks.Where(p => p.Component.Kind != Kind.PowerPlant).Sum(p => p.Component.PowerDraw);
        double coolUse = picks.Where(p => p.Component.Kind != Kind.Cooler).Sum(p => p.Component.CoolantDraw);
        return new Budget(powerGen > 0 ? powerGen : ship.PowerGeneration, powerUse,
            coolGen > 0 ? coolGen : ship.CoolingGeneration, coolUse);
    }

    public static Totals TotalsOf(IReadOnlyList<Pick> picks) => new(
        picks.Where(p => p.Component.Kind == Kind.Gun).Sum(p => p.Component.Stat("dps")),
        picks.Where(p => p.Component.Kind == Kind.Shield).Sum(p => p.Component.Stat("hp")),
        picks.Where(p => p.Component.Kind == Kind.Missile).Sum(p => p.Component.Stat("damage") * p.Quantity),
        picks.Sum(p => p.Price));
}
