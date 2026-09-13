namespace Statesman.Conformance.Tests;

/// <summary>The shared cancellation suite, run against the filesystem provider.</summary>
public sealed class FileSystemCancellationConformanceTests : CancellationConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.FileSystemAsync(TimeProvider.System);
}
