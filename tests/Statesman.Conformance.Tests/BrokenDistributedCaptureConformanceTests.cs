using Xunit.Sdk;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The break-the-mechanism proof for <see cref="DistributedCaptureConformanceTests"/>'s every-address
/// assertion. A conformance suite's own mechanism is discrimination: if its assertions pass against a
/// store that does not implement the contract, they prove nothing about the stores that do. The shared
/// assertion is therefore run here twice — against a deliberately wrong double, which must fail, and
/// against a deliberately correct one, which must pass. The second half matters as much as the first: a
/// suite that failed against everything would be as useless as one that passed against everything.
/// </summary>
public sealed class BrokenDistributedCaptureConformanceTests
{
    [Fact]
    public async Task The_distributed_capture_assertion_fails_against_a_store_that_omits_absent_addresses()
    {
        await using var store = new AbsentOmittingCaptureStore();

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            DistributedCaptureConformanceTests.AssertEveryRequestedAddressGetsAnEntryAsync(store, store));
    }

    [Fact]
    public async Task The_distributed_capture_assertion_passes_against_a_store_that_reports_every_address()
    {
        await using var store = new EveryAddressCaptureStore();

        await DistributedCaptureConformanceTests.AssertEveryRequestedAddressGetsAnEntryAsync(store, store);
    }

    /// <summary>
    /// Implements both <see cref="IStateLedgerStore"/> and <see cref="IDistributedCapture"/>,
    /// wrapping an inner in-memory store but omitting any address that has never been written from
    /// its captured dictionary, instead of reporting it with a null value. Deliberately wrong: this
    /// is the exact defect the shared assertion exists to catch — a caller that trusted "the caller
    /// must never need to handle a missing key" would fault on a dictionary lookup for exactly the
    /// address this store forgot.
    /// </summary>
    private sealed class AbsentOmittingCaptureStore : IStateLedgerStore, IDistributedCapture
    {
        private readonly InMemoryStateLedgerStore _inner = new("absent-omitting-capture", TimeProvider.System);

        public string Name => _inner.Name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            _inner.ReadLatestAsync(address, cancellationToken);

        public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryAsync(address, options, cancellationToken);

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(address, condition, commit, cancellationToken);

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(address, policy, cancellationToken);

        public async ValueTask<IReadOnlyDictionary<StateAddress, StateRecord?>> CaptureAsync(
            IEnumerable<StateAddress> addresses,
            StateCaptureConsistency required,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(addresses);
            var result = new Dictionary<StateAddress, StateRecord?>();
            foreach (StateAddress address in addresses)
            {
                // Deliberately wrong: an address with no record is skipped instead of being
                // reported with a null value.
                StateRecord? record = await _inner.ReadLatestAsync(address, cancellationToken).ConfigureAwait(false);
                if (record is not null)
                {
                    result[address] = record;
                }
            }

            return result;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>
    /// Implements both <see cref="IStateLedgerStore"/> and <see cref="IDistributedCapture"/>,
    /// wrapping an inner in-memory store and reporting a null value for every address that has never
    /// been written. Correct: this is what <see cref="IDistributedCapture"/> requires, without
    /// needing any provider's real cross-process machinery.
    /// </summary>
    private sealed class EveryAddressCaptureStore : IStateLedgerStore, IDistributedCapture
    {
        private readonly InMemoryStateLedgerStore _inner = new("every-address-capture", TimeProvider.System);

        public string Name => _inner.Name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            _inner.ReadLatestAsync(address, cancellationToken);

        public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryAsync(address, options, cancellationToken);

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(address, condition, commit, cancellationToken);

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(address, policy, cancellationToken);

        public async ValueTask<IReadOnlyDictionary<StateAddress, StateRecord?>> CaptureAsync(
            IEnumerable<StateAddress> addresses,
            StateCaptureConsistency required,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(addresses);
            var result = new Dictionary<StateAddress, StateRecord?>();
            foreach (StateAddress address in addresses)
            {
                result[address] = await _inner.ReadLatestAsync(address, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
