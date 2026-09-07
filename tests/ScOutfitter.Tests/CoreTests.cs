using System.Text.Json.Nodes;
using ScOutfitter.Core;

namespace ScOutfitter.Tests;

public static class StarmapTests
{
    public static Task RunAsync(TestRunner t)
    {
        Starmap map = Starmap.LoadEmbedded();
        t.Equal("three systems in order", "Stanton,Pyro,Nyx", string.Join(",", map.Systems()));

        Location? everus = map.Locate("Everus Harbor");
        t.Check("Everus Harbor found", everus is not null);
        t.Equal("Everus Harbor sits at Hurston", "Hurston", everus?.Container);
        t.Equal("Everus Harbor label", "Everus Harbor (Hurston)", everus?.Label);

        Location? checkmate = map.Locate("Pyro/Checkmate");
        t.Equal("system prefix pins Pyro", "Pyro", checkmate?.System);
        t.Equal("deep-space label has no container", "Checkmate", checkmate?.Label);
        t.Equal("alias Area 18 -> Area18", "Area18", map.Locate("Area 18")?.Name);
        t.Equal("alias GrimHEX", "Grim HEX", map.Locate("Green Imperial Housing Exchange")?.Name);
        t.Equal("station suffix '(Stanton)' stripped", "Pyro Gateway", map.Locate("Pyro Gateway (Stanton)")?.Name);
        t.Equal("substring match on R&R prefix", "HUR-L5 High Course Station", map.Locate("R&R HUR-L5 High Course Station")?.Name);
        t.Check("unknown name gives null", map.Locate("Definitely Not A Place") is null);

        List<string> bodies = map.Bodies("Stanton").ToList();
        t.Check("Stanton bodies list planets", bodies.Contains("Hurston") && bodies.Contains("microTech"));
        t.Check("moons follow their planet", bodies.IndexOf("Aberdeen") == bodies.IndexOf("Hurston") + 1 || bodies.IndexOf("Arial") == bodies.IndexOf("Hurston") + 1);
        t.Equal("deep space entry last", "Stanton (deep space)", bodies[^1]);
        IReadOnlyList<string> places = map.Places("Stanton", "Hurston");
        t.Check("Hurston places: Everus + Lorville", places.Contains("Everus Harbor") && places.Contains("Lorville"));
        t.Check("outposts hidden by default", !places.Contains("HDMS-Oparei"));
        t.Check("deep space lists lagrange stations", map.Places("Stanton", "Stanton (deep space)").Any(p => p.StartsWith("HUR-L")));

        Location lorville = map.Locate("Lorville")!;
        Location tressler = map.Locate("Port Tressler")!;
        double d = lorville.DistanceKm(tressler);
        t.Check("Lorville -> Port Tressler about 38 Gm", d > 36e6 && d < 40e6, $"{d / 1e6:0.00} Gm");

        IReadOnlyList<Leg> path = map.Path(lorville, checkmate!);
        t.Equal("cross-system path has 3 legs", 3, path.Count);
        t.Check("middle leg is the jump", path[1].IsJump && !path[0].IsJump && !path[2].IsJump);
        t.Equal("jump enters at Pyro Gateway", "Pyro Gateway", path[0].To.Name);
        t.Equal("jump exits at Stanton Gateway in Pyro", "Pyro", path[1].To.System);
        t.Equal("same-system path is one leg", 1, map.Path(lorville, tressler).Count);

        var term = new JsonObject { ["star_system_name"] = "Stanton", ["space_station_name"] = "Baijini Point", ["planet_name"] = "ArcCorp" };
        t.Equal("terminal resolves to its station", "Baijini Point", map.LocateTerminal(term)?.Name);
        return Task.CompletedTask;
    }
}

