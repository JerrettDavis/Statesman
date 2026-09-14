namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared retention suite, run against the filesystem provider. Its change log is append-only, so
/// the feed fact reaches <see cref="ConformanceStore.Maintain"/> to compact away the dangling line a
/// prune leaves behind.
/// </summary>
public sealed class FileSystemRetentionConformanceTests : RetentionConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.FileSystemAsync(Clock);
}
