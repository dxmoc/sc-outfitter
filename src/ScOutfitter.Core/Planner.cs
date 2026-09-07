using System.Text.Json.Nodes;

namespace ScOutfitter.Core;

/// <summary>Everything the planner needs, loaded once at start-up (behind the splash screen).</summary>
public sealed class DataStore
{
    public required WikiClient Client { get; init; }
    public required Dictionary<Kind, List<Component>> Catalog { get; init; }
    public required Dictionary<int, JsonNode> Terminals { get; init; }
    public required Starmap Starmap { get; init; }
    public required List<ShipRef> Ships { get; init; }

    /// <summary>Labels for the ship picker ("S-65 Stingray", "S-65 Stingray (Ballistic)").</summary>
    public List<string> ShipNames => Ships.Select(s => s.Label).ToList();
    /// <summary>When this data was assembled (local time).</summary>
    public DateTime LoadedAt { get; init; } = DateTime.Now;
    /// <summary>Newest price report UEX has, UTC; null when prices came from the wiki mirror only.</summary>
    public DateTime? PricesNewestUtc { get; init; }
    /// <summary>How many components carry live UEX prices.</summary>
    public int LivePriced { get; init; }

    public static async Task<DataStore> LoadAsync(WikiClient client, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Loading ship list ...");
        List<ShipRef> ships = await client.VehiclesAsync(true, ct).ConfigureAwait(false);
        Dictionary<Kind, List<Component>> catalog = await Core.Catalog.LoadAsync(client, progress, ct).ConfigureAwait(false);
        progress?.Report("Loading shop locations ...");
        Dictionary<int, JsonNode> terminals = await client.TerminalsAsync(ct).ConfigureAwait(false);

        progress?.Report("Loading current prices from UEX ...");
        int live = 0;
        DateTime? newest = null;
        try
        {
            List<UexPrice> prices = await client.PricesAsync(ct).ConfigureAwait(false);
            live = Core.Catalog.ApplyUexPrices(catalog, prices);
            newest = prices.Count > 0 ? prices.Max(p => p.Modified) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            // UEX down: the wiki mirror prices already in the catalog are good enough for today
        }

        progress?.Report("Loading star map ...");
        Starmap starmap = Starmap.LoadEmbedded();
        return new DataStore
        {
            Client = client, Catalog = catalog, Terminals = terminals, Starmap = starmap, Ships = ships,
            PricesNewestUtc = newest, LivePriced = live,
        };
    }

    /// <summary>Throw the cache away and load everything again.</summary>
    public Task<DataStore> ReloadAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Client.ClearCache();
        return LoadAsync(Client, progress, ct);
    }
}

public sealed record PlanOptions
{
    public bool KeepGimbals { get; init; }
    public bool MannedTurrets { get; init; }
    public string? MaxGrade { get; init; }
    public bool ReplaceAll { get; init; }
    public bool BuyAll { get; init; }
    public bool PlanWithNewDrive { get; init; }
    public double AuecPerMinute { get; init; }
    public int MaxStops { get; init; } = 5;
}

public sealed class Plan
{
    public required Ship Ship { get; init; }
    public required List<Pick> Picks { get; init; }
    public required Trip Trip { get; init; }
    public required Budget Budget { get; init; }
    public required Totals Totals { get; init; }
    public required QuantumDrive Drive { get; init; }
    public required Goals Goals { get; init; }
    /// <summary>Where the loadout came from: "goals: ..." or the erkul build name.</summary>
    public string Source { get; init; } = string.Empty;
    /// <summary>erkul parts the catalog does not know (paints, jump drives, unknown class names).</summary>
    public List<string> Unknown { get; init; } = [];
}

public static class Planner
{
    public static async Task<Plan> MakeAsync(DataStore data, string shipName, string startName, Goals? goals = null,
        PlanOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PlanOptions();
        goals ??= Goals.Default();
        Location start = data.Starmap.Locate(startName)
                         ?? throw new LookupException($"Unknown start location '{startName}' (try 'Pyro/Checkmate').");
        ShipRef ship = ShipResolver.Resolve(data.Ships, shipName);
        JsonNode vehicle = await data.Client.VehicleAsync(ship, ct).ConfigureAwait(false);
        return Make(data, vehicle, start, goals, options);
    }

