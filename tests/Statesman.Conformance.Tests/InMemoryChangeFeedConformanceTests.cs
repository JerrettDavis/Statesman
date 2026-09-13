namespace Statesman.Conformance.Tests;

/// <summary>The shared change-feed conformance suite, run against the in-memory provider.</summary>
public sealed class InMemoryChangeFeedConformanceTests : ChangeFeedConformanceTests
{
    // AppendAsync's only clock read is OccurredAt, immediately after the position is allocated.
    protected override int PauseCallIndex => 1;

    protected override ValueTask<ConformanceStore?> CreateAsync(TimeProvider clock) =>
        ConformanceProviders.InMemoryAsync(clock);
}
