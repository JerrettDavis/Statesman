namespace Statesman;

/// <summary>Simple resolver for hosts that do not use the dependency-injection integration.</summary>
public sealed class StateStoreResolver : IStateStoreResolver, IAsyncDisposable
{
    private readonly Dictionary<string, IStateLedgerStore> _stores;
    private readonly bool _ownsStores;

    public StateStoreResolver(IEnumerable<IStateLedgerStore> stores, bool ownsStores = false)
    {
        _stores = stores.ToDictionary(store => store.Name, StringComparer.OrdinalIgnoreCase);
        _ownsStores = ownsStores;
    }

    public static StateStoreResolver InMemory(string name = "memory", TimeProvider? timeProvider = null) =>
        new(new[] { new InMemoryStateLedgerStore(name, timeProvider) }, ownsStores: true);

    public IStateLedgerStore Resolve(string name)
    {
        if (_stores.TryGetValue(name, out IStateLedgerStore? store))
        {
            return store;
        }

        throw new KeyNotFoundException(
            $"No Statesman ledger store named '{name}' is registered. Available stores: {string.Join(", ", _stores.Keys.Order(StringComparer.OrdinalIgnoreCase))}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ownsStores)
        {
            return;
        }

        foreach (IStateLedgerStore store in _stores.Values.Distinct())
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }
}
