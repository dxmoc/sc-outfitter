using System.Text.Json.Nodes;

namespace ScOutfitter.Core;

/// <summary>Everything the planner needs, loaded once at start-up (behind the splash screen).</summary>
public sealed class DataStore
{
    public required WikiClient Client { get; init; }
    public required Dictionary<Kind, List<Component>> Catalog { get; init; }
    public required Dictionary<int, JsonNode> Terminals { get; init; }
    public required Starmap Starmap { get; init; }
    public required List<string> ShipNames { get; init; }

    public static async Task<DataStore> LoadAsync(WikiClient client, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Loading ship list ...");
        List<string> ships = await client.VehicleNamesAsync(true, ct).ConfigureAwait(false);
        Dictionary<Kind, List<Component>> catalog = await Core.Catalog.LoadAsync(client, progress, ct).ConfigureAwait(false);
        progress?.Report("Loading shop locations ...");
        Dictionary<int, JsonNode> terminals = await client.TerminalsAsync(ct).ConfigureAwait(false);
        progress?.Report("Loading star map ...");
        Starmap starmap = Starmap.LoadEmbedded();
        return new DataStore { Client = client, Catalog = catalog, Terminals = terminals, Starmap = starmap, ShipNames = ships };
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
        JsonNode vehicle = await data.Client.VehicleAsync(shipName, ct).ConfigureAwait(false);
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
