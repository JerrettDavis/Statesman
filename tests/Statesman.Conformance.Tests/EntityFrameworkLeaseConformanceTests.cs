using Statesman.Testing;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared lease conformance suite, run against the Entity Framework Core provider over the shared
/// single-connection test database.
/// </summary>
/// <remarks>
/// In-memory rather than the file-backed database <c>EntityFrameworkChangeFeedConformanceTests</c>
/// needs: every assertion in the lease suite is sequential, so no two of this provider's serializable
/// transactions are ever open at once. That is the condition a shared single connection cannot meet,
/// and the change-feed suite's in-flight test is the one that needs it.
/// <c>EntityFrameworkLeaseProviderTests</c> already proves the shared-connection fixture works for
/// exactly these operations. Expiry is induced by fast-forwarding the <see cref="ManualTimeProvider"/>
/// the store judges expiry against, which makes it exact rather than a real wait.
/// </remarks>
public sealed class EntityFrameworkLeaseConformanceTests : LeaseConformanceTests
{
    private readonly ManualTimeProvider _clock = new();

    protected override TimeSpan LeaseTtl => TimeSpan.FromSeconds(30);

    protected override Task ExpireAsync()
    {
        _clock.Advance(LeaseTtl + TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.EntityFrameworkAsync(_clock);
}
