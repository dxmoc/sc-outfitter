using ScOutfitter.Tests;

var runner = new TestRunner();

await runner.RunAsync("Star map", StarmapTests.RunAsync);
await runner.RunAsync("Catalog parsing", CatalogTests.RunAsync);
await runner.RunAsync("Ship hardpoints", ShipTests.RunAsync);
await runner.RunAsync("Optimizer", OptimizerTests.RunAsync);
await runner.RunAsync("Routing", RoutingTests.RunAsync);
await runner.RunAsync("Planner", PlannerTests.RunAsync);
await runner.RunAsync("erkul import", ErkulTests.RunAsync);
await runner.RunAsync("Windows construct", AppTests.RunAsync);

return runner.Report();
