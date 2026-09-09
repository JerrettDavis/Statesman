namespace Statesman.Conformance.Tests;

/// <summary>
/// One provider's store, its change feed, optionally its lease provider, and whatever cleanup that
/// provider needs, handed to the shared conformance tests as a single disposable unit.
/// </summary>
public sealed class ConformanceStore : IAsyncDisposable
{
    /// <summary>The store under test, constructed with default options.</summary>
    public required IStateLedgerStore Store { get; init; }

    /// <summary>The same store's change feed.</summary>
    public required IStateChangeFeed Feed { get; init; }

    /// <summary>The same store's lease provider, when it has one. Null for a provider that does not implement <see cref="IStateLeaseProvider"/>.</summary>
    public IStateLeaseProvider? Leases { get; init; }

    /// <summary>Provider-specific cleanup run after the store is disposed, if any.</summary>
    public Func<ValueTask>? Cleanup { get; init; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync().ConfigureAwait(false);
        if (Cleanup is not null)
        {
            await Cleanup().ConfigureAwait(false);
        }
    }
}
