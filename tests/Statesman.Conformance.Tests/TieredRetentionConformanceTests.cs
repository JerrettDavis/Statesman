namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared retention suite, run against the tiered provider over in-memory hot and cold stores.
/// Both tiers are constructed with the same injected clock, so the age cutoff each computes is the
/// same one; see ROADMAP 0.3 pre-Phase-19 addendum decision 92.
/// </summary>
public sealed class TieredRetentionConformanceTests : RetentionConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.TieredOverInMemoryAsync(Clock);
}
