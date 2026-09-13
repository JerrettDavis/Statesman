namespace Statesman.Conformance.Tests;

/// <summary>The shared cancellation suite, run against the in-memory provider.</summary>
public sealed class InMemoryCancellationConformanceTests : CancellationConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.InMemoryAsync(TimeProvider.System);
}
