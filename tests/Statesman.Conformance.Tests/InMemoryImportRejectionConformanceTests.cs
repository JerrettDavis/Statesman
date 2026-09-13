namespace Statesman.Conformance.Tests;

/// <summary>The shared import-rejection suite, run against the in-memory provider.</summary>
public sealed class InMemoryImportRejectionConformanceTests : ImportRejectionConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.InMemoryAsync(TimeProvider.System);
}
