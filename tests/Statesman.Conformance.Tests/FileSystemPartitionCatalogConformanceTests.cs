namespace Statesman.Conformance.Tests;

/// <summary>The shared partition-catalog suite, run against the filesystem provider.</summary>
public sealed class FileSystemPartitionCatalogConformanceTests : PartitionCatalogConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.FileSystemAsync(TimeProvider.System);
}