public static class RoutingTests
{
    public static Task RunAsync(TestRunner t)
    {
        var qd = new QuantumDrive("test", 200e6, 20e6, 5, 10, 1e-8);
        (double s1, double f1) = qd.Hop(10e9);
        (double s2, double f2) = qd.Hop(20e9);
        t.Check("longer hop takes longer", s2 > s1);
        t.Close("fuel is linear in distance", 2 * f1, f2, 1e-9);
        t.Check("short hop never reaches cruise", qd.Hop(1000).Seconds < qd.Hop(1e9).Seconds);
        t.Equal("zero distance costs nothing", (0.0, 0.0), qd.Hop(0));
        t.Equal("duration formatting minutes", "8m 05s", Router.FormatDuration(485));
        t.Equal("duration formatting hours", "1h 01m", Router.FormatDuration(3660));

        t.Equal("C(4,2) = 6", 6, Router.Combinations(["a", "b", "c", "d"], 2).Count());
        t.Equal("4! = 24", 24, Router.Permutations(["a", "b", "c", "d"]).Count());
        t.Equal("C(3,4) is empty", 0, Router.Combinations(["a", "b", "c"], 4).Count());

        DataStore store = Fixtures.Store();
        Starmap map = store.Starmap;
        Location start = map.Locate("Everus Harbor")!;
        Dictionary<Kind, List<Component>> cat = store.Catalog;
        Component bigGun = cat[Kind.Gun].First(c => c.Name == "Big Gun");     // terminal 1 = HUR-L2
        Component tank = cat[Kind.Shield].First(c => c.Name == "Tank Shield"); // terminal 2 = Port Tressler
        Slot gunSlot = new() { Kind = Kind.Gun, Size = 3, Port = "g" };
        Slot shieldSlot = new() { Kind = Kind.Shield, Size = 1, Port = "s" };
        var picks = new List<Pick>
        {
            new() { Slot = gunSlot, Component = bigGun },
            new() { Slot = gunSlot, Component = bigGun },
            new() { Slot = shieldSlot, Component = tank },
        };
        Trip trip = Router.Plan(picks, store.Terminals, map, start, qd);
        t.Equal("two stops needed", 2, trip.Stops.Count);
        t.Equal("nearest shop first (HUR-L2 before Port Tressler)", "HUR-L2 Faithful Dream Station", trip.Stops[0].Location.Name);
        Buy gunBuy = trip.Stops[0].Buys.Single();
        t.Equal("two guns bought at once", 2, gunBuy.Quantity);
        t.Equal("price times quantity", 100000, gunBuy.Price);
        t.Equal("trip cost sums all buys", 160000, trip.Cost);
        t.Check("second leg is the long one", trip.Stops[1].LegKm > trip.Stops[0].LegKm);
        t.Check("no jumps inside Stanton", trip.Stops.All(s => s.Jumps == 0));

        // Pyro shop reached through the gateway
        Component pyroOnly = Fixtures.Comp("Pyro Gun", Kind.Gun, 3, new() { ["dps"] = 1 }, 100, terminal: 3);
        Trip pyro = Router.Plan([new Pick { Slot = gunSlot, Component = pyroOnly }], store.Terminals, map, start, qd);
        t.Equal("Pyro stop via one jump point", 1, pyro.Stops.Single().Jumps);
        t.Equal("Pyro stop is planned with distance", true, pyro.Stops.Single().Planned);

        // unknown place -> extra stop without distance; unsold -> unavailable
        Component nowhere = Fixtures.Comp("Nowhere Gun", Kind.Gun, 3, new() { ["dps"] = 1 }, 100, terminal: 4);
        Component unsold = Fixtures.Comp("Unsold Gun", Kind.Gun, 3, new() { ["dps"] = 1 });
        Trip odd = Router.Plan([new Pick { Slot = gunSlot, Component = nowhere }, new Pick { Slot = gunSlot, Component = unsold }],
            store.Terminals, map, start, qd);
        t.Equal("unmapped shop becomes extra stop", 1, odd.Extra.Count());
        t.Equal("extra stop keeps its system", "Nyx", odd.Extra.Single().System);
        t.Equal("unsold item reported", "Unsold Gun", odd.Unavailable.SingleOrDefault());
        t.Equal("kept picks are not bought", 0, Router.Plan([new Pick { Slot = gunSlot, Component = bigGun, Keep = true }], store.Terminals, map, start, qd).Stops.Count);
        return Task.CompletedTask;
    }
}

