namespace Statesman.Conformance.Tests;

/// <summary>The shared change-notifier suite, run against the in-memory provider.</summary>
public sealed class InMemoryChangeNotifierConformanceTests : ChangeNotifierConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.InMemoryAsync(TimeProvider.System);
}
