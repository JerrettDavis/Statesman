namespace Statesman.Conformance.Tests;

/// <summary>The shared conditional-write suite, run against the in-memory provider.</summary>
public sealed class InMemoryLedgerWriteConformanceTests : LedgerWriteConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.InMemoryAsync(TimeProvider.System);
}
