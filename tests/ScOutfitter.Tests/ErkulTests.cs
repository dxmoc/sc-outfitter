using System.Text.Json.Nodes;
using ScOutfitter.Core;

namespace ScOutfitter.Tests;

public static class ErkulTests
{
    public static Task RunAsync(TestRunner t)
    {
        t.Equal("share link", "q7wvw5hg", ErkulClient.ParseId("https://erkul.games/s/q7wvw5hg"));
        t.Equal("share link with slash", "q7wvw5hg", ErkulClient.ParseId("https://erkul.games/s/q7wvw5hg/"));
        t.Equal("browse link", "q7wvw5hg", ErkulClient.ParseId("https://erkul.games/browse?q=@q7wvw5hg"));
        t.Equal("bare id", "q7wvw5hg", ErkulClient.ParseId("  Q7WVW5HG "));
        t.Check("garbage rejected", ErkulClient.ParseId("https://erkul.games/calculator") is null);

        string json = """
            {"name":"Test build","payload":{"schemaVersion":1,"v":"4.10.0","branch":"LIVE","ship":"aegs_gladius","gimbalMode":"lock",
            "slotOverrides":{
              "hardpoint_gun_left/hardpoint_class_2":{"kind":"weapon","className":"GUN_BIG_S3"},
              "hardpoint_gun_nose":{"kind":"weapon","className":"gun_cheap_s3"},
              "hardpoint_shield_generator":{"kind":"shield","className":"SHLD_TANK"},
              "hardpoint_rack_left":{"kind":"rack","className":"RACK_4X1"},
              "hardpoint_rack_left/missile_01_attach":{"kind":"missile","className":"MISL_S1"},
              "hardpoint_rack_left/missile_02_attach":{"kind":"missile","className":"MISL_S1"},
              "hardpoint_rack_left/missile_03_attach":{"kind":"missile","className":"MISL_S1"},
              "hardpoint_rack_left/missile_04_attach":{"kind":"missile","className":"MISL_S1"},
              "hardpoint_paint":{"kind":"paint","className":"paint_red"},
              "hardpoint_radar":{"kind":"radar","className":"radar_unknown"}
            }}}
            """;
        byte[] packed = ErkulClient.Encode(json);
        t.Check("deflate round trip", packed.Length < json.Length && ErkulClient.Decode(packed)["name"]!.ToString() == "Test build");
        t.Check("plain json still decodes", ErkulClient.Decode(System.Text.Encoding.UTF8.GetBytes(json))["name"]!.ToString() == "Test build");

        ErkulBuild build = ErkulBuild.FromJson("abc", ErkulClient.Decode(packed));
        t.Equal("ship class", "aegs_gladius", build.ShipClass);
        t.Check("gimbal lock read", build.GimbalLocked);
        t.Equal("ten overrides", 10, build.Overrides.Count);

        Dictionary<Kind, List<Component>> cat = Fixtures.Catalog();
        Give(cat, "Big Gun", "GUN_BIG_S3");
        Give(cat, "Cheap Gun", "GUN_CHEAP_S3");
        Give(cat, "Tank Shield", "SHLD_TANK");
        Give(cat, "Rack 4x1", "RACK_4X1");
        Give(cat, "Missile S1", "MISL_S1");
        Ship ship = ShipLoader.FromJson(Fixtures.ShipJson(), fixedGuns: false);
        ErkulImporter.Result r = ErkulImporter.ToPicks(build, ship, cat);

        Pick left = r.Picks.First(p => p.Slot.Port == "hardpoint_gun_left");
        t.Equal("gun inside the gimbal replaced", "Big Gun", left.Component.Name);
        t.Check("override marked as purchase", !left.Keep);
        t.Equal("case-insensitive class match", "Cheap Gun", r.Picks.First(p => p.Slot.Port == "hardpoint_gun_nose").Component.Name);
        t.Check("untouched gun keeps stock", r.Picks.First(p => p.Slot.Port == "hardpoint_gun_right").Keep);
        t.Equal("rack swapped", "Rack 4x1", r.Picks.First(p => p.Slot.Port == "hardpoint_rack_left" && p.Component.Kind == Kind.MissileRack).Component.Name);
        Pick missiles = r.Picks.First(p => p.Slot.Port == "hardpoint_rack_left" && p.Component.Kind == Kind.Missile);
        t.Equal("four tubes grouped", 4, missiles.Quantity);
        t.Check("welded rack stays fixed", r.Picks.First(p => p.Slot.Port == "hardpoint_rack_right" && p.Component.Kind == Kind.MissileRack).Fixed);
        t.Check("stock plant kept", r.Picks.First(p => p.Component.Kind == Kind.PowerPlant).Keep);
        t.Check("unknown radar class reported", r.Unknown.Contains("radar_unknown"));
        t.Check("paint reported as not covered", r.Unknown.Any(u => u.Contains("paint_red")));

        DataStore store = Fixtures.Store(cat);
        Plan plan = Planner.FromErkul(store, build, Fixtures.ShipJson(), store.Starmap.Locate("Everus Harbor")!);
        t.Check("plan title names the build", plan.Source.Contains("Test build"));
        t.Check("route has stops", plan.Trip.Stops.Count > 0);
        t.Check("cost counts the swapped parts", plan.Trip.Cost >= 50000 + 10000 + 60000 + 3000);
        return Task.CompletedTask;
    }

    private static void Give(Dictionary<Kind, List<Component>> cat, string name, string className)
    {
        foreach (List<Component> list in cat.Values)
        {
            int i = list.FindIndex(c => c.Name == name);
            if (i < 0)
            {
                continue;
            }

            Component old = list[i];
            var fresh = new Component
            {
                Name = old.Name, Kind = old.Kind, Size = old.Size, Grade = old.Grade, ClassName = className,
                Tags = old.Tags, RequiredTags = old.RequiredTags, PowerDraw = old.PowerDraw,
            };
            foreach ((string k, double v) in old.Stats)
            {
                fresh.Stats[k] = v;
            }

            fresh.Offers.AddRange(old.Offers);
            list[i] = fresh;
            return;
        }
    }
}
