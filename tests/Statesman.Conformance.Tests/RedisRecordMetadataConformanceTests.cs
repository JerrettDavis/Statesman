namespace Statesman.Conformance.Tests;

/// <summary>The shared record-metadata suite, run against a live Redis when one is configured.</summary>
public sealed class RedisRecordMetadataConformanceTests : RecordMetadataConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.RedisAsync(TimeProvider.System);

    protected override string SkipReason => ConformanceProviders.RedisSkipReason;
}
