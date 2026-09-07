using System.Globalization;
using System.Text.Json.Nodes;

namespace ScOutfitter.Core;

public enum Kind
{
    Gun,
    Shield,
    PowerPlant,
    Cooler,
    QuantumDrive,
    MissileRack,
    Missile,
    Radar,
}

public static class KindNames
{
    public static string Label(this Kind kind) => kind switch
    {
        Kind.Gun => "gun",
        Kind.Shield => "shield",
        Kind.PowerPlant => "power plant",
        Kind.Cooler => "cooler",
        Kind.QuantumDrive => "quantum drive",
        Kind.MissileRack => "missile rack",
        Kind.Missile => "missile",
        Kind.Radar => "radar",
        _ => kind.ToString(),
    };
}

public sealed record Offer(int TerminalId, string TerminalName, int Price);

/// <summary>One purchasable (or stock) part with the stats the optimizer scores on.</summary>
public sealed class Component
{
    public required string Name { get; init; }
    public required Kind Kind { get; init; }
    public required int Size { get; init; }
    public string Grade { get; init; } = "?";
    public string Class { get; init; } = string.Empty;
    public Dictionary<string, double> Stats { get; } = new(StringComparer.Ordinal);
    public bool Ammo { get; init; }
    public string Signal { get; init; } = string.Empty;
    public double PowerDraw { get; init; }
    public double CoolantDraw { get; init; }
    public List<Offer> Offers { get; } = [];
    public HashSet<string> Tags { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> RequiredTags { get; init; } = new(StringComparer.Ordinal);

    public bool Buyable => Offers.Count > 0;

    public int? Cheapest => Offers.Count == 0 ? null : Offers.Min(o => o.Price);

    public double Stat(string key) => Stats.TryGetValue(key, out double v) ? v : 0;

    public string Summary()
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        return Kind switch
        {
            Kind.Gun => $"{Stat("dps"):0} dps, {Stat("range"):0} m{(Ammo ? ", ammo" : "")}",
            Kind.Shield => $"{Stat("hp"):0} hp, {Stat("regen"):0}/s regen",
            Kind.PowerPlant => $"{Stat("power"):0} power segments",
            Kind.Cooler => $"{Stat("coolant"):0} coolant segments",
            Kind.QuantumDrive => string.Format(inv, "{0:0} Mm/s, spool {1:0.0}s, {2:0.00} fuel/Gm",
                Stat("speed") / 1e6, Stat("spool"), Stat("fuel_rate") * 1e9),
            Kind.MissileRack => $"{Stat("count"):0}x S{Stat("missile_size"):0}",
            Kind.Missile => $"{Stat("damage"):0} dmg, {Stat("speed"):0} m/s, {Stat("range") / 1000:0} km, {Signal}",
            Kind.Radar => $"aim assist {Stat("aim_range"):0} m, EM {Stat("em"):0}",
            _ => string.Empty,
        };
    }

    public override string ToString() => $"{Name} (S{Size} {Kind})";
}

/// <summary>Turns wiki items into <see cref="Component"/> records grouped by kind.</summary>
public static class Catalog
{
    public static readonly (string WikiType, Kind Kind)[] Types =
    [
        ("QuantumDrive", Kind.QuantumDrive),
        ("Shield", Kind.Shield),
        ("PowerPlant", Kind.PowerPlant),
        ("Cooler", Kind.Cooler),
        ("WeaponGun", Kind.Gun),
        ("MissileLauncher", Kind.MissileRack),
        ("Missile", Kind.Missile),
        ("Radar", Kind.Radar),
    ];

    public static async Task<Dictionary<Kind, List<Component>>> LoadAsync(
        WikiClient client, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var catalog = new Dictionary<Kind, List<Component>>();
        foreach ((string wikiType, Kind kind) in Types)
        {
            progress?.Report($"Loading {kind.Label()}s ...");
            List<JsonNode> items = await client.ItemsAsync(wikiType, null, ct).ConfigureAwait(false);
            catalog[kind] = Build(kind, items);
        }

        return catalog;
    }

    /// <summary>
    /// Stock parts (e.g. Regulus) are flagged as variants on the wiki and the same name can appear
    /// several times (loot/paint variants) with the shop offers on only one of them, so entries are
    /// merged by (name, size) instead of filtering on is_base_variant.
    /// </summary>
    public static List<Component> Build(Kind kind, IEnumerable<JsonNode> items)
    {
        var byKey = new Dictionary<(string, int), Component>();
        foreach (JsonNode item in items)
        {
            Component? comp = FromJson(kind, item);
            if (comp is null)
            {
                continue;
            }

            (string Name, int Size) key = (comp.Name, comp.Size);
            if (byKey.TryGetValue(key, out Component? existing))
            {
                existing.Offers.AddRange(comp.Offers);
            }
            else
            {
                byKey[key] = comp;
            }
        }

        return byKey.Values.ToList();
    }

