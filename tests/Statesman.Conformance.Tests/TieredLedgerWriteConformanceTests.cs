namespace Statesman.Conformance.Tests;

/// <summary>The shared conditional-write suite, run against the tiered provider over in-memory hot and cold stores.</summary>
public sealed class TieredLedgerWriteConformanceTests : LedgerWriteConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.TieredOverInMemoryAsync(TimeProvider.System);
}