public static class OptimizerTests
{
    public static Task RunAsync(TestRunner t)
    {
        Dictionary<Kind, List<Component>> cat = Fixtures.Catalog();
        List<Component> guns = cat[Kind.Gun].Where(c => c.Size == 3 && c.Name != "Bespoke Gun").ToList();
        var dps = new Goals { ["dps"] = 1 };
        Component big = guns.First(c => c.Name == "Big Gun");
        Component cheap = guns.First(c => c.Name == "Cheap Gun");
        t.Close("best candidate scores 1.0", 1.0, Optimizer.Score(big, dps, guns));
        t.Close("normalized against the best", 0.9, Optimizer.Score(cheap, dps, guns));
        var cheapGoal = new Goals { ["dps"] = 1, ["cheap"] = 1 };
        t.Check("cheap goal flips the order", Optimizer.Score(cheap, cheapGoal, guns) > Optimizer.Score(big, cheapGoal, guns));

        List<Component> radars = cat[Kind.Radar];
        var none = new Goals { ["dps"] = 1 };
        Component far = radars.First(c => c.Name == "Far Radar");
        Component quiet = radars.First(c => c.Name == "Quiet Radar");
        t.Check("stealth is opt-in: fallback prefers range", Optimizer.Score(far, none, radars) > Optimizer.Score(quiet, none, radars));
        var stealth = new Goals { ["stealth"] = 1 };
        t.Check("stealth goal prefers low EM", Optimizer.Score(quiet, stealth, radars) > Optimizer.Score(far, stealth, radars));

        Slot plain = new() { Kind = Kind.Gun, Size = 3, Port = "p" };
        Slot bespoke = new() { Kind = Kind.Gun, Size = 3, Port = "b", RequiredTags = ["Wolf_Gun"], PortTags = ["Wolf_Gun"] };
        Component bespokeGun = cat[Kind.Gun].First(c => c.Name == "Bespoke Gun");
        t.Check("generic gun fits plain port", Optimizer.Fits(big, plain));
        t.Check("generic gun does not fit bespoke port", !Optimizer.Fits(big, bespoke));
        t.Check("bespoke gun fits bespoke port", Optimizer.Fits(bespokeGun, bespoke));
        t.Check("bespoke gun does not fit plain port", !Optimizer.Fits(bespokeGun, plain));

        Ship ship = ShipLoader.FromJson(Fixtures.ShipJson());
        List<Pick> picks = Optimizer.Choose(ship, cat, Goals.Default());
        Pick gun = picks.First(p => p.Slot.Port == "hardpoint_gun_nose");
        t.Equal("best sold gun chosen", "Big Gun", gun.Component.Name);
        t.Equal("stock name carried along", "Stock Gun", gun.Stock);
        t.Check("stock gun replaced", !gun.Keep);
        Pick shield = picks.First(p => p.Component.Kind == Kind.Shield);
        t.Equal("shield picked by hp+regen weights (regen 0.5 still tips it)", "Regen Shield", shield.Component.Name);
        Pick welded = picks.First(p => p.Slot.Port == "hardpoint_rack_right" && p.Component.Kind == Kind.MissileRack);
        t.Check("welded rack is fixed and kept", welded.Fixed && welded.Keep);
        Pick weldedMissiles = picks.First(p => p.Slot.Port == "hardpoint_rack_right" && p.Component.Kind == Kind.Missile);
        t.Equal("missiles still chosen for welded rack", 4, weldedMissiles.Quantity);
        Pick rack = picks.First(p => p.Slot.Port == "hardpoint_rack_left" && p.Component.Kind == Kind.MissileRack);
        t.Equal("rack with highest payload wins (2x2400 > 4x1150)", "Rack 2x2", rack.Component.Name);
        t.Check("stock rack kept since it is the best", rack.Keep);
        Pick rackMissiles = picks.First(p => p.Slot.Port == "hardpoint_rack_left" && p.Component.Kind == Kind.Missile);
        t.Check("stock missiles kept when they match", rackMissiles.Keep);
        Pick plant = picks.First(p => p.Component.Kind == Kind.PowerPlant);
        t.Check("equal plant keeps stock", plant.Keep);
        t.Equal("kept parts cost nothing", 0, plant.Price);

        List<Pick> buyAll = Optimizer.Choose(ship, cat, Goals.Default(), trustStock: false);
        t.Check("buy-all buys the plant too", !buyAll.First(p => p.Component.Kind == Kind.PowerPlant).Keep);
        t.Check("buy-all still keeps welded rack", buyAll.First(p => p.Slot.Port == "hardpoint_rack_right" && p.Component.Kind == Kind.MissileRack).Fixed);

        Totals totals = Optimizer.TotalsOf(picks);
        t.Close("dps total = 3 big guns", 3000, totals.Dps);
        t.Close("missile total counts quantity", 2 * 2400 + 4 * 2400, totals.MissileDamage);
        Budget budget = Optimizer.BudgetOf(ship, picks);
        t.Close("power generation from the plant", 16, budget.PowerGeneration);

        List<Pick> graded = Optimizer.Choose(ship, cat, Goals.Default(), maxGrade: "B");
        t.Check("max grade B still yields a gun (fallback when nothing fits)", graded.Any(p => p.Component.Kind == Kind.Gun));
        return Task.CompletedTask;
    }
}

