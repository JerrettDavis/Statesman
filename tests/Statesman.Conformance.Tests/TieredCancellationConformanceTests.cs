namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared cancellation suite, run against the tiered provider over in-memory hot and cold stores.
/// This fixture advertises <see cref="IStateChangeFeed"/> and <see cref="IPartitionCatalog"/> through
/// the cold tier, plus <see cref="IDistributedCapture"/> through <see cref="TieredStateLedgerStore"/>'s
/// own self-type check, even though the in-memory cold tier itself does not implement it. The base
/// suite's <c>Assert.NotEmpty</c> guard is what proves capability discovery still ran.
/// </summary>
public sealed class TieredCancellationConformanceTests : CancellationConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.TieredOverInMemoryAsync(TimeProvider.System);
}
