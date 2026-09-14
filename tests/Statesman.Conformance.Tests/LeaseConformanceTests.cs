using Statesman.Testing;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The behaviour every <see cref="IStateLeaseProvider"/> implementation must share, run once per
/// provider by a subclass. ROADMAP 0.3 Phase 1 deferred this suite "to when a third provider exists";
/// a third still does not, and that stopped being the reason to wait once the two that do exist were
/// found to disagree about renewal after expiry for nine phases. This suite is what would have caught
/// that.
/// </summary>
public abstract class LeaseConformanceTests
{
    /// <summary>
    /// Creates a store with its lease provider, or returns <see langword="null"/> when the provider's
    /// infrastructure is not available here.
    /// </summary>
    protected abstract ValueTask<ConformanceStore?> CreateAsync();

    /// <summary>
    /// The TTL an acquisition takes when its lease must still be held at the moment the assertion
    /// lands. Long on every provider, because nothing in this suite waits it out: a hold TTL that is
    /// merely comfortable on an idle machine is the shape that fails on a loaded one.
    /// </summary>
    /// <remarks>
    /// Splitting the suite's original single TTL in two is what closes a measured Redis flake. The
    /// stale-holder fact below spends <b>two</b> Redis round trips between the successor's acquire and
    /// its renew — the stale holder's release script, then the renew script — so with one short TTL
    /// serving both roles a stall of about 200 ms anywhere in that window expired the successor's own
    /// lease and made its renew return false. Measured: with both TTLs at 200 ms an injected 250 ms
    /// stall failed ten runs out of ten, and with the hold TTL at 30 s a 1000 ms stall passed ten out
    /// of ten. <c>tests/Statesman.Redis.Tests/RedisLeaseProviderTests.cs</c> always had the asymmetry;
    /// the conformance suite lost it when it collapsed both acquisitions onto one constant.
    /// ROADMAP 0.3 pre-Phase-19 addendum decision 93.
    /// </remarks>
    protected abstract TimeSpan HoldTtl { get; }

    /// <summary>
    /// The TTL an acquisition takes when <see cref="ExpireAsync"/> is going to make it lapse.
    /// Provider-specific because expiry is: Entity Framework Core judges it against an injected
    /// <see cref="TimeProvider"/> a test can fast-forward, so a long TTL is exact there, while Redis
    /// judges it against the Redis server's own clock and needs a genuinely short one. The same shape
    /// as <c>ChangeFeedConformanceTests.PauseCallIndex</c>: a provider-specific constant a shared
    /// assertion needs.
    /// </summary>
    protected abstract TimeSpan ExpiringTtl { get; }

    /// <summary>
    /// Makes a lease acquired with <see cref="ExpiringTtl"/> lapse, without anyone else acquiring it.
    /// Advances a <see cref="ManualTimeProvider"/> past <see cref="ExpiringTtl"/> on Entity Framework
    /// Core; waits out a real delay on Redis, whose expiry no injected clock can reach.
    /// </summary>
    protected abstract Task ExpireAsync();

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    /// <summary>
    /// Asserts that one lease id can be held by only one caller at a time. Public and static so the
    /// negative test in this project can run it against a deliberately broken provider and require it
    /// to fail — a conformance suite that cannot fail is not a suite.
    /// </summary>
    /// <param name="leases">The lease provider under test.</param>
    /// <param name="ttl">The TTL each acquisition takes; every one of them must still be held.</param>
    public static async Task AssertExclusiveAcquisitionAsync(IStateLeaseProvider leases, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(leases);
        string leaseId = $"conformance-exclusive-{Guid.NewGuid():N}";

        IStateLease? first = await leases.AcquireAsync(leaseId, ttl);
        Assert.NotNull(first);

        // Null, not an exception: contention is a documented outcome, not a failure.
        IStateLease? second = await leases.AcquireAsync(leaseId, ttl);
        Assert.Null(second);

        await first!.DisposeAsync();

        IStateLease? third = await leases.AcquireAsync(leaseId, ttl);
        Assert.NotNull(third);
        await third!.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_grants_one_holder_at_a_time_and_a_release_frees_it()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store?.Leases is not null, SkipReason);