    public static Component? FromJson(Kind kind, JsonNode item)
    {
        var stats = new Dictionary<string, double>(StringComparer.Ordinal);
        bool ammo = false;
        string signal = string.Empty;

        switch (kind)
        {
            case Kind.Gun:
            {
                JsonNode? w = item.At("vehicle_weapon");
                double dps = w.Arr("modes").Select(m => m.Num("damage_per_second")).DefaultIfEmpty(0).Max();
                if (dps <= 0)
                {
                    return null;
                }

                stats["dps"] = dps;
                stats["range"] = w.Num("range");
                ammo = w.Num("capacity") > 0;
                break;
            }

            case Kind.Shield:
            {
                double hp = item.Num("shield", "max_health");
                if (hp <= 0)
                {
                    return null;
                }

                stats["hp"] = hp;
                stats["regen"] = item.Num("shield", "regen_rate");
                break;
            }

            case Kind.PowerPlant:
            {
                double gen = item.Num("power_plant", "power_segment_generation");
                if (gen <= 0)
                {
                    return null;
                }

                stats["power"] = gen;
                break;
            }

            case Kind.Cooler:
            {
                double gen = item.Num("cooler", "coolant_segment_generation");
                if (gen <= 0)
                {
                    return null;
                }

                stats["coolant"] = gen;
                break;
            }

            case Kind.QuantumDrive:
            {
                JsonNode? q = item.At("quantum_drive");
                JsonNode? j = q.At("standard_jump");
                double speed = j.Num("drive_speed");
                if (speed <= 0)
                {
                    return null;
                }

                stats["speed"] = speed;
                stats["spool"] = j.Num("spool_up_time");
                stats["cooldown"] = j.Num("cooldown_time");
                double accel = j.Num("stage_two_accel_rate");
                stats["accel"] = accel > 0 ? accel : j.Num("stage_one_accel_rate");
                stats["fuel_rate"] = q.Num("fuel_rate"); // fuel units per metre
                break;
            }

            case Kind.MissileRack:
            {
                double count = item.Num("missile_rack", "missile_count");
                if (count <= 0)
                {
                    return null;
                }

                // do not filter on required_tags: the MSD-322 entry that carries the shop offers has them
                stats["count"] = count;
                stats["missile_size"] = item.Num("missile_rack", "missile_size");
                break;
            }

            case Kind.Missile:
            {
                JsonNode? m = item.At("missile");
                double dmg = m.Num("damage_total");
                if (dmg <= 0)
                {
                    return null;
                }

                double speed = m.Num("flight", "speed");
                if (speed <= 0)
                {
                    speed = m.Num("speed");
                }

                stats["damage"] = dmg;
                stats["speed"] = speed;
                stats["range"] = m.Num("flight", "range");
                stats["lock_time"] = m.Num("lock_time");
                signal = m.Str("signal_type");
                if (signal.Length == 0)
                {
                    signal = "?";
                }

                break;
            }

            case Kind.Radar:
            {
                JsonNode? r = item.At("radar");
                double aim = r.Num("aim_assist", "distance_max_assignment");
                double[] sens = new[] { "infrared", "cross_section", "electromagnetic" }
                    .Select(k => r.Num("sensitivity", k)).Where(v => v > 0).ToArray();
                if (aim <= 0 && sens.Length == 0)
                {
                    return null;
                }

                // sensitivity is identical across all radars in 4.x; what differs is the aim-assist
                // range (the number Erkul shows in green) and the EM signature
                stats["aim_range"] = aim;
                stats["sensitivity"] = sens.Length > 0 ? sens.Average() : 0;
                stats["em"] = item.Num("emission", "em_max");
                break;
            }

            default:
                return null;
        }

        var comp = new Component
        {
            Name = item.Str("name"),
            Kind = kind,
            Size = item.Int("size"),
            Grade = item.StrOrNull("grade") is { Length: > 0 } g ? g : "?",
            Class = item.Str("class"),
            Ammo = ammo,
            Signal = signal,
            PowerDraw = item.Num("resource_network", "usage", "power", "max"),
            CoolantDraw = item.Num("resource_network", "usage", "coolant", "max"),
            Tags = item.StrSet("tags"),
            RequiredTags = item.StrSet("required_tags"),
        };
        foreach ((string k, double v) in stats)
        {
            comp.Stats[k] = v;
        }

        foreach (JsonNode o in item.Arr("uex_prices", "purchase"))
        {
            int price = o.Int("price_buy");
            if (price > 0)
            {
                comp.Offers.Add(new Offer(o.Int("terminal_id"), o.Str("terminal_name"), price));
            }
        }

        return comp.Name.Length == 0 ? null : comp;
    }
}
