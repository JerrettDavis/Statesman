namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared ledger-replica suite, run against the tiered provider over in-memory hot and cold stores.
/// <c>TieredStateLedgerStore</c> vetoes <see cref="IStateLedgerReplica"/> outright — the hot tier's
/// import channel is a private cache-repair mechanism, never forwarded to a caller — so
/// <c>TryGetCapability</c> returns false here and all four facts skip with "This provider is not an
/// import target." That is the honest "No", loud rather than silent, not four passes that would say
/// nothing about this provider.
/// </summary>
public sealed class TieredLedgerReplicaConformanceTests : LedgerReplicaConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.TieredOverInMemoryAsync(TimeProvider.System);
}
