using System.Collections.Concurrent;

namespace Statesman;

internal sealed class DependencyInjectionStateStoreResolver : IStateStoreResolver, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly IReadOnlyDictionary<string, IStateStoreFactory> _factories;
    private readonly ConcurrentDictionary<string, IStateLedgerStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public DependencyInjectionStateStoreResolver(
        IServiceProvider services,
        IEnumerable<IStateStoreFactory> factories)
    {
        _services = services;
        _factories = factories
            .GroupBy(factory => factory.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public IStateLedgerStore Resolve(string name)
    {
        if (!_factories.TryGetValue(name, out IStateStoreFactory? factory))
        {
            throw new KeyNotFoundException(
                $"No Statesman ledger store named '{name}' is registered. Available stores: {string.Join(", ", _factories.Keys.Order(StringComparer.OrdinalIgnoreCase))}.");
        }

        return _stores.GetOrAdd(name, _ => factory.Create(_services));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IStateLedgerStore store in _stores.Values.Distinct())
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }

        _stores.Clear();
    }
}
