namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared cancellation suite, run against the Entity Framework Core provider over the shared
/// single-connection fixture. Every assertion in this suite is sequential, so the default
/// concurrency (a single connection) is sufficient, unlike the conditional-write suite's racing
/// appends.
/// </summary>
public sealed class EntityFrameworkCancellationConformanceTests : CancellationConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.EntityFrameworkAsync(TimeProvider.System);
}
