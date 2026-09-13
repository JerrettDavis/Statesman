namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared cancellation suite, run against the tiered provider over in-memory hot and cold stores.
/// This fixture advertises <see cref="IStateChangeFeed"/> and <see cref="IPartitionCatalog"/> through
/// the cold tier. It does <b>not</b> advertise <see cref="IDistributedCapture"/>: the in-memory cold
/// tier cannot back it, and since ROADMAP 0.3 Phase 17 the tiered store declines a delegated capability
/// its tier cannot back rather than answering yes and throwing at first use. The base suite's
/// <c>Assert.NotEmpty</c> guard is what proves capability discovery still ran.
/// </summary>
public sealed class TieredCancellationConformanceTests : CancellationConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.TieredOverInMemoryAsync(TimeProvider.System);
}
