namespace Statesman.Conformance.Tests;

/// <summary>The shared retention suite, run against the in-memory provider.</summary>
public sealed class InMemoryRetentionConformanceTests : RetentionConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.InMemoryAsync(Clock);
}