    /// <summary>Synchronous core so tests can run it on canned JSON.</summary>
    public static Plan Make(DataStore data, JsonNode vehicle, Location start, Goals goals, PlanOptions options)
    {
        Ship ship = ShipLoader.FromJson(vehicle, fixedGuns: !options.KeepGimbals, mannedTurrets: options.MannedTurrets);
        if (ship.Slots.Count == 0)
        {
            throw new LookupException($"The wiki has no hardpoint data for '{ship.Name}'.");
        }

        List<Pick> picks = Optimizer.Choose(ship, data.Catalog, goals, keepEqual: !options.ReplaceAll,
            maxGrade: options.MaxGrade, trustStock: !options.BuyAll);
        QuantumDrive drive = TripDrive(picks, data.Catalog, ship, options.PlanWithNewDrive);
        Trip trip = Router.Plan(picks, data.Terminals, data.Starmap, start, drive, options.AuecPerMinute, options.MaxStops);
        return new Plan
        {
            Ship = ship, Picks = picks, Trip = trip, Drive = drive, Goals = goals,
            Budget = Optimizer.BudgetOf(ship, picks), Totals = Optimizer.TotalsOf(picks),
            Source = "goals: " + goals.Describe(),
        };
    }

    /// <summary>Route for a build shared on erkul.games: the parts are given, only the trip is planned.</summary>
    public static async Task<Plan> FromErkulAsync(DataStore data, string linkOrId, string startName, CancellationToken ct = default)
    {
        Location start = data.Starmap.Locate(startName)
                         ?? throw new LookupException($"Unknown start location '{startName}'.");
        ErkulBuild build = await new ErkulClient().FetchAsync(linkOrId, ct).ConfigureAwait(false);
        ShipRef ship = data.Ships.FirstOrDefault(s => s.ClassName.Equals(build.ShipClass, StringComparison.OrdinalIgnoreCase))
                       ?? data.Ships.FirstOrDefault(s => s.Slug.Replace("-", "_").Equals(build.ShipClass, StringComparison.OrdinalIgnoreCase))
                       ?? throw new LookupException($"The wiki has no ship with class '{build.ShipClass}'.");
        JsonNode vehicle = await data.Client.VehicleAsync(ship, ct).ConfigureAwait(false);
        return FromErkul(data, build, vehicle, start);
    }

    public static Plan FromErkul(DataStore data, ErkulBuild build, JsonNode vehicle, Location start)
    {
        // gimbals stay as the player set them: overrides address the gun inside the mount
        Ship ship = ShipLoader.FromJson(vehicle, fixedGuns: !build.GimbalLocked);
        ErkulImporter.Result result = ErkulImporter.ToPicks(build, ship, data.Catalog);
        QuantumDrive drive = TripDrive(result.Picks, data.Catalog, ship, usePlanned: false);
        Trip trip = Router.Plan(result.Picks, data.Terminals, data.Starmap, start, drive);
        string title = build.Name.Length > 0 ? build.Name : build.Id;
        return new Plan
        {
            Ship = ship, Picks = result.Picks, Trip = trip, Drive = drive, Goals = new Goals(),
            Budget = Optimizer.BudgetOf(ship, result.Picks), Totals = Optimizer.TotalsOf(result.Picks),
            Source = $"erkul.games build \"{title}\"" + (build.GameVersion.Length > 0 ? $" ({build.GameVersion})" : string.Empty),
            Unknown = result.Unknown,
        };
    }

    /// <summary>Quantum drive used for the trip: the equipped one unless asked for the planned one.</summary>
    private static QuantumDrive TripDrive(List<Pick> picks, Dictionary<Kind, List<Component>> catalog, Ship ship, bool usePlanned)
    {
        Component? planned = picks.FirstOrDefault(p => p.Component.Kind == Kind.QuantumDrive)?.Component;
        Component? current = catalog.TryGetValue(Kind.QuantumDrive, out List<Component>? qds)
            ? qds.FirstOrDefault(c => c.Name == ship.DefaultQuantumDrive)
            : null;
        Component? comp = usePlanned && planned is not null ? planned : current ?? planned;
        return comp is null
            ? throw new LookupException("No quantum drive data for this ship.")
            : QuantumDrive.From(comp);
    }
}
