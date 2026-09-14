namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared retention suite, run against the Entity Framework Core provider over the shared
/// single-connection test database. Every assertion here is sequential, so no two of this provider's
/// transactions are ever open at once.
/// </summary>
public sealed class EntityFrameworkRetentionConformanceTests : RetentionConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.EntityFrameworkAsync(Clock);
}
