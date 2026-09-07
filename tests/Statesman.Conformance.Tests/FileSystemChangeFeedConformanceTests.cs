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

    protected override ValueTask<ConformanceStore?> CreateAsync(TimeProvider clock)
    {
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemStateLedgerStore(
            "feed",
            new FileSystemStateLedgerStoreOptions { RootDirectory = directory },
            clock);

        return ValueTask.FromResult<ConformanceStore?>(new ConformanceStore
        {
            Store = store,
            Feed = store,
            Cleanup = () =>
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return ValueTask.CompletedTask;
            },
        });
    }
}
