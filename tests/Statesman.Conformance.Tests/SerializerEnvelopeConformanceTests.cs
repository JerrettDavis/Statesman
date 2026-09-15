using System.Text;

namespace Statesman.Conformance.Tests;

/// <summary>
/// What every provider does with a record's <see cref="StateRecord.Envelope"/>: store it and give it
/// back, unchanged, from the head, from history, and through an exact import, and store its absence
/// as absence rather than as an empty object.
/// </summary>
/// <remarks>
/// A <b>pinning</b> suite, in the shape <see cref="RecordMetadataConformanceTests"/> already uses.
/// Every provider persists the envelope somewhere of its own: a nested <c>envelope</c> object in the
/// filesystem <c>head.json</c> and each history file, the same nested object inside the Redis record
/// blob across two keys, and a nullable <c>EnvelopeJson</c> column on both Entity Framework Core
/// entities. Four storage layouts, one contract, and the null half is as load-bearing as the
/// populated half: a record written before envelopes existed must keep reading back with a null
/// envelope forever, which is the whole of this feature's compatibility story. The discrimination
/// proof is <see cref="BrokenSerializerEnvelopeConformanceTests"/>; the stored-bytes half is
/// <see cref="SerializerEnvelopeLegacyReadTests"/>.
/// </remarks>
public abstract class SerializerEnvelopeConformanceTests
{
    /// <summary>The envelope every fact in this suite writes.</summary>
    public static readonly StateEnvelope Sample = new()
    {
        ContentType = "application/json",
        SerializerId = "statesman.json/v1",
        Fingerprint = "fingerprint-conformance",
        FormatVersion = StateEnvelope.CurrentFormatVersion,
    };

    /// <summary>
    /// Creates a store with <b>default</b> options, or returns <see langword="null"/> when this
    /// provider's infrastructure is not available here.
    /// </summary>
    protected abstract ValueTask<ConformanceStore?> CreateAsync();

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    /// <summary>
    /// Asserts that a committed record's envelope comes back from the head read exactly as written.
    /// Public and static so the negative test in this project can run it against a store that drops
    /// the envelope and require it to fail.
    /// </summary>
    /// <param name="store">The store under test.</param>
    public static async Task AssertEnvelopeSurvivesAnAppendAsync(IStateLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var address = new StateAddress("app", $"conformance/envelope-{Guid.NewGuid():N}", StatePartition.Default);

        StateAppendResult appended = await store.AppendAsync(
            address, StateWriteCondition.Absent, Commit("one", Sample));
        Assert.True(appended.Succeeded);

        StateRecord? head = await store.ReadLatestAsync(address);
        Assert.NotNull(head);
        Assert.Equal(Sample, head.Envelope);
    }

    [Fact]
    public async Task An_envelope_survives_an_append_and_a_head_read()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);

        await AssertEnvelopeSurvivesAnAppendAsync(store!.Store);
    }

    [Fact]
    public async Task An_envelope_survives_a_history_read()
    {
        // The half that discriminates a provider which keeps the envelope on the head and drops it
        // from the history entry. Two of the four storage layouts write those separately, which is
        // exactly where the metadata contract found the same class of defect worth pinning.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", $"conformance/envelope-history-{Guid.NewGuid():N}", StatePartition.Default);

        Assert.True((await store!.Store.AppendAsync(
            address, StateWriteCondition.Absent, Commit("one", Sample))).Succeeded);

        StateRecord single = await SingleHistoryRecordAsync(store.Store, address);
        Assert.Equal(Sample, single.Envelope);
    }

    [Fact]
    public async Task An_envelope_survives_an_exact_import()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateLedgerReplica? replica),
            "This provider is not an import target.");
        var address = new StateAddress("app", $"conformance/envelope-import-{Guid.NewGuid():N}", StatePartition.Default);

        await replica!.ImportAsync(new StateRecord
        {
            Address = address,
            Revision = 1,
            GlobalPosition = 100,
            OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Operation = StateOperation.Imported,
            Status = StateStatus.Ready,
            ValueType = typeof(string).FullName!,
            SchemaVersion = 1,
            Payload = Encoding.UTF8.GetBytes("imported"),
            Envelope = Sample,
            Source = "test",
        });

        StateRecord? head = await store.Store.ReadLatestAsync(address);
        Assert.NotNull(head);
        Assert.Equal(Sample, head.Envelope);
    }

    [Fact]
    public async Task A_record_written_without_an_envelope_reads_back_with_none()
    {
        // Absence stored as absence. A provider that materialised an empty envelope here, or that
        // round-tripped null through a default instance, would break every record written before
        // this release without failing anything else in the suite.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", $"conformance/envelope-none-{Guid.NewGuid():N}", StatePartition.Default);

        Assert.True((await store!.Store.AppendAsync(
            address, StateWriteCondition.Absent, Commit("one", envelope: null))).Succeeded);

        StateRecord? head = await store.Store.ReadLatestAsync(address);
        Assert.NotNull(head);
        Assert.Null(head.Envelope);
        Assert.Null((await SingleHistoryRecordAsync(store.Store, address)).Envelope);
    }

    private static async Task<StateRecord> SingleHistoryRecordAsync(IStateLedgerStore store, StateAddress address)
    {
        var records = new List<StateRecord>();
        await foreach (StateRecord record in store.ReadHistoryAsync(address, new StateHistoryOptions()))
        {
            records.Add(record);
        }

        return Assert.Single(records);
    }

    private static StateCommit Commit(string value, StateEnvelope? envelope) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Envelope = envelope,
        Source = "test",
    };
}
