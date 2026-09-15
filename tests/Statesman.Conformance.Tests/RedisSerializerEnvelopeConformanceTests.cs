namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared serializer-envelope suite, run against a live Redis when one is configured. Redis
/// member identity is the record's serialized bytes across two keys, so the envelope changes what a
/// member looks like and this suite is what says the change is symmetric.
/// </summary>
public sealed class RedisSerializerEnvelopeConformanceTests : SerializerEnvelopeConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.RedisAsync(TimeProvider.System);

    protected override string SkipReason => ConformanceProviders.RedisSkipReason;
}
