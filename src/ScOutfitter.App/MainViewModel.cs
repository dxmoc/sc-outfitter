using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ScOutfitter.Core;

namespace ScOutfitter.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Func<Task> run, Func<bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;

    public async void Execute(object? parameter) => await run();

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class GoalRow(string name, string description, bool enabled, double weight) : Observable
{
    private bool _enabled = enabled;
    private string _weight = weight.ToString("0.#", CultureInfo.InvariantCulture);

    public string Name { get; } = name;
    public string Description { get; } = description;

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public string Weight
    {
        get => _weight;
        set => Set(ref _weight, value);
    }

    public double WeightValue =>
        double.TryParse(Weight.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w > 0 ? w : 1;
}

public sealed record RouteRow(int Step, string StepText, string Where, string System, string Distance, string Items, bool IsExtra, Stop Stop);

public sealed record BuyRow(string Item, int Quantity, string Shop, string Price);

public sealed record LoadoutRow(string Kind, int Size, string Item, string Grade, string Status, string Stock, string Stats, bool IsKeep, bool IsFixed);

public sealed class MainViewModel : Observable
{
    private DataStore _data;
    private string _shipText = "Gladius";
    private string _selectedSystem = "Stanton";
    private string _selectedBody = "Hurston";
    private string _selectedPlace = "Everus Harbor";
    private bool _keepGimbals, _mannedTurrets, _buyAll;
    private string _maxGrade = "any";
    private bool _busy;
    private string _status = "Pick a ship and where you are, then plan the route.";
    private RouteRow? _selectedRoute;
    private Plan? _plan;

    public MainViewModel(DataStore data)
    {
        _data = data;
        ShipNames = data.ShipNames;
        Systems = data.Starmap.Systems();
        Goals? defaults = Core.Goals.Default();
        foreach ((string name, string desc) in Core.Goals.All)
        {
            GoalRows.Add(new GoalRow(name, desc, defaults.ContainsKey(name), defaults.GetValueOrDefault(name, 1)));
        }

        PlanCommand = new RelayCommand(PlanAsync, () => !Busy);
        RefreshCommand = new RelayCommand(RefreshAsync, () => !Busy);
        RefreshBodies();
    }

    public IReadOnlyList<string> ShipNames { get; private set; }
    public RelayCommand RefreshCommand { get; }

    /// <summary>"prices: UEX, newest report 2 h ago · loaded 15:40"</summary>
    public string DataText
    {
        get
        {
            string prices = _data.PricesNewestUtc is { } n
                ? $"prices: UEX live ({_data.LivePriced} parts), newest report {Ago(n)}"
                : "prices: wiki mirror (UEX unreachable)";
            return $"{prices}  ·  loaded {_data.LoadedAt:HH:mm}";
        }
    }

    private static string Ago(DateTime utc)
    {
        TimeSpan d = DateTime.UtcNow - utc;
        return d.TotalMinutes < 90 ? $"{Math.Max(1, (int)d.TotalMinutes)} min ago"
            : d.TotalHours < 36 ? $"{(int)d.TotalHours} h ago"
            : $"{(int)d.TotalDays} days ago";
    }

    private async Task RefreshAsync()
    {
        Busy = true;
        Status = "Reloading everything from the wiki and UEX ...";
        var progress = new Progress<string>(text => Status = text);
        try
        {
            DataStore fresh = await Task.Run(() => _data.ReloadAsync(progress));
            _data = fresh;
            ShipNames = fresh.ShipNames;
            Raise(nameof(ShipNames));
            Raise(nameof(DataText));
            Status = "Data refreshed. Plan again to use the new prices.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException or IOException)
        {
            Status = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }
    public IReadOnlyList<string> Systems { get; }
    public ObservableCollection<string> Bodies { get; } = [];
    public ObservableCollection<string> Places { get; } = [];
    public IReadOnlyList<string> Grades { get; } = ["any", "A", "B", "C", "D"];
    public ObservableCollection<GoalRow> GoalRows { get; } = [];
    public ObservableCollection<RouteRow> RouteRows { get; } = [];
    public ObservableCollection<BuyRow> BuyRows { get; } = [];
    public ObservableCollection<LoadoutRow> LoadoutRows { get; } = [];
    public RelayCommand PlanCommand { get; }

    public string ShipText
    {
        get => _shipText;
        set => Set(ref _shipText, value);
    }

    public string SelectedSystem
    {
        get => _selectedSystem;
        set
        {
            if (Set(ref _selectedSystem, value))
            {
                RefreshBodies();
            }
        }
    }

    public string SelectedBody
    {
        get => _selectedBody;
        set
        {
            if (Set(ref _selectedBody, value))
            {
                RefreshPlaces();
            }
        }
    }

    public string SelectedPlace
    {
        get => _selectedPlace;
        set => Set(ref _selectedPlace, value);
    }

    public bool KeepGimbals
    {
        get => _keepGimbals;
        set => Set(ref _keepGimbals, value);
    }

    public bool MannedTurrets
    {
        get => _mannedTurrets;
        set => Set(ref _mannedTurrets, value);
    }

    public bool BuyAll
    {
        get => _buyAll;
        set => Set(ref _buyAll, value);
    }

    public string MaxGrade
    {
        get => _maxGrade;
        set => Set(ref _maxGrade, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                PlanCommand.Refresh();
                RefreshCommand.Refresh();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool HasPlan => _plan is not null;

    public RouteRow? SelectedRoute
    {
        get => _selectedRoute;
        set
        {
            if (Set(ref _selectedRoute, value))
            {
                BuyRows.Clear();
                foreach (Buy b in value?.Stop.Buys ?? [])
                {
                    BuyRows.Add(new BuyRow(b.Item, b.Quantity, b.Shop, b.Price.ToString("N0", CultureInfo.InvariantCulture)));
                }
            }
        }
    }

    // summary tiles
    public string ShipTitle => _plan is null ? "No plan yet" : _plan.Ship.Name;
    public string GoalsText => _plan is null ? string.Empty : "goals: " + _plan.Goals.Describe();
    public string DpsText => _plan is null ? "-" : Inv($"{_plan.Totals.Dps:N0}");
    public string ShieldText => _plan is null ? "-" : Inv($"{_plan.Totals.ShieldHp:N0}");
    public string MissileText => _plan is null ? "-" : Inv($"{_plan.Totals.MissileDamage:N0}");
    public string CostText => _plan is null ? "-" : Inv($"{_plan.Trip.Cost:N0}");
    public string DistanceText => _plan is null ? "-" : Inv($"{_plan.Trip.Km / 1e6:0.00} Gm");
    public string PowerText => _plan is null ? "-" : Inv($"{_plan.Budget.PowerUsage:0.#} / {_plan.Budget.PowerGeneration:0}");
    public string CoolingText => _plan is null ? "-" : Inv($"{_plan.Budget.CoolingUsage:0.#} / {_plan.Budget.CoolingGeneration:0}");
    public string RouteNote { get; private set; } = string.Empty;

    private static string Inv(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);

    private void RefreshBodies()
    {
        Bodies.Clear();
        foreach (string b in _data.Starmap.Bodies(SelectedSystem))
        {
            Bodies.Add(b);
        }

        SelectedBody = Bodies.FirstOrDefault() ?? string.Empty;
        RefreshPlaces();
    }

    private void RefreshPlaces()
    {
        Places.Clear();
        Places.Add("(in orbit)");
        foreach (string p in _data.Starmap.Places(SelectedSystem, SelectedBody))
        {
            Places.Add(p);
        }

        SelectedPlace = Places.Count > 1 ? Places[1] : Places[0];
    }

    /// <summary>Preselect a place by name (used once at start-up for Everus Harbor).</summary>
    public void SelectStart(string system, string body, string place)
    {
        SelectedSystem = system;
        SelectedBody = body;
        SelectedPlace = Places.Contains(place) ? place : SelectedPlace;
    }

    private string StartName()
    {
        string body = SelectedBody.Replace(" (deep space)", "", StringComparison.Ordinal);
        return $"{SelectedSystem}/{(SelectedPlace == "(in orbit)" ? body : SelectedPlace)}";
    }

    private Goals CurrentGoals()
    {
        var goals = new Goals();
        foreach (GoalRow g in GoalRows.Where(g => g.Enabled))
        {
            goals[g.Name] = g.WeightValue;
        }

        return goals.Count > 0 ? goals : Core.Goals.Default();
    }

    private async Task PlanAsync()
    {
        string ship = ShipText.Trim();
        if (ship.Length == 0)
        {
            Status = "Enter a ship name.";
            return;
        }

        Busy = true;
        Status = $"Planning {ship} from {StartName()} ...";
        var options = new PlanOptions
        {
            KeepGimbals = KeepGimbals, MannedTurrets = MannedTurrets, BuyAll = BuyAll,
            MaxGrade = MaxGrade == "any" ? null : MaxGrade,
        };
        Goals goals = CurrentGoals();
        string start = StartName();
        try
        {
            Plan plan = await Task.Run(() => Planner.MakeAsync(_data, ship, start, goals, options));
            Render(plan);
            Status = $"{plan.Ship.Name}: {plan.Trip.Stops.Count} stop(s)";
        }
        catch (Exception ex) when (ex is LookupException or HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            Status = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private void Render(Plan plan)
    {
        _plan = plan;
        RouteRows.Clear();
        LoadoutRows.Clear();
        BuyRows.Clear();
        int i = 0;
        foreach (Stop s in plan.Trip.Planned)
        {
            i++;
            string where = s.Location.Label + (s.Jumps > 0 ? $"   via {s.Jumps} jump point{(s.Jumps > 1 ? "s" : "")}" : string.Empty);
            RouteRows.Add(new RouteRow(i, i.ToString(CultureInfo.InvariantCulture), where, s.System,
                Inv($"{s.LegKm / 1e6:0.00} Gm"), Items(s), false, s));
        }

        foreach (Stop s in plan.Trip.Extra)
        {
            RouteRows.Add(new RouteRow(0, "-", s.Location.Name, s.System, "?", Items(s) + "  (not on the map)", true, s));
        }

        foreach (Pick p in plan.Picks)
        {
            string status = p.Fixed ? "fixed" : p.Keep ? "keep" : p.Price.ToString("N0", CultureInfo.InvariantCulture);
            string name = p.Quantity > 1 ? $"{p.Quantity}x {p.Component.Name}" : p.Component.Name;
            LoadoutRows.Add(new LoadoutRow(p.Component.Kind.Label(), p.Component.Size, name, p.Component.Grade, status,
                p.Stock ?? "-", p.Component.Summary(), p.Keep, p.Fixed));
        }

        RouteNote = plan.Trip.Stops.Count == 0
            ? "Nothing to buy: the stock parts already win for these goals."
            : $"Start {plan.Trip.Start.Label} [{plan.Trip.Start.System}], quantum drive {plan.Drive.Name}"
              + (plan.Trip.Unavailable.Count > 0 ? $"   ·   not sold anywhere: {string.Join(", ", plan.Trip.Unavailable)}" : string.Empty);
        SelectedRoute = RouteRows.FirstOrDefault();

        foreach (string prop in new[] { nameof(HasPlan), nameof(ShipTitle), nameof(GoalsText), nameof(DpsText), nameof(ShieldText),
                     nameof(MissileText), nameof(CostText), nameof(DistanceText),
                     nameof(PowerText), nameof(CoolingText), nameof(RouteNote) })
        {
            Raise(prop);
        }
    }

    private static string Items(Stop s) =>
        string.Join(", ", s.Buys.Select(b => b.Quantity > 1 ? $"{b.Quantity}x {b.Item}" : b.Item));
}
