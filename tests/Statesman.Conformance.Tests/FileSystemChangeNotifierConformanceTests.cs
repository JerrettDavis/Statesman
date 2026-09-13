namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared change-notifier suite, run against the filesystem provider. Its change log is the only
/// channel it has to a reader in another process, and no reliable cross-process notification
/// primitive matches it (Ledger.cs), so every fact here skips with an honest "No".
/// </summary>
public sealed class FileSystemChangeNotifierConformanceTests : ChangeNotifierConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.FileSystemAsync(TimeProvider.System);
}
