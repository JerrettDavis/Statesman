namespace Statesman.Conformance.Tests;

/// <summary>The shared conditional-write suite, run against the filesystem provider.</summary>
public sealed class FileSystemLedgerWriteConformanceTests : LedgerWriteConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.FileSystemAsync(TimeProvider.System);
}
