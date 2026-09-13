namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared change-notifier suite, run against the Entity Framework Core provider. Every push
/// mechanism is engine-specific, so the provider-neutral Entity Framework Core store implements no
/// notifier (Ledger.cs), and every fact here skips with an honest "No".
/// </summary>
public sealed class EntityFrameworkChangeNotifierConformanceTests : ChangeNotifierConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.EntityFrameworkAsync(TimeProvider.System);
}
