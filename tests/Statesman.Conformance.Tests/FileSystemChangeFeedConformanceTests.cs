namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared change-feed conformance suite, run against the filesystem provider with default
/// options — including <c>FlushToDisk = true</c>, the fsync the widened critical section
/// serializes.
/// </summary>
public sealed class FileSystemChangeFeedConformanceTests : ChangeFeedConformanceTests
{
    // NextGlobalPosition reads the clock to seed the position before OccurredAt reads it again, so
    // the call that sits between allocation and publication is the second one.
    protected override int PauseCallIndex => 2;

    protected override ValueTask<ConformanceStore?> CreateAsync(TimeProvider clock) =>
        ConformanceProviders.FileSystemAsync(clock);
}
