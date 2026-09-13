namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared cancellation suite, run against the tiered provider over in-memory hot and cold stores.
/// This fixture advertises only <see cref="IStateChangeFeed"/> and <see cref="IPartitionCatalog"/> —
/// expected, and the base suite's <c>Assert.NotEmpty</c> guard is what proves capability discovery
/// still ran.
/// </summary>
public sealed class TieredCancellationConformanceTests : CancellationConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.TieredOverInMemoryAsync(TimeProvider.System);
}
