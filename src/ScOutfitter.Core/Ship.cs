using System.Text.Json.Nodes;

namespace ScOutfitter.Core;

/// <summary>One hardpoint the optimizer can fill.</summary>
public sealed class Slot
{
    public required Kind Kind { get; init; }
    public required int Size { get; init; }
    public required string Port { get; init; }
    public string? Equipped { get; init; }
    /// <summary>Gun slot currently carrying a gimbal mount.</summary>
    public bool Gimbal { get; init; }
    /// <summary>Missile rack slots: what the stock rack is loaded with.</summary>
    public string? EquippedMissile { get; init; }
    /// <summary>Bespoke ports (Stingray "Merlin_Nose"): an item must carry these tags.</summary>
    public HashSet<string> RequiredTags { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Tags this port offers to items that require some.</summary>
    public HashSet<string> PortTags { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Port is not editable in game: keep whatever is fitted.</summary>
    public bool Fixed { get; init; }

    public override string ToString() => $"{Kind} S{Size} ({Port})";
}

public sealed class Ship
{
    public required string Name { get; init; }
    public List<Slot> Slots { get; } = [];
    /// <summary>Wiki capacity x 1000; matches quantum_range / fuel_rate.</summary>
    public double QuantumFuelUnits { get; init; }
    public double QuantumRangeM { get; init; }
    public double PowerGeneration { get; init; }
    public double CoolingGeneration { get; init; }

    public IEnumerable<Slot> SlotsOf(Kind kind) => Slots.Where(s => s.Kind == kind);

    public string? DefaultQuantumDrive => SlotsOf(Kind.QuantumDrive).FirstOrDefault()?.Equipped;
}

public static class ShipLoader
{
    private static readonly Dictionary<string, Kind> PortTypes = new(StringComparer.Ordinal)
    {
        ["QuantumDrive"] = Kind.QuantumDrive,
        ["Shield"] = Kind.Shield,
        ["PowerPlant"] = Kind.PowerPlant,
        ["Cooler"] = Kind.Cooler,
        ["Radar"] = Kind.Radar,
    };

    /// <param name="fixedGuns">Replace gimbal mounts by fixed guns of the full hardpoint size.</param>
    /// <param name="mannedTurrets">Include guns inside manned turrets.</param>
    public static Ship FromJson(JsonNode v, bool fixedGuns = true, bool mannedTurrets = false)
    {
        var ship = new Ship
        {
            Name = v.Str("name") is { Length: > 0 } n ? n : "?",
            QuantumFuelUnits = v.Num("quantum", "quantum_fuel_capacity") * 1000,
            QuantumRangeM = v.Num("quantum", "quantum_range"),
            PowerGeneration = v.Num("power", "generation_segments"),
            CoolingGeneration = v.Num("cooling", "generation_segments"),
        };
        HashSet<string> vehicleTags = v.StrSet("port_tags");
        Walk(v.Arr("ports"), ship.Slots, fixedGuns, mannedTurrets, vehicleTags);
        return ship;
    }

    private static bool Compatible(JsonNode? port, string typeName) =>
        port.Arr("compatible_types").Any(c => c.Str("type") == typeName);

    private static (HashSet<string> Required, HashSet<string> Offered) Tags(HashSet<string> vehicleTags, params JsonNode?[] ports)
    {
        var req = new HashSet<string>(StringComparer.Ordinal);
        var offered = new HashSet<string>(vehicleTags, StringComparer.Ordinal);
        foreach (JsonNode? p in ports)
        {
            if (p is null)
            {
                continue;
            }

            req.UnionWith(p.StrSet("required_tags"));
            offered.UnionWith(p.StrSet("port_tags"));
            offered.UnionWith(p.StrSet("required_tags"));
        }

        return (req, offered);
    }

    private static void Walk(IEnumerable<JsonNode> ports, List<Slot> slots, bool fixedGuns, bool manned, HashSet<string> vehicleTags)
    {
        foreach (JsonNode p in ports)
        {
            string type = p.Str("type");
            int size = p.Int("sizes", "max");
            string? equipped = p.StrOrNull("equipped_item", "name");
            List<JsonNode> children = p.Arr("ports").ToList();
            bool editable = p.BoolOr(true, "editable");
            (HashSet<string> req, HashSet<string> offered) = Tags(vehicleTags, p);

            if (type == "MissileLauncher")
            {
                string? missile = children.FirstOrDefault(c => c.Str("type") == "Missile").StrOrNull("equipped_item", "name");
                slots.Add(new Slot
                {
                    Kind = Kind.MissileRack, Size = size, Port = p.Str("name"), Equipped = equipped,
                    EquippedMissile = missile, RequiredTags = req, PortTags = offered, Fixed = !editable,
                });
                continue;
            }

            if (PortTypes.TryGetValue(type, out Kind kind))
            {
                slots.Add(new Slot
                {
                    Kind = kind, Size = size, Port = p.Str("name"), Equipped = equipped,
                    RequiredTags = req, PortTags = offered, Fixed = !editable,
                });
                continue;
            }

            if (Compatible(p, "WeaponGun") && size > 0)
            {
                // a gun hardpoint; may hold a gimbal mount whose child port is one size smaller
                JsonNode? childGun = children.FirstOrDefault(c => c.Str("type") == "WeaponGun" || Compatible(c, "WeaponGun"));
                bool isGimbal = childGun is not null && (equipped ?? string.Empty).Contains("gimbal", StringComparison.OrdinalIgnoreCase);
                string? childEquipped = childGun.StrOrNull("equipped_item", "name");
                (req, offered) = Tags(vehicleTags, p, childGun);

                if (!editable && childGun is not null)
                {
                    // mount is welded on (Stingray): only the gun inside swaps, at the child's size
                    int childSize = childGun.Int("sizes", "max");
                    slots.Add(new Slot
                    {
                        Kind = Kind.Gun, Size = childSize > 0 ? childSize : size, Port = p.Str("name"),
                        Equipped = childEquipped, Gimbal = isGimbal, RequiredTags = req, PortTags = offered,
                        Fixed = !childGun.BoolOr(true, "editable"),
                    });
                }
                else if (fixedGuns || !isGimbal)
                {
                    slots.Add(new Slot
                    {
                        Kind = Kind.Gun, Size = size, Port = p.Str("name"),
                        Equipped = isGimbal ? childEquipped : equipped, Gimbal = false,
                        RequiredTags = req, PortTags = offered, Fixed = !editable,
                    });
                }
                else
                {
                    slots.Add(new Slot
                    {
                        Kind = Kind.Gun, Size = size - 1, Port = p.Str("name"), Equipped = childEquipped, Gimbal = true,
                        RequiredTags = req, PortTags = offered,
                    });
                }

                continue;
            }

            if ((type == "Turret" || type == "TurretBase") && manned)
            {
                Walk(children, slots, fixedGuns, manned, vehicleTags);
            }
        }
    }
}
