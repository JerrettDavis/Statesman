using Xunit.Sdk;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The break-the-mechanism proof for <see cref="RecordMetadataConformanceTests"/>'s round-trip
/// assertion. A conformance suite's own mechanism is discrimination: if its assertions pass against a
/// store that does not implement the contract, they prove nothing about the stores that do. The shared
/// assertion is therefore run here twice — against a deliberately wrong double, which must fail, and
/// against a deliberately correct one, which must pass. The second half matters as much as the first:
/// a suite that failed against everything would be as useless as one that passed against everything.
/// </summary>
public sealed class BrokenRecordMetadataConformanceTests
{
    [Fact]
    public async Task The_metadata_round_trip_assertion_fails_against_a_store_that_drops_metadata()
    {
        await using var store = new MetadataDroppingStore();

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            RecordMetadataConformanceTests.AssertMetadataSurvivesAnAppendAsync(store));
    }

    [Fact]
    public async Task The_metadata_round_trip_assertion_passes_against_a_store_that_keeps_it()
    {
        await using var store = new InMemoryStateLedgerStore("correct-metadata", TimeProvider.System);

        await RecordMetadataConformanceTests.AssertMetadataSurvivesAnAppendAsync(store);
    }

    /// <summary>
    /// Forwards every member to an inner in-memory store except <see cref="AppendAsync"/>, which strips
    /// the commit's metadata before passing it on. Deliberately wrong: it is the provider that treats a
    /// free-form dictionary as optional, which is the defect this contract exists to catch.
    /// </summary>
    private sealed class MetadataDroppingStore : IStateLedgerStore
    {
        private readonly InMemoryStateLedgerStore _inner = new("metadata-dropping", TimeProvider.System);

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
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(commit);
            return _inner.AppendAsync(
                address,
                condition,
                commit with { Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) },
                cancellationToken);
        }

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(address, policy, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
