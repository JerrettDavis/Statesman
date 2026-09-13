namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared record-metadata suite, run against the filesystem provider, whose head and history
/// records are separate JSON files — the layout where a metadata dictionary is easiest to write to one
/// and not the other.
/// </summary>
public sealed class FileSystemRecordMetadataConformanceTests : RecordMetadataConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.FileSystemAsync(TimeProvider.System);
}
