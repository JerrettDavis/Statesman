namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared record-metadata suite, run against the Entity Framework Core provider, whose metadata is
/// a serialized <c>MetadataJson</c> column on the head entity and on the history entity alike.
/// </summary>
public sealed class EntityFrameworkRecordMetadataConformanceTests : RecordMetadataConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.EntityFrameworkAsync(TimeProvider.System);
}
