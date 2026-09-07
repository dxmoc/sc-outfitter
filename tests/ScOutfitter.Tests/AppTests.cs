using System.Windows;
using System.Windows.Threading;
using ScOutfitter.App;
using ScOutfitter.Core;

namespace ScOutfitter.Tests;

/// <summary>Construct the windows on an STA thread: catches XAML/resource mistakes and binding names.</summary>
public static class AppTests
{
    public static Task RunAsync(TestRunner t)
    {
        var tcs = new TaskCompletionSource<bool>();
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/sc-outfitter;component/Theme.xaml"),
                    });
                }

                var splash = new SplashWindow();
                t.Check("splash constructs", splash.Title == "sc-outfitter");

                DataStore store = Fixtures.Store();
                var vm = new MainViewModel(store);
                var main = new MainWindow(vm);
                t.Check("main window constructs", main.Title == "sc-outfitter");
                t.Equal("start preselected", "Everus Harbor", vm.SelectedPlace);
                t.Equal("ten goal rows", 10, vm.GoalRows.Count);
                t.Check("default goals ticked", vm.GoalRows.First(g => g.Name == "dps").Enabled && !vm.GoalRows.First(g => g.Name == "cheap").Enabled);

                vm.SelectedSystem = "Pyro";
                t.Check("switching system refreshes bodies", vm.Bodies.Contains("Pyro (deep space)"));
                vm.SelectedBody = "Pyro (deep space)";
                t.Check("deep space lists Checkmate", vm.Places.Contains("Checkmate"));

                // run a plan on the fixture catalogue through the real view model
                vm.ShipText = "Gladius";
                main.Show();
                main.Hide();
                t.Check("plan command enabled when idle", vm.PlanCommand.CanExecute(null));
                splash.Close();
                main.Close();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                string chain = string.Join(" <- ", Walk(ex).Select(e => $"{e.GetType().Name}: {e.Message}"));
                t.Check("windows construct without exception", false, chain);
                tcs.SetResult(false);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static IEnumerable<Exception> Walk(Exception? ex)
    {
        while (ex is not null)
        {
            yield return ex;
            ex = ex.InnerException;
        }
    }
}

public static class PlannerTests
{
    public static Task RunAsync(TestRunner t)
    {
        DataStore store = Fixtures.Store();
        Location start = store.Starmap.Locate("Everus Harbor")!;
        Plan plan = Planner.Make(store, Fixtures.ShipJson(), start, Goals.Default(), new PlanOptions());
        t.Equal("ship name", "Test Ship", plan.Ship.Name);
        t.Equal("trip uses the equipped drive", "Beacon", plan.Drive.Name);
        t.Check("something to buy", plan.Trip.Stops.Count > 0);
        t.Check("cost matches picks", plan.Trip.Cost == plan.Totals.Cost);
        Plan newDrive = Planner.Make(store, Fixtures.ShipJson(), start, Goals.Default(), new PlanOptions { PlanWithNewDrive = true });
        t.Equal("planned drive when asked", "FoxFire", newDrive.Drive.Name);
        t.Check("faster drive, shorter trip", newDrive.Trip.Seconds < plan.Trip.Seconds);

        var empty = new System.Text.Json.Nodes.JsonObject { ["name"] = "Hull", ["ports"] = new System.Text.Json.Nodes.JsonArray() };
        bool threw = false;
        try
        {
            Planner.Make(store, empty, start, Goals.Default(), new PlanOptions());
        }
        catch (LookupException)
        {
            threw = true;
        }

        t.Check("hull without hardpoints is reported", threw);
        return Task.CompletedTask;
    }
}
