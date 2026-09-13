namespace Statesman.Conformance.Tests;

/// <summary>
/// What every provider does with a record's free-form <see cref="StateRecord.Metadata"/> dictionary:
/// store it and give it back, unchanged, from the head, from history, and through an exact import.
/// </summary>
/// <remarks>
/// A <b>pinning</b> suite. Every provider serializes this dictionary somewhere — a filesystem
/// <c>head.json</c> and each <c>history/&lt;revision&gt;.json</c>, the Redis record blob, the Entity
/// Framework Core <c>MetadataJson</c> column on both the head and the history entity — and none of them
/// filters by key. That is load-bearing since ROADMAP 0.3 Phase 17, which put three reserved
/// <c>statesman.load.*</c> keys in it precisely so an operator can read a load's timing off a stored
/// record long after the process that wrote it is gone. The discrimination proof is
/// <see cref="BrokenRecordMetadataConformanceTests"/>.
/// </remarks>
public abstract class RecordMetadataConformanceTests
{
    /// <summary>
    /// Creates a store with <b>default</b> options, or returns <see langword="null"/> when this
    /// provider's infrastructure is not available here.
    /// </summary>
    protected abstract ValueTask<ConformanceStore?> CreateAsync();

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    /// <summary>
    /// Asserts that a committed record's metadata comes back from the head read exactly as written.
    /// Public and static so the negative test in this project can run it against a store that drops
    /// metadata and require it to fail.
    /// </summary>
    /// <param name="store">The store under test.</param>
    public static async Task AssertMetadataSurvivesAnAppendAsync(IStateLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var address = new StateAddress("app", $"conformance/metadata-{Guid.NewGuid():N}", StatePartition.Default);

        StateAppendResult appended = await store.AppendAsync(
            address, StateWriteCondition.Absent, Commit("one", Reserved));
        Assert.True(appended.Succeeded);

        StateRecord? head = await store.ReadLatestAsync(address);
        Assert.NotNull(head);
        AssertSame(Reserved, head!.Metadata);
    }

    [Fact]
    public async Task Metadata_survives_an_append_and_a_head_read()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);

        await AssertMetadataSurvivesAnAppendAsync(store!.Store);
    }

    [Fact]
    public async Task Metadata_survives_a_history_read()
    {
        // The half that discriminates a provider which keeps metadata on the head row and drops it
        // from the history row — the shape an operator reading a record months later actually hits,
        // and the shape two of the four storage layouts make easy to get wrong, because the head and
        // the history entry are separate writes.
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", $"conformance/metadata-history-{Guid.NewGuid():N}", StatePartition.Default);

        Assert.True((await store!.Store.AppendAsync(
            address, StateWriteCondition.Absent, Commit("one", Reserved))).Succeeded);

        StateRecord single = await SingleHistoryRecordAsync(store.Store, address);
        AssertSame(Reserved, single.Metadata);
    }

    [Fact]
    public async Task Metadata_survives_an_exact_import()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        Assert.SkipUnless(
            store!.Store.TryGetCapability(out IStateLedgerReplica? replica),
            "This provider is not an import target.");
        var address = new StateAddress("app", $"conformance/metadata-import-{Guid.NewGuid():N}", StatePartition.Default);

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
            Payload = System.Text.Encoding.UTF8.GetBytes("imported"),
            Source = "test",
            Metadata = Reserved,
        });

        StateRecord? head = await store.Store.ReadLatestAsync(address);
        Assert.NotNull(head);
        AssertSame(Reserved, head!.Metadata);
    }

    /// <summary>
    /// The exact key shapes Phase 17 relies on: two reserved loader keys, one reserved timing key, and
    /// one application key, so a provider that filtered on the reserved prefix — or that dropped
    /// anything unfamiliar — fails here rather than in production.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Reserved =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["statesman.load.completeness"] = "partial",
            ["statesman.load.duration.ms"] = "250",
            ["statesman.load.started"] = "2026-01-01T00:00:00.0000000+00:00",
            ["statesman.load.completed"] = "2026-01-01T00:00:00.2500000+00:00",
            ["statesman.source.upstream.status"] = "faulted",
            ["tenant"] = "acme",
        };

    private static void AssertSame(
        IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(
            expected.Keys.Order(StringComparer.OrdinalIgnoreCase),
            actual.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach ((string key, string value) in expected)
        {
            Assert.Equal(value, actual[key]);
        }
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

    private static StateCommit Commit(string value, IReadOnlyDictionary<string, string> metadata) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "test",
        Metadata = metadata,
    };
}
