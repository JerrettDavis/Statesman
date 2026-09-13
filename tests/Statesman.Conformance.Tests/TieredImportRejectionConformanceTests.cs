namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared import-rejection suite, run against the tiered provider over in-memory hot and cold
/// stores. <c>TieredStateLedgerStore</c> vetoes <see cref="IStateLedgerReplica"/> outright — the
/// "authoritative write" forwarding classification — so <c>TryGetCapability</c> returns false here and
/// all three facts skip with "This provider is not an import target." That is the honest "No", loud
/// rather than silent, not three passes that would say nothing about this provider.
/// </summary>
public sealed class TieredImportRejectionConformanceTests : ImportRejectionConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.TieredOverInMemoryAsync(TimeProvider.System);
}
