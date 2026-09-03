using Microsoft.Extensions.DependencyInjection;

namespace Statesman.DependencyInjection.Tests;

public sealed class MultiRootTests
{
    private static readonly StateKey<int> Count = StateKey.Define<int>("shared/count");

    [Fact]
    public async Task Multiple_roots_have_independent_addresses_lifecycles_and_values()
    {
        StatesmanDeclaration users = global::Statesman.Statesman.Declare("users")
            .State(Count, state => state.Initial(0))
            .Build();
        StatesmanDeclaration inventory = global::Statesman.Statesman.Declare("inventory")
            .State(Count, state => state.Initial(0))
            .Build();
        var services = new ServiceCollection();
        services.AddStatesman(users);
        services.AddStatesman(inventory);
        await using ServiceProvider provider = services.BuildServiceProvider();

        IStatesmanRegistry registry = provider.GetRequiredService<IStatesmanRegistry>();
        IStatesman userRoot = registry.Get("users");
        IStatesman inventoryRoot = registry.Get("inventory");
        await userRoot.State(Count).SetAsync(3);
        await inventoryRoot.State(Count).SetAsync(8);

        Assert.Equal(2, registry.All.Count);
        Assert.Equal(3, userRoot.State(Count).Current.RequiredValue);
        Assert.Equal(8, inventoryRoot.State(Count).Current.RequiredValue);
        Assert.NotEqual(
            userRoot.State(Count).Address.Canonical,
            inventoryRoot.State(Count).Address.Canonical);
    }

    [Fact]
    public async Task Duplicate_root_ids_are_rejected_when_the_registry_is_materialized()
    {
        StatesmanDeclaration first = global::Statesman.Statesman.Declare("duplicate")
            .State<int>("one", state => state.Initial(1))
            .Build();
        StatesmanDeclaration second = global::Statesman.Statesman.Declare("duplicate")
            .State<int>("two", state => state.Initial(2))
            .Build();
        var services = new ServiceCollection();
        services.AddStatesman(first);
        services.AddStatesman(second);
        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<StateDeclarationException>(() => provider.GetRequiredService<IStatesmanRegistry>());
    }
}
