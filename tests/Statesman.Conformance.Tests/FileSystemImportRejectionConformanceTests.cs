namespace Statesman.Conformance.Tests;

/// <summary>The shared import-rejection suite, run against the filesystem provider.</summary>
public sealed class FileSystemImportRejectionConformanceTests : ImportRejectionConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.FileSystemAsync(TimeProvider.System);
}
