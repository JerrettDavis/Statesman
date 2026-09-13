namespace Statesman.Conformance.Tests;

/// <summary>The shared record-metadata suite, run against the in-memory provider.</summary>
public sealed class InMemoryRecordMetadataConformanceTests : RecordMetadataConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.InMemoryAsync(TimeProvider.System);
}
