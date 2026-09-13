namespace Statesman.Conformance.Tests;

/// <summary>The shared ledger-replica suite, run against the filesystem provider.</summary>
public sealed class FileSystemLedgerReplicaConformanceTests : LedgerReplicaConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.FileSystemAsync(TimeProvider.System);
}
