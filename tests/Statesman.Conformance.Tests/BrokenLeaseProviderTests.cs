using Xunit.Sdk;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The break-the-mechanism proof for <see cref="LeaseConformanceTests"/>. A conformance suite's own
/// mechanism is discrimination: if its assertions pass against a provider that does not implement the
/// contract, they prove nothing about the providers that do. So the suite's exclusion assertion is a
/// reusable method, and this runs it against a lease provider whose acquire always succeeds and
/// requires it to fail.
/// </summary>
public sealed class BrokenLeaseProviderTests
{
    [Fact]
    public async Task The_exclusion_assertion_fails_against_a_provider_that_grants_every_acquire()
    {
        await Assert.ThrowsAnyAsync<XunitException>(() =>
            LeaseConformanceTests.AssertExclusiveAcquisitionAsync(
                new AlwaysGrantingLeaseProvider(), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task The_exclusion_assertion_passes_against_a_provider_that_excludes_correctly()
    {
        // The other half: a suite that failed against everything would also be useless. This double
        // holds one lease id at a time, so the assertion must pass -- which is what shows the failure
        // above came from the broken behaviour rather than from the assertion itself.
        await LeaseConformanceTests.AssertExclusiveAcquisitionAsync(
            new ExcludingLeaseProvider(), TimeSpan.FromSeconds(30));
    }

    /// <summary>Grants every acquire, ignoring who holds the lease. Deliberately wrong.</summary>
    private sealed class AlwaysGrantingLeaseProvider : IStateLeaseProvider
    {
        public ValueTask<IStateLease?> AcquireAsync(string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IStateLease?>(new NoOpLease());
    }

    /// <summary>Holds one lease id at a time, in memory. Deliberately correct.</summary>
    private sealed class ExcludingLeaseProvider : IStateLeaseProvider
    {
        private readonly HashSet<string> _held = new(StringComparer.Ordinal);

        public ValueTask<IStateLease?> AcquireAsync(string leaseId, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            lock (_held)
            {
                return ValueTask.FromResult<IStateLease?>(
                    _held.Add(leaseId) ? new HeldLease(this, leaseId) : null);
            }
        }

        private void Release(string leaseId)
        {
            lock (_held)
            {
                _held.Remove(leaseId);
            }
        }

        private sealed class HeldLease : IStateLease
        {
            private readonly ExcludingLeaseProvider _provider;
            private readonly string _leaseId;

            public HeldLease(ExcludingLeaseProvider provider, string leaseId)
            {
                _provider = provider;
                _leaseId = leaseId;
            }

            public ValueTask<bool> RenewAsync(TimeSpan ttl, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(true);

            public ValueTask DisposeAsync()
            {
                _provider.Release(_leaseId);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class NoOpLease : IStateLease
    {
        public ValueTask<bool> RenewAsync(TimeSpan ttl, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
