namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared retention suite, run against the filesystem provider. Its change log is append-only, so
/// the feed fact reaches <see cref="ConformanceStore.Maintain"/> for parity with
/// <see cref="ChangeFeedConformanceTests"/>, not because the assertion depends on it: the provider's
/// read path already skips the dangling line a prune leaves behind, so the fact stays green with or
/// without compaction.
/// </summary>
public sealed class FileSystemRetentionConformanceTests : RetentionConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.FileSystemAsync(Clock);
}
