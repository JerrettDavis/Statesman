namespace Statesman.Conformance.Tests;

/// <summary>The shared serializer-envelope suite, run against the in-memory provider.</summary>
public sealed class InMemorySerializerEnvelopeConformanceTests : SerializerEnvelopeConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.InMemoryAsync(TimeProvider.System);
}
