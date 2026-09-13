namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared ledger-replica suite, run against the Entity Framework Core provider over the shared
/// single-connection fixture. Every assertion in this suite is sequential, so the default concurrency
/// (a single connection) is sufficient.
/// </summary>
public sealed class EntityFrameworkLedgerReplicaConformanceTests : LedgerReplicaConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.EntityFrameworkAsync(TimeProvider.System);
}
