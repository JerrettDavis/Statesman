using System.Runtime.CompilerServices;
using Xunit.Sdk;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The break-the-mechanism proof for <see cref="PartitionCatalogConformanceTests"/>'s
/// one-descriptor-per-address assertion. A conformance suite's own mechanism is discrimination: if
/// its assertions pass against a store that does not implement the contract, they prove nothing about
/// the stores that do. The shared assertion is therefore run here twice — against a deliberately wrong
/// double, which must fail, and against a deliberately correct one, which must pass. The second half
/// matters as much as the first: a suite that failed against everything would be as useless as one
/// that passed against everything.
/// </summary>
public sealed class BrokenPartitionCatalogConformanceTests
{
    [Fact]
    public async Task The_one_descriptor_per_address_assertion_fails_against_a_catalog_that_reports_one_per_revision()
    {
        await using var store = new PerRevisionCatalogStore();

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            PartitionCatalogConformanceTests.AssertOneDescriptorPerAddressAsync(store, store));
    }

    [Fact]
    public async Task The_one_descriptor_per_address_assertion_passes_against_a_catalog_that_collapses_by_address()
    {
        await using var store = new InMemoryStateLedgerStore("correct-catalog", TimeProvider.System);

        await PartitionCatalogConformanceTests.AssertOneDescriptorPerAddressAsync(store, store);
    }

    /// <summary>
    /// Implements both <see cref="IStateLedgerStore"/> and <see cref="IPartitionCatalog"/>, forwarding
    /// every store member to an inner in-memory store but reporting one descriptor per change-feed
    /// entry instead of collapsing by address. Deliberately wrong: this is the exact defect the shared
    /// assertion exists to catch — a catalog that never collapses a stream's revisions into one
    /// partition.
    /// </summary>
    private sealed class PerRevisionCatalogStore : IStateLedgerStore, IPartitionCatalog
    {
        private readonly InMemoryStateLedgerStore _inner = new("per-revision-catalog", TimeProvider.System);

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

        public async IAsyncEnumerable<StatePartitionDescriptor> ListPartitionsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Deliberately wrong: yields one descriptor per feed entry (one per revision) instead of
            // collapsing every address's revisions into a single descriptor.
            await foreach (StateChangeEnvelope envelope in
                _inner.ReadAsync(null, StateChangeReadOptions.Default, cancellationToken))
            {
                yield return new StatePartitionDescriptor
                {
                    Address = envelope.Record.Address,
                    LastPosition = envelope.Cursor,
                };
            }
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
