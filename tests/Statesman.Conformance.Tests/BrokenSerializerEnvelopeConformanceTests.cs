using Xunit.Sdk;

namespace Statesman.Conformance.Tests;

/// <summary>
/// The break-the-mechanism proof for <see cref="SerializerEnvelopeConformanceTests"/>'s round-trip
/// assertion. A conformance suite's own mechanism is discrimination: if its assertions pass against a
/// store that does not implement the contract, they prove nothing about the stores that do. The
/// shared assertion is therefore run here twice — against a deliberately wrong double, which must
/// fail, and against a deliberately correct one, which must pass. The second half matters as much as
/// the first: a suite that failed against everything would be as useless as one that passed against
/// everything.
/// </summary>
public sealed class BrokenSerializerEnvelopeConformanceTests
{
    [Fact]
    public async Task The_envelope_round_trip_assertion_fails_against_a_store_that_drops_the_envelope()
    {
        await using var store = new EnvelopeDroppingStore();

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            SerializerEnvelopeConformanceTests.AssertEnvelopeSurvivesAnAppendAsync(store));
    }

    [Fact]
    public async Task The_envelope_round_trip_assertion_passes_against_a_store_that_keeps_it()
    {
        await using var store = new InMemoryStateLedgerStore("correct-envelope", TimeProvider.System);

        await SerializerEnvelopeConformanceTests.AssertEnvelopeSurvivesAnAppendAsync(store);
    }

    /// <summary>
    /// Forwards every member to an inner in-memory store except <see cref="AppendAsync"/>, which
    /// strips the commit's envelope before passing it on. Deliberately wrong: it is the provider that
    /// persists the payload and forgets how the payload was encoded, which is precisely the defect
    /// this contract exists to catch, and it is the defect a provider gets by simply not adding the
    /// member to its own storage record.
    /// </summary>
    private sealed class EnvelopeDroppingStore : IStateLedgerStore
    {
        private readonly InMemoryStateLedgerStore _inner = new("envelope-dropping", TimeProvider.System);

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
            return _inner.AppendAsync(address, condition, commit with { Envelope = null }, cancellationToken);
        }

        public ValueTask PruneAsync(
            StateAddress address, StateRetentionPolicy policy, CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(address, policy, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