        await AssertExclusiveAcquisitionAsync(store!.Leases!, HoldTtl);
    }

    [Fact]
    public async Task Renew_extends_a_lease_that_is_still_held()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store?.Leases is not null, SkipReason);
        string leaseId = $"conformance-renew-{Guid.NewGuid():N}";

        IStateLease? lease = await store!.Leases!.AcquireAsync(leaseId, HoldTtl);
        Assert.NotNull(lease);

        Assert.True(await lease!.RenewAsync(HoldTtl));

        // Still exclusive after the renewal: extending a hold must not release it.
        Assert.Null(await store.Leases.AcquireAsync(leaseId, HoldTtl));

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Renew_returns_false_after_the_lease_was_released()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store?.Leases is not null, SkipReason);
        string leaseId = $"conformance-renew-released-{Guid.NewGuid():N}";

        IStateLease? lease = await store!.Leases!.AcquireAsync(leaseId, HoldTtl);
        Assert.NotNull(lease);
        await lease!.DisposeAsync();

        Assert.False(await lease.RenewAsync(HoldTtl));
    }

    [Fact]
    public async Task Renew_returns_false_after_the_lease_expired_with_nobody_racing()
    {
        // The disagreement this suite exists to expose. Before Phase 10, Entity Framework Core
        // renewed an expired lease that nobody else had taken and Redis did not; the interface said
        // only "returns false if it was lost" and never defined "lost". Nobody acquires in the gap
        // here, so this assertion is about the TTL bounding the hold and nothing else. The acquisition
        // takes the EXPIRING TTL, because this is a lease that has to lapse.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store?.Leases is not null, SkipReason);
        string leaseId = $"conformance-renew-expired-{Guid.NewGuid():N}";

        IStateLease? lease = await store!.Leases!.AcquireAsync(leaseId, ExpiringTtl);
        Assert.NotNull(lease);

        await ExpireAsync();

        Assert.False(await lease!.RenewAsync(ExpiringTtl));
    }

    [Fact]
    public async Task An_expired_lease_can_be_acquired_by_a_successor()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store?.Leases is not null, SkipReason);
        string leaseId = $"conformance-successor-{Guid.NewGuid():N}";

        IStateLease? staleHolder = await store!.Leases!.AcquireAsync(leaseId, ExpiringTtl);
        Assert.NotNull(staleHolder);

        await ExpireAsync();

        IStateLease? successor = await store.Leases.AcquireAsync(leaseId, HoldTtl);
        Assert.NotNull(successor);
        await successor!.DisposeAsync();
    }

    [Fact]
    public async Task A_stale_holders_dispose_does_not_release_a_successors_lease()
    {
        // The property that makes "drop the handle and re-acquire" safe for the outbox: release is
        // "delete only if the token still matches" on both providers, so disposing a handle someone
        // else has taken over is a no-op rather than a release of their lease.
        //
        // The split TTLs are load-bearing here and nowhere else in this file is the difference as
        // sharp. The stale holder's lease must LAPSE, so it takes the expiring TTL. The successor's
        // lease must still be alive two round trips later -- past the stale holder's release script
        // and through its own renew script -- so it takes the hold TTL. Giving the successor the
        // expiring TTL is the shape that flaked on a loaded runner.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store?.Leases is not null, SkipReason);
        string leaseId = $"conformance-stale-{Guid.NewGuid():N}";

        IStateLease? staleHolder = await store!.Leases!.AcquireAsync(leaseId, ExpiringTtl);
        Assert.NotNull(staleHolder);

        await ExpireAsync();

        IStateLease? successor = await store.Leases.AcquireAsync(leaseId, HoldTtl);
        Assert.NotNull(successor);

        await staleHolder!.DisposeAsync();

        Assert.True(await successor!.RenewAsync(HoldTtl));
        await successor.DisposeAsync();
    }
}
