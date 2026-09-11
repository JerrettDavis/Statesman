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

    /// <summary>
    /// The maintenance step this provider needs before its change feed is fully repaired, or
    /// <see langword="null"/> when the provider repairs itself on every write.
    /// </summary>
    /// <remarks>
    /// Only the filesystem provider sets it. Its change log is append-only, so an import cannot
    /// rewrite an earlier line; the repair is <c>CompactChangeLogAsync</c>, which an operator calls
    /// on a maintenance cadence. This is deliberately a STEP rather than a per-provider expectation:
    /// a boolean that relaxed the assertion would let a provider regress into the "no" column
    /// silently, where a step cannot be satisfied by regressing. ROADMAP 0.3 Phase 13, addendum
    /// decision 10.
    /// </remarks>
    public Func<ValueTask>? Maintain { get; init; }

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