public static class CatalogTests
{
    public static Task RunAsync(TestRunner t)
    {
        var gun = JsonNode.Parse("""
            {"name":"Test Repeater","size":3,"grade":"A","class":"Military","tags":["LaserRepeater"],"required_tags":[],
             "vehicle_weapon":{"range":1924,"capacity":0,"modes":[{"damage_per_second":545.6},{"damage_per_second":300}]},
             "resource_network":{"usage":{"power":{"max":2.1},"coolant":{"max":1}}},
             "uex_prices":{"purchase":[{"terminal_id":7,"terminal_name":"x","price_buy":1000},{"terminal_id":8,"price_buy":null}]}}
            """)!;
        Component? c = Catalog.FromJson(Kind.Gun, gun);
        t.Check("gun parsed", c is not null);
        t.Close("dps is the best mode", 545.6, c!.Stat("dps"));
        t.Check("energy weapon has no ammo", !c.Ammo);
        t.Equal("offers without price_buy skipped", 1, c.Offers.Count);
        t.Close("power draw read", 2.1, c.PowerDraw);
        t.Check("tags read", c.Tags.Contains("LaserRepeater"));

        var dupA = JsonNode.Parse("""{"name":"Twin","size":3,"vehicle_weapon":{"modes":[{"damage_per_second":10}]}}""")!;
        var dupB = JsonNode.Parse("""{"name":"Twin","size":3,"vehicle_weapon":{"modes":[{"damage_per_second":10}]},"uex_prices":{"purchase":[{"terminal_id":1,"price_buy":5}]}}""")!;
        var other = JsonNode.Parse("""{"name":"Twin","size":4,"vehicle_weapon":{"modes":[{"damage_per_second":20}]}}""")!;
        List<Component> merged = Catalog.Build(Kind.Gun, [dupA, dupB, other]);
        t.Equal("same name+size merged", 2, merged.Count);
        t.Check("offers merged onto the first entry", merged.First(m => m.Size == 3).Buyable);

        var qd = JsonNode.Parse("""
            {"name":"Atlas","size":1,"grade":"A","quantum_drive":{"fuel_rate":7.546e-9,
             "standard_jump":{"drive_speed":259054500,"cooldown_time":7.6,"stage_two_accel_rate":26053770,"spool_up_time":4.2}}}
            """)!;
        Component q = Catalog.FromJson(Kind.QuantumDrive, qd)!;
        t.Close("drive speed", 259054500, q.Stat("speed"));
        t.Check("summary mentions Mm/s", q.Summary().Contains("259 Mm/s"));

        var radar = JsonNode.Parse("""{"name":"R","size":1,"radar":{"sensitivity":{"infrared":0.8},"aim_assist":{"distance_max_assignment":977.5}},"emission":{"em_max":1600}}""")!;
        Component r = Catalog.FromJson(Kind.Radar, radar)!;
        t.Close("radar aim range", 977.5, r.Stat("aim_range"));
        t.Close("radar em", 1600, r.Stat("em"));
        t.Check("missile without damage rejected", Catalog.FromJson(Kind.Missile, JsonNode.Parse("""{"name":"m","size":2,"missile":{}}""")!) is null);
        t.Check("rack keeps required_tags entries", Catalog.FromJson(Kind.MissileRack,
            JsonNode.Parse("""{"name":"MSD","size":3,"required_tags":["x"],"missile_rack":{"missile_count":2,"missile_size":2}}""")!) is not null);

        // live UEX prices replace the wiki mirror, matched by uuid first and name second
        Dictionary<Kind, List<Component>> cat = Fixtures.Catalog();
        Component big = cat[Kind.Gun].First(c => c.Name == "Big Gun");
        var withUuid = new Component { Name = "Renamed Gun", Kind = Kind.Gun, Size = 3, Uuid = "u-1" };
        cat[Kind.Gun].Add(withUuid);
        DateTime now = DateTime.UtcNow;
        int n = Catalog.ApplyUexPrices(cat, [
            new UexPrice("", "Big Gun", 9, "UEX Shop", 42000, now),
            new UexPrice("", "Big Gun", 10, "UEX Shop 2", 41000, now),
            new UexPrice("u-1", "Whatever UEX calls it", 11, "UEX Shop 3", 5, now),
        ]);
        t.Equal("two components got live prices", 2, n);
        t.Equal("wiki offers replaced by UEX rows", 2, big.Offers.Count);
        t.Equal("cheapest live price", 41000, big.Cheapest);
        t.Equal("uuid beats name", 5, withUuid.Cheapest);
        t.Check("unmatched parts keep wiki offers", cat[Kind.Gun].First(c => c.Name == "Cheap Gun").Cheapest == 10000);
        return Task.CompletedTask;
    }
}

