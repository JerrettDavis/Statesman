namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared change-notifier suite, run against the tiered provider over in-memory hot and cold
/// stores. <c>TieredStateLedgerStore.SubscribeAsync</c> delegates to its cold tier (not hot, unlike
/// the generic capability forwarder), and the in-memory cold tier here does implement
/// <see cref="IStateChangeNotifier"/>, so tiered delegation is observable in this subclass.
/// </summary>
public sealed class TieredChangeNotifierConformanceTests : ChangeNotifierConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.TieredOverInMemoryAsync(TimeProvider.System);
}
