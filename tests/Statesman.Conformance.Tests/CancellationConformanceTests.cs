namespace Statesman.Conformance.Tests;

/// <summary>
/// The cancellation contract every ledger store shares: an already-cancelled token is observed before
/// the operation has any effect, on the store's four operations and on every capability the store
/// advertises. Before ROADMAP 0.3 Phase 16 this was stated nowhere and pinned in three places, one of
/// them added in Phase 15 after a provider was found issuing an HGETALL before checking its token.
/// </summary>
/// <remarks>
/// A <b>pinning</b> suite: a pre-flight probe measured all thirty-nine provider-and-operation pairs
/// already throwing <see cref="OperationCanceledException"/>. Its discrimination proof is
/// <see cref="BrokenStoreConformanceTests"/>, which runs
/// <see cref="AssertAppendHonoursCancellationAsync"/> against a store that swallows its token.
/// Capabilities are reached through <c>TryGetCapability</c> at the assertion site, never through a
/// per-provider boolean, so a provider that honestly does not implement one skips with a reason
/// instead of passing silently.
/// </remarks>
public abstract class CancellationConformanceTests
{
    /// <summary>
    /// Creates a store with <b>default</b> options, or returns <see langword="null"/> when this
    /// provider's infrastructure is not available here.
    /// </summary>
    protected abstract ValueTask<ConformanceStore?> CreateAsync();

    /// <summary>Why this provider was skipped, shown when <see cref="CreateAsync"/> returns null.</summary>
    protected virtual string SkipReason => "This provider's infrastructure is not available.";

    /// <summary>
    /// Asserts that an append with an already-cancelled token throws and writes nothing. Public and
    /// static so the negative test in this project can run it against a store that swallows its token
    /// and require it to fail.
    /// </summary>
    /// <param name="store">The store under test.</param>
    public static async Task AssertAppendHonoursCancellationAsync(IStateLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var address = new StateAddress("app", $"conformance/cancel-{Guid.NewGuid():N}", StatePartition.Default);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("never"), source.Token));

        // The half that discriminates a store which throws after writing.
        Assert.Null(await store.ReadLatestAsync(address));
    }

    [Fact]
    public async Task An_already_cancelled_token_is_honoured_by_every_store_operation()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/cancel-core", StatePartition.Default);
        await store!.Store.AppendAsync(address, StateWriteCondition.Absent, Commit("seed"));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        CancellationToken cancelled = source.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.Store.ReadLatestAsync(address, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (StateRecord _ in store.Store.ReadHistoryAsync(
                address, new StateHistoryOptions(), cancelled))
            {
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.Store.AppendAsync(
                address, StateWriteCondition.AtRevision(1), Commit("never"), cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.Store.PruneAsync(
                address, new StateRetentionPolicy { MaxRevisions = 1 }, cancelled));
    }

    [Fact]
    public async Task An_already_cancelled_token_is_honoured_by_every_capability_this_provider_advertises()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);
        var address = new StateAddress("app", "conformance/cancel-capabilities", StatePartition.Default);
        await store!.Store.AppendAsync(address, StateWriteCondition.Absent, Commit("seed"));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        CancellationToken cancelled = source.Token;

        // Every capability is gated by TryGetCapability rather than by a per-provider flag: a store
        // that stops implementing one starts skipping here loudly instead of passing quietly, and a
        // store that starts implementing one is covered with no edit to this file.
        var checkedCapabilities = new List<string>();

        if (store.Store.TryGetCapability(out IStateChangeFeed? feed))
        {
            checkedCapabilities.Add(nameof(IStateChangeFeed));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (StateChangeEnvelope _ in feed!.ReadAsync(
                    null, StateChangeReadOptions.Default, cancelled))
                {
                }
            });
        }

        if (store.Store.TryGetCapability(out IPartitionCatalog? catalog))
        {
            checkedCapabilities.Add(nameof(IPartitionCatalog));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (StatePartitionDescriptor _ in catalog!.ListPartitionsAsync(cancelled))
                {
                }
            });
        }

        if (store.Store.TryGetCapability(out IStateLedgerReplica? replica))
        {
            checkedCapabilities.Add(nameof(IStateLedgerReplica));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await replica!.ImportAsync(Record(address, revision: 5, position: 5000), cancelled));
        }

        if (store.Store.TryGetCapability(out IDistributedCapture? capture))
        {
            checkedCapabilities.Add(nameof(IDistributedCapture));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await capture!.CaptureAsync(
                    [address], StateCaptureConsistency.ReadCommittedDistributed, cancelled));
        }

        if (store.Store.TryGetCapability(out IStateLeaseProvider? leases))
        {
            checkedCapabilities.Add(nameof(IStateLeaseProvider));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await leases!.AcquireAsync($"conformance-cancel-{Guid.NewGuid():N}", TimeSpan.FromSeconds(30), cancelled));
        }

        // Every shipped provider advertises at least the change feed and the partition catalog, so a
        // run that checked nothing means discovery itself broke, not that this provider is minimal.
        Assert.NotEmpty(checkedCapabilities);
    }

    [Fact]
    public async Task A_cancelled_append_writes_nothing()
    {
        await using ConformanceStore? store = await CreateAsync();
        Assert.SkipUnless(store is not null, SkipReason);

        await AssertAppendHonoursCancellationAsync(store!.Store);
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes(value),
        Source = "test",
    };

    private static StateRecord Record(StateAddress address, long revision, long position) => new()
    {
        Address = address,
        Revision = revision,
        GlobalPosition = position,
        OccurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Operation = StateOperation.Imported,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = System.Text.Encoding.UTF8.GetBytes("never"),
        Source = "test",
    };
}
