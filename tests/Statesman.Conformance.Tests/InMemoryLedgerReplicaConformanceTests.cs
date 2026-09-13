namespace Statesman.Conformance.Tests;

/// <summary>The shared ledger-replica suite, run against the in-memory provider.</summary>
public sealed class InMemoryLedgerReplicaConformanceTests : LedgerReplicaConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.InMemoryAsync(TimeProvider.System);
}