public static class ShipTests
{
    public static Task RunAsync(TestRunner t)
    {
        List<ShipRef> ships = Fixtures.Store().Ships;
        t.Equal("five entries labelled", 5, ships.Count);
        t.Check("base variant keeps the plain name", ships.Any(s => s.Label == "S-65 Stingray" && s.Slug == "krig-s65-stingray"));
        t.Check("edition gets a tag", ships.Any(s => s.Label == "S-65 Stingray (Ballistic)"));
        t.Check("BIS edition tag keeps digits", ships.Any(s => s.Label == "Cutlass Black (BIS2950)"));
        t.Equal("plain name resolves to the base slug", "krig-s65-stingray", ShipResolver.Resolve(ships, "S-65 Stingray").Slug);
        t.Equal("label resolves to the variant", "krig-s65-stingray-ballistic", ShipResolver.Resolve(ships, "S-65 Stingray (Ballistic)").Slug);
        t.Equal("substring resolves to the base", "krig-s65-stingray", ShipResolver.Resolve(ships, "stingray").Slug);
        t.Equal("case does not matter", "aegs-gladius", ShipResolver.Resolve(ships, "GLADIUS").Slug);
        bool ambiguous = false;
        try
        {
            ShipResolver.Resolve(ships, "a");
        }
        catch (LookupException)
        {
            ambiguous = true;
        }

        t.Check("ambiguous substring is reported", ambiguous);

        Ship ship = ShipLoader.FromJson(Fixtures.ShipJson());
        t.Equal("name", "Test Ship", ship.Name);
        t.Close("fuel units = capacity x 1000", 600, ship.QuantumFuelUnits);
        t.Equal("ten slots (seat ignored)", 10, ship.Slots.Count);
        Slot left = ship.Slots.First(s => s.Port == "hardpoint_gun_left");
        t.Equal("gimbal replaced by full-size fixed gun", 3, left.Size);
        t.Equal("gun behind the gimbal is the stock part", "Stock Gun", left.Equipped);
        t.Check("port tags include the hull tag", left.PortTags.Contains("test_hull"));
        Slot rack = ship.Slots.First(s => s.Port == "hardpoint_rack_left");
        t.Equal("rack knows its stock missile", "Missile S2", rack.EquippedMissile);
        t.Check("welded rack flagged fixed", ship.Slots.First(s => s.Port == "hardpoint_rack_right").Fixed);
        t.Equal("default drive", "Beacon", ship.DefaultQuantumDrive);

        Ship gimballed = ShipLoader.FromJson(Fixtures.ShipJson(), fixedGuns: false);
        Slot g = gimballed.Slots.First(s => s.Port == "hardpoint_gun_left");
        t.Check("keep gimbals: one size smaller and flagged", g.Size == 2 && g.Gimbal);
        t.Equal("nose without gimbal keeps size", 3, gimballed.Slots.First(s => s.Port == "hardpoint_gun_nose").Size);

        Ship welded = ShipLoader.FromJson(Fixtures.ShipJson(weldedMount: true));
        Slot w = welded.Slots.First(s => s.Port == "hardpoint_gun_left");
        t.Check("welded mount: gun at child size, still swappable", w.Size == 2 && !w.Fixed);
        return Task.CompletedTask;
    }
}
