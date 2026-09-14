using System.Runtime.CompilerServices;
using Statesman.Testing;
using Xunit.Sdk;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The break-the-mechanism proof for <see cref="RetentionConformanceTests"/>. That suite is pure
/// pinning — every assertion in it was already true on all five providers before it was written — so
/// its own mechanism is discrimination, and discrimination is what this file measures. Each of the
/// four wrong doubles below gets exactly one part of retention wrong, and each is run against the
/// shared assertion that part belongs to and required to fail. The correct double is run against all
/// five and required to pass: a suite that failed against everything would be as useless as one that
/// passed against everything.
/// </summary>
/// <remarks>
/// The doubles are narrow on purpose. A double that pruned nothing at all, or pruned everything
/// including the head, fails every assertion and therefore proves only that the assertions are not
/// vacuous. These four fail exactly the assertions they should and no others, which is what proves
/// each assertion is pinning its own rule rather than riding on a neighbour's.
/// </remarks>
public sealed class BrokenRetentionConformanceTests
{
    [Fact]
    public async Task The_byte_budget_assertion_fails_against_a_store_that_ignores_MaxBytes()
    {
        await using var store = new PolicyDroppingStore(dropMaxBytes: true, dropMaxAge: false);

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            RetentionConformanceTests.AssertMaxBytesAdmitsNewestFirstAsync(store));
    }

    [Fact]
    public async Task The_age_assertion_fails_against_a_store_that_ignores_MaxAge()
    {
        var clock = new ManualTimeProvider();
        await using var store = new PolicyDroppingStore(dropMaxBytes: false, dropMaxAge: true, clock);

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            RetentionConformanceTests.AssertMaxAgeKeepsOnlyTheRecentAsync(store, clock));
    }

    [Fact]
    public async Task The_head_assertion_fails_against_a_store_that_prunes_everything()
    {
        await using var store = new PruneEverythingStore();

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            RetentionConformanceTests.AssertPruneKeepsTheHeadAsync(store));
    }

    [Fact]
    public async Task The_feed_assertion_fails_against_a_store_whose_feed_does_not_shrink()
    {
        await using var store = new FeedBlindStore();

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            RetentionConformanceTests.AssertPrunedRevisionsLeaveTheFeedAsync(store, store));
    }

    [Fact]
    public async Task Every_assertion_passes_against_a_store_that_honours_retention()
    {
        var clock = new ManualTimeProvider();
        await using var store = new InMemoryStateLedgerStore("correct", clock);

        await RetentionConformanceTests.AssertMaxRevisionsKeepsTheNewestAsync(store);
        await RetentionConformanceTests.AssertMaxAgeKeepsOnlyTheRecentAsync(store, clock);
        await RetentionConformanceTests.AssertMaxBytesAdmitsNewestFirstAsync(store);
        await RetentionConformanceTests.AssertPruneKeepsTheHeadAsync(store);
        await RetentionConformanceTests.AssertPrunedRevisionsLeaveTheFeedAsync(store, store);
    }

    /// <summary>
    /// Honours every part of the retention policy except the one it is told to drop, by blanking that
    /// bound before delegating. Deliberately wrong, and deliberately narrow: it keeps pruning
    /// correctly in every other respect, so an assertion that fails against it is failing because of
    /// the one bound and not because the store stopped pruning.
    /// </summary>
    private sealed class PolicyDroppingStore : IStateLedgerStore
    {
        private readonly InMemoryStateLedgerStore _inner;
        private readonly bool _dropMaxBytes;
        private readonly bool _dropMaxAge;

        public PolicyDroppingStore(bool dropMaxBytes, bool dropMaxAge, TimeProvider? clock = null)
        {
            _inner = new InMemoryStateLedgerStore("policy-dropping", clock ?? TimeProvider.System);
            _dropMaxBytes = dropMaxBytes;
            _dropMaxAge = dropMaxAge;
        }

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
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default)
        {
            StateRetentionPolicy weakened = policy;
            if (_dropMaxBytes)
            {
                weakened = weakened with { MaxBytes = null };
            }

            if (_dropMaxAge)
            {
                weakened = weakened with { MaxAge = null };
            }

            return _inner.PruneAsync(address, weakened, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    /// <summary>
    /// Treats a prune as "forget this address", head included. Deliberately wrong: the latest revision
    /// is the one record retention may never remove, and no bound in the policy can authorise it.
    /// </summary>
    private sealed class PruneEverythingStore : IStateLedgerStore
    {
        private readonly InMemoryStateLedgerStore _inner = new("prune-everything", TimeProvider.System);
        private readonly HashSet<string> _erased = new(StringComparer.Ordinal);

        public string Name => _inner.Name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            _erased.Contains(address.Canonical)
                ? ValueTask.FromResult<StateRecord?>(null)
                : _inner.ReadLatestAsync(address, cancellationToken);

        public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
            _erased.Contains(address.Canonical)
                ? Empty(cancellationToken)
                : _inner.ReadHistoryAsync(address, options, cancellationToken);

        public ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(address, condition, commit, cancellationToken);

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default)
        {
            _erased.Add(address.Canonical);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

#pragma warning disable CS1998
        private static async IAsyncEnumerable<StateRecord> Empty(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
#pragma warning restore CS1998
    }

    /// <summary>
    /// Prunes its history correctly and leaves its change feed alone, which is exactly the defect
    /// Phase 11 found in three providers: two bounds that exist to cap memory did not cap the feed.
    /// </summary>
    /// <remarks>
    /// The envelopes are recorded <b>at append time</b> rather than snapshotted on the first read. A
    /// double that built its feed lazily from the inner store on first read would not discriminate at
    /// all, because the shared assertion's only feed read happens after the prune, by which point the
    /// inner store has already dropped the records.
    /// </remarks>
    private sealed class FeedBlindStore : IStateLedgerStore, IStateChangeFeed
    {
        private readonly InMemoryStateLedgerStore _inner = new("feed-blind", TimeProvider.System);
        private readonly List<StateChangeEnvelope> _published = [];

        public string Name => _inner.Name;

        public ValueTask<StateRecord?> ReadLatestAsync(
            StateAddress address, CancellationToken cancellationToken = default) =>
            _inner.ReadLatestAsync(address, cancellationToken);

        public IAsyncEnumerable<StateRecord> ReadHistoryAsync(
            StateAddress address, StateHistoryOptions options, CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryAsync(address, options, cancellationToken);

        public async ValueTask<StateAppendResult> AppendAsync(
            StateAddress address,
            StateWriteCondition condition,
            StateCommit commit,
            CancellationToken cancellationToken = default)
        {
            StateAppendResult result = await _inner.AppendAsync(address, condition, commit, cancellationToken);
            if (result.Record is StateRecord record)
            {
                _published.Add(new StateChangeEnvelope
                {
                    Record = record,
                    Cursor = new StateChangeCursor(record.GlobalPosition),
                });
            }

            return result;
        }

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(address, policy, cancellationToken);

        public async IAsyncEnumerable<StateChangeEnvelope> ReadAsync(
            StateChangeCursor? from,
            StateChangeReadOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            await Task.CompletedTask;
            int yielded = 0;
            foreach (StateChangeEnvelope envelope in _published.OrderBy(entry => entry.Cursor.Position))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (from is StateChangeCursor cursor && envelope.Cursor.Position <= cursor.Position)
                {
                    continue;
                }

                yield return envelope;
                yielded++;
                if (options.Take is int take && yielded >= take)
                {
                    yield break;
                }
            }
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
