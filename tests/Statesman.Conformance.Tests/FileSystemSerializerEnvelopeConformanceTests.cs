namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared serializer-envelope suite, run against the filesystem provider, whose head and history
/// records are separate JSON files, each carrying the envelope as a nested object.
/// </summary>
public sealed class FileSystemSerializerEnvelopeConformanceTests : SerializerEnvelopeConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.FileSystemAsync(TimeProvider.System);
}
