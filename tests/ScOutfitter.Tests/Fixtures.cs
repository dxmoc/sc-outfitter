using System.Text.Json.Nodes;
using ScOutfitter.Core;

namespace ScOutfitter.Tests;

/// <summary>Hand-made components, terminals and ship JSON so the tests never touch the network.</summary>
public static class Fixtures
{
    public static Component Comp(string name, Kind kind, int size, Dictionary<string, double> stats, int? price = null,
        int terminal = 1, string grade = "A", string[]? tags = null, string[]? required = null, double power = 0)
    {
        var c = new Component
        {
            Name = name, Kind = kind, Size = size, Grade = grade, PowerDraw = power,
            Tags = new HashSet<string>(tags ?? [], StringComparer.Ordinal),
            RequiredTags = new HashSet<string>(required ?? [], StringComparer.Ordinal),
        };
        foreach ((string k, double v) in stats)
        {
            c.Stats[k] = v;
        }

        if (price is not null)
        {
            c.Offers.Add(new Offer(terminal, $"shop-{terminal}", price.Value));
        }

        return c;
    }

    public static Dictionary<Kind, List<Component>> Catalog()
    {
        var cat = new Dictionary<Kind, List<Component>>();
        cat[Kind.Gun] =
        [
            Comp("Stock Gun", Kind.Gun, 3, new() { ["dps"] = 500, ["range"] = 2000 }),               // not sold
            Comp("Big Gun", Kind.Gun, 3, new() { ["dps"] = 1000, ["range"] = 2000 }, 50000, 1),
            Comp("Cheap Gun", Kind.Gun, 3, new() { ["dps"] = 900, ["range"] = 2000 }, 10000, 2),
            Comp("Small Gun", Kind.Gun, 2, new() { ["dps"] = 400, ["range"] = 1500 }, 5000, 2),
            Comp("Bespoke Gun", Kind.Gun, 3, new() { ["dps"] = 1200, ["range"] = 2000 }, 20000, 1, tags: ["Wolf_Gun"], required: ["Wolf_Gun"]),
        ];
        cat[Kind.Shield] =
        [
            Comp("Stock Shield", Kind.Shield, 1, new() { ["hp"] = 3000, ["regen"] = 600 }),
            Comp("Tank Shield", Kind.Shield, 1, new() { ["hp"] = 3200, ["regen"] = 500 }, 60000, 2),
            Comp("Regen Shield", Kind.Shield, 1, new() { ["hp"] = 2800, ["regen"] = 900 }, 60000, 1),
        ];
        cat[Kind.PowerPlant] = [Comp("Plant", Kind.PowerPlant, 1, new() { ["power"] = 16 }, 20000, 1)];
        cat[Kind.Cooler] = [Comp("Cooler", Kind.Cooler, 1, new() { ["coolant"] = 34 }, 10000, 1)];
        cat[Kind.QuantumDrive] =
        [
            Comp("Beacon", Kind.QuantumDrive, 1, new() { ["speed"] = 161e6, ["spool"] = 4.4, ["cooldown"] = 12, ["accel"] = 22e6, ["fuel_rate"] = 1.86e-8 }),
            Comp("FoxFire", Kind.QuantumDrive, 1, new() { ["speed"] = 263e6, ["spool"] = 4.8, ["cooldown"] = 8, ["accel"] = 30e6, ["fuel_rate"] = 5.9e-9 }, 115500, 2),
        ];
        cat[Kind.MissileRack] =
        [
            Comp("Rack 2x2", Kind.MissileRack, 3, new() { ["count"] = 2, ["missile_size"] = 2 }, 5000, 1),
            Comp("Rack 4x1", Kind.MissileRack, 3, new() { ["count"] = 4, ["missile_size"] = 1 }, 3000, 1),
            Comp("Welded Rack", Kind.MissileRack, 4, new() { ["count"] = 4, ["missile_size"] = 2 }),
        ];
        cat[Kind.Missile] =
        [
            Comp("Missile S2", Kind.Missile, 2, new() { ["damage"] = 2400, ["speed"] = 1000, ["range"] = 30000 }, 160, 1),
            Comp("Missile S1", Kind.Missile, 1, new() { ["damage"] = 1150, ["speed"] = 1300, ["range"] = 20000 }, 90, 1),
        ];
        cat[Kind.Radar] =
        [
            Comp("Stock Radar", Kind.Radar, 1, new() { ["aim_range"] = 978, ["em"] = 1600 }),
            Comp("Far Radar", Kind.Radar, 1, new() { ["aim_range"] = 1326, ["em"] = 1440 }, 176000, 2),
            Comp("Quiet Radar", Kind.Radar, 1, new() { ["aim_range"] = 1122, ["em"] = 1040 }, 112000, 2),
        ];
        return cat;
    }

    /// <summary>Terminal 1 at HUR-L2, terminal 2 at Port Tressler, terminal 3 at Checkmate (Pyro), 4 unknown place.</summary>
    public static Dictionary<int, JsonNode> Terminals() => new()
    {
        [1] = Term(1, "Platinum Bay - HUR-L2", "Stanton", station: "HUR-L2 Faithful Dream Station"),
        [2] = Term(2, "Platinum Bay - Port Tressler", "Stanton", station: "Port Tressler", planet: "MicroTech"),
        [3] = Term(3, "Ship Parts - Checkmate", "Pyro", station: "Checkmate Station"),
        [4] = Term(4, "Mystery Shop", "Nyx", station: "Nowhere Station"),
    };

