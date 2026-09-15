namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared serializer-envelope suite, run against the Entity Framework Core provider, whose
/// envelope is a nullable <c>EnvelopeJson</c> column on the head entity and on the history entity
/// alike.
/// </summary>
public sealed class EntityFrameworkSerializerEnvelopeConformanceTests : SerializerEnvelopeConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.EntityFrameworkAsync(TimeProvider.System);
}
