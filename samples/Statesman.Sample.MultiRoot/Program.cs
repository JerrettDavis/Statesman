using Microsoft.Extensions.DependencyInjection;
using Statesman;

StateKey<int> activeUsers = StateKey.Define<int>("users/active-count");
StateKey<int> queuedJobs = StateKey.Define<int>("jobs/queued-count");

StatesmanDeclaration identity = global::Statesman.Statesman.Declare("identity", "1.0")
    .Defaults(defaults => defaults.StoreWith("identity-memory"))
    .State(activeUsers, state => state.Initial(0))
    .Build();
StatesmanDeclaration operations = global::Statesman.Statesman.Declare("operations", "1.0")
    .Defaults(defaults => defaults.StoreWith("operations-memory"))
    .State(queuedJobs, state => state.Initial(0))
    .Build();

var services = new ServiceCollection();
services.AddStatesman(identity, statesman => statesman.UseInMemoryStore("identity-memory"));
services.AddStatesman(operations, statesman => statesman.UseInMemoryStore("operations-memory"));
await using ServiceProvider provider = services.BuildServiceProvider();
IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();

await registry.Get("identity").State(activeUsers).SetAsync(127);
await registry.Get("operations").State(queuedJobs).SetAsync(9);

foreach (IStatesman root in registry.All.OrderBy(root => root.Id))
{
    Console.WriteLine($"{root.Id}: {root.Manifest.Fingerprint}");
}