    private static JsonNode Term(int id, string name, string system, string? station = null, string? planet = null) =>
        new JsonObject
        {
            ["id"] = id, ["name"] = name, ["star_system_name"] = system,
            ["space_station_name"] = station, ["planet_name"] = planet,
        };

    public static DataStore Store(Dictionary<Kind, List<Component>>? catalog = null) => new()
    {
        Client = new WikiClient(Path.Combine(Path.GetTempPath(), "sc-outfitter-tests")),
        Catalog = catalog ?? Catalog(),
        Terminals = Terminals(),
        Starmap = Starmap.LoadEmbedded(),
        Ships = ShipResolver.Label([
            new ShipRef("Gladius", "aegs-gladius", "AEGS_Gladius", true),
            new ShipRef("Cutlass Black", "drak-cutlass-black", "DRAK_Cutlass_Black", true),
            new ShipRef("Cutlass Black", "drak-cutlass-black-bis2950", "DRAK_Cutlass_Black_BIS2950", true),
            new ShipRef("S-65 Stingray", "krig-s65-stingray-ballistic", "KRIG_S65_Stingray_BALLISTIC", true),
            new ShipRef("S-65 Stingray", "krig-s65-stingray", "KRIG_S65_Stingray", true),
        ]),
    };

    /// <summary>A Gladius-like hull: 3 gun mounts (one gimballed), 2 racks, shields, plant, coolers, QD, radar.</summary>
    public static JsonNode ShipJson(bool weldedMount = false) => new JsonObject
    {
        ["name"] = "Test Ship",
        ["quantum"] = new JsonObject { ["quantum_fuel_capacity"] = 0.6, ["quantum_range"] = 32e9 },
        ["power"] = new JsonObject { ["generation_segments"] = 16 },
        ["cooling"] = new JsonObject { ["generation_segments"] = 68 },
        ["port_tags"] = new JsonArray("test_hull"),
        ["ports"] = new JsonArray(
            GunPort("hardpoint_gun_left", 3, "Gimbal Mount S3", "Stock Gun", editable: !weldedMount),
            GunPort("hardpoint_gun_right", 3, "Gimbal Mount S3", "Stock Gun", editable: !weldedMount),
            GunPort("hardpoint_gun_nose", 3, "Stock Gun", null, editable: true),
            RackPort("hardpoint_rack_left", 3, "Rack 2x2", "Missile S2", editable: true),
            RackPort("hardpoint_rack_right", 4, "Welded Rack", "Missile S2", editable: false),
            Simple("hardpoint_shield_generator", "Shield", 1, "Stock Shield"),
            Simple("hardpoint_power_plant", "PowerPlant", 1, "Plant"),
            Simple("hardpoint_cooler", "Cooler", 1, "Cooler"),
            Simple("hardpoint_quantum_drive", "QuantumDrive", 1, "Beacon"),
            Simple("hardpoint_radar", "Radar", 1, "Stock Radar"),
            new JsonObject { ["name"] = "hardpoint_seat", ["type"] = "SeatAccess", ["sizes"] = Sizes(1) }),
    };

    private static JsonObject Sizes(int s) => new() { ["min"] = s, ["max"] = s };

    private static JsonObject Simple(string name, string type, int size, string equipped) => new()
    {
        ["name"] = name, ["type"] = type, ["sizes"] = Sizes(size), ["editable"] = true,
        ["equipped_item"] = new JsonObject { ["name"] = equipped },
        ["compatible_types"] = new JsonArray(new JsonObject { ["type"] = type }),
    };

    private static JsonObject GunPort(string name, int size, string equipped, string? childGun, bool editable)
    {
        var port = new JsonObject
        {
            ["name"] = name, ["type"] = "Turret", ["sizes"] = Sizes(size), ["editable"] = editable,
            ["equipped_item"] = new JsonObject { ["name"] = equipped },
            ["compatible_types"] = new JsonArray(new JsonObject { ["type"] = "Turret" }, new JsonObject { ["type"] = "WeaponGun" }),
        };
        if (childGun is not null)
        {
            port["ports"] = new JsonArray(new JsonObject
            {
                ["name"] = "hardpoint_class_2", ["type"] = "WeaponGun", ["sizes"] = Sizes(size - 1), ["editable"] = true,
                ["equipped_item"] = new JsonObject { ["name"] = childGun },
                ["compatible_types"] = new JsonArray(new JsonObject { ["type"] = "WeaponGun" }),
            });
        }

        return port;
    }

    private static JsonObject RackPort(string name, int size, string rack, string missile, bool editable) => new()
    {
        ["name"] = name, ["type"] = "MissileLauncher", ["sizes"] = Sizes(size), ["editable"] = editable,
        ["equipped_item"] = new JsonObject { ["name"] = rack },
        ["ports"] = new JsonArray(new JsonObject
        {
            ["name"] = "missile_01_attach", ["type"] = "Missile", ["sizes"] = Sizes(2), ["editable"] = true,
            ["equipped_item"] = new JsonObject { ["name"] = missile },
        }),
    };
}
